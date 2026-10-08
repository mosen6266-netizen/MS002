using Microsoft.Data.Sqlite;
using SignalScheduler.Engine;
using SignalScheduler.Engine.Engine;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Engine.Signal;
using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class LivePilotTests
{
    static readonly CancellationToken Ct=CancellationToken.None;

    [Fact]
    public async Task ThreeRealScriptStepsAreJournaledOnce_WithSpacingAndManualPause()
    {
        var (store,db)=await StoreAsync();
        var script=await CreateScript(store,new[]{
            Step(0,"第一条",false,0),Step(1,"第二条",true,0),
            Step(2,"第三条",false,0)});
        var pilot=await store.PlanLivePilotAsync(
            new LivePilotPlanRequest(script.ScriptId,"+49123","group-1",true),Ct);
        Assert.Equal("Running",pilot.State);
        Assert.Equal(3,pilot.TotalSteps);
        var sendCount=0;
        var engine=new DurableTaskEngine(store,new MockTransport((d,p,ct)=>
        {
            sendCount++;
            Assert.Contains("[SignalScheduler 实发测试]",p);
            return Task.FromResult(new SignalSendResult(
                SignalDeliveryOutcome.Confirmed,sendCount.ToString(),"RPC ACK"));
        }));
        var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000;
        var first=await store.FindDueLivePilotAsync(now,Ct);
        Assert.NotNull(first);
        await engine.DispatchAsync(first!.Dispatch,first.Text,Ct);
        await store.ConfirmLivePilotStepAsync(first.Dispatch,now,Ct);
        Assert.Equal(1,(await store.ListLivePilotsAsync(Ct)).Single().Cursor);
        Assert.Null(await store.FindDueLivePilotAsync(now+1000,Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            engine.DispatchAsync(first.Dispatch,first.Text,Ct));

        var second=await store.FindDueLivePilotAsync(now+16000,Ct);
        Assert.NotNull(second);
        await engine.DispatchAsync(second!.Dispatch,second.Text,Ct);
        await store.ConfirmLivePilotStepAsync(second.Dispatch,now+16000,Ct);
        var paused=(await store.ListLivePilotsAsync(Ct)).Single();
        Assert.Equal("Paused",paused.State);
        Assert.Equal(2,paused.Cursor);
        Assert.Contains("按剧本设置暂停",paused.Detail);
        Assert.Null(await store.FindDueLivePilotAsync(now+999999,Ct));

        await store.ControlLivePilotAsync(new(pilot.JobId,"resume"),Ct);
        var third=await store.FindDueLivePilotAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000,Ct);
        Assert.NotNull(third);
        await engine.DispatchAsync(third!.Dispatch,third.Text,Ct);
        await store.ConfirmLivePilotStepAsync(third.Dispatch,now+32000,Ct);
        var completed=(await store.ListLivePilotsAsync(Ct)).Single();
        Assert.Equal("Completed",completed.State);
        Assert.Equal(3,completed.Cursor);
        Assert.Equal(3,sendCount);
        Assert.Equal("3",await ValueAsync(db,
            "SELECT COUNT(*) FROM v8_dispatch_journal WHERE state='Confirmed'"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.ControlLivePilotAsync(new(pilot.JobId,"resume"),Ct));
    }

    [Fact]
    public async Task AmbiguousSendStopsAtCursorAndCannotBeResumedOrReplanned()
    {
        var (store,db)=await StoreAsync();
        var script=await CreateScript(store,new[]{
            Step(0,"第一条",false,0),Step(1,"第二条",false,0)});
        var pilot=await store.PlanLivePilotAsync(
            new(script.ScriptId,"+49123","group-1",true),Ct);
        var due=(await store.FindDueLivePilotAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000,Ct))!;
        var engine=new DurableTaskEngine(store,new MockTransport((_,_,_)=>
            Task.FromResult(new SignalSendResult(
                SignalDeliveryOutcome.Ambiguous,Detail:"network failure"))));
        await engine.DispatchAsync(due.Dispatch,due.Text,Ct);
        Assert.Equal("RecoveryRequired",
            (await store.ListLivePilotsAsync(Ct)).Single().State);
        Assert.Equal("0",await ValueAsync(db,
            "SELECT cursor FROM v8_jobs"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.ControlLivePilotAsync(new(pilot.JobId,"resume"),Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.PlanLivePilotAsync(new(script.ScriptId,"+49123","group-1",true),Ct));
        var restarted=new StateStore(RuntimePaths.ForTesting(
            Path.GetDirectoryName(db)!,db));
        await restarted.InitializeAsync(Ct);
        Assert.Equal("RecoveryRequired",
            (await restarted.ListLivePilotsAsync(Ct)).Single().State);
        Assert.Null(await restarted.FindDueLivePilotAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+999999,Ct));
    }

    [Fact]
    public async Task PilotRejectsMultipleAccountsImagesMoreThanThreeMessagesAndBadConsent()
    {
        var (store,db)=await StoreAsync();
        var script=await CreateScript(store,new[]{Step(0,"one",false,0)});
        await Assert.ThrowsAsync<ArgumentException>(()=>store.PlanLivePilotAsync(
            new(script.ScriptId,"+49123","group-1",false),Ct));
        await Assert.ThrowsAsync<ArgumentException>(()=>store.PlanLivePilotAsync(
            new(script.ScriptId,"+49123","unknown",true),Ct));

        var many=await CreateScript(store,Enumerable.Range(0,4)
            .Select(i=>Step(i,"msg",false,0)).ToArray());
        await Assert.ThrowsAsync<ArgumentException>(()=>store.PlanLivePilotAsync(
            new(many.ScriptId,"+49123","group-1",true),Ct));
        var multi=await CreateScript(store,new[]{Step(0,"msg",false,0) with {
            Account="+49333"
        }});
        await Assert.ThrowsAsync<ArgumentException>(()=>store.PlanLivePilotAsync(
            new(multi.ScriptId,"+49123","group-1",true),Ct));
        Assert.Equal("0",await ValueAsync(db,
            "SELECT COUNT(*) FROM v8_live_pilot_plans"));
    }

    [Fact]
    public async Task RestartPausesRunningWithoutResendingAndStopIsTerminal()
    {
        var (store,db)=await StoreAsync();
        var script=await CreateScript(store,new[]{
            Step(0,"one",false,0),Step(1,"two",false,0)});
        var item=await store.PlanLivePilotAsync(
            new(script.ScriptId,"+49123","group-1",true),Ct);
        await store.InitializeAsync(Ct);
        Assert.Equal("Paused",(await store.ListLivePilotsAsync(Ct)).Single().State);
        Assert.Null(await store.FindDueLivePilotAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+2000,Ct));
        await store.ControlLivePilotAsync(new(item.JobId,"stop"),Ct);
        Assert.Equal("Stopped",(await store.ListLivePilotsAsync(Ct)).Single().State);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.ControlLivePilotAsync(new(item.JobId,"resume"),Ct));
    }

    static ScriptEditorStep Step(int position,string message,bool pause,int delay)=>
        new(position,"",message,"",pause,"人工确认",delay,0);

    static Task<ScriptEditorDocument> CreateScript(StateStore store,
        ScriptEditorStep[] steps)=>
        store.SaveEditorScriptAsync(new ScriptSaveRequest(
            null,"自动实发测试","",0,steps),Ct);

    static async Task<(StateStore,string)> StoreAsync()
    {
        var dir=Path.Combine(Path.GetTempPath(),"SignalLivePilot",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db=Path.Combine(dir,"data.db");
        var store=new StateStore(RuntimePaths.ForTesting(dir,db));
        await store.InitializeAsync(Ct);
        await store.InitializeLiveProbeAsync(Ct);
        await store.InitializeLivePilotAsync(Ct);
        await store.SyncSignalCatalogAsync(new[]{"+49123"},
            new[]{new SignalGroupCatalogItem("+49123","group-1","专用测试群",
                true,Array.Empty<string>())},new[]{"+49123"},Ct);
        await store.SetSelectedGroupsAsync(
            new UpdateGroupSelection(new[]{"group-1"}),Ct);
        return (store,db);
    }

    static async Task<string?> ValueAsync(string db,string sql)
    {
        await using var c=new SqliteConnection($"Data Source={db}");
        await c.OpenAsync(Ct);
        await using var q=c.CreateCommand();
        q.CommandText=sql;
        return Convert.ToString(await q.ExecuteScalarAsync(Ct));
    }

    sealed class MockTransport : ISignalTransport
    {
        readonly Func<DispatchIdentity,string,CancellationToken,Task<SignalSendResult>> _fn;
        public bool IsReady=>true;
        public MockTransport(Func<DispatchIdentity,string,CancellationToken,Task<SignalSendResult>> fn)
            =>_fn=fn;
        public Task<SignalSendResult> SendAsync(DispatchIdentity d,string p,CancellationToken ct)
            =>_fn(d,p,ct);
    }
}
