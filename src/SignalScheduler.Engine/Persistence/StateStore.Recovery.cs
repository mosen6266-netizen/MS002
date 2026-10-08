using Microsoft.Data.Sqlite;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Persistence;

public sealed partial class StateStore
{
    /// <summary>
    /// Snapshot of native V8 task state and per-message evidence, plus
    /// read-only legacy recovery metadata. No journal row is modified here.
    /// </summary>
    public async Task<RecoveryOverview> GetRecoveryOverviewAsync(CancellationToken ct)
    {
        await using var c=Open();
        var jobs=new List<RecoveryJobItem>();
        var dispatches=new List<RecoveryDispatchItem>();

        // Recovery snapshots must also work on older databases that contain
        // only the core dispatch journal and have not initialized newer modules.
        // Never create or alter tables as a side effect of reading recovery.
        async Task<bool> HasTableAsync(string tableName)
        {
            await using var check=c.CreateCommand();
            check.CommandText="SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
            check.Parameters.AddWithValue("$name",tableName);
            return await check.ExecuteScalarAsync(ct) is not null;
        }
        var batchTable=await HasTableAsync("v8_live_batch_jobs");
        var pilotTable=await HasTableAsync("v8_live_pilot_plans");
        var previewTable=await HasTableAsync("v8_preview_plans");
        var probeTable=await HasTableAsync("v8_live_probe_jobs");
        var groupsTable=await HasTableAsync("v8_signal_groups");
        var prefsTable=await HasTableAsync("v8_account_settings");
        var accountsTable=await HasTableAsync("v8_signal_accounts");
        var batchJoin=batchTable
            ?"LEFT JOIN v8_live_batch_jobs b ON b.job_id=j.job_id"
            :"LEFT JOIN (SELECT NULL AS job_id, NULL AS group_name, NULL AS script_name WHERE 0) b ON b.job_id=j.job_id";
        var pilotJoin=pilotTable
            ?"LEFT JOIN v8_live_pilot_plans p ON p.job_id=j.job_id"
            :"LEFT JOIN (SELECT NULL AS job_id, NULL AS group_name, NULL AS script_name WHERE 0) p ON p.job_id=j.job_id";
        var previewJoin=previewTable
            ?"LEFT JOIN v8_preview_plans v ON v.job_id=j.job_id"
            :"LEFT JOIN (SELECT NULL AS job_id, NULL AS group_name, NULL AS script_name WHERE 0) v ON v.job_id=j.job_id";
        var probeJoin=probeTable
            ?"LEFT JOIN v8_live_probe_jobs q ON q.job_id=j.job_id"
            :"LEFT JOIN (SELECT NULL AS job_id, NULL AS group_name WHERE 0) q ON q.job_id=j.job_id";


        await using(var query=c.CreateCommand())
        {
            query.CommandText=$"""
                SELECT j.job_id,j.state,j.cursor,
                       COALESCE(
                           NULLIF(b.group_name,'') || ' · ' || NULLIF(b.script_name,''),
                           NULLIF(p.group_name,'') || ' · ' || NULLIF(p.script_name,''),
                           NULLIF(v.group_name,'') || ' · ' || NULLIF(v.script_name,''),
                           NULLIF(q.group_name,'') || ' · 实发测试',
                           '未命名任务')
                FROM v8_jobs j
                {batchJoin}
                {pilotJoin}
                {previewJoin}
                {probeJoin}
                ORDER BY j.updated_at DESC,j.job_id LIMIT 500;
                """;
            await using var r=await query.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                var state=r.GetString(1);
                jobs.Add(new RecoveryJobItem(
                    r.GetString(0),r.GetString(3),state,r.GetInt64(2),false,
                    state=="RecoveryRequired"));
            }
        }

        // Backward-compatible V7 history is never edited by V8 recovery actions.
        await using(var table=c.CreateCommand())
        {
            table.CommandText="""
                SELECT 1 FROM sqlite_master
                WHERE type='table' AND name='v8_legacy_jobs' LIMIT 1;
                """;
            if(await table.ExecuteScalarAsync(ct) is not null)
            {
                await using var query=c.CreateCommand();
                query.CommandText="""
                    SELECT legacy_id,name,mapped_state,step_cursor,recovery_needed
                    FROM v8_legacy_jobs ORDER BY legacy_id DESC LIMIT 300;
                    """;
                await using var r=await query.ExecuteReaderAsync(ct);
                while(await r.ReadAsync(ct))
                    jobs.Add(new RecoveryJobItem(
                        $"v7:{r.GetInt64(0)}",r.GetString(1),r.GetString(2),
                        r.GetInt64(3),true,r.GetInt64(4)!=0 ||
                        r.GetString(2)=="RecoveryRequired"));
            }
        }

        var dispatchBatchJoin=batchTable
            ?"LEFT JOIN v8_live_batch_jobs b ON b.job_id=d.job_id"
            :"LEFT JOIN (SELECT NULL AS job_id, NULL AS group_name WHERE 0) b ON b.job_id=d.job_id";
        var groupJoin=groupsTable
            ?"LEFT JOIN v8_signal_groups g ON g.group_id=d.group_id AND g.account=d.account_id"
            :"LEFT JOIN (SELECT NULL AS group_id, NULL AS account, NULL AS name WHERE 0) g ON g.group_id=d.group_id AND g.account=d.account_id";
        var prefsJoin=prefsTable
            ?"LEFT JOIN v8_account_settings pref ON pref.account=d.account_id"
            :"LEFT JOIN (SELECT NULL AS account, NULL AS label WHERE 0) pref ON pref.account=d.account_id";
        var accountsJoin=accountsTable
            ?"LEFT JOIN v8_signal_accounts a ON a.account=d.account_id"
            :"LEFT JOIN (SELECT NULL AS account, NULL AS label WHERE 0) a ON a.account=d.account_id";
        await using(var q=c.CreateCommand())
        {
            q.CommandText=$"""
                SELECT d.dispatch_key,d.job_id,d.cursor,d.group_id,d.account_id,d.state,
                       d.provider_message_id,d.detail,d.updated_at,
                       COALESCE(NULLIF(g.name,''),NULLIF(b.group_name,'')),
                       COALESCE(NULLIF(pref.label,''),NULLIF(a.label,''))
                FROM v8_dispatch_journal d
                {dispatchBatchJoin}
                {groupJoin}
                {prefsJoin}
                {accountsJoin}
                ORDER BY d.updated_at DESC,d.dispatch_key LIMIT 800;
                """;
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                dispatches.Add(new RecoveryDispatchItem(
                    r.GetString(0),r.GetString(1),r.GetInt64(2),r.GetString(3),
                    r.GetString(4),r.GetString(5),
                    r.IsDBNull(6)?null:r.GetString(6),
                    r.IsDBNull(7)?null:r.GetString(7),
                    r.GetInt64(8),
                    r.IsDBNull(9)?null:r.GetString(9),
                    r.IsDBNull(10)?null:r.GetString(10)));
        }
        return new RecoveryOverview(jobs,dispatches);
    }

    /// <summary>
    /// A pause command never kills a Signal request and never destroys a
    /// Sending journal row. An in-flight confirmed send can still commit its
    /// exact cursor while the job is Paused. An ambiguous send changes the
    /// job to RecoveryRequired. No resume is available in alpha.6.
    /// </summary>
    public async Task<PauseJobResult> PauseJobAsync(string jobId,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(jobId) || jobId.Length>256 ||
            jobId.StartsWith("v7:",StringComparison.Ordinal))
            throw new ArgumentException("不是可操作的 V8 任务编号。",nameof(jobId));

        await using var c=Open();
        using var tx=c.BeginTransaction();

        string state;
        await using(var lookup=c.CreateCommand())
        {
            lookup.Transaction=tx;
            lookup.CommandText="SELECT state FROM v8_jobs WHERE job_id=$j";
            lookup.Parameters.AddWithValue("$j",jobId);
            state=(string?)await lookup.ExecuteScalarAsync(ct)
                ??throw new KeyNotFoundException("未找到指定的 V8 任务。");
        }

        if(state=="Paused")
        {
            tx.Commit();
            return new PauseJobResult(jobId,"Paused","此任务已经暂停，不会自动继续。");
        }
        if(state=="RecoveryRequired")
        {
            tx.Commit();
            return new PauseJobResult(jobId,state,
                "任务需要人工核对发送结果，不能把待恢复任务直接改成已暂停。");
        }
        if(state is not ("Running" or "Stopping" or "WaitingSignal"))
        {
            tx.Commit();
            return new PauseJobResult(jobId,state,
                "当前任务状态无需暂停，数据库记录未改变。");
        }

        var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using(var update=c.CreateCommand())
        {
            update.Transaction=tx;
            update.CommandText="""
                UPDATE v8_jobs SET state='Paused',updated_at=$n
                WHERE job_id=$j AND state IN ('Running','Stopping','WaitingSignal');
                """;
            update.Parameters.AddWithValue("$j",jobId);
            update.Parameters.AddWithValue("$n",now);
            if(await update.ExecuteNonQueryAsync(ct)!=1)
                throw new InvalidOperationException("任务状态已经改变，本次暂停未完成。");
        }

        await using(var log=c.CreateCommand())
        {
            log.Transaction=tx;
            log.CommandText="""
                INSERT INTO v8_event_log(job_id,event_type,detail,created_at)
                VALUES($j,'manual_pause','用户在恢复中心手动暂停任务',$n);
                """;
            log.Parameters.AddWithValue("$j",jobId);
            log.Parameters.AddWithValue("$n",now);
            await log.ExecuteNonQueryAsync(ct);
        }

        await using var pending=c.CreateCommand();
        pending.Transaction=tx;
        pending.CommandText="""
            SELECT COUNT(*) FROM v8_dispatch_journal
            WHERE job_id=$j AND state IN ('Sending','Unknown');
            """;
        pending.Parameters.AddWithValue("$j",jobId);
        var inFlight=Convert.ToInt64(await pending.ExecuteScalarAsync(ct)??0);
        tx.Commit();

        return new PauseJobResult(jobId,"Paused",inFlight>0
            ?"暂停已保存，但仍有发送结果未确认的消息，请检查恢复明细，不能自动重发。"
            :"任务已暂停并保存。重启程序也不会自动继续。");
    }
}
