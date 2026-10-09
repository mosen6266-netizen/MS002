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
    public async Task LiveJobSnapshotReportsPreparedAndSendingPhasesWithoutMovingCursor()
    {
        var (store,db)=await NewStoreAsync();
        await SeedAsync(store);
        var script=await MakeScriptAsync(store,new[]{Step(0,"第一条"),Step(1,"第二条")});
        await store.StartLiveBatchAsync(
            new(script.ScriptId,new[]{"g1"},true),Ct);
        var initial=Assert.Single(await store.ListLiveBatchAsync(Ct));
        Assert.Equal("Running",initial.State);
        Assert.Equal("",initial.DispatchState);
        var due=Assert.Single(await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000,10,Ct));
        await store.ReserveAsync(due.Dispatch,Ct);
        var prepared=Assert.Single(await store.ListLiveBatchAsync(Ct));
        Assert.Equal("Prepared",prepared.DispatchState);
        Assert.Equal(0,prepared.Cursor);
        await store.MarkSendingAsync(due.Dispatch,Ct);
        var sending=Assert.Single(await store.ListLiveBatchAsync(Ct));
        Assert.Equal("Sending",sending.DispatchState);
        Assert.Equal(0,sending.Cursor);
        Assert.Equal("请求发送中",LiveBatchTiming.Format(
            sending,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Phase);
    }

    [Fact]
    public async Task ReviewedSentStepCanBeManuallyResumedWithoutReplayingOriginal()
    {
        var (store,db)=await NewStoreAsync();
        await SeedAsync(store);
        var script=await MakeScriptAsync(store,new[]{
            Step(0,"第一条"),Step(1,"第二条")});
        var start=await store.StartLiveBatchAsync(
            new(script.ScriptId,new[]{"g1"},true),Ct);
        var first=Assert.Single(await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000,20,Ct));
        var engine=new DurableTaskEngine(store,new StubTransport((d,p,ct)=>
            Task.FromResult(new SignalSendResult(
                SignalDeliveryOutcome.Ambiguous,Detail:"timeout"))));
        await engine.DispatchAsync(first.Dispatch,"test",Ct);
        Assert.Equal("RecoveryRequired",
            Assert.Single(await store.ListLiveBatchAsync(Ct)).State);

        var reviewed=await store.ReviewAmbiguousDispatchAsync(
            new(start.JobIds[0],first.Dispatch.DispatchKey,"seen",
                "已在 Signal 群中核实这条消息存在"),Ct);
        Assert.Equal("ManuallyConfirmed",reviewed.JournalState);
        Assert.Equal("Paused",reviewed.JobState);
        Assert.Equal(1,reviewed.Cursor);
        Assert.Empty(await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+60000,20,Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.ReviewAmbiguousDispatchAsync(
                new(start.JobIds[0],first.Dispatch.DispatchKey,"seen",
                    "再次尝试核实同一条消息"),Ct));

        await store.ControlLiveBatchAsync(new(start.JobIds[0],"resume"),Ct);
        var next=Assert.Single(await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+60000,20,Ct));
        Assert.Equal(1,next.Dispatch.Cursor);
        Assert.NotEqual(first.Dispatch.DispatchKey,next.Dispatch.DispatchKey);
        Assert.Equal("1",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_event_log WHERE event_type='manual_confirmed_sent'"));
        Assert.Equal("ManuallyConfirmed",await ScalarAsync(db,
            "SELECT state FROM v8_dispatch_journal LIMIT 1"));
    }

    [Fact]
    public async Task ReviewedUnsentStepRequiresManualResumeAtSameCursor()
    {
        var (store,db)=await NewStoreAsync();
        await SeedAsync(store);
        var script=await MakeScriptAsync(store,new[]{Step(0,"第一条")});
        await store.StartLiveBatchAsync(new(script.ScriptId,new[]{"g1"},true),Ct);
        var first=Assert.Single(await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000,20,Ct));
        var engine=new DurableTaskEngine(store,new StubTransport((d,p,ct)=>
            Task.FromResult(new SignalSendResult(
                SignalDeliveryOutcome.Ambiguous,Detail:"timeout"))));
        await engine.DispatchAsync(first.Dispatch,"test",Ct);
        var reviewed=await store.ReviewAmbiguousDispatchAsync(
            new(first.Dispatch.JobId,first.Dispatch.DispatchKey,"not_seen",
                "已在 Signal 群记录中确认该消息不存在"),Ct);
        Assert.Equal("Paused",reviewed.JobState);
        Assert.Equal(0,reviewed.Cursor);
        Assert.Empty(await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+60000,20,Ct));
        await store.ControlLiveBatchAsync(new(first.Dispatch.JobId,"resume"),Ct);
        var next=Assert.Single(await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+60000,20,Ct));
        Assert.Equal(0,next.Dispatch.Cursor);
        Assert.Equal("DefinitelyNotSent",await ScalarAsync(db,
            "SELECT state FROM v8_dispatch_journal LIMIT 1"));
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
    public async Task EngineRestartQuarantinesOnlyInFlightGroupAndPreservesReasons()
    {
        var (store,db)=await NewStoreAsync();
        await SeedAsync(store);
        var script=await MakeScriptAsync(store,new[]{
            Step(0,"第一条"),Step(1,"第二条")});
        await store.StartLiveBatchAsync(new(script.ScriptId,new[]{"g1","g2"},true),Ct);
        var due=await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000,20,Ct);
        var first=Assert.Single(due.Where(x=>x.Dispatch.GroupId=="g1"));
        await store.ReserveAsync(first.Dispatch,Ct);
        await store.MarkSendingAsync(first.Dispatch,Ct);

        var restarted=new StateStore(RuntimePaths.ForTesting(
            Path.GetDirectoryName(db)!,db));
        await restarted.InitializeAsync(Ct);
        var jobs=await restarted.ListLiveBatchAsync(Ct);
        var uncertain=Assert.Single(jobs.Where(x=>x.GroupId=="g1"));
        var idle=Assert.Single(jobs.Where(x=>x.GroupId=="g2"));
        Assert.Equal("RecoveryRequired",uncertain.State);
        Assert.Contains("发送结果不明确",uncertain.Detail);
        Assert.Equal(0,uncertain.Cursor);
        Assert.Equal("Paused",idle.State);
        Assert.Contains("重启",idle.Detail);
        Assert.Equal(0,idle.Cursor);
        Assert.Equal("1",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_event_log WHERE event_type='startup_recovery_required'"));
        Assert.Equal("1",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_event_log WHERE event_type='startup_paused'"));
        Assert.Equal("RecoveryRequired",await ScalarAsync(db,
            "SELECT state FROM v8_dispatch_journal LIMIT 1"));
        Assert.Empty(await restarted.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+60000,20,Ct));

        // Re-running startup recovery is idempotent: original reasons remain
        // and event counts don't increase.
        await restarted.InitializeAsync(Ct);
        var after=await restarted.ListLiveBatchAsync(Ct);
        Assert.Equal(uncertain.Detail,
            Assert.Single(after.Where(x=>x.GroupId=="g1")).Detail);
        Assert.Equal(idle.Detail,
            Assert.Single(after.Where(x=>x.GroupId=="g2")).Detail);
        Assert.Equal("1",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_event_log WHERE event_type='startup_recovery_required'"));
        Assert.Equal("1",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_event_log WHERE event_type='startup_paused'"));
    }

    [Fact]
    public async Task SignalDaemonRestartFreezesGroupsAndQuarantinesOnlyUnknownSends()
    {
        var (store,db)=await NewStoreAsync();
        await SeedAsync(store);
        var script=await MakeScriptAsync(store,new[]{
            Step(0,"群组第一条"),Step(1,"群组第二条")});
        await store.StartLiveBatchAsync(
            new(script.ScriptId,new[]{"g1","g2"},true),Ct);
        var due=await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000,20,Ct);
        var sending=Assert.Single(due.Where(x=>x.Dispatch.GroupId=="g1"));
        await store.ReserveAsync(sending.Dispatch,Ct);
        await store.MarkSendingAsync(sending.Dispatch,Ct);

        // Guardian first wants to restart a live daemon, but a Signal call
        // might still be executing. All running groups freeze and no kill is
        // permitted. The live journal must stay Sending until actual exit.
        Assert.False(await store.TryPrepareGuardianRestartAsync(false,Ct));
        var frozen=await store.ListLiveBatchAsync(Ct);
        Assert.All(frozen,x=>Assert.Equal("Paused",x.State));
        Assert.All(frozen,x=>Assert.Contains("Signal 后台",x.Detail));
        Assert.Equal("Sending",await ScalarAsync(db,
            "SELECT state FROM v8_dispatch_journal LIMIT 1"));
        Assert.Empty(await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+60000,20,Ct));

        // Once daemon exit has been verified, quarantine only the group
        // with unknown external delivery. The other group stays paused.
        Assert.True(await store.TryPrepareGuardianRestartAsync(true,Ct));
        var after=await store.ListLiveBatchAsync(Ct);
        var unknown=Assert.Single(after.Where(x=>x.GroupId=="g1"));
        var idle=Assert.Single(after.Where(x=>x.GroupId=="g2"));
        Assert.Equal("RecoveryRequired",unknown.State);
        Assert.Contains("发送结果尚未确认",unknown.Detail);
        Assert.Equal("Paused",idle.State);
        Assert.Contains("Signal 后台",idle.Detail);
        Assert.Equal(0,unknown.Cursor);
        Assert.Equal(0,idle.Cursor);
        Assert.Equal("RecoveryRequired",await ScalarAsync(db,
            "SELECT state FROM v8_dispatch_journal LIMIT 1"));
        Assert.Equal("2",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_event_log WHERE event_type='daemon_restart_paused'"));
        Assert.Equal("1",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_event_log WHERE event_type='daemon_recovery_required'"));

        // Repeated health checks must not erase an operator-facing reason.
        Assert.True(await store.TryPrepareGuardianRestartAsync(true,Ct));
        Assert.Equal("1",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_event_log WHERE event_type='daemon_recovery_required'"));
        Assert.Equal(unknown.Detail,Assert.Single(
            (await store.ListLiveBatchAsync(Ct)).Where(x=>x.GroupId=="g1")).Detail);
    }

    [Fact]
    public async Task RestartKeepsExistingUserPausedReasonUnchanged()
    {
        var (store,db)=await NewStoreAsync();
        await SeedAsync(store);
        var script=await MakeScriptAsync(store,new[]{Step(0,"内容")});
        var started=await store.StartLiveBatchAsync(
            new(script.ScriptId,new[]{"g1"},true),Ct);
        await store.ControlLiveBatchAsync(new(started.JobIds[0],"pause"),Ct);
        var before=Assert.Single(await store.ListLiveBatchAsync(Ct));
        Assert.Equal("Paused",before.State);

        var restarted=new StateStore(RuntimePaths.ForTesting(
            Path.GetDirectoryName(db)!,db));
        await restarted.InitializeAsync(Ct);
        var after=Assert.Single(await restarted.ListLiveBatchAsync(Ct));
        Assert.Equal(before.Detail,after.Detail);
        Assert.Equal("Paused",after.State);
        Assert.Equal("0",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_event_log WHERE event_type='startup_paused'"));
    }

    [Fact]
    public async Task ConfirmedSendWithoutBatchCheckpointIsPausedBeforeNextMessage()
    {
        var (store,db)=await NewStoreAsync();
        await SeedAsync(store);
        var script=await MakeScriptAsync(store,new[]{
            Step(0,"第一条",true),Step(1,"第二条")});
        var started=await store.StartLiveBatchAsync(
            new(script.ScriptId,new[]{"g1"},true),Ct);
        var id=Assert.Single(started.JobIds);
        var first=Assert.Single(await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000,10,Ct));
        var calls=0;
        var engine=new DurableTaskEngine(store,new StubTransport((d,p,ct)=>
        {
            calls++;
            return Task.FromResult(new SignalSendResult(
                SignalDeliveryOutcome.Confirmed,"accepted-1","accepted"));
        }));
        await engine.DispatchAsync(first.Dispatch,"sent-by-signal",Ct);
        Assert.Equal("Confirmed",await ScalarAsync(db,
            "SELECT state FROM v8_dispatch_journal LIMIT 1"));
        Assert.Equal("1",await ScalarAsync(db,
            "SELECT cursor FROM v8_jobs WHERE job_id='"+id+"'"));
        // Simulate process death before ConfirmLiveBatchStepAsync wrote
        // the reminder and next_due_ms. A new dispatch must be blocked.
        var eligible=await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+100000,10,Ct);
        Assert.Empty(eligible);
        var frozen=Assert.Single(await store.ListLiveBatchAsync(Ct));
        Assert.Equal("Paused",frozen.State);
        Assert.Contains("进度或提醒尚未写入",frozen.Detail);
        Assert.Equal("1",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_event_log WHERE event_type='batch_checkpoint_incomplete'"));
        Assert.Equal(1,calls);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.ControlLiveBatchAsync(new(id,"resume"),Ct));

        var restarted=new StateStore(RuntimePaths.ForTesting(
            Path.GetDirectoryName(db)!,db));
        await restarted.InitializeAsync(Ct);
        Assert.Equal("Paused",
            Assert.Single(await restarted.ListLiveBatchAsync(Ct)).State);
        Assert.Empty(await restarted.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+200000,10,Ct));
    }

    [Fact]
    public async Task CompletedBatchCheckpointAllowsTheNextScheduledStep()
    {
        var (store,db)=await NewStoreAsync();
        await SeedAsync(store);
        var script=await MakeScriptAsync(store,new[]{
            Step(0,"第一条"),Step(1,"第二条")});
        await store.StartLiveBatchAsync(new(script.ScriptId,new[]{"g1"},true),Ct);
        var before=Assert.Single(await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000,10,Ct));
        var engine=new DurableTaskEngine(store,new StubTransport((d,p,ct)=>
            Task.FromResult(new SignalSendResult(
                SignalDeliveryOutcome.Confirmed,"accepted-1","accepted"))));
        await engine.DispatchAsync(before.Dispatch,"sent-by-signal",Ct);
        await store.ConfirmLiveBatchStepAsync(before.Dispatch,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),Ct);
        var after=Assert.Single(await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+60000,10,Ct));
        Assert.Equal(1,after.Dispatch.Cursor);
        Assert.Equal("Running",Assert.Single(await store.ListLiveBatchAsync(Ct)).State);
        Assert.Equal("0",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_event_log WHERE event_type='batch_checkpoint_incomplete'"));
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
    public async Task SafetyPauseIsIdempotentAndDoesNotOverwriteStoppedOrPausedEvidence()
    {
        var (store,db)=await NewStoreAsync();
        await SeedAsync(store);
        var script=await MakeScriptAsync(store,new[]{
            Step(0,"第一条"),Step(1,"第二条")});
        var start=await store.StartLiveBatchAsync(
            new(script.ScriptId,new[]{"g1"},true),Ct);
        var jobId=Assert.Single(start.JobIds);

        await store.PauseLiveBatchForSafetyAsync(
            jobId,"异常停止：首次检测到网络断开",Ct);
        var snapshot=Assert.Single(await store.ListLiveBatchAsync(Ct));
        Assert.Equal("Paused",snapshot.State);
        Assert.Contains("首次检测到网络断开",snapshot.Detail);
        Assert.Equal("1",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_event_log WHERE event_type='batch_safety_pause'"));

        await store.PauseLiveBatchForSafetyAsync(
            jobId,"异常停止：迟到的第二次故障",Ct);
        snapshot=Assert.Single(await store.ListLiveBatchAsync(Ct));
        Assert.Contains("首次检测到网络断开",snapshot.Detail);
        Assert.DoesNotContain("第二次故障",snapshot.Detail);
        Assert.Equal("1",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_event_log WHERE event_type='batch_safety_pause'"));

        await store.ControlLiveBatchAsync(new(jobId,"stop"),Ct);
        var stopped=Assert.Single(await store.ListLiveBatchAsync(Ct));
        Assert.Equal("Stopped",stopped.State);
        await store.PauseLiveBatchForSafetyAsync(
            jobId,"异常停止：应当忽略的延迟回调",Ct);
        snapshot=Assert.Single(await store.ListLiveBatchAsync(Ct));
        Assert.Equal(stopped.State,snapshot.State);
        Assert.Equal(stopped.Detail,snapshot.Detail);
    }

    [Fact]
    public async Task LateSafetyPauseMustNotDowngradeAmbiguousRecoveryOrOverwriteDetails()
    {
        var (store,db)=await NewStoreAsync();
        await SeedAsync(store);
        var script=await MakeScriptAsync(store,new[]{Step(0,"通知")});
        var started=await store.StartLiveBatchAsync(
            new(script.ScriptId,new[]{"g1"},true),Ct);
        var due=Assert.Single(await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000,20,Ct));
        var engine=new DurableTaskEngine(store,new StubTransport((d,p,ct)=>
            Task.FromResult(new SignalSendResult(
                SignalDeliveryOutcome.Ambiguous,Detail:"provider timeout"))));
        await engine.DispatchAsync(due.Dispatch,"test",Ct);
        var before=Assert.Single(await store.ListLiveBatchAsync(Ct));
        Assert.Equal("RecoveryRequired",before.State);

        await store.PauseLiveBatchForSafetyAsync(
            started.JobIds[0],"异常停止：旧任务的延迟回调",Ct);
        var after=Assert.Single(await store.ListLiveBatchAsync(Ct));
        Assert.Equal("RecoveryRequired",after.State);
        Assert.Equal(before.Detail,after.Detail);
        Assert.Equal(before.Cursor,after.Cursor);
        Assert.Equal("RecoveryRequired",await ScalarAsync(db,
            "SELECT state FROM v8_dispatch_journal LIMIT 1"));
        Assert.Empty(await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+60000,20,Ct));
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
