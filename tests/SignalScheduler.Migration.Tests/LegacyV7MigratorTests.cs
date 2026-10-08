using Xunit;
using Microsoft.Data.Sqlite;
using SignalScheduler.Engine;
using SignalScheduler.Engine.Migration;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Engine.Signal;

namespace SignalScheduler.Migration.Tests;

public sealed class LegacyV7MigratorTests
{
    [Fact]
    public async Task MigratesMetadata_PreservesLabel_AndFailsClosedForInflightJob()
    {
        var root=Path.Combine(Path.GetTempPath(),"SignalSchedulerV8Tests",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db=Path.Combine(root,"data.db");

        await using(var c=new SqliteConnection($"Data Source={db}"))
        {
            await c.OpenAsync();
            var cmd=c.CreateCommand();
            cmd.CommandText="""
            CREATE TABLE accounts(id INTEGER PRIMARY KEY,account TEXT,label TEXT,enabled INTEGER,sort_order INTEGER);
            CREATE TABLE groups(id INTEGER PRIMARY KEY,account TEXT,group_id TEXT,name TEXT,enabled INTEGER);
            CREATE TABLE scripts(id INTEGER PRIMARY KEY,name TEXT,target_group_id TEXT,created_at INTEGER,updated_at INTEGER);
            CREATE TABLE script_steps(id INTEGER PRIMARY KEY,script_id INTEGER,sort_order INTEGER,account TEXT,message TEXT,attachment TEXT,pause_after INTEGER,reminder_text TEXT,delay_after INTEGER,typing_seconds INTEGER);
            CREATE TABLE group_jobs(id INTEGER PRIMARY KEY,name TEXT,group_id TEXT,script_id INTEGER,running INTEGER,paused INTEGER,step_cursor INTEGER,recovery_needed INTEGER,inflight_cursor INTEGER,snapshot_id INTEGER);
            CREATE TABLE v8_schema(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            INSERT INTO accounts VALUES(1,'+491234','客户A',1,0);
            INSERT INTO groups VALUES(1,'+491234','g1','Group A',1);
            INSERT INTO scripts VALUES(1,'Script A','g1',1,1);
            INSERT INTO script_steps VALUES(1,1,0,'+491234','hello','',0,'',30,5);
            INSERT INTO group_jobs VALUES(1,'Job A','g1',1,1,0,7,1,7,3);
            """;
            await cmd.ExecuteNonQueryAsync();
        }

        var paths=new TestPaths(root,db);
        var migrator=new LegacyV7Migrator(paths.ToRuntimePaths());
        var inventory=await migrator.AnalyzeAsync(CancellationToken.None);
        Assert.True(inventory.LegacyDetected);

        await migrator.MigrateMetadataAsync(CancellationToken.None);

        await using var verify=new SqliteConnection($"Data Source={db}");
        await verify.OpenAsync();
        var label=Convert.ToString(await Scalar(verify,"SELECT label FROM v8_accounts WHERE legacy_id=1"));
        var state=Convert.ToString(await Scalar(verify,"SELECT mapped_state FROM v8_legacy_jobs WHERE legacy_id=1"));
        Assert.Equal("客户A",label);
        Assert.Equal("RecoveryRequired",state);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(root,"backups"),"v8-pre-migration-*.db"));

        var store=new StateStore(paths.ToRuntimePaths());
        var signal=new SignalHealthSnapshot(
            SignalHealthState.Healthy,"ok","0.14.8",true,false,Array.Empty<SignalAccountInfo>(),DateTimeOffset.UtcNow);
        var dashboard=await store.GetDashboardAsync(signal,CancellationToken.None);
        Assert.Equal(1,dashboard.Accounts);
        Assert.Equal(1,dashboard.EnabledAccounts);
        Assert.Equal(1,dashboard.Groups);
        Assert.Equal(1,dashboard.Scripts);
        Assert.Equal(1,dashboard.Jobs);
        Assert.Equal(1,dashboard.RecoveryJobs);
        Assert.Equal("客户A",dashboard.AccountItems.Single().Label);
    }

    static async Task<object?> Scalar(SqliteConnection c,string sql)
    {
        await using var cmd=c.CreateCommand(); cmd.CommandText=sql; return await cmd.ExecuteScalarAsync();
    }

    sealed record TestPaths(string Root,string Db)
    {
        public RuntimePaths ToRuntimePaths()
        {
            Environment.SetEnvironmentVariable("SIGNAL_SCHEDULER_V8_TEST_DATA_ROOT",Root);
            return RuntimePaths.ForTesting(Root,Db);
        }
    }
}
