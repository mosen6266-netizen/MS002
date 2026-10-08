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

        await using(var query=c.CreateCommand())
        {
            query.CommandText="""
                SELECT job_id,state,cursor
                FROM v8_jobs ORDER BY updated_at DESC,job_id LIMIT 500;
                """;
            await using var r=await query.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                var state=r.GetString(1);
                jobs.Add(new RecoveryJobItem(
                    r.GetString(0),r.GetString(0),state,r.GetInt64(2),false,
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

        await using(var q=c.CreateCommand())
        {
            q.CommandText="""
                SELECT dispatch_key,job_id,cursor,group_id,account_id,state,
                       provider_message_id,detail,updated_at
                FROM v8_dispatch_journal
                ORDER BY updated_at DESC,dispatch_key LIMIT 800;
                """;
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                dispatches.Add(new RecoveryDispatchItem(
                    r.GetString(0),r.GetString(1),r.GetInt64(2),r.GetString(3),
                    r.GetString(4),r.GetString(5),
                    r.IsDBNull(6)?null:r.GetString(6),
                    r.IsDBNull(7)?null:r.GetString(7),
                    r.GetInt64(8)));
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
