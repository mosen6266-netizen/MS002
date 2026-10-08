using Microsoft.Data.Sqlite;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Persistence;

public sealed partial class StateStore
{
    /// <summary>
    /// Atomically authorize a single diagnostic message. The caller must have
    /// explicitly confirmed the real send, and its destination must currently
    /// be a user-selected group with this linked, enabled, online member
    /// account. A cooldown prevents clicking the button into a flood.
    /// </summary>
    public async Task<(DispatchIdentity Dispatch,string Name,string Message)>
        CreateLiveProbeAsync(LiveProbeRequest request,CancellationToken ct)
    {
        if(request is null || !request.ConfirmRealSend ||
           string.IsNullOrWhiteSpace(request.Account) || request.Account.Length>128 ||
           string.IsNullOrWhiteSpace(request.GroupId) || request.GroupId.Length>512)
            throw new ArgumentException(
                "必须选择本人可用的账号、已勾选的测试群，并明确确认实际发送。");

        var now=DateTimeOffset.UtcNow;
        await using var c=Open();
        using var tx=c.BeginTransaction();

        // Only one diagnostic send may be pending; in-flight and ambiguous
        // outcomes are never bypassed with a second test.
        await using(var unresolved=c.CreateCommand())
        {
            unresolved.Transaction=tx;
            unresolved.CommandText="""
                SELECT 1 FROM v8_dispatch_journal
                WHERE state IN ('Prepared','Sending','Unknown','RecoveryRequired')
                LIMIT 1;
                """;
            if(await unresolved.ExecuteScalarAsync(ct) is not null)
                throw new InvalidOperationException(
                    "存在正在发送或需要核对的消息，请先在恢复中心处理。禁止重复实发。");
        }

        await using(var other=c.CreateCommand())
        {
            other.Transaction=tx;
            other.CommandText="""
                SELECT 1 FROM v8_jobs j JOIN v8_live_probe_jobs p
                  ON j.job_id=p.job_id WHERE j.state='Running' LIMIT 1;
                """;
            if(await other.ExecuteScalarAsync(ct) is not null)
                throw new InvalidOperationException(
                    "已有一条测试消息正在发送，不能同时发起第二条。");
        }

        await using(var rate=c.CreateCommand())
        {
            rate.Transaction=tx;
            rate.CommandText="""
                SELECT 1 FROM v8_live_probe_jobs
                WHERE created_at>=$last LIMIT 1;
                """;
            rate.Parameters.AddWithValue("$last",now.ToUnixTimeSeconds()-90);
            if(await rate.ExecuteScalarAsync(ct) is not null)
                throw new InvalidOperationException(
                    "测试发送间隔至少 90 秒，请不要反复点击。");
        }

        string groupName;
        await using(var member=c.CreateCommand())
        {
            member.Transaction=tx;
            member.CommandText="""
                SELECT g.name FROM v8_signal_groups g
                JOIN v8_signal_accounts a ON a.account=g.account
                JOIN v8_selected_groups s ON s.group_id=g.group_id
                LEFT JOIN v8_account_settings setting ON setting.account=a.account
                WHERE g.account=$account AND g.group_id=$group AND g.is_member=1
                  AND a.enabled=1 AND a.online=1
                  AND COALESCE(setting.enabled,1)=1 LIMIT 1;
                """;
            member.Parameters.AddWithValue("$account",request.Account);
            member.Parameters.AddWithValue("$group",request.GroupId);
            groupName=(string?)await member.ExecuteScalarAsync(ct)
                ??throw new ArgumentException(
                    "账号未启用/已离线，或者该群不属于此账号、尚未勾选。请刷新账号和群组。");
        }

        var jobId=Guid.NewGuid().ToString("N");
        var id=new DispatchIdentity(jobId,Guid.NewGuid().ToString("N"),
            0,0,request.GroupId,request.Account,"");
        var message="[SignalScheduler 实发测试] 这是一条经操作者确认的单条测试消息；请勿回复。"+
            $" 测试编号：{jobId[..8]}";
        var hash=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
        id=id with {PayloadHash=hash};

        await using(var job=c.CreateCommand())
        {
            job.Transaction=tx;
            job.CommandText="""
                INSERT INTO v8_jobs(job_id,state,cursor,updated_at)
                VALUES($j,'Running',0,$now);
                """;
            job.Parameters.AddWithValue("$j",jobId);
            job.Parameters.AddWithValue("$now",now.ToUnixTimeSeconds());
            await job.ExecuteNonQueryAsync(ct);
        }
        await using(var probe=c.CreateCommand())
        {
            probe.Transaction=tx;
            probe.CommandText="""
                INSERT INTO v8_live_probe_jobs(
                    job_id,group_id,account_id,group_name,created_at)
                VALUES($j,$group,$account,$name,$now);
                """;
            probe.Parameters.AddWithValue("$j",jobId);
            probe.Parameters.AddWithValue("$group",request.GroupId);
            probe.Parameters.AddWithValue("$account",request.Account);
            probe.Parameters.AddWithValue("$name",groupName);
            probe.Parameters.AddWithValue("$now",now.ToUnixTimeSeconds());
            await probe.ExecuteNonQueryAsync(ct);
        }
        await using(var log=c.CreateCommand())
        {
            log.Transaction=tx;
            log.CommandText="""
                INSERT INTO v8_event_log(job_id,event_type,detail,created_at)
                VALUES($j,'live_probe_user_confirmed',
                       '操作者已确认向已勾选测试群发送一条真实诊断消息。',$now);
                """;
            log.Parameters.AddWithValue("$j",jobId);
            log.Parameters.AddWithValue("$now",now.ToUnixTimeSeconds());
            await log.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
        return (id,groupName,message);
    }

    public async Task InitializeLiveProbeAsync(CancellationToken ct)
    {
        await using var c=Open();
        await using var cmd=c.CreateCommand();
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS v8_live_probe_jobs(
                job_id TEXT PRIMARY KEY REFERENCES v8_jobs(job_id) ON DELETE CASCADE,
                group_id TEXT NOT NULL,
                account_id TEXT NOT NULL,
                group_name TEXT NOT NULL,
                created_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_v8_live_probe_created
              ON v8_live_probe_jobs(created_at DESC);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task FinishLiveProbeAsync(
        string jobId,bool confirmed,string detail,CancellationToken ct)
    {
        await using var c=Open();
        using var tx=c.BeginTransaction();
        var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using(var cmd=c.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText=confirmed
                ?"""
                  UPDATE v8_jobs SET state='Completed',updated_at=$now
                  WHERE job_id=$j AND state='Running' AND cursor=1
                    AND EXISTS(
                      SELECT 1 FROM v8_dispatch_journal d WHERE d.job_id=$j
                      AND d.state='Confirmed'
                    );
                  """
                :"""
                  UPDATE v8_jobs SET state='Failed',updated_at=$now
                  WHERE job_id=$j AND state='Running' AND cursor=0
                    AND NOT EXISTS(
                      SELECT 1 FROM v8_dispatch_journal d WHERE d.job_id=$j
                      AND d.state IN ('Sending','Unknown','RecoveryRequired')
                    );
                  """;
            cmd.Parameters.AddWithValue("$j",jobId);
            cmd.Parameters.AddWithValue("$now",now);
            if(await cmd.ExecuteNonQueryAsync(ct)!=1)
            {
                if(confirmed)
                    throw new InvalidOperationException("实发确认已返回，但不能安全结束任务，请检查恢复中心。");
                // A failure with an in-flight/ambiguous journal must remain in
                // RecoveryRequired and cannot be disguised as Failed.
            }
        }
        await using(var log=c.CreateCommand())
        {
            log.Transaction=tx;
            log.CommandText="""
                INSERT INTO v8_event_log(job_id,event_type,detail,created_at)
                VALUES($j,$event,$detail,$now);
                """;
            log.Parameters.AddWithValue("$j",jobId);
            log.Parameters.AddWithValue("$event",confirmed?
                "live_probe_confirmed":"live_probe_stopped_or_ambiguous");
            log.Parameters.AddWithValue("$detail",
                detail.Length>400?detail[..400]:detail);
            log.Parameters.AddWithValue("$now",now);
            await log.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
    }

    public async Task<LiveProbeResult> GetLiveProbeResultAsync(
        string jobId,CancellationToken ct)
    {
        await using var c=Open();
        await using var q=c.CreateCommand();
        q.CommandText="""
            SELECT p.group_name,p.account_id,j.state,
                COALESCE((SELECT d.detail FROM v8_dispatch_journal d
                  WHERE d.job_id=j.job_id ORDER BY d.updated_at DESC LIMIT 1),''),
                (SELECT d.provider_message_id FROM v8_dispatch_journal d
                  WHERE d.job_id=j.job_id ORDER BY d.updated_at DESC LIMIT 1)
            FROM v8_jobs j JOIN v8_live_probe_jobs p ON p.job_id=j.job_id
            WHERE j.job_id=$j;
            """;
        q.Parameters.AddWithValue("$j",jobId);
        await using var r=await q.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct)) throw new KeyNotFoundException("找不到该测试发送记录。");
        return new LiveProbeResult(jobId,r.GetString(0),r.GetString(1),
            r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:r.GetString(4));
    }
}
