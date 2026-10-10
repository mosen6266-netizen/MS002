using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SignalScheduler.Engine;
using SignalScheduler.Engine.Signal;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class ReadReceiptRpcIntegrationTests
{
    sealed class RpcHandler : HttpMessageHandler
    {
        public readonly List<JsonElement> Calls=new();
        public int? ErrorCode {get;set;}
        public bool BadId {get;set;}
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req,CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Post,req.Method);
            Assert.Equal("/api/v1/rpc",req.RequestUri!.AbsolutePath);
            using var payload=JsonDocument.Parse(await req.Content!.ReadAsStringAsync(ct));
            Calls.Add(payload.RootElement.Clone());
            var id=BadId?"incorrect":payload.RootElement.GetProperty("id").GetString();
            var body=ErrorCode.HasValue
                ?JsonSerializer.Serialize(new {
                    jsonrpc="2.0",id,
                    error=new {code=ErrorCode.Value,message="secret +491234567890"}
                })
                :JsonSerializer.Serialize(new {jsonrpc="2.0",id,result=(object?)null});
            return new HttpResponseMessage(HttpStatusCode.OK){
                Content=new StringContent(body,Encoding.UTF8,"application/json")
            };
        }
    }

    static async Task<(SignalReadCoordinator Read,RpcHandler Handler,string Database,string Directory)> Setup()
    {
        var dir=Path.Combine(Path.GetTempPath(),"ms002-read-batch-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var database=Path.Combine(dir,"data.db");
        var handler=new RpcHandler();
        var client=new HttpClient(handler) {
            BaseAddress=new Uri("http://127.0.0.1:7583/"),
            Timeout=Timeout.InfiniteTimeSpan
        };
        var read=new SignalReadCoordinator(RuntimePaths.ForTesting(dir,database),
            NullLogger<SignalReadCoordinator>.Instance,client);
        await read.GetHealthAsync(CancellationToken.None);
        return (read,handler,database,dir);
    }

    static async Task Seed(string db,string account,string group,string author,
        params long[] times)
    {
        await using var conn=new SqliteConnection($"Data Source={db}");
        await conn.OpenAsync();
        foreach(var time in times)
        {
            await using var command=conn.CreateCommand();
            command.CommandText="""
                INSERT INTO v8_read_events(account,group_id,author,timestamp_ms)
                VALUES($a,$g,$u,$t);
                """;
            command.Parameters.AddWithValue("$a",account);
            command.Parameters.AddWithValue("$g",group);
            command.Parameters.AddWithValue("$u",author);
            command.Parameters.AddWithValue("$t",time);
            await command.ExecuteNonQueryAsync();
        }
    }

    static async Task<int> StateCount(string db,string state)
    {
        await using var conn=new SqliteConnection($"Data Source={db}");
        await conn.OpenAsync();
        await using var command=conn.CreateCommand();
        command.CommandText="SELECT COUNT(*) FROM v8_read_events WHERE state=$s";
        command.Parameters.AddWithValue("$s",state);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task DueSpeakerBatchesMultipleTimestampsWithoutReadingOtherAccountsOrGroups()
    {
        var t=await Setup();
        try
        {
            await Seed(t.Database,"account-A","group-A","author-1",101,102,103,104);
            await Seed(t.Database,"account-A","group-B","author-2",105);
            await Seed(t.Database,"account-B","group-A","author-3",106);
            await t.Read.TrySendForGroupAsync("account-A","group-A",CancellationToken.None);
            var rpc=Assert.Single(t.Handler.Calls);
            Assert.Equal("sendReceipt",rpc.GetProperty("method").GetString());
            var p=rpc.GetProperty("params");
            Assert.Equal("account-A",p.GetProperty("account").GetString());
            Assert.Equal("author-1",p.GetProperty("recipient").GetString());
            Assert.Equal("read",p.GetProperty("type").GetString());
            var timestamps=p.GetProperty("targetTimestamps").EnumerateArray()
                .Select(x=>x.GetInt64()).ToArray();
            Assert.Equal(new long[]{101,102,103,104},timestamps);
            Assert.Equal(4,await StateCount(t.Database,"attempted"));
            Assert.Equal(2,await StateCount(t.Database,"pending"));
            var health=await t.Read.GetHealthAsync(CancellationToken.None);
            Assert.Equal("RPC_ACCEPTED",health.LastReceiptRpcCode);
            Assert.Equal(4,health.LastReceiptSelected);
            Assert.Equal(4,health.LastReceiptAccepted);
        }
        finally{
            SqliteConnection.ClearAllPools();
            Directory.Delete(t.Directory,true);
        }
    }

    [Theory]
    [InlineData(-32602,"failed","RPC_REJECTED_-32602")]
    [InlineData(-32603,"unknown","RPC_INTERNAL_ERROR")]
    public async Task RejectedAndAmbiguousResponsesAreNeverRecordedAsReads(
        int code,string state,string diagnostic)
    {
        var t=await Setup();
        try
        {
            t.Handler.ErrorCode=code;
            await Seed(t.Database,"account","group","author",201,202);
            await t.Read.TrySendForGroupAsync("account","group",CancellationToken.None);
            Assert.Single(t.Handler.Calls);
            Assert.Equal(0,await StateCount(t.Database,"attempted"));
            Assert.Equal(2,await StateCount(t.Database,state));
            Assert.Equal(diagnostic,(await t.Read.GetHealthAsync(CancellationToken.None)).LastReceiptRpcCode);
            var after=new SignalReadCoordinator(RuntimePaths.ForTesting(t.Directory,t.Database),
                NullLogger<SignalReadCoordinator>.Instance);
            await after.TrySendForGroupAsync("account","group",CancellationToken.None);
            Assert.Single(t.Handler.Calls); // no automatic retry of a failure or unknown result
        }
        finally{
            SqliteConnection.ClearAllPools();
            Directory.Delete(t.Directory,true);
        }
    }

    [Fact]
    public async Task NoPendingMessagesNeverTriggersReadReceiptRpc()
    {
        var t=await Setup();
        try
        {
            await t.Read.TrySendForGroupAsync("account","group",CancellationToken.None);
            Assert.Empty(t.Handler.Calls);
            Assert.Equal("NO_PENDING_FOR_SPEAKER",
                (await t.Read.GetHealthAsync(CancellationToken.None)).LastReceiptRpcCode);
        }
        finally{
            SqliteConnection.ClearAllPools();
            Directory.Delete(t.Directory,true);
        }
    }

    [Fact]
    public void RpcContractRequiresMatchingIdAndResultAndDoesNotLeakDaemonText()
    {
        Assert.Equal(ReceiptRpcStatus.Accepted,ReadReceiptRpcContract.Check(
            200,"{\"jsonrpc\":\"2.0\",\"id\":\"abc\",\"result\":null}","abc").Status);
        Assert.Equal("RPC_MISMATCH",ReadReceiptRpcContract.Check(
            200,"{\"jsonrpc\":\"2.0\",\"id\":\"other\",\"result\":null}","abc").Code);
        Assert.Equal("RPC_NO_RESULT",ReadReceiptRpcContract.Check(
            200,"{\"jsonrpc\":\"2.0\",\"id\":\"abc\"}","abc").Code);
        Assert.Equal("RPC_INVALID_JSON",ReadReceiptRpcContract.Check(
            200,"private-message-not-json","abc").Code);
        Assert.False(ReadReceiptRpcContract.IsSafeCode("private +491234567890"));
    }
}
