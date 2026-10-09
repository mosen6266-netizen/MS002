using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SignalScheduler.Engine;
using SignalScheduler.Engine.Engine;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Engine.Signal;
using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class LiveProbeTests
{
    static readonly CancellationToken Ct=CancellationToken.None;

    [Fact]
    public async Task JsonRpcSendConfirmedOnlyForMatchingIdAndTimestamp()
    {
        var handler=new MockRpcHandler(request=>
        {
            var id=request.GetProperty("id").GetString();
            Assert.Equal("2.0",request.GetProperty("jsonrpc").GetString());
            Assert.Equal("send",request.GetProperty("method").GetString());
            var p=request.GetProperty("params");
            Assert.Equal("+49123",p.GetProperty("account").GetString());
            Assert.False(p.TryGetProperty("groupId",out _));
            Assert.Equal("group-1",
                Assert.Single(p.GetProperty("groupIds").EnumerateArray()).GetString());
            Assert.StartsWith("[SignalScheduler 实发测试]",
                p.GetProperty("message").GetString());
            return (HttpStatusCode.OK,JsonSerializer.Serialize(new
            {
                jsonrpc="2.0",id,result=new {timestamp=1724000000000L}
            }));
        });
        var transport=new SignalCliTransport(
            new HttpClient(handler){BaseAddress=new Uri("http://127.0.0.1:7583/")},
            ()=>true);
        var sent=await transport.SendAsync(Dispatch(),
            "[SignalScheduler 实发测试] only diagnostic",Ct);
        Assert.Equal(SignalDeliveryOutcome.Confirmed,sent.Outcome);
        Assert.Equal("1724000000000",sent.ProviderMessageId);
        Assert.Equal(1,handler.Calls);
    }

    [Theory]
    [InlineData("wrong-id")]
    [InlineData("rpc-error")]
    [InlineData("no-timestamp")]
    [InlineData("http-error")]
    [InlineData("invalid-json")]
    public async Task InvalidOrUncertainRpcResponsesNeverCountAsConfirmed(string variant)
    {
        var handler=new MockRpcHandler(req=>
        {
            var id=req.GetProperty("id").GetString();
            return variant switch
            {
                "wrong-id"=>(HttpStatusCode.OK,JsonSerializer.Serialize(new
                    {jsonrpc="2.0",id="unknown",result=new{timestamp=12L}})),
                "rpc-error"=>(HttpStatusCode.OK,JsonSerializer.Serialize(new
                    {jsonrpc="2.0",id,error=new{code=-32000,message="bad"}})),
                "no-timestamp"=>(HttpStatusCode.OK,JsonSerializer.Serialize(new
                    {jsonrpc="2.0",id,result=new{}})),
                "http-error"=>(HttpStatusCode.ServiceUnavailable,""),
                _=>(HttpStatusCode.OK,"not-json")
            };
        });
        var transport=new SignalCliTransport(new HttpClient(handler)
            {BaseAddress=new Uri("http://127.0.0.1:7583/")},()=>true);
        var result=await transport.SendAsync(Dispatch(),
            "[SignalScheduler 实发测试] test",Ct);
        Assert.Equal(SignalDeliveryOutcome.Ambiguous,result.Outcome);
        Assert.Null(result.ProviderMessageId);
        Assert.Equal(1,handler.Calls);
    }

    [Fact]
    public async Task HealthGatePreventsAnyHttpCall()
    {
        var handler=new MockRpcHandler(_=>throw new Exception("never called"));
        var transport=new SignalCliTransport(new HttpClient(handler)
            {BaseAddress=new Uri("http://127.0.0.1:7583/")},()=>false);
        Assert.False(transport.IsReady);
        var result=await transport.SendAsync(Dispatch(),
            "[SignalScheduler 实发测试] test",Ct);
        Assert.Equal(SignalDeliveryOutcome.DefinitelyNotSent,result.Outcome);
        Assert.Equal(0,handler.Calls);
    }

    [Fact]
    public async Task ConfirmedOneShotPersistsAndSecondClickCannotResend()
    {
        var (store,db)=await SetupAsync();
        var request=new LiveProbeRequest("+49123","group-1",true);
        var (dispatch,_,message)=await store.CreateLiveProbeAsync(request,Ct);
        var calls=0;
        var engine=new DurableTaskEngine(store,new FakeTransport((d,p,ct)=>
        {
            calls++;
            return Task.FromResult(new SignalSendResult(
                SignalDeliveryOutcome.Confirmed,"123456789","RPC ACK"));
        }));
        var result=await engine.DispatchAsync(dispatch,message,Ct);
        Assert.Equal(SignalDeliveryOutcome.Confirmed,result.Outcome);
        await store.FinishLiveProbeAsync(dispatch.JobId,true,result.Detail!,Ct);
        var status=await store.GetLiveProbeResultAsync(dispatch.JobId,Ct);
        Assert.Equal("Completed",status.State);
        Assert.Equal("123456789",status.ProviderMessageId);
        Assert.Equal("1",await ScalarAsync(db,"SELECT cursor FROM v8_jobs"));

        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.CreateLiveProbeAsync(request,Ct)); // 90-second machine cooldown
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            engine.DispatchAsync(dispatch,message,Ct));
        Assert.Equal(1,calls);
    }

    [Fact]
    public async Task AmbiguousOneShotNeverResends_AndStartupQuarantines()
    {
        var (store,db)=await SetupAsync();
        var request=new LiveProbeRequest("+49123","group-1",true);
        var (dispatch,_,message)=await store.CreateLiveProbeAsync(request,Ct);
        var calls=0;
        var engine=new DurableTaskEngine(store,new FakeTransport((d,p,ct)=>
        {
            calls++;
            return Task.FromResult(new SignalSendResult(
                SignalDeliveryOutcome.Ambiguous,Detail:"socket disconnected"));
        }));
        await engine.DispatchAsync(dispatch,message,Ct);
        await store.FinishLiveProbeAsync(dispatch.JobId,false,"unknown",Ct);
        Assert.Equal("RecoveryRequired",
            (await store.GetLiveProbeResultAsync(dispatch.JobId,Ct)).State);
        await store.InitializeAsync(Ct);
        Assert.Equal("RecoveryRequired",
            (await store.GetLiveProbeResultAsync(dispatch.JobId,Ct)).State);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.CreateLiveProbeAsync(request,Ct));
        Assert.Equal(1,calls);
        Assert.Equal("0",await ScalarAsync(db,"SELECT cursor FROM v8_jobs"));
    }

    [Fact]
    public async Task MissingConsentAndNonMemberDoNotCreateSendingJobs()
    {
        var (store,db)=await SetupAsync();
        await Assert.ThrowsAsync<ArgumentException>(()=>
            store.CreateLiveProbeAsync(new("+49123","group-1",false),Ct));
        await Assert.ThrowsAsync<ArgumentException>(()=>
            store.CreateLiveProbeAsync(new("+49123","different",true),Ct));
        Assert.Equal("0",await ScalarAsync(db,"SELECT COUNT(*) FROM v8_live_probe_jobs"));
        Assert.Equal("0",await ScalarAsync(db,"SELECT COUNT(*) FROM v8_dispatch_journal"));
    }

    static DispatchIdentity Dispatch()=>
        new("job","token",0,0,"group-1","+49123","hash");

    static async Task<(StateStore,string)> SetupAsync()
    {
        var dir=Path.Combine(Path.GetTempPath(),"SignalLiveProbe",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db=Path.Combine(dir,"data.db");
        var store=new StateStore(RuntimePaths.ForTesting(dir,db));
        await store.InitializeAsync(Ct);
        await store.InitializeLiveProbeAsync(Ct);
        await store.SyncSignalCatalogAsync(new[]{"+49123"},
            new[]{new SignalGroupCatalogItem("+49123","group-1","Test Group",
                true,Array.Empty<string>())},new[]{"+49123"},Ct);
        await store.SetSelectedGroupsAsync(
            new UpdateGroupSelection(new[]{"group-1"}),Ct);
        return (store,db);
    }

    static async Task<string?> ScalarAsync(string db,string sql)
    {
        await using var c=new SqliteConnection($"Data Source={db}");
        await c.OpenAsync();
        await using var cmd=c.CreateCommand();
        cmd.CommandText=sql;
        return Convert.ToString(await cmd.ExecuteScalarAsync());
    }

    sealed class MockRpcHandler : HttpMessageHandler
    {
        readonly Func<JsonElement,(HttpStatusCode,string)> _handle;
        public int Calls {get;private set;}
        public MockRpcHandler(Func<JsonElement,(HttpStatusCode,string)> handle)=>
            _handle=handle;
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;
            using var doc=JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(ct));
            var (code,body)=_handle(doc.RootElement);
            return new HttpResponseMessage(code){
                Content=new StringContent(body,Encoding.UTF8,"application/json")
            };
        }
    }

    sealed class FakeTransport : ISignalTransport
    {
        readonly Func<DispatchIdentity,string,CancellationToken,Task<SignalSendResult>> _send;
        public bool IsReady=>true;
        public FakeTransport(Func<DispatchIdentity,string,CancellationToken,Task<SignalSendResult>> send)
            =>_send=send;
        public Task<SignalSendResult> SendAsync(
            DispatchIdentity d,string p,CancellationToken ct)=>_send(d,p,ct);
    }
}
