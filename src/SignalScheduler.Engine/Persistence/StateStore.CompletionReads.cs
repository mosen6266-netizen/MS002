using Microsoft.Data.Sqlite;

namespace SignalScheduler.Engine.Persistence;

public sealed partial class StateStore
{
    public sealed record PendingCompletionRead(
        string JobId,string Account,string GroupId,int Attempt,bool Online);

    // A process death may follow a read RPC acceptance. Persist uncertain
    // completion work for review rather than blindly retransmitting it.
    public async Task RecoverInterruptedCompletionReadsAsync(CancellationToken ct)
    {
        await InitializeLiveBatchAsync(ct);
        await using var db=Open();
        await using var q=db.CreateCommand();
        q.CommandText="""
            UPDATE v8_completion_read_jobs
            SET state='review',detail='程序中断：上次已读请求结果未知，已避免重复发送'
            WHERE state='inflight';
            """;
        await q.ExecuteNonQueryAsync(ct);
    }

    public async Task<PendingCompletionRead?> ClaimCompletionReadAsync(
        long nowMs,CancellationToken ct)
    {
        await using var db=Open();
        using var tx=db.BeginTransaction();
        string job="",account="",group="";
        int attempts=0;
        bool online=false;
        await using(var q=db.CreateCommand())
        {
            q.Transaction=tx;
            q.CommandText="""
                SELECT r.job_id,r.account,r.group_id,r.attempts,
                       COALESCE(a.online,0)
                FROM v8_completion_read_jobs r
                LEFT JOIN v8_signal_accounts a ON a.account=r.account
                WHERE r.state='pending' AND r.next_due_ms<=$now
                ORDER BY r.next_due_ms,r.job_id,r.account LIMIT 1;
                """;
            q.Parameters.AddWithValue("$now",nowMs);
            await using var reader=await q.ExecuteReaderAsync(ct);
            if(!await reader.ReadAsync(ct))return null;
            job=reader.GetString(0);
            account=reader.GetString(1);
            group=reader.GetString(2);
            attempts=reader.GetInt32(3)+1;
            online=reader.GetInt64(4)!=0;
        }
        await using(var update=db.CreateCommand())
        {
            update.Transaction=tx;
            update.CommandText="""
                UPDATE v8_completion_read_jobs
                SET state='inflight',attempts=$attempt,detail='完成后已读处理中'
                WHERE job_id=$job AND account=$account AND state='pending'
                  AND attempts=$previous;
                """;
            update.Parameters.AddWithValue("$job",job);
            update.Parameters.AddWithValue("$account",account);
            update.Parameters.AddWithValue("$attempt",attempts);
            update.Parameters.AddWithValue("$previous",attempts-1);
            if(await update.ExecuteNonQueryAsync(ct)!=1)
                throw new InvalidOperationException("Completion read claim changed");
        }
        tx.Commit();
        return new(job,account,group,attempts,online);
    }

    public async Task FinishCompletionReadAsync(
        PendingCompletionRead item,bool ready,bool retry,string code,
        long nowMs,CancellationToken ct)
    {
        if(code.Length>128 || code.Any(ch=>!(char.IsLetterOrDigit(ch) ||
            ch is '_' or '-' )))code="READ_ERROR";
        var state=ready?"done":retry&&item.Attempt<4?"pending":"review";
        await using var db=Open();
        using var tx=db.BeginTransaction();
        await using(var change=db.CreateCommand())
        {
            change.Transaction=tx;
            change.CommandText="""
                UPDATE v8_completion_read_jobs
                SET state=$state,detail=$code,next_due_ms=$next
                WHERE job_id=$job AND account=$account AND state='inflight'
                  AND attempts=$attempt;
                """;
            change.Parameters.AddWithValue("$job",item.JobId);
            change.Parameters.AddWithValue("$account",item.Account);
            change.Parameters.AddWithValue("$attempt",item.Attempt);
            change.Parameters.AddWithValue("$state",state);
            change.Parameters.AddWithValue("$code",code);
            change.Parameters.AddWithValue("$next",state=="pending"?
                nowMs+(long)TimeSpan.FromSeconds(Math.Min(60,5*item.Attempt*item.Attempt)).TotalMilliseconds:0);
            if(await change.ExecuteNonQueryAsync(ct)!=1)
                throw new InvalidOperationException("Completion read result changed");
        }
        int total=0,done=0,review=0,pending=0;
        await using(var summary=db.CreateCommand())
        {
            summary.Transaction=tx;
            summary.CommandText="""
                SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN state='done' THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN state='review' THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN state IN ('pending','inflight') THEN 1 ELSE 0 END),0)
                FROM v8_completion_read_jobs WHERE job_id=$job;
                """;
            summary.Parameters.AddWithValue("$job",item.JobId);
            await using var row=await summary.ExecuteReaderAsync(ct);
            if(await row.ReadAsync(ct))
            {
                total=row.GetInt32(0);
                done=row.GetInt32(1);
                review=row.GetInt32(2);
                pending=row.GetInt32(3);
            }
        }
        await using(var plan=db.CreateCommand())
        {
            plan.Transaction=tx;
            plan.CommandText="""
                UPDATE v8_live_batch_jobs SET detail=$detail
                WHERE job_id=$job AND EXISTS(
                    SELECT 1 FROM v8_jobs WHERE job_id=$job AND state='Completed'
                );
                """;
            plan.Parameters.AddWithValue("$job",item.JobId);
            plan.Parameters.AddWithValue("$detail",
                $"剧本发送已完成。群内账号已读请求：处理成功 {done}/{total}"+
                (pending>0?$"，待处理 {pending}":string.Empty)+
                (review>0?$"，需人工核查 {review}":string.Empty)+
                "；仅代表 Signal RPC 的接收结果。");
            await plan.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
    }
}
