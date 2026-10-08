using Microsoft.Data.Sqlite;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Persistence;

public sealed class StateStore
{
    readonly RuntimePaths _paths;
    public StateStore(RuntimePaths paths)=>_paths=paths;

    SqliteConnection Open()
    {
        var cs=new SqliteConnectionStringBuilder{
            DataSource=_paths.DatabasePath,
            Mode=SqliteOpenMode.ReadWriteCreate,
            Cache=SqliteCacheMode.Shared
        }.ToString();
        var c=new SqliteConnection(cs); c.Open();
        using var cmd=c.CreateCommand();
        cmd.CommandText="PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
        return c;
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        await using var c=Open();
        await using var cmd=c.CreateCommand();
        cmd.CommandText="""
        CREATE TABLE IF NOT EXISTS v8_schema(key TEXT PRIMARY KEY,value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS v8_jobs(
          job_id TEXT PRIMARY KEY,state TEXT NOT NULL,cursor INTEGER NOT NULL DEFAULT 0,updated_at INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS v8_dispatch_journal(
          dispatch_key TEXT PRIMARY KEY,job_id TEXT NOT NULL,run_token TEXT NOT NULL,run_cycle INTEGER NOT NULL,
          cursor INTEGER NOT NULL,group_id TEXT NOT NULL,account_id TEXT NOT NULL,payload_hash TEXT NOT NULL,
          state TEXT NOT NULL,provider_message_id TEXT,detail TEXT,created_at INTEGER NOT NULL,updated_at INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_v8_dispatch_job ON v8_dispatch_journal(job_id,state,updated_at);
        CREATE TABLE IF NOT EXISTS v8_event_log(
          id INTEGER PRIMARY KEY AUTOINCREMENT,job_id TEXT,dispatch_key TEXT,event_type TEXT NOT NULL,detail TEXT,created_at INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS v8_signal_accounts(
          account TEXT PRIMARY KEY,
          label TEXT NOT NULL,
          enabled INTEGER NOT NULL DEFAULT 1,
          online INTEGER NOT NULL DEFAULT 0,
          last_seen INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS v8_signal_groups(
          account TEXT NOT NULL,
          group_id TEXT NOT NULL,
          name TEXT NOT NULL,
          is_member INTEGER NOT NULL DEFAULT 1,
          members_json TEXT NOT NULL DEFAULT '[]',
          last_seen INTEGER NOT NULL DEFAULT 0,
          PRIMARY KEY(account,group_id)
        );
        CREATE INDEX IF NOT EXISTS idx_v8_signal_groups_name ON v8_signal_groups(name);
        INSERT INTO v8_schema(key,value) VALUES('schema_version','2')
          ON CONFLICT(key) DO UPDATE SET value=excluded.value;
        """;
        await cmd.ExecuteNonQueryAsync(ct);
    }


    public async Task SyncSignalCatalogAsync(
        IReadOnlyList<string> liveAccounts,
        IReadOnlyList<SignalGroupCatalogItem> groups,
        CancellationToken ct)
    {
        await using var c=Open();
        using var tx=c.BeginTransaction();
        var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await using(var offline=c.CreateCommand())
        {
            offline.Transaction=tx;
            offline.CommandText="UPDATE v8_signal_accounts SET online=0";
            await offline.ExecuteNonQueryAsync(ct);
        }

        foreach(var account in liveAccounts.Distinct(StringComparer.Ordinal))
        {
            string label=account;
            long enabled=1;

            await using(var legacy=c.CreateCommand())
            {
                legacy.Transaction=tx;
                legacy.CommandText="SELECT label,enabled FROM v8_accounts WHERE account=$a LIMIT 1";
                legacy.Parameters.AddWithValue("$a",account);
                try
                {
                    await using var r=await legacy.ExecuteReaderAsync(ct);
                    if(await r.ReadAsync(ct))
                    {
                        label=r.IsDBNull(0)?account:r.GetString(0);
                        enabled=r.IsDBNull(1)?1:r.GetInt64(1);
                    }
                }
                catch(SqliteException)
                {
                    // Fresh V8 installs may not have migrated legacy account tables.
                }
            }

            await using var cmd=c.CreateCommand();
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO v8_signal_accounts(account,label,enabled,online,last_seen)
                VALUES($a,$label,$enabled,1,$now)
                ON CONFLICT(account) DO UPDATE SET
                  online=1,
                  last_seen=excluded.last_seen;
                """;
            cmd.Parameters.AddWithValue("$a",account);
            cmd.Parameters.AddWithValue("$label",label);
            cmd.Parameters.AddWithValue("$enabled",enabled);
            cmd.Parameters.AddWithValue("$now",now);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        foreach(var group in groups)
        {
            await using var cmd=c.CreateCommand();
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO v8_signal_groups(account,group_id,name,is_member,members_json,last_seen)
                VALUES($a,$g,$name,$member,$members,$now)
                ON CONFLICT(account,group_id) DO UPDATE SET
                  name=excluded.name,
                  is_member=excluded.is_member,
                  members_json=excluded.members_json,
                  last_seen=excluded.last_seen;
                """;
            cmd.Parameters.AddWithValue("$a",group.Account);
            cmd.Parameters.AddWithValue("$g",group.GroupId);
            cmd.Parameters.AddWithValue("$name",group.Name);
            cmd.Parameters.AddWithValue("$member",group.IsMember?1:0);
            cmd.Parameters.AddWithValue("$members",System.Text.Json.JsonSerializer.Serialize(group.Members));
            cmd.Parameters.AddWithValue("$now",now);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        tx.Commit();
    }

    public async Task ReserveAsync(DispatchIdentity d,CancellationToken ct)
    {
        await using var c=Open();
        using var tx=c.BeginTransaction();
        await using var q=c.CreateCommand(); q.Transaction=tx;
        q.CommandText="SELECT state,payload_hash FROM v8_dispatch_journal WHERE dispatch_key=$k";
        q.Parameters.AddWithValue("$k",d.DispatchKey);
        await using var r=await q.ExecuteReaderAsync(ct);
        if(await r.ReadAsync(ct))
        {
            var state=r.GetString(0); var hash=r.GetString(1);
            if(hash!=d.PayloadHash) throw new InvalidOperationException("Dispatch key collision.");
            if(state is "Confirmed" or "Committed") throw new InvalidOperationException("Dispatch already confirmed.");
            if(state is "Sending" or "Unknown" or "RecoveryRequired") throw new InvalidOperationException("Manual recovery required.");
        }
        await r.DisposeAsync();

        var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using var cmd=c.CreateCommand(); cmd.Transaction=tx;
        cmd.CommandText="""
        INSERT INTO v8_dispatch_journal(dispatch_key,job_id,run_token,run_cycle,cursor,group_id,account_id,payload_hash,state,created_at,updated_at)
        VALUES($k,$j,$t,$cy,$cu,$g,$a,$h,'Prepared',$n,$n)
        ON CONFLICT(dispatch_key) DO UPDATE SET state='Prepared',updated_at=excluded.updated_at;
        """;
        cmd.Parameters.AddWithValue("$k",d.DispatchKey); cmd.Parameters.AddWithValue("$j",d.JobId);
        cmd.Parameters.AddWithValue("$t",d.RunToken); cmd.Parameters.AddWithValue("$cy",d.RunCycle);
        cmd.Parameters.AddWithValue("$cu",d.Cursor); cmd.Parameters.AddWithValue("$g",d.GroupId);
        cmd.Parameters.AddWithValue("$a",d.AccountId); cmd.Parameters.AddWithValue("$h",d.PayloadHash);
        cmd.Parameters.AddWithValue("$n",now);
        await cmd.ExecuteNonQueryAsync(ct);
        tx.Commit();
    }

    public Task MarkSendingAsync(DispatchIdentity d,CancellationToken ct)=>SetStateAsync(d.DispatchKey,"Sending",null,ct);
    public Task MarkDefinitelyNotSentAsync(DispatchIdentity d,string? detail,CancellationToken ct)=>SetStateAsync(d.DispatchKey,"DefinitelyNotSent",detail,ct);
    public Task MarkRecoveryAsync(DispatchIdentity d,string? detail,CancellationToken ct)=>SetStateAsync(d.DispatchKey,"RecoveryRequired",detail,ct);

    async Task SetStateAsync(string key,string state,string? detail,CancellationToken ct)
    {
        await using var c=Open();
        await using var cmd=c.CreateCommand();
        cmd.CommandText="UPDATE v8_dispatch_journal SET state=$s,detail=$d,updated_at=$n WHERE dispatch_key=$k";
        cmd.Parameters.AddWithValue("$s",state); cmd.Parameters.AddWithValue("$d",(object?)detail??DBNull.Value);
        cmd.Parameters.AddWithValue("$n",DateTimeOffset.UtcNow.ToUnixTimeSeconds()); cmd.Parameters.AddWithValue("$k",key);
        if(await cmd.ExecuteNonQueryAsync(ct)!=1) throw new InvalidOperationException("Dispatch transition failed.");
    }

    public async Task CommitConfirmedAsync(DispatchIdentity d,SignalSendResult result,CancellationToken ct)
    {
        await using var c=Open();
        using var tx=c.BeginTransaction();
        var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await using var journal=c.CreateCommand(); journal.Transaction=tx;
        journal.CommandText="""
        UPDATE v8_dispatch_journal SET state='Confirmed',provider_message_id=$p,detail=$d,updated_at=$n
        WHERE dispatch_key=$k AND state='Sending';
        """;
        journal.Parameters.AddWithValue("$p",(object?)result.ProviderMessageId??DBNull.Value);
        journal.Parameters.AddWithValue("$d",(object?)result.Detail??DBNull.Value);
        journal.Parameters.AddWithValue("$n",now); journal.Parameters.AddWithValue("$k",d.DispatchKey);
        if(await journal.ExecuteNonQueryAsync(ct)!=1) throw new InvalidOperationException("Confirmed transition rejected.");

        await using var job=c.CreateCommand(); job.Transaction=tx;
        job.CommandText="""
        INSERT INTO v8_jobs(job_id,state,cursor,updated_at) VALUES($j,'Running',$next,$n)
        ON CONFLICT(job_id) DO UPDATE SET state='Running',
          cursor=CASE WHEN v8_jobs.cursor<excluded.cursor THEN excluded.cursor ELSE v8_jobs.cursor END,
          updated_at=excluded.updated_at;
        """;
        job.Parameters.AddWithValue("$j",d.JobId); job.Parameters.AddWithValue("$next",d.Cursor+1); job.Parameters.AddWithValue("$n",now);
        await job.ExecuteNonQueryAsync(ct);

        await using var log=c.CreateCommand(); log.Transaction=tx;
        log.CommandText="INSERT INTO v8_event_log(job_id,dispatch_key,event_type,detail,created_at) VALUES($j,$k,'dispatch_confirmed',$d,$n)";
        log.Parameters.AddWithValue("$j",d.JobId); log.Parameters.AddWithValue("$k",d.DispatchKey);
        log.Parameters.AddWithValue("$d",(object?)result.Detail??DBNull.Value); log.Parameters.AddWithValue("$n",now);
        await log.ExecuteNonQueryAsync(ct);
        tx.Commit();
    }


    public async Task<UpdateReadiness> GetUpdateReadinessAsync(CancellationToken ct)
    {
        await using var c=Open();

        async Task<int> CountAsync(string sql)
        {
            await using var cmd=c.CreateCommand();
            cmd.CommandText=sql;
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct) ?? 0);
        }

        var activeJobs=await CountAsync("""
            SELECT COUNT(*) FROM v8_jobs
            WHERE lower(state) IN ('running','stopping','waitingsignal');
            """);

        var inFlight=await CountAsync("""
            SELECT COUNT(*) FROM v8_dispatch_journal
            WHERE lower(state)='sending';
            """);

        if(activeJobs>0 || inFlight>0)
        {
            var reason=$"当前还有 {activeJobs} 个运行中的任务、{inFlight} 条正在发送的消息。为避免重复发送或漏发，暂时不能升级。";
            return new UpdateReadiness(false,activeJobs,inFlight,reason);
        }

        return new UpdateReadiness(true,0,0,"可以安全升级。");
    }

    public async Task<DashboardSnapshot> GetDashboardAsync(SignalGuardianSnapshot signal,CancellationToken ct)
    {
        await using var c=Open();

        async Task<bool> TableExists(string name)
        {
            await using var cmd=c.CreateCommand();
            cmd.CommandText="SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n LIMIT 1";
            cmd.Parameters.AddWithValue("$n",name);
            return await cmd.ExecuteScalarAsync(ct) is not null;
        }

        var accounts=new List<DashboardAccount>();
        var groups=new List<DashboardGroup>();
        var scripts=new List<DashboardScript>();
        var jobs=new List<DashboardJob>();
        var legacyDetected=await TableExists("accounts") && await TableExists("groups") && await TableExists("scripts");
        var metadataMigrated=false;

        if(await TableExists("v8_schema"))
        {
            await using var cmd=c.CreateCommand();
            cmd.CommandText="SELECT value FROM v8_schema WHERE key='v7_metadata_migration'";
            metadataMigrated=string.Equals(Convert.ToString(await cmd.ExecuteScalarAsync(ct)),"complete-v1",StringComparison.Ordinal);
        }

        if(await TableExists("v8_accounts"))
        {
            await using var cmd=c.CreateCommand();
            cmd.CommandText="SELECT legacy_id,account,label,enabled FROM v8_accounts ORDER BY sort_order,legacy_id LIMIT 200";
            await using var r=await cmd.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                accounts.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetInt64(3)!=0));
        }

        if(await TableExists("v8_groups"))
        {
            await using var cmd=c.CreateCommand();
            cmd.CommandText="SELECT legacy_id,account,group_id,name,enabled FROM v8_groups ORDER BY name,legacy_id LIMIT 500";
            await using var r=await cmd.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                groups.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt64(4)!=0));
        }

        if(await TableExists("v8_scripts"))
        {
            await using var cmd=c.CreateCommand();
            cmd.CommandText="""
                SELECT s.legacy_id,s.name,COUNT(st.legacy_id)
                FROM v8_scripts s
                LEFT JOIN v8_script_steps st ON st.script_id=s.legacy_id
                GROUP BY s.legacy_id,s.name
                ORDER BY s.name,s.legacy_id
                LIMIT 300;
                """;
            await using var r=await cmd.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                scripts.Add(new(r.GetInt64(0),r.GetString(1),r.GetInt64(2)));
        }

        if(await TableExists("v8_legacy_jobs"))
        {
            await using var cmd=c.CreateCommand();
            cmd.CommandText="""
                SELECT legacy_id,name,group_id,mapped_state,step_cursor,recovery_needed
                FROM v8_legacy_jobs
                ORDER BY legacy_id DESC
                LIMIT 200;
                """;
            await using var r=await cmd.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                jobs.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.GetInt64(4),r.GetInt64(5)!=0));
        }

        return new DashboardSnapshot(
            "8.0.0-alpha.4.1",
            "running",
            "send-disabled-alpha4",
            signal.State,
            signal.Detail,
            signal.SignalCliVersion,
            signal.LiveAccounts.Count,
            legacyDetected,
            metadataMigrated,
            accounts.Count,
            accounts.Count(x=>x.Enabled),
            groups.Count,
            scripts.Count,
            jobs.Count,
            jobs.Count(x=>x.RecoveryRequired || string.Equals(x.State,"RecoveryRequired",StringComparison.OrdinalIgnoreCase)),
            accounts,
            groups,
            scripts,
            jobs);
    }

}
