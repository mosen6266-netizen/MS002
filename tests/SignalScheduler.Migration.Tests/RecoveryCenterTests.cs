using Microsoft.Data.Sqlite;
using SignalScheduler.Engine;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class RecoveryCenterTests
{
    [Fact]
    public async Task ManualPause_PersistsAndStaysPausedAcrossRestart()
    {
        var (store,db)=await CreateAsync();
        await InsertJob(db,"Running");
        var reply=await store.PauseJobAsync("job-1",CancellationToken.None);
        Assert.Equal("Paused",reply.State);
        Assert.Equal("Paused",await ValueAsync(db,"SELECT state FROM v8_jobs WHERE job_id='job-1'"));
        Assert.Equal("1",await ValueAsync(db,
            "SELECT COUNT(*) FROM v8_event_log WHERE event_type='manual_pause'"));

        var reopened=new StateStore(RuntimePaths.ForTesting(Path.GetDirectoryName(db)!,db));
        await reopened.InitializeAsync(CancellationToken.None);
        Assert.Equal("Paused",await ValueAsync(db,"SELECT state FROM v8_jobs WHERE job_id='job-1'"));
        var repeat=await reopened.PauseJobAsync("job-1",CancellationToken.None);
        Assert.Equal("Paused",repeat.State);
        Assert.Equal("1",await ValueAsync(db,
            "SELECT COUNT(*) FROM v8_event_log WHERE event_type='manual_pause'"));
    }

    [Fact]
    public async Task PauseDuringSending_DoesNotRewriteJournalOrCursor()
    {
        var (store,db)=await CreateAsync();
        await InsertJob(db,"Running");
        await AddDispatchAsync(db,"Sending");
        var reply=await store.PauseJobAsync("job-1",CancellationToken.None);
        Assert.Contains("未确认",reply.Detail);
        Assert.Equal("Paused",await ValueAsync(db,"SELECT state FROM v8_jobs"));
        Assert.Equal("Sending",await ValueAsync(db,"SELECT state FROM v8_dispatch_journal"));
        Assert.Equal("0",await ValueAsync(db,"SELECT cursor FROM v8_jobs"));

        // A crashed Engine must quarantine the ambiguous in-flight message.
        await store.InitializeAsync(CancellationToken.None);
        Assert.Equal("RecoveryRequired",await ValueAsync(db,"SELECT state FROM v8_jobs"));
        Assert.Equal("RecoveryRequired",await ValueAsync(db,"SELECT state FROM v8_dispatch_journal"));
        Assert.Equal("0",await ValueAsync(db,"SELECT cursor FROM v8_jobs"));
    }

    [Fact]
    public async Task RecoveryRequired_CannotBeDowngradedToPaused()
    {
        var (store,db)=await CreateAsync();
        await InsertJob(db,"RecoveryRequired");
        await AddDispatchAsync(db,"RecoveryRequired");
        var result=await store.PauseJobAsync("job-1",CancellationToken.None);
        Assert.Equal("RecoveryRequired",result.State);
        Assert.Equal("RecoveryRequired",await ValueAsync(db,"SELECT state FROM v8_jobs"));
    }

    [Fact]
    public async Task Overview_ReadsNativeJournalAndLegacyJobs_WithoutMutation()
    {
        var (store,db)=await CreateAsync();
        await InsertJob(db,"RecoveryRequired");
        await AddDispatchAsync(db,"RecoveryRequired");

        await using(var c=new SqliteConnection($"Data Source={db}"))
        {
            await c.OpenAsync();
            await using var cmd=c.CreateCommand();
            cmd.CommandText="""
                CREATE TABLE v8_legacy_jobs(
                    legacy_id INTEGER PRIMARY KEY,name TEXT,mapped_state TEXT,
                    step_cursor INTEGER,recovery_needed INTEGER);
                INSERT INTO v8_legacy_jobs VALUES(6,'历史任务','RecoveryRequired',41,1);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var snapshot=await store.GetRecoveryOverviewAsync(CancellationToken.None);
        Assert.Equal(2,snapshot.Jobs.Count);
        Assert.Contains(snapshot.Jobs,x=>x.JobId=="v7:6" && x.IsLegacy);
        Assert.Contains(snapshot.Jobs,x=>x.JobId=="job-1" && x.NeedsReview);
        Assert.Single(snapshot.Dispatches);
        Assert.Equal("RecoveryRequired",snapshot.Dispatches.Single().State);
        Assert.Equal("RecoveryRequired",await ValueAsync(db,"SELECT state FROM v8_jobs"));
        await Assert.ThrowsAsync<ArgumentException>(()=>
            store.PauseJobAsync("v7:6",CancellationToken.None));
    }

    [Fact]
    public async Task InvalidOrNonexistentTask_IsRejected()
    {
        var (store,_)=await CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(()=>
            store.PauseJobAsync("",CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(()=>
            store.PauseJobAsync("missing",CancellationToken.None));
    }

    static async Task<(StateStore Store,string Db)> CreateAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"SignalSchedulerRecoveryTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db=Path.Combine(root,"data.db");
        var store=new StateStore(RuntimePaths.ForTesting(root,db));
        await store.InitializeAsync(CancellationToken.None);
        return (store,db);
    }

    static async Task InsertJob(string db,string state)
    {
        await using var c=new SqliteConnection($"Data Source={db}");
        await c.OpenAsync();
        await using var cmd=c.CreateCommand();
        cmd.CommandText="""
            INSERT INTO v8_jobs(job_id,state,cursor,updated_at)
            VALUES('job-1',$s,0,1);
            """;
        cmd.Parameters.AddWithValue("$s",state);
        await cmd.ExecuteNonQueryAsync();
    }

    static async Task AddDispatchAsync(string db,string state)
    {
        await using var c=new SqliteConnection($"Data Source={db}");
        await c.OpenAsync();
        await using var cmd=c.CreateCommand();
        cmd.CommandText="""
            INSERT INTO v8_dispatch_journal(
                dispatch_key,job_id,run_token,run_cycle,cursor,group_id,account_id,
                payload_hash,state,created_at,updated_at)
            VALUES('key-1','job-1','run',0,0,'group','account','hash',$s,1,1);
            """;
        cmd.Parameters.AddWithValue("$s",state);
        await cmd.ExecuteNonQueryAsync();
    }

    static async Task<string?> ValueAsync(string db,string sql)
    {
        await using var c=new SqliteConnection($"Data Source={db}");
        await c.OpenAsync();
        await using var cmd=c.CreateCommand();
        cmd.CommandText=sql;
        return Convert.ToString(await cmd.ExecuteScalarAsync());
    }
}
