using Microsoft.Data.Sqlite;
using SignalScheduler.Engine;
using SignalScheduler.Engine.Engine;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Engine.Signal;
using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class LiveBatchTests
{
    static readonly CancellationToken Ct=CancellationToken.None;

    [Fact]
    public async Task TwoSelectedGroupsCreateIndependentFrozenScripts_AndOnlyOneDispatchPerStep()
    {
        var (store,db)=await NewStoreAsync();
        await SeedAsync(store);
        var script=await MakeScriptAsync(store,new []{
            Step(0,"第一条",false),
            Step(1,"第二条",true),
            Step(2,"第三条",false),
            Step(3,"第四条",false)
        });
        var started=await store.StartLiveBatchAsync(
            new LiveBatchStartRequest(script.ScriptId,new[]{"g1","g2"},true),Ct);
        Assert.Equal(2,started.GroupCount);
        Assert.Equal(4,started.MessageCount);
        Assert.Equal(2,started.JobIds.Count);
        var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000;
        var due=await store.FindDueLiveBatchAsync(now,20,Ct);
        Assert.Equal(2,due.Count);
        Assert.All(due,x=>Assert.Equal(0,x.Dispatch.Cursor));

        var sends=0;
        var engine=new DurableTaskEngine(store,new StubTransport((d,p,ct)=>{
            sends++;
            return Task.FromResult(new SignalSendResult(
                SignalDeliveryOutcome.Confirmed,sends.ToString(),"ACK"));
        }));
        var group1=due.Single(x=>x.Dispatch.GroupId=="g1");
        var group2=due.Single(x=>x.Dispatch.GroupId=="g2");
        await engine.DispatchAsync(group1.Dispatch,"signal-structured:{\"Message\":\"test\"}",Ct);
        await store.ConfirmLiveBatchStepAsync(group1.Dispatch,now,Ct);
        Assert.Equal(1,(await store.ListLiveBatchAsync(Ct))
            .Single(x=>x.GroupId=="g1").Cursor);
        Assert.Equal(0,(await store.ListLiveBatchAsync(Ct))
            .Single(x=>x.GroupId=="g2").Cursor);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            engine.DispatchAsync(group1.Dispatch,"signal-structured:{\"Message\":\"test\"}",Ct));
        await engine.DispatchAsync(group2.Dispatch,"signal-structured:{\"Message\":\"test\"}",Ct);
        await store.ConfirmLiveBatchStepAsync(group2.Dispatch,now,Ct);
        Assert.Equal(2,sends);

        await store.ControlLiveBatchAsync(new(started.JobIds[0],"pause"),Ct);
        Assert.Equal(1,(await store.ListLiveBatchAsync(Ct))
            .Count(x=>x.State=="Paused"));
        Assert.Equal("2",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_dispatch_journal WHERE state='Confirmed'"));
        Assert.Equal("0",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_dispatch_journal WHERE state='RecoveryRequired'"));
    }

    [Fact]
    public async Task AmbiguousResultStopsOnlyItsGroupAndRestartPreservesRecovery()
    {
        var (store,db)=await NewStoreAsync();
        await SeedAsync(store);
        var script=await MakeScriptAsync(store,new []{Step(0,"消息")});
        await store.StartLiveBatchAsync(
            new(script.ScriptId,new[]{"g1","g2"},true),Ct);
        var due=await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000,20,Ct);
        var group1=due.Single(x=>x.Dispatch.GroupId=="g1");
        var engine=new DurableTaskEngine(store,new StubTransport((d,p,ct)=>
            Task.FromResult(new SignalSendResult(
                SignalDeliveryOutcome.Ambiguous,Detail:"timeout"))));
        await engine.DispatchAsync(group1.Dispatch,"signal-structured:{\"Message\":\"test\"}",Ct);
        var rows=await store.ListLiveBatchAsync(Ct);
        Assert.Equal("RecoveryRequired",rows.Single(x=>x.GroupId=="g1").State);
        Assert.Equal("Running",rows.Single(x=>x.GroupId=="g2").State);
        Assert.Equal(0,rows.Single(x=>x.GroupId=="g1").Cursor);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.ControlLiveBatchAsync(new(group1.Dispatch.JobId,"resume"),Ct));

        var restarted=new StateStore(RuntimePaths.ForTesting(
            Path.GetDirectoryName(db)!,db));
        await restarted.InitializeAsync(Ct);
        rows=await restarted.ListLiveBatchAsync(Ct);
        Assert.Equal("RecoveryRequired",rows.Single(x=>x.GroupId=="g1").State);
        Assert.Equal("Paused",rows.Single(x=>x.GroupId=="g2").State);
        Assert.Empty(await restarted.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+60000,20,Ct));
    }

    [Fact]
    public async Task ManualControlRejectsInvalidTransitionsAndPreservesStoppedJobs()
    {
        var (store,db)=await NewStoreAsync();
        await SeedAsync(store);
        var script=await MakeScriptAsync(store,new[]{
            Step(0,"消息一"),Step(1,"消息二")});
        var started=await store.StartLiveBatchAsync(
            new(script.ScriptId,new[]{"g1"},true),Ct);
        var id=Assert.Single(started.JobIds);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.ControlLiveBatchAsync(new(id,"resume"),Ct));
        await store.ControlLiveBatchAsync(new(id,"pause"),Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.ControlLiveBatchAsync(new(id,"pause"),Ct));
        await store.ControlLiveBatchAsync(new(id,"stop"),Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.ControlLiveBatchAsync(new(id,"resume"),Ct));
        Assert.Equal("Stopped",Assert.Single(await store.ListLiveBatchAsync(Ct)).State);
        Assert.Equal("Stopped",await ScalarAsync(db,
            "SELECT state FROM v8_jobs WHERE job_id='"+id+"'"));
    }

    [Fact]
    public async Task HistoryPagingReturnsOnlyFinishedTasksAndFiltersByGroup()
    {
        var (store,_)=await NewStoreAsync();
        await SeedAsync(store);
        var script=await MakeScriptAsync(store,new[]{Step(0,"消息")});
        var first=await store.StartLiveBatchAsync(
            new(script.ScriptId,new[]{"g1","g2"},true),Ct);
        foreach(var id in first.JobIds)
            await store.ControlLiveBatchAsync(new(id,"stop"),Ct);
        var result=await store.ListLiveBatchHistoryPageAsync(
            new LiveBatchHistoryPageRequest(0,1),Ct);
        Assert.Equal(2,result.Total);
        Assert.Single(result.Jobs);
        var next=await store.ListLiveBatchHistoryPageAsync(
            new LiveBatchHistoryPageRequest(1,1),Ct);
        Assert.Single(next.Jobs);
        Assert.NotEqual(result.Jobs[0].JobId,next.Jobs[0].JobId);
        var filtered=await store.ListLiveBatchHistoryPageAsync(
            new LiveBatchHistoryPageRequest(0,10,"群 0","Stopped"),Ct);
        Assert.Single(filtered.Jobs);
        Assert.Equal("g1",filtered.Jobs[0].GroupId);
    }

    [Fact]
    public async Task PreflightOnlyReadsAndDetectsConflictsBeforeDispatch()
    {
        var (store,db)=await NewStoreAsync();
        await SeedAsync(store);
        var script=await MakeScriptAsync(store,new[]{Step(0,"健康检查")});
        var preflight=await store.PreflightLiveBatchAsync(
            new LiveBatchPreflightRequest(script.ScriptId,new[]{"g1"}),Ct);
        Assert.True(preflight.CanStart);
        Assert.Equal(1,preflight.SendableRows);
        Assert.Equal("0",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_live_batch_jobs"));
        await store.StartLiveBatchAsync(
            new LiveBatchStartRequest(script.ScriptId,new[]{"g1"},true),Ct);
        var afterStart=await store.PreflightLiveBatchAsync(
            new LiveBatchPreflightRequest(script.ScriptId,new[]{"g1"}),Ct);
        Assert.False(afterStart.CanStart);
        Assert.Contains(afterStart.Issues,x=>x.Level=="错误" &&
            x.Message.Contains("已有未结束的任务"));
        Assert.Equal("1",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_live_batch_jobs"));
    }

    [Fact]
    public async Task PreflightWarnsOnBadImageAndBlocksUnavailableAssignedAccount()
    {
        var (store,db)=await NewStoreAsync();
        await SeedAsync(store);
        var script=await MakeScriptAsync(store,new[]{
            Step(0,"带坏图片的文字") with {Attachment="img:not-valid"},
            Step(1,"指定离线账号") with {Account="+490000"}
        });
        var preflight=await store.PreflightLiveBatchAsync(
            new LiveBatchPreflightRequest(script.ScriptId,new[]{"g1"}),Ct);
        Assert.False(preflight.CanStart);
        Assert.Contains(preflight.Issues,x=>x.Level=="提醒" &&
            x.Message.Contains("图片"));
        Assert.Contains(preflight.Issues,x=>x.Level=="错误" &&
            x.Message.Contains("指定的账号"));
        Assert.Equal("0",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_live_batch_jobs"));
    }

    [Fact]
    public async Task InvalidGroupOrOfflineRoleCausesAtomicRollback()
    {
        var (store,db)=await NewStoreAsync();
        await SeedAsync(store);
        var script=await MakeScriptAsync(store,new[]{Step(0,"消息")});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.StartLiveBatchAsync(new(script.ScriptId,new[]{"g1","not-saved"},true),Ct));
        Assert.Equal("0",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_live_batch_jobs"));
        await Assert.ThrowsAsync<ArgumentException>(()=>
            store.StartLiveBatchAsync(new(script.ScriptId,new[]{"g1"},false),Ct));

        await store.UpdateManagedAccountAsync(
            new UpdateManagedAccount("+49123","disabled",false,0),Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.StartLiveBatchAsync(new(script.ScriptId,new[]{"g1"},true),Ct));
    }

    [Fact]
    public async Task TwentyGroupsAreDistinctAndProtectedByOneActiveJobPerGroup()
    {
        var (store,db)=await NewStoreAsync();
        var account="+49123";
        var groups=Enumerable.Range(1,20)
            .Select(x=>new SignalGroupCatalogItem(account,"group-"+x,"Group "+x,
                true,Array.Empty<string>())).ToArray();
        await store.SyncSignalCatalogAsync(new[]{account},groups,new[]{account},Ct);
        await store.SetSelectedGroupsAsync(
            new UpdateGroupSelection(groups.Select(x=>x.GroupId).ToArray()),Ct);
        var script=await MakeScriptAsync(store,new[]{Step(0,"通知")});
        var first=await store.StartLiveBatchAsync(
            new(script.ScriptId,groups.Select(g=>g.GroupId).ToArray(),true),Ct);
        Assert.Equal(20,first.GroupCount);
        Assert.Equal(20,(await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000,20,Ct)).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.StartLiveBatchAsync(new(script.ScriptId,new[]{"group-1"},true),Ct));
        Assert.Equal("20",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_live_batch_jobs"));
    }

    [Fact]
    public async Task HomeCanStartFromLiveGroupCatalogWithoutPreviouslySavedGroupSelection()
    {
        var (store,db)=await NewStoreAsync();
        const string account="+49123";
        await store.SyncSignalCatalogAsync(new[]{account},
            new[]{new SignalGroupCatalogItem(
                account,"direct-group","首页直接选取",true,Array.Empty<string>())},
            new[]{account},Ct);
        var script=await MakeScriptAsync(store,new[]{Step(0,"授权通知")});
        var result=await store.StartLiveBatchAsync(
            new(script.ScriptId,new[]{"direct-group"},true),Ct);
        Assert.Equal(1,result.GroupCount);
        Assert.Equal(1,result.MessageCount);
        Assert.Equal("0",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_selected_groups"));
    }

    [Fact]
    public async Task BlankEditorBubblesAreOmittedOnlyFromFrozenRun_NotOriginalDraft()
    {
        var (store,db)=await NewStoreAsync();
        await SeedAsync(store);
        var script=await MakeScriptAsync(store,new[]
        {
            Step(0,""),
            Step(1,"真的需要发送的通知"),
            Step(2," "),
            Step(3,"第二条通知")
        });
        var result=await store.StartLiveBatchAsync(
            new(script.ScriptId,new[]{"g1"},true),Ct);
        Assert.Equal(2,result.MessageCount);
        var due=await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000,10,Ct);
        Assert.Single(due);
        Assert.Equal("真的需要发送的通知",due[0].Step.Message);
        var original=await store.ReadEditorScriptAsync(script.ScriptId,Ct);
        Assert.Equal(4,original.Steps.Count);
        Assert.Equal("2",await ScalarAsync(db,
            "SELECT total_steps FROM v8_live_batch_jobs LIMIT 1"));
    }

    [Fact]
    public async Task EntirelyBlankScriptStillFailsWithSpecificScriptName()
    {
        var (store,db)=await NewStoreAsync();
        await SeedAsync(store);
        var script=await MakeScriptAsync(store,new[]{Step(0,""),Step(1," ")});
        var ex=await Assert.ThrowsAsync<ArgumentException>(()=>
            store.StartLiveBatchAsync(new(script.ScriptId,new[]{"g1"},true),Ct));
        Assert.Contains(script.Name,ex.Message);
        Assert.Equal("0",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_live_batch_jobs"));
    }

    static ScriptEditorStep Step(int i,string msg,bool pause=false)=>
        new(i,"",msg,"",pause,"确认继续",0,0);

    static Task<ScriptEditorDocument> MakeScriptAsync(StateStore store,
        ScriptEditorStep[] steps)=>
        store.SaveEditorScriptAsync(new ScriptSaveRequest(
            null,"合法群通知","",0,steps),Ct);

    static async Task<(StateStore,string)> NewStoreAsync()
    {
        var folder=Path.Combine(Path.GetTempPath(),"SignalV8Batch",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var db=Path.Combine(folder,"data.db");
        var store=new StateStore(RuntimePaths.ForTesting(folder,db));
        await store.InitializeAsync(Ct);
        await store.InitializeLiveBatchAsync(Ct);
        return(store,db);
    }

    static async Task SeedAsync(StateStore store)
    {
        const string account="+49123";
        var groups=new[]{"g1","g2"}
            .Select((g,i)=>new SignalGroupCatalogItem(account,g,"群 "+i,
                true,Array.Empty<string>())).ToArray();
        await store.SyncSignalCatalogAsync(new[]{account},groups,new[]{account},Ct);
        await store.SetSelectedGroupsAsync(new UpdateGroupSelection(
            groups.Select(g=>g.GroupId).ToArray()),Ct);
    }

    static async Task<string?> ScalarAsync(string db,string query)
    {
        await using var c=new SqliteConnection($"Data Source={db}");
        await c.OpenAsync();
        await using var q=c.CreateCommand();q.CommandText=query;
        return Convert.ToString(await q.ExecuteScalarAsync());
    }

    sealed class StubTransport : ISignalTransport
    {
        readonly Func<DispatchIdentity,string,CancellationToken,Task<SignalSendResult>> _handle;
        public bool IsReady=>true;
        public StubTransport(Func<DispatchIdentity,string,CancellationToken,Task<SignalSendResult>> handle)=>
            _handle=handle;
        public Task<SignalSendResult> SendAsync(DispatchIdentity d,string payload,CancellationToken ct)=>
            _handle(d,payload,ct);
    }
}
