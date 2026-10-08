using Microsoft.Data.Sqlite;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Persistence;

public sealed partial class StateStore
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
        CREATE TABLE IF NOT EXISTS v8_account_settings(
          account TEXT PRIMARY KEY,label TEXT NOT NULL,enabled INTEGER NOT NULL,
          revision INTEGER NOT NULL DEFAULT 1,updated_at INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS v8_selected_groups(
          group_id TEXT PRIMARY KEY,selected_at INTEGER NOT NULL
        );
        INSERT INTO v8_schema(key,value) VALUES('schema_version','2')
          ON CONFLICT(key) DO UPDATE SET value=excluded.value;
        """;
        await cmd.ExecuteNonQueryAsync(ct);
        await QuarantineInterruptedDispatchesAsync(ct);
    }

    /// <summary>
    /// Invoked at engine startup, before the IPC server or any task runner starts.
    /// A send interrupted by a crash has an UNKNOWN external delivery result.
    /// Quarantine the journal and owning job atomically; never re-send automatically.
    /// An ordinary running job without an in-flight send is paused after restart.
    /// </summary>
    public async Task QuarantineInterruptedDispatchesAsync(CancellationToken ct)
    {
        await using var c=Open();
        using var tx=c.BeginTransaction();
        var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await using(var affected=c.CreateCommand())
        {
            affected.Transaction=tx;
            affected.CommandText="""
                UPDATE v8_jobs SET state='RecoveryRequired',updated_at=$n
                WHERE job_id IN (
                    SELECT job_id FROM v8_dispatch_journal
                    WHERE state IN ('Sending','Unknown','RecoveryRequired')
                );
                """;
            affected.Parameters.AddWithValue("$n",now);
            await affected.ExecuteNonQueryAsync(ct);
        }

        await using(var journal=c.CreateCommand())
        {
            journal.Transaction=tx;
            journal.CommandText="""
                UPDATE v8_dispatch_journal
                SET state='RecoveryRequired',
                    detail=COALESCE(detail,'发送结果未确认：后台曾在发送过程中退出，必须人工核对。'),
                    updated_at=$n
                WHERE state IN ('Sending','Unknown');
                """;
            journal.Parameters.AddWithValue("$n",now);
            await journal.ExecuteNonQueryAsync(ct);
        }

        await using(var jobs=c.CreateCommand())
        {
            jobs.Transaction=tx;
            jobs.CommandText="""
                UPDATE v8_jobs SET state='Paused',updated_at=$n
                WHERE state IN ('Running','Stopping','WaitingSignal');
                """;
            jobs.Parameters.AddWithValue("$n",now);
            await jobs.ExecuteNonQueryAsync(ct);
        }

        await MarkPreparedAsNotSentAsync(c,tx,now,ct);
        tx.Commit();
    }

    /// <summary>
    /// Prepared is strictly before the send boundary. Once tasks are frozen it
    /// can safely become DefinitelyNotSent; never strand an installer on a
    /// prepared record left by a previous clean or unclean shutdown.
    /// </summary>
    static async Task MarkPreparedAsNotSentAsync(
        SqliteConnection c,SqliteTransaction tx,long now,CancellationToken ct)
    {
        await using var cmd=c.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            UPDATE v8_dispatch_journal
            SET state='DefinitelyNotSent',
                detail=COALESCE(detail,'任务在真正发送前已安全中止。'),
                updated_at=$n
            WHERE state='Prepared';
            """;
        cmd.Parameters.AddWithValue("$n",now);
        await cmd.ExecuteNonQueryAsync(ct);
    }


    public async Task SyncSignalCatalogAsync(
        IReadOnlyList<string> liveAccounts,
        IReadOnlyList<SignalGroupCatalogItem> groups,
        IReadOnlyList<string> completeGroupAccounts,
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

            // Settings are durable across a missing/relinked Signal account.
            // Never overwrite the user's remark/disabled preference on sync.
            await using(var settings=c.CreateCommand())
            {
                settings.Transaction=tx;
                settings.CommandText="SELECT label,enabled FROM v8_account_settings WHERE account=$a";
                settings.Parameters.AddWithValue("$a",account);
                await using var r=await settings.ExecuteReaderAsync(ct);
                if(await r.ReadAsync(ct))
                {
                    label=r.GetString(0);
                    enabled=r.GetInt64(1);
                }
            }

            await using var cmd=c.CreateCommand();
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO v8_signal_accounts(account,label,enabled,online,last_seen)
                VALUES($a,$label,$enabled,1,$now)
                ON CONFLICT(account) DO UPDATE SET
                  online=1,
                  label=excluded.label,
                  enabled=excluded.enabled,
                  last_seen=excluded.last_seen;
                """;
            cmd.Parameters.AddWithValue("$a",account);
            cmd.Parameters.AddWithValue("$label",label);
            cmd.Parameters.AddWithValue("$enabled",enabled);
            cmd.Parameters.AddWithValue("$now",now);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Only a successfully fetched full listGroups result may deactivate
        // previously known memberships. A transient per-account RPC failure
        // must not erase that account's group catalog.
        var onlineSet=liveAccounts.ToHashSet(StringComparer.Ordinal);
        foreach(var account in completeGroupAccounts.Distinct(StringComparer.Ordinal))
        {
            if(!onlineSet.Contains(account)) continue;
            await using var reset=c.CreateCommand();
            reset.Transaction=tx;
            reset.CommandText="UPDATE v8_signal_groups SET is_member=0 WHERE account=$a";
            reset.Parameters.AddWithValue("$a",account);
            await reset.ExecuteNonQueryAsync(ct);
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

    /// <summary>
    /// Reserve a single logical message only while its durable job is Running at
    /// the exact expected cursor. A previously prepared/sent key cannot be
    /// re-reserved, even when two callers race on the same SQLite database.
    /// </summary>
    public async Task ReserveAsync(DispatchIdentity d,CancellationToken ct)
    {
        await using var c=Open();
        using var tx=c.BeginTransaction();

        await using(var job=c.CreateCommand())
        {
            job.Transaction=tx;
            job.CommandText="SELECT state,cursor FROM v8_jobs WHERE job_id=$j";
            job.Parameters.AddWithValue("$j",d.JobId);
            await using var r=await job.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct) ||
                r.GetString(0)!="Running" ||
                r.GetInt64(1)!=d.Cursor)
                throw new InvalidOperationException("Task is not running at this cursor; dispatch rejected.");
        }

        await using(var existing=c.CreateCommand())
        {
            existing.Transaction=tx;
            existing.CommandText="SELECT state,payload_hash FROM v8_dispatch_journal WHERE dispatch_key=$k";
            existing.Parameters.AddWithValue("$k",d.DispatchKey);
            await using var r=await existing.ExecuteReaderAsync(ct);
            if(await r.ReadAsync(ct))
            {
                if(r.GetString(1)!=d.PayloadHash)
                    throw new InvalidOperationException("Dispatch key collision.");
                if(r.GetString(0)!="DefinitelyNotSent")
                    throw new InvalidOperationException("Dispatch already reserved, sent or requires manual recovery.");
            }
        }

        await using(var conflict=c.CreateCommand())
        {
            conflict.Transaction=tx;
            conflict.CommandText="""
                SELECT 1 FROM v8_dispatch_journal
                WHERE job_id=$j AND cursor=$cu AND dispatch_key<>$k
                  AND state NOT IN ('DefinitelyNotSent','Skipped','Failed','Cancelled')
                LIMIT 1;
                """;
            conflict.Parameters.AddWithValue("$j",d.JobId);
            conflict.Parameters.AddWithValue("$cu",d.Cursor);
            conflict.Parameters.AddWithValue("$k",d.DispatchKey);
            if(await conflict.ExecuteScalarAsync(ct) is not null)
                throw new InvalidOperationException("Another dispatch already owns this task cursor.");
        }

        var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using var cmd=c.CreateCommand();
        cmd.Transaction=tx;
        cmd.CommandText="""
            INSERT INTO v8_dispatch_journal(
                dispatch_key,job_id,run_token,run_cycle,cursor,group_id,account_id,payload_hash,
                state,created_at,updated_at)
            VALUES($k,$j,$t,$cy,$cu,$g,$a,$h,'Prepared',$n,$n)
            ON CONFLICT(dispatch_key) DO UPDATE SET
                state='Prepared',provider_message_id=NULL,detail=NULL,updated_at=excluded.updated_at
            WHERE v8_dispatch_journal.state='DefinitelyNotSent';
            """;
        cmd.Parameters.AddWithValue("$k",d.DispatchKey); cmd.Parameters.AddWithValue("$j",d.JobId);
        cmd.Parameters.AddWithValue("$t",d.RunToken); cmd.Parameters.AddWithValue("$cy",d.RunCycle);
        cmd.Parameters.AddWithValue("$cu",d.Cursor); cmd.Parameters.AddWithValue("$g",d.GroupId);
        cmd.Parameters.AddWithValue("$a",d.AccountId); cmd.Parameters.AddWithValue("$h",d.PayloadHash);
        cmd.Parameters.AddWithValue("$n",now);
        if(await cmd.ExecuteNonQueryAsync(ct)!=1)
            throw new InvalidOperationException("Dispatch reservation was rejected.");
        tx.Commit();
    }

    /// <summary>
    /// Last DB gate before the irreversible operation: the job must still be
    /// running at the reserved cursor, and the live account and target group
    /// must still be enabled. All checks and the transition are atomic.
    /// </summary>
    public async Task MarkSendingAsync(DispatchIdentity d,CancellationToken ct)
    {
        await using var c=Open();
        using var tx=c.BeginTransaction();
        var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await using var sending=c.CreateCommand();
        sending.Transaction=tx;
        sending.CommandText="""
            UPDATE v8_dispatch_journal SET state='Sending',updated_at=$n
            WHERE dispatch_key=$k AND state='Prepared'
              AND EXISTS (
                SELECT 1 FROM v8_jobs j
                WHERE j.job_id=v8_dispatch_journal.job_id
                  AND j.state='Running' AND j.cursor=v8_dispatch_journal.cursor
              )
              AND EXISTS (
                SELECT 1 FROM v8_signal_accounts a
                WHERE a.account=v8_dispatch_journal.account_id AND a.enabled=1 AND a.online=1
                  AND NOT EXISTS (
                    SELECT 1 FROM v8_account_settings p
                    WHERE p.account=a.account AND p.enabled=0
                  )
              )
              AND EXISTS (
                SELECT 1 FROM v8_signal_groups g
                WHERE g.account=v8_dispatch_journal.account_id
                  AND g.group_id=v8_dispatch_journal.group_id AND g.is_member=1
              );
            """;
        sending.Parameters.AddWithValue("$k",d.DispatchKey);
        sending.Parameters.AddWithValue("$n",now);
        if(await sending.ExecuteNonQueryAsync(ct)==1)
        {
            tx.Commit();
            return;
        }

        // If permission/online state changed before Sending, no irreversible
        // call took place. Record the non-send and pause for explicit review.
        await using(var notSent=c.CreateCommand())
        {
            notSent.Transaction=tx;
            notSent.CommandText="""
                UPDATE v8_dispatch_journal
                SET state='DefinitelyNotSent',
                    detail='发送前校验失败：任务已暂停，或账号、群组不可用。',
                    updated_at=$n
                WHERE dispatch_key=$k AND state='Prepared';
                """;
            notSent.Parameters.AddWithValue("$k",d.DispatchKey);
            notSent.Parameters.AddWithValue("$n",now);
            if(await notSent.ExecuteNonQueryAsync(ct)==1)
            {
                await using var pause=c.CreateCommand();
                pause.Transaction=tx;
                pause.CommandText="""
                    UPDATE v8_jobs SET state='Paused',updated_at=$n
                    WHERE job_id=$j AND state='Running';
                    """;
                pause.Parameters.AddWithValue("$j",d.JobId);
                pause.Parameters.AddWithValue("$n",now);
                await pause.ExecuteNonQueryAsync(ct);
            }
        }
        tx.Commit();
        throw new InvalidOperationException("Pre-send validation rejected; no Signal send was attempted.");
    }

    public Task MarkDefinitelyNotSentAsync(DispatchIdentity d,string? detail,CancellationToken ct)=>
        CompleteUncertainAsync(d,"DefinitelyNotSent","Paused",detail,ct);

    public Task MarkRecoveryAsync(DispatchIdentity d,string? detail,CancellationToken ct)=>
        CompleteUncertainAsync(d,"RecoveryRequired","RecoveryRequired",detail,ct);

    /// <summary>
    /// A failed or ambiguous send must also change the durable JOB state so
    /// that the runner cannot silently progress to the next message.
    /// </summary>
    async Task CompleteUncertainAsync(
        DispatchIdentity d,string journalState,string jobState,string? detail,CancellationToken ct)
    {
        await using var c=Open();
        using var tx=c.BeginTransaction();
        var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await using(var journal=c.CreateCommand())
        {
            journal.Transaction=tx;
            journal.CommandText="""
                UPDATE v8_dispatch_journal
                SET state=$s,detail=$d,updated_at=$n
                WHERE dispatch_key=$k AND state='Sending';
                """;
            journal.Parameters.AddWithValue("$s",journalState);
            journal.Parameters.AddWithValue("$d",(object?)detail??DBNull.Value);
            journal.Parameters.AddWithValue("$n",now);
            journal.Parameters.AddWithValue("$k",d.DispatchKey);
            if(await journal.ExecuteNonQueryAsync(ct)!=1)
                throw new InvalidOperationException("Cannot overwrite a non-Sending dispatch result.");
        }

        await using(var job=c.CreateCommand())
        {
            job.Transaction=tx;
            job.CommandText="UPDATE v8_jobs SET state=$s,updated_at=$n WHERE job_id=$j";
            job.Parameters.AddWithValue("$s",jobState);
            job.Parameters.AddWithValue("$n",now);
            job.Parameters.AddWithValue("$j",d.JobId);
            if(await job.ExecuteNonQueryAsync(ct)!=1)
                throw new InvalidOperationException("Owning job missing for dispatch.");
        }

        await using(var log=c.CreateCommand())
        {
            log.Transaction=tx;
            log.CommandText="""
                INSERT INTO v8_event_log(job_id,dispatch_key,event_type,detail,created_at)
                VALUES($j,$k,$ev,$d,$n);
                """;
            log.Parameters.AddWithValue("$j",d.JobId);
            log.Parameters.AddWithValue("$k",d.DispatchKey);
            log.Parameters.AddWithValue("$ev",journalState=="RecoveryRequired"?"dispatch_ambiguous":"dispatch_not_sent");
            log.Parameters.AddWithValue("$d",(object?)detail??DBNull.Value);
            log.Parameters.AddWithValue("$n",now);
            await log.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
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

        await using var job=c.CreateCommand();
        job.Transaction=tx;
        job.CommandText="""
            UPDATE v8_jobs SET cursor=$next,updated_at=$n
            WHERE job_id=$j AND cursor=$current
              AND state IN ('Running','Paused','Stopping');
            """;
        job.Parameters.AddWithValue("$j",d.JobId);
        job.Parameters.AddWithValue("$current",d.Cursor);
        job.Parameters.AddWithValue("$next",d.Cursor+1);
        job.Parameters.AddWithValue("$n",now);
        if(await job.ExecuteNonQueryAsync(ct)!=1)
            throw new InvalidOperationException("Owning job moved or stopped before confirmed commit.");

        await using var log=c.CreateCommand(); log.Transaction=tx;
        log.CommandText="INSERT INTO v8_event_log(job_id,dispatch_key,event_type,detail,created_at) VALUES($j,$k,'dispatch_confirmed',$d,$n)";
        log.Parameters.AddWithValue("$j",d.JobId); log.Parameters.AddWithValue("$k",d.DispatchKey);
        log.Parameters.AddWithValue("$d",(object?)result.Detail??DBNull.Value); log.Parameters.AddWithValue("$n",now);
        await log.ExecuteNonQueryAsync(ct);
        tx.Commit();
    }


    /// <summary>
    /// Phase 1 of the Guardian restart protocol. Freeze all jobs in the same
    /// SQLite write transaction that checks for an in-flight send. If the
    /// owned daemon has already exited, those sends are unavoidably ambiguous:
    /// quarantine them instead of trying them again.
    /// Phase 2 (process stop/start) is permitted only when this method returns true.
    /// </summary>
    public async Task<bool> TryPrepareGuardianRestartAsync(
        bool daemonAlreadyExited,CancellationToken ct)
    {
        await using var c=Open();
        using var tx=c.BeginTransaction();
        var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Never auto-resume a task merely because signal-cli recovers.
        await using(var pause=c.CreateCommand())
        {
            pause.Transaction=tx;
            pause.CommandText="""
                UPDATE v8_jobs SET state='Paused',updated_at=$n
                WHERE state IN ('Running','Stopping','WaitingSignal');
                """;
            pause.Parameters.AddWithValue("$n",now);
            await pause.ExecuteNonQueryAsync(ct);
        }

        await MarkPreparedAsNotSentAsync(c,tx,now,ct);

        if(daemonAlreadyExited)
        {
            await using(var jobs=c.CreateCommand())
            {
                jobs.Transaction=tx;
                jobs.CommandText="""
                    UPDATE v8_jobs SET state='RecoveryRequired',updated_at=$n
                    WHERE job_id IN (
                        SELECT job_id FROM v8_dispatch_journal
                        WHERE state IN ('Sending','Unknown','RecoveryRequired')
                    );
                    """;
                jobs.Parameters.AddWithValue("$n",now);
                await jobs.ExecuteNonQueryAsync(ct);
            }

            await using(var journal=c.CreateCommand())
            {
                journal.Transaction=tx;
                journal.CommandText="""
                    UPDATE v8_dispatch_journal SET
                        state='RecoveryRequired',
                        detail=COALESCE(detail,'Signal 进程意外退出，发送结果未知，请人工核对。'),
                        updated_at=$n
                    WHERE state IN ('Sending','Unknown');
                    """;
                journal.Parameters.AddWithValue("$n",now);
                await journal.ExecuteNonQueryAsync(ct);
            }
            tx.Commit();
            return true;
        }

        await using var pending=c.CreateCommand();
        pending.Transaction=tx;
        pending.CommandText="""
            SELECT COUNT(*) FROM v8_dispatch_journal WHERE state IN ('Sending','Unknown');
            """;
        var inFlight=Convert.ToInt64(await pending.ExecuteScalarAsync(ct)??0);
        tx.Commit();
        return inFlight==0;
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
            WHERE lower(state) IN ('sending','prepared','unknown');
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

        // Use the editable V8 authoring copy as the canonical dashboard view,
        // rather than the immutable V7 snapshot. This also exposes brand-new
        // scripts created after migrating an older installation.
        if(await TableExists("v8_editor_scripts"))
        {
            scripts.Clear();
            await using var cmd=c.CreateCommand();
            cmd.CommandText="""
                SELECT s.script_id,s.name,COUNT(st.step_id)
                FROM v8_editor_scripts s
                LEFT JOIN v8_editor_steps st ON st.script_id=s.script_id
                GROUP BY s.script_id,s.name
                ORDER BY s.updated_at DESC LIMIT 300;
                """;
            await using var r=await cmd.ExecuteReaderAsync(ct);
            long syntheticId=-1;
            while(await r.ReadAsync(ct))
                scripts.Add(new DashboardScript(syntheticId--,r.GetString(1),r.GetInt64(2)));
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

        // On a clean V8 install the legacy metadata tables do not exist.
        // Live synced identities and groups must still be visible on the
        // native dashboard. Keep existing V7 labels/remarks for overlaps.
        if(await TableExists("v8_signal_accounts"))
        {
            var existing=accounts.Select(a=>a.Account).ToHashSet(StringComparer.Ordinal);
            await using var cmd=c.CreateCommand();
            cmd.CommandText="""
                SELECT account,label,enabled FROM v8_signal_accounts
                ORDER BY label,account LIMIT 500;
                """;
            await using var r=await cmd.ExecuteReaderAsync(ct);
            long syntheticId=-1000000;
            while(await r.ReadAsync(ct))
            {
                var account=r.GetString(0);
                if(existing.Add(account))
                    accounts.Add(new DashboardAccount(
                        syntheticId--,account,r.GetString(1),r.GetInt64(2)!=0));
            }
        }

        if(await TableExists("v8_signal_groups"))
        {
            var existing=groups.Select(g=>(g.Account,g.GroupId)).ToHashSet();
            await using var cmd=c.CreateCommand();
            cmd.CommandText="""
                SELECT account,group_id,name,is_member FROM v8_signal_groups
                ORDER BY name,account LIMIT 2000;
                """;
            await using var r=await cmd.ExecuteReaderAsync(ct);
            long syntheticId=-1000000;
            while(await r.ReadAsync(ct))
            {
                var account=r.GetString(0);
                var groupId=r.GetString(1);
                if(existing.Add((account,groupId)))
                    groups.Add(new DashboardGroup(
                        syntheticId--,account,groupId,r.GetString(2),r.GetInt64(3)!=0));
            }
        }

        // Editing an account remark does not mutate the original V7 metadata.
        // Overlay the locally saved preference in the native dashboard.
        if(await TableExists("v8_account_settings"))
        {
            var settings=new Dictionary<string,(string Label,bool Enabled)>(StringComparer.Ordinal);
            await using(var q=c.CreateCommand())
            {
                q.CommandText="SELECT account,label,enabled FROM v8_account_settings";
                await using var r=await q.ExecuteReaderAsync(ct);
                while(await r.ReadAsync(ct))
                    settings[r.GetString(0)]=(r.GetString(1),r.GetInt64(2)!=0);
            }
            for(var i=0;i<accounts.Count;i++)
            {
                var account=accounts[i];
                if(settings.TryGetValue(account.Account,out var preference))
                    accounts[i]=account with {Label=preference.Label,Enabled=preference.Enabled};
            }
        }

        // Show new V8 durable jobs alongside migrated V7 history, including
        // all crash/restart quarantines which must be visible to the operator.
        if(await TableExists("v8_jobs"))
        {
            await using var cmd=c.CreateCommand();
            cmd.CommandText="""
                SELECT j.job_id,j.state,j.cursor,
                    COALESCE((
                        SELECT x.group_id FROM v8_dispatch_journal x
                        WHERE x.job_id=j.job_id
                        ORDER BY x.updated_at DESC LIMIT 1
                    ),'')
                FROM v8_jobs j
                ORDER BY j.updated_at DESC LIMIT 200;
                """;
            await using var r=await cmd.ExecuteReaderAsync(ct);
            long syntheticId=-1;
            while(await r.ReadAsync(ct))
            {
                var state=r.GetString(1);
                jobs.Add(new DashboardJob(
                    syntheticId--,
                    r.GetString(0),
                    r.GetString(3),
                    state,
                    r.GetInt64(2),
                    string.Equals(state,"RecoveryRequired",StringComparison.OrdinalIgnoreCase)));
            }
        }

        return new DashboardSnapshot(
            "8.0.0-alpha.11",
            "running",
            "send-disabled-alpha11",
            signal.State,
            signal.Detail,
            signal.SignalCliVersion,
            signal.LiveAccounts.Count,
            legacyDetected,
            metadataMigrated,
            accounts.Count,
            accounts.Count(x=>x.Enabled),
            groups.Select(x=>x.GroupId).Distinct(StringComparer.Ordinal).Count(),
            scripts.Count,
            jobs.Count,
            jobs.Count(x=>x.RecoveryRequired || string.Equals(x.State,"RecoveryRequired",StringComparison.OrdinalIgnoreCase)),
            accounts,
            groups,
            scripts,
            jobs);
    }

}
