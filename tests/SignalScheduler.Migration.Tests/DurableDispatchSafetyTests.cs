using Microsoft.Data.Sqlite;
using SignalScheduler.Engine;
using SignalScheduler.Engine.Engine;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Engine.Signal;
using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class DurableDispatchSafetyTests
{
    static readonly CancellationToken NoCancel=CancellationToken.None;

    [Fact]
    public async Task ConfirmedSend_AdvancesCursorOnce_AndCannotReplay()
    {
        var (store,db)=await NewStoreAsync();
        var d=Identity();
        await SeedRunningJobAsync(db);

        var transport=new FakeTransport((_,_,_)=>Task.FromResult(
            new SignalSendResult(SignalDeliveryOutcome.Confirmed,"message-1","accepted")));
        var engine=new DurableTaskEngine(store,transport);
        var result=await engine.DispatchAsync(d,"hello",NoCancel);
        Assert.Equal(SignalDeliveryOutcome.Confirmed,result.Outcome);
        Assert.Equal("Confirmed",await ValueAsync(db,"SELECT state FROM v8_dispatch_journal"));
        Assert.Equal("1",await ValueAsync(db,"SELECT cursor FROM v8_jobs"));
        Assert.Equal("1",await ValueAsync(db,"SELECT COUNT(*) FROM v8_event_log WHERE event_type='dispatch_confirmed'"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>engine.DispatchAsync(d,"hello",NoCancel));
        Assert.Equal(1,transport.SendCount);
    }

    [Fact]
    public async Task SameCursor_CannotBeReservedTwice_WithSameOrDifferentKey()
    {
        var (store,db)=await NewStoreAsync();
        await SeedRunningJobAsync(db);
        var d=Identity();
        await store.ReserveAsync(d,NoCancel);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.ReserveAsync(d,NoCancel));
        var other=d with {RunToken="new-run",PayloadHash="different"};
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.ReserveAsync(other,NoCancel));
        Assert.Equal("1",await ValueAsync(db,"SELECT COUNT(*) FROM v8_dispatch_journal"));
    }

    [Fact]
    public async Task CrashDuringSending_RequiresRecovery_AndDoesNotAdvance()
    {
        var (store,db)=await NewStoreAsync();
        await SeedRunningJobAsync(db);
        var d=Identity();
        await store.ReserveAsync(d,NoCancel);
        await store.MarkSendingAsync(d,NoCancel);

        // Simulate the next Engine instance opening the same persistent DB.
        var restarted=new StateStore(RuntimePaths.ForTesting(
            Path.GetDirectoryName(db)!,db));
        await restarted.InitializeAsync(NoCancel);

        Assert.Equal("RecoveryRequired",await ValueAsync(db,"SELECT state FROM v8_dispatch_journal"));
        Assert.Equal("RecoveryRequired",await ValueAsync(db,"SELECT state FROM v8_jobs"));
        Assert.Equal("0",await ValueAsync(db,"SELECT cursor FROM v8_jobs"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>restarted.ReserveAsync(d,NoCancel));
    }

    [Fact]
    public async Task RestartPausesRunningJobWithoutInflightSend()
    {
        var (store,db)=await NewStoreAsync();
        await SeedRunningJobAsync(db);
        await store.InitializeAsync(NoCancel);
        Assert.Equal("Paused",await ValueAsync(db,"SELECT state FROM v8_jobs"));
    }

    [Fact]
    public async Task DisabledOrRemovedGroup_PreventsIrreversibleSend()
    {
        var (store,db)=await NewStoreAsync();
        await SeedRunningJobAsync(db,member:false);
        var d=Identity();
        await store.ReserveAsync(d,NoCancel);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.MarkSendingAsync(d,NoCancel));
        Assert.Equal("DefinitelyNotSent",await ValueAsync(db,"SELECT state FROM v8_dispatch_journal"));
        Assert.Equal("Paused",await ValueAsync(db,"SELECT state FROM v8_jobs"));
        Assert.Equal("0",await ValueAsync(db,"SELECT cursor FROM v8_jobs"));
    }

    [Fact]
    public async Task RemovedGroupAfterReservationCannotSendOrReplayFollowingRestart()
    {
        var (store,db)=await NewStoreAsync();
        await SeedRunningJobAsync(db);
        var identity=Identity();
        await store.ReserveAsync(identity,NoCancel);

        // Membership is revoked after reservation but before the irreversible RPC.
        await using(var c=new SqliteConnection($"Data Source={db}"))
        {
            await c.OpenAsync();
            await using var q=c.CreateCommand();
            q.CommandText="UPDATE v8_signal_groups SET is_member=0 WHERE group_id='group-1'";
            Assert.Equal(1,await q.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.MarkSendingAsync(identity,NoCancel));
        Assert.Equal("DefinitelyNotSent",await ValueAsync(db,
            "SELECT state FROM v8_dispatch_journal"));
        Assert.Equal("Paused",await ValueAsync(db,"SELECT state FROM v8_jobs"));
        Assert.Equal("0",await ValueAsync(db,"SELECT cursor FROM v8_jobs"));

        var restarted=new StateStore(RuntimePaths.ForTesting(
            Path.GetDirectoryName(db)!,db));
        await restarted.InitializeAsync(NoCancel);
        var transport=new FakeTransport((_,_,_)=>
            Task.FromResult(new SignalSendResult(SignalDeliveryOutcome.Confirmed)));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            new DurableTaskEngine(restarted,transport).DispatchAsync(
                identity,"test",NoCancel));
        Assert.Equal(0,transport.SendCount);
        Assert.Equal("Paused",await ValueAsync(db,"SELECT state FROM v8_jobs"));
    }

    [Fact]
    public async Task DisabledAccountAfterReservationCannotReachTransport()
    {
        var (store,db)=await NewStoreAsync();
        await SeedRunningJobAsync(db);
        var dispatch=Identity();
        await store.ReserveAsync(dispatch,NoCancel);

        // Operator disables a sending account during the prepared window.
        await using(var c=new SqliteConnection($"Data Source={db}"))
        {
            await c.OpenAsync();
            await using var q=c.CreateCommand();
            q.CommandText="UPDATE v8_signal_accounts SET enabled=0 WHERE account='+49123456789'";
            Assert.Equal(1,await q.ExecuteNonQueryAsync());
        }

        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.MarkSendingAsync(dispatch,NoCancel));
        Assert.Equal("DefinitelyNotSent",await ValueAsync(db,
            "SELECT state FROM v8_dispatch_journal"));
        Assert.Equal("Paused",await ValueAsync(db,
            "SELECT state FROM v8_jobs"));
        Assert.Equal("0",await ValueAsync(db,"SELECT cursor FROM v8_jobs"));

        var restarted=new StateStore(RuntimePaths.ForTesting(
            Path.GetDirectoryName(db)!,db));
        await restarted.InitializeAsync(NoCancel);
        var transport=new FakeTransport((_,_,_)=>
            Task.FromResult(new SignalSendResult(SignalDeliveryOutcome.Confirmed)));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            new DurableTaskEngine(restarted,transport).DispatchAsync(
                dispatch,"test",NoCancel));
        Assert.Equal(0,transport.SendCount);
    }

    [Fact]
    public async Task OfflineAccountAfterReservationPausesBeforeIrreversibleSend()
    {
        var (store,db)=await NewStoreAsync();
        await SeedRunningJobAsync(db);
        var d=Identity();
        await store.ReserveAsync(d,NoCancel);
        await using(var c=new SqliteConnection($"Data Source={db}"))
        {
            await c.OpenAsync();
            await using var q=c.CreateCommand();
            q.CommandText="UPDATE v8_signal_accounts SET online=0 WHERE account='+49123456789'";
            Assert.Equal(1,await q.ExecuteNonQueryAsync());
        }
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.MarkSendingAsync(d,NoCancel));
        Assert.Equal("DefinitelyNotSent",await ValueAsync(db,
            "SELECT state FROM v8_dispatch_journal"));
        Assert.Equal("Paused",await ValueAsync(db,
            "SELECT state FROM v8_jobs"));
        Assert.Equal("0",await ValueAsync(db,"SELECT cursor FROM v8_jobs"));
    }

    [Fact]
    public async Task UnavailableTransportDoesNotReserveOrAdvanceMessage()
    {
        var (store,db)=await NewStoreAsync();
        await SeedRunningJobAsync(db);
        var transport=new NotReadyTransport();
        var engine=new DurableTaskEngine(store,transport);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            engine.DispatchAsync(Identity(),"never sent",NoCancel));
        Assert.Equal(0,transport.SendCount);
        Assert.Equal("0",await ValueAsync(db,
            "SELECT COUNT(*) FROM v8_dispatch_journal"));
        Assert.Equal("Running",await ValueAsync(db,"SELECT state FROM v8_jobs"));
        Assert.Equal("0",await ValueAsync(db,"SELECT cursor FROM v8_jobs"));
    }

    [Fact]
    public async Task AmbiguousSend_FailsClosed_AndStopsTask()
    {
        var (store,db)=await NewStoreAsync();
        await SeedRunningJobAsync(db);
        var engine=new DurableTaskEngine(store,new FakeTransport((_,_,_)=>
            Task.FromResult(new SignalSendResult(SignalDeliveryOutcome.Ambiguous,Detail:"RPC disconnected"))));
        await engine.DispatchAsync(Identity(),"hello",NoCancel);
        Assert.Equal("RecoveryRequired",await ValueAsync(db,"SELECT state FROM v8_dispatch_journal"));
        Assert.Equal("RecoveryRequired",await ValueAsync(db,"SELECT state FROM v8_jobs"));
        Assert.Equal("0",await ValueAsync(db,"SELECT cursor FROM v8_jobs"));
    }

    [Fact]
    public async Task CancellationAfterProviderAck_StillCommitsConfirmed()
    {
        var (store,db)=await NewStoreAsync();
        await SeedRunningJobAsync(db);
        using var caller=new CancellationTokenSource();
        var engine=new DurableTaskEngine(store,new FakeTransport((_,_,_)=>
        {
            caller.Cancel();
            return Task.FromResult(new SignalSendResult(SignalDeliveryOutcome.Confirmed,"message-2"));
        }));
        await engine.DispatchAsync(Identity(),"hello",caller.Token);
        Assert.Equal("Confirmed",await ValueAsync(db,"SELECT state FROM v8_dispatch_journal"));
        Assert.Equal("1",await ValueAsync(db,"SELECT cursor FROM v8_jobs"));
    }

    [Fact]
    public async Task CancellationDuringSignalRpcQuarantinesSendAndNeverRetriesOnRestart()
    {
        var (store,db)=await NewStoreAsync();
        await SeedRunningJobAsync(db);
        using var cancel=new CancellationTokenSource();
        var transport=new FakeTransport((_,_,ct)=>{
            cancel.Cancel();
            throw new OperationCanceledException(ct);
        });
        var engine=new DurableTaskEngine(store,transport);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>
            engine.DispatchAsync(Identity(),"test",cancel.Token));
        Assert.Equal(1,transport.SendCount);
        Assert.Equal("RecoveryRequired",await ValueAsync(db,
            "SELECT state FROM v8_dispatch_journal"));
        Assert.Equal("RecoveryRequired",await ValueAsync(db,
            "SELECT state FROM v8_jobs"));
        Assert.Equal("0",await ValueAsync(db,"SELECT cursor FROM v8_jobs"));

        var restarted=new StateStore(RuntimePaths.ForTesting(
            Path.GetDirectoryName(db)!,db));
        await restarted.InitializeAsync(NoCancel);
        Assert.Equal("RecoveryRequired",await ValueAsync(db,
            "SELECT state FROM v8_dispatch_journal"));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            new DurableTaskEngine(restarted,transport).DispatchAsync(
                Identity(),"test",NoCancel));
        Assert.Equal(1,transport.SendCount);
    }

    [Fact]
    public async Task UnexpectedSendError_RequiresRecovery()
    {
        var (store,db)=await NewStoreAsync();
        await SeedRunningJobAsync(db);
        var engine=new DurableTaskEngine(store,new FakeTransport((_,_,_)=>
            throw new IOException("network error after sending")));
        await Assert.ThrowsAsync<IOException>(()=>engine.DispatchAsync(Identity(),"hello",NoCancel));
        Assert.Equal("RecoveryRequired",await ValueAsync(db,"SELECT state FROM v8_dispatch_journal"));
        Assert.Equal("RecoveryRequired",await ValueAsync(db,"SELECT state FROM v8_jobs"));
    }

    [Fact]
    public async Task PreparedBeforeCrash_IsProvablyNotSent_AndDoesNotBlockUpgrade()
    {
        var (store,db)=await NewStoreAsync();
        await SeedRunningJobAsync(db);
        await store.ReserveAsync(Identity(),NoCancel);

        await store.InitializeAsync(NoCancel);
        Assert.Equal("DefinitelyNotSent",await ValueAsync(db,"SELECT state FROM v8_dispatch_journal"));
        Assert.Equal("Paused",await ValueAsync(db,"SELECT state FROM v8_jobs"));
        Assert.True((await store.GetUpdateReadinessAsync(NoCancel)).CanUpdate);
    }

    [Fact]
    public async Task GuardianRestart_BlocksWhileSending_ThenQuarantinesIfDaemonExited()
    {
        var (store,db)=await NewStoreAsync();
        await SeedRunningJobAsync(db);
        var d=Identity();
        await store.ReserveAsync(d,NoCancel);
        await store.MarkSendingAsync(d,NoCancel);

        var canRestart=await store.TryPrepareGuardianRestartAsync(false,NoCancel);
        Assert.False(canRestart);
        Assert.Equal("Paused",await ValueAsync(db,"SELECT state FROM v8_jobs"));
        Assert.Equal("Sending",await ValueAsync(db,"SELECT state FROM v8_dispatch_journal"));

        // Java then crashes. Do NOT assume success or try the same send again.
        Assert.True(await store.TryPrepareGuardianRestartAsync(true,NoCancel));
        Assert.Equal("RecoveryRequired",await ValueAsync(db,"SELECT state FROM v8_jobs"));
        Assert.Equal("RecoveryRequired",await ValueAsync(db,"SELECT state FROM v8_dispatch_journal"));
    }

    [Fact]
    public async Task GuardianRestart_PausesIdleRunningTasksAndDoesNotAutoResume()
    {
        var (store,db)=await NewStoreAsync();
        await SeedRunningJobAsync(db);
        Assert.True(await store.TryPrepareGuardianRestartAsync(false,NoCancel));
        Assert.Equal("Paused",await ValueAsync(db,"SELECT state FROM v8_jobs"));
    }

    [Fact]
    public async Task CleanInstall_ShowsLiveAccountsAndGroups_PartialSyncKeepsOtherMemberships()
    {
        var (store,db)=await NewStoreAsync();
        var accounts=new[]{"+49111","+49222"};
        var initial=new[]{
            new SignalGroupCatalogItem(accounts[0],"group-live","Signal Alpha",true,Array.Empty<string>()),
            new SignalGroupCatalogItem(accounts[1],"group-live","Signal Alpha",true,Array.Empty<string>())
        };
        await store.SyncSignalCatalogAsync(accounts,initial,accounts,NoCancel);

        var guardian=new SignalGuardianSnapshot("healthy","ok","signal-cli test",
            true,0,DateTimeOffset.UtcNow,accounts);
        var dashboard=await store.GetDashboardAsync(guardian,NoCancel);
        Assert.Equal(2,dashboard.Accounts);
        Assert.Equal(1,dashboard.Groups);
        Assert.Equal(2,dashboard.GroupItems.Count);

        // Only account[0] returned a complete group list. Mark its old
        // membership inactive, but keep account[1]'s last good catalog.
        await store.SyncSignalCatalogAsync(
            accounts,Array.Empty<SignalGroupCatalogItem>(),new[]{accounts[0]},NoCancel);
        Assert.Equal("0",await ValueAsync(db,
            "SELECT is_member FROM v8_signal_groups WHERE account='+49111'"));
        Assert.Equal("1",await ValueAsync(db,
            "SELECT is_member FROM v8_signal_groups WHERE account='+49222'"));
    }

    static DispatchIdentity Identity()=>
        new("job-1","run-1",0,0,"group-1","+49123456789","hash-1");

    static async Task<(StateStore Store,string Database)> NewStoreAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"SignalSchedulerV8Safety",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db=Path.Combine(root,"data.db");
        var store=new StateStore(RuntimePaths.ForTesting(root,db));
        await store.InitializeAsync(NoCancel);
        return (store,db);
    }

    static async Task SeedRunningJobAsync(string db,bool member=true)
    {
        await using var c=new SqliteConnection($"Data Source={db}");
        await c.OpenAsync();
        await using var q=c.CreateCommand();
        q.CommandText=$"""
            INSERT INTO v8_jobs(job_id,state,cursor,updated_at)
              VALUES('job-1','Running',0,1);
            INSERT INTO v8_signal_accounts(account,label,enabled,online,last_seen)
              VALUES('+49123456789','Test',1,1,1);
            INSERT INTO v8_signal_groups(account,group_id,name,is_member,last_seen)
              VALUES('+49123456789','group-1','Test group',{(member?1:0)},1);
            """;
        await q.ExecuteNonQueryAsync();
    }

    static async Task<string?> ValueAsync(string db,string sql)
    {
        await using var c=new SqliteConnection($"Data Source={db}");
        await c.OpenAsync();
        await using var q=c.CreateCommand();
        q.CommandText=sql;
        return Convert.ToString(await q.ExecuteScalarAsync());
    }

    sealed class NotReadyTransport : ISignalTransport
    {
        public bool IsReady=>false;
        public int SendCount{get;private set;}
        public Task<SignalSendResult> SendAsync(
            DispatchIdentity d,string content,CancellationToken ct)
        {
            SendCount++;
            throw new InvalidOperationException("Transport must not be called.");
        }
    }

    sealed class FakeTransport : ISignalTransport
    {
        readonly Func<DispatchIdentity,string,CancellationToken,Task<SignalSendResult>> _send;
        public FakeTransport(Func<DispatchIdentity,string,CancellationToken,Task<SignalSendResult>> send)
            =>_send=send;
        public bool IsReady=>true;
        public int SendCount{get;private set;}
        public Task<SignalSendResult> SendAsync(DispatchIdentity d,string content,CancellationToken ct)
        {
            SendCount++;
            return _send(d,content,ct);
        }
    }
}
