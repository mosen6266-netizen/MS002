using Microsoft.Data.Sqlite;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Persistence;

public sealed partial class StateStore
{
    /// <summary>
    /// Manually resolve a single ambiguous dispatch after inspecting Signal.
    /// The decision, evidence and both state changes are committed together.
    /// "Seen" advances the cursor without calling Signal; "NotSeen" leaves
    /// the cursor in place and permits a later explicitly resumed retry.
    /// Neither path auto-resumes, and V7 history is never modified.
    /// </summary>
    public async Task<ManualDispatchReviewResult> ReviewAmbiguousDispatchAsync(
        ManualDispatchReviewRequest request,CancellationToken ct)
    {
        if(request is null ||
           string.IsNullOrWhiteSpace(request.JobId) ||
           request.JobId.Length>256 ||
           request.JobId.StartsWith("v7:",StringComparison.Ordinal) ||
           string.IsNullOrWhiteSpace(request.DispatchKey) ||
           request.DispatchKey.Length>512 ||
           request.Decision is not ("seen" or "not_seen") ||
           string.IsNullOrWhiteSpace(request.Evidence) ||
           request.Evidence.Trim().Length<8 ||
           request.Evidence.Trim().Length>500)
            throw new ArgumentException(
                "请选择待核对的 V8 消息、核对结论，并输入至少 8 个字符的实际检查依据。");

        await using var c=Open();
        using var tx=c.BeginTransaction();
        string jobState;
        long jobCursor;
        await using(var job=c.CreateCommand())
        {
            job.Transaction=tx;
            job.CommandText="SELECT state,cursor FROM v8_jobs WHERE job_id=$j";
            job.Parameters.AddWithValue("$j",request.JobId);
            await using var r=await job.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct))
                throw new KeyNotFoundException("任务不存在。");
            jobState=r.GetString(0);
            jobCursor=r.GetInt64(1);
        }

        string journalState;
        long dispatchCursor;
        await using(var dispatch=c.CreateCommand())
        {
            dispatch.Transaction=tx;
            dispatch.CommandText="""
                SELECT state,cursor FROM v8_dispatch_journal
                WHERE dispatch_key=$key AND job_id=$job LIMIT 1;
                """;
            dispatch.Parameters.AddWithValue("$key",request.DispatchKey);
            dispatch.Parameters.AddWithValue("$job",request.JobId);
            await using var r=await dispatch.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct))
                throw new KeyNotFoundException("发送记录已不存在，请重新刷新。");
            journalState=r.GetString(0);
            dispatchCursor=r.GetInt64(1);
        }
        if(jobState!="RecoveryRequired" ||
           journalState!="RecoveryRequired" || dispatchCursor!=jobCursor)
            throw new InvalidOperationException(
                "任务已变更，只有当前游标的 RecoveryRequired 记录允许人工核对。");

        int total=0;
        string planTable="";
        foreach(var candidate in new []{
            "v8_live_batch_jobs","v8_live_pilot_plans"
        })
        {
            await using var lookup=c.CreateCommand();
            lookup.Transaction=tx;
            lookup.CommandText=$"SELECT total_steps FROM {candidate} WHERE job_id=$job";
            lookup.Parameters.AddWithValue("$job",request.JobId);
            var found=await lookup.ExecuteScalarAsync(ct);
            if(found is null)continue;
            total=Convert.ToInt32(found);
            planTable=candidate;
            break;
        }
        var oneShot=false;
        if(planTable=="")
        {
            await using var probe=c.CreateCommand();
            probe.Transaction=tx;
            probe.CommandText="SELECT 1 FROM v8_live_probe_jobs WHERE job_id=$job";
            probe.Parameters.AddWithValue("$job",request.JobId);
            if(await probe.ExecuteScalarAsync(ct) is null)
                throw new InvalidOperationException(
                    "不支持对该任务人工改写发送记录，只能查看日志。");
            oneShot=true;
            total=1;
        }
        if(dispatchCursor<0 || dispatchCursor>=total)
            throw new InvalidOperationException("任务进度异常，无法安全核对。");

        var countCmd=c.CreateCommand();
        countCmd.Transaction=tx;
        countCmd.CommandText="""
            SELECT COUNT(*) FROM v8_dispatch_journal
            WHERE job_id=$job AND state IN
                ('Prepared','Sending','Unknown','RecoveryRequired');
            """;
        countCmd.Parameters.AddWithValue("$job",request.JobId);
        if(Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct))!=1)
            throw new InvalidOperationException("存在其他未处理的发送记录，不能只核对这一条。");

        var now=DateTimeOffset.UtcNow;
        var recognized=request.Decision=="seen";
        var nextJournalState=recognized?"ManuallyConfirmed":"DefinitelyNotSent";
        var nextCursor=recognized?jobCursor+1:jobCursor;
        var nextJobState=recognized && nextCursor>=total?"Completed":
            oneShot && !recognized?"Failed":"Paused";
        var evidence=request.Evidence.Trim();
        var description=recognized
            ?"操作者已在 Signal 中独立核对这条消息确实出现，并决定不再发送此条。"
            :"操作者已核实此条没有发出。需要再次手动继续，才允许发送当前条。";

        await using(var audit=c.CreateCommand())
        {
            audit.Transaction=tx;
            audit.CommandText="""
                UPDATE v8_dispatch_journal
                SET state=$state,detail=$details,updated_at=$now
                WHERE job_id=$job AND dispatch_key=$key
                  AND cursor=$cursor AND state='RecoveryRequired';
                """;
            audit.Parameters.AddWithValue("$state",nextJournalState);
            audit.Parameters.AddWithValue("$details",description+" 检查依据："+evidence);
            audit.Parameters.AddWithValue("$now",now.ToUnixTimeSeconds());
            audit.Parameters.AddWithValue("$job",request.JobId);
            audit.Parameters.AddWithValue("$key",request.DispatchKey);
            audit.Parameters.AddWithValue("$cursor",jobCursor);
            if(await audit.ExecuteNonQueryAsync(ct)!=1)
                throw new InvalidOperationException("发送记录已变化，本次核对未保存。");
        }

        await using(var task=c.CreateCommand())
        {
            task.Transaction=tx;
            task.CommandText="""
                UPDATE v8_jobs SET cursor=$next,state=$state,updated_at=$now
                WHERE job_id=$job AND state='RecoveryRequired' AND cursor=$old;
                """;
            task.Parameters.AddWithValue("$next",nextCursor);
            task.Parameters.AddWithValue("$state",nextJobState);
            task.Parameters.AddWithValue("$now",now.ToUnixTimeSeconds());
            task.Parameters.AddWithValue("$job",request.JobId);
            task.Parameters.AddWithValue("$old",jobCursor);
            if(await task.ExecuteNonQueryAsync(ct)!=1)
                throw new InvalidOperationException("任务状态已变化，本次核对未保存。");
        }

        if(planTable!="")
        {
            await using var plan=c.CreateCommand();
            plan.Transaction=tx;
            plan.CommandText=$"""
                UPDATE {planTable}
                SET detail=$text,next_due_ms=$due
                WHERE job_id=$job;
                """;
            plan.Parameters.AddWithValue("$text",description);
            plan.Parameters.AddWithValue("$due",now.ToUnixTimeMilliseconds()+15000);
            plan.Parameters.AddWithValue("$job",request.JobId);
            await plan.ExecuteNonQueryAsync(ct);
        }

        await using(var eventLog=c.CreateCommand())
        {
            eventLog.Transaction=tx;
            eventLog.CommandText="""
                INSERT INTO v8_event_log(
                    job_id,dispatch_key,event_type,detail,created_at)
                VALUES($job,$key,$type,$detail,$now);
                """;
            eventLog.Parameters.AddWithValue("$job",request.JobId);
            eventLog.Parameters.AddWithValue("$key",request.DispatchKey);
            eventLog.Parameters.AddWithValue("$type",
                recognized?"manual_confirmed_sent":"manual_confirmed_not_sent");
            eventLog.Parameters.AddWithValue("$detail",description+" 检查依据："+evidence);
            eventLog.Parameters.AddWithValue("$now",now.ToUnixTimeSeconds());
            await eventLog.ExecuteNonQueryAsync(ct);
        }

        tx.Commit();
        return new ManualDispatchReviewResult(
            request.JobId,request.DispatchKey,nextJournalState,nextJobState,
            nextCursor,description);
    }
}
