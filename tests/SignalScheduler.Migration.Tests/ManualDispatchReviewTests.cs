using Microsoft.Data.Sqlite;
using SignalScheduler.Engine;
using SignalScheduler.Engine.Engine;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Engine.Signal;
using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class ManualDispatchReviewTests
{
    static readonly CancellationToken Ct=CancellationToken.None;

    [Fact]
    public async Task ConfirmedSeenSkipsAlreadyReceivedMessageButDoesNotResend()
    {
        var (store,db,script)=await StartAsync(1);
        var d=(await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000,10,Ct)).Single();
        await MakeAmbiguousAsync(store,d);

        var review=await store.ReviewAmbiguousDispatchAsync(
            new(d.Dispatch.JobId,d.Dispatch.DispatchKey,"seen",
                "在测试 Signal 群中核实消息和时间"),Ct);
        Assert.Equal("ManuallyConfirmed",review.JournalState);
        Assert.Equal("Completed",review.JobState);
        Assert.Equal(1,review.Cursor);
        Assert.Empty(await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+60000,10,Ct));
        Assert.Equal("1",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_dispatch_journal WHERE state='ManuallyConfirmed'"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.ReviewAmbiguousDispatchAsync(
                new(d.Dispatch.JobId,d.Dispatch.DispatchKey,"seen",
                    "重复核对记录绝对不应成功"),Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.StartLiveBatchAsync(new(script.ScriptId,new[]{"g1"},true),Ct));
    }

    [Fact]
    public async Task VerifiedNotSentKeepsCursor_UntilExplicitManualResume()
    {
        var (store,db,_)=await StartAsync(2);
        var due=(await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000,10,Ct)).Single();
        await MakeAmbiguousAsync(store,due);
        var reviewed=await store.ReviewAmbiguousDispatchAsync(
            new(due.Dispatch.JobId,due.Dispatch.DispatchKey,"not_seen",
                "我已检查测试群及发送账号，没有这条消息"),Ct);
        Assert.Equal("DefinitelyNotSent",reviewed.JournalState);
        Assert.Equal("Paused",reviewed.JobState);
        Assert.Equal(0,reviewed.Cursor);
        Assert.Empty(await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+60000,10,Ct));

        await store.ControlLiveBatchAsync(new(due.Dispatch.JobId,"resume"),Ct);
        var pending=await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+60000,10,Ct);
        Assert.Single(pending);
        Assert.Equal(0,pending[0].Dispatch.Cursor);
        var engine=new DurableTaskEngine(store,new FakeTransport(
            new SignalSendResult(SignalDeliveryOutcome.Confirmed,"123","ACK")));
        await engine.DispatchAsync(pending[0].Dispatch,"signal-structured:{\"Message\":\"hello\"}",Ct);
        await store.ConfirmLiveBatchStepAsync(
            pending[0].Dispatch,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+60000,Ct);
        Assert.Equal("Confirmed",await ScalarAsync(db,
            "SELECT state FROM v8_dispatch_journal LIMIT 1"));
        Assert.Equal("1",await ScalarAsync(db,"SELECT cursor FROM v8_jobs LIMIT 1"));
        Assert.Equal("1",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_event_log WHERE event_type='manual_confirmed_not_sent'"));
    }

    [Fact]
    public async Task CannotReconcileWithoutSpecificEvidenceOrCorrectCurrentCursor()
    {
        var (store,db,_)=await StartAsync(1);
        var item=(await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000,10,Ct)).Single();
        await MakeAmbiguousAsync(store,item);
        await Assert.ThrowsAsync<ArgumentException>(()=>
            store.ReviewAmbiguousDispatchAsync(
                new(item.Dispatch.JobId,item.Dispatch.DispatchKey,"seen","yes"),Ct));
        await Assert.ThrowsAsync<ArgumentException>(()=>
            store.ReviewAmbiguousDispatchAsync(
                new(item.Dispatch.JobId,item.Dispatch.DispatchKey,"other",
                    "已经核对了消息在 Signal 中的位置"),Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(()=>
            store.ReviewAmbiguousDispatchAsync(
                new(item.Dispatch.JobId,"wrong-key","seen",
                    "已经核对了消息在 Signal 中的位置"),Ct));
        Assert.Equal("RecoveryRequired",(await store.ListLiveBatchAsync(Ct)).Single().State);
        Assert.Equal("0",await ScalarAsync(db,"SELECT cursor FROM v8_jobs"));
    }

    static async Task MakeAmbiguousAsync(StateStore store,StateStore.DueLiveBatch due)
    {
        var engine=new DurableTaskEngine(store,
            new FakeTransport(new SignalSendResult(
                SignalDeliveryOutcome.Ambiguous,Detail:"timeout")));
        await engine.DispatchAsync(due.Dispatch,
            "signal-structured:{\"Message\":\"not sure\"}",Ct);
        Assert.Equal("RecoveryRequired",
            (await store.ListLiveBatchAsync(Ct)).Single().State);
    }

    static async Task<(StateStore,string,ScriptEditorDocument)> StartAsync(int count)
    {
        var root=Path.Combine(Path.GetTempPath(),"SignalManualReview",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db=Path.Combine(root,"data.db");
        var store=new StateStore(RuntimePaths.ForTesting(root,db));
        await store.InitializeAsync(Ct);
        await store.InitializeLiveBatchAsync(Ct);
        var account="+49123";
        await store.SyncSignalCatalogAsync(new[]{account},
            new[]{new SignalGroupCatalogItem(account,"g1","已授权的测试群",
                true,Array.Empty<string>())},new[]{account},Ct);
        await store.SetSelectedGroupsAsync(new UpdateGroupSelection(new[]{"g1"}),Ct);
        var steps=Enumerable.Range(0,count)
            .Select(i=>new ScriptEditorStep(i,"","消息"+i,"",false,"",0,0)).ToArray();
        var script=await store.SaveEditorScriptAsync(
            new ScriptSaveRequest(null,"消息排程测试","",0,steps),Ct);
        await store.StartLiveBatchAsync(
            new LiveBatchStartRequest(script.ScriptId,new[]{"g1"},true),Ct);
        return (store,db,script);
    }

    static async Task<string?> ScalarAsync(string db,string sql)
    {
        await using var c=new SqliteConnection($"Data Source={db}");
        await c.OpenAsync();
        await using var q=c.CreateCommand();q.CommandText=sql;
        return Convert.ToString(await q.ExecuteScalarAsync());
    }

    sealed class FakeTransport : ISignalTransport
    {
        readonly SignalSendResult _result;
        public FakeTransport(SignalSendResult result)=>_result=result;
        public bool IsReady=>true;
        public Task<SignalSendResult> SendAsync(
            DispatchIdentity d,string p,CancellationToken ct)=>
            Task.FromResult(_result);
    }
}
