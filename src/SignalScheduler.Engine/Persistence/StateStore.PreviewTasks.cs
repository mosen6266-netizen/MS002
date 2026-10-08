using System.Text.Json;
using Microsoft.Data.Sqlite;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Persistence;

public sealed partial class StateStore
{
    /// <summary>
    /// Rehearsal-only plans. No Signal send is ever performed by this subsystem.
    /// A script is frozen into JSON on creation so edits during a run do not
    /// change its message count or scheduling. V7 originals remain untouched.
    /// </summary>
    public async Task InitializePreviewTasksAsync(CancellationToken ct)
    {
        await using var c=Open();
        await using var cmd=c.CreateCommand();
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS v8_preview_plans(
                job_id TEXT PRIMARY KEY REFERENCES v8_jobs(job_id) ON DELETE CASCADE,
                script_id TEXT NOT NULL,
                script_name TEXT NOT NULL,
                group_id TEXT NOT NULL,
                group_name TEXT NOT NULL,
                steps_json TEXT NOT NULL,
                total_steps INTEGER NOT NULL,
                next_due_ms INTEGER NOT NULL,
                detail TEXT NOT NULL DEFAULT '',
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_v8_preview_due
              ON v8_preview_plans(next_due_ms,job_id);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<PreviewTaskPlanResult> PlanPreviewTasksAsync(
        PreviewTaskPlanRequest request,CancellationToken ct)
    {
        if(request is null ||
            string.IsNullOrWhiteSpace(request.ScriptId) ||
            request.ScriptId.Length>256 ||
            request.GroupIds is null ||
            request.GroupIds.Count is <1 or >100 ||
            request.GroupIds.Any(x=>string.IsNullOrWhiteSpace(x) || x.Length>512))
            throw new ArgumentException("请选择一个剧本和 1～100 个群组。");

        var groupIds=request.GroupIds.Distinct(StringComparer.Ordinal).ToArray();
        var script=await ReadEditorScriptAsync(request.ScriptId,ct);
        if(script.Steps.Count==0)
            throw new ArgumentException("剧本没有消息，请先编辑并保存。");
        if(script.Steps.Any(x=>string.IsNullOrWhiteSpace(x.Message) &&
                                   string.IsNullOrWhiteSpace(x.Attachment)))
            throw new ArgumentException("剧本中包含空白消息，请先补齐内容。");

        await InitializePreviewTasksAsync(ct);
        await InitializeAccountManagementAsync(ct);
        var now=DateTimeOffset.UtcNow;
        var created=new List<string>();

        await using var c=Open();
        using var tx=c.BeginTransaction();
        foreach(var groupId in groupIds)
        {
            string groupName;
            await using(var group=c.CreateCommand())
            {
                group.Transaction=tx;
                group.CommandText="""
                    SELECT g.name FROM v8_signal_groups g
                    JOIN v8_selected_groups s ON s.group_id=g.group_id
                    WHERE g.group_id=$g AND g.is_member=1
                    ORDER BY g.name LIMIT 1;
                    """;
                group.Parameters.AddWithValue("$g",groupId);
                groupName=(string?)await group.ExecuteScalarAsync(ct)
                    ??throw new ArgumentException(
                        "存在未勾选或尚未同步的 Signal 群，请在群组管理中刷新、勾选并保存。");
            }

            var eligible=new List<string>();
            await using(var q=c.CreateCommand())
            {
                q.Transaction=tx;
                q.CommandText="""
                    SELECT DISTINCT a.account FROM v8_signal_accounts a
                    JOIN v8_signal_groups g ON g.account=a.account
                    LEFT JOIN v8_account_settings p ON p.account=a.account
                    WHERE g.group_id=$g AND g.is_member=1 AND a.online=1
                      AND a.enabled=1 AND COALESCE(p.enabled,1)=1
                    ORDER BY a.account;
                    """;
                q.Parameters.AddWithValue("$g",groupId);
                await using var r=await q.ExecuteReaderAsync(ct);
                while(await r.ReadAsync(ct)) eligible.Add(r.GetString(0));
            }
            if(eligible.Count==0)
                throw new InvalidOperationException(
                    $"群组「{groupName}」没有已启用且在线的成员账号。");

            var chosen=new List<ScriptEditorStep>(script.Steps.Count);
            for(var i=0;i<script.Steps.Count;i++)
            {
                var step=script.Steps[i];
                var account=string.IsNullOrWhiteSpace(step.Account)
                    ?eligible[i%eligible.Count]
                    :step.Account;
                if(!eligible.Contains(account,StringComparer.Ordinal))
                    throw new ArgumentException(
                        $"群组「{groupName}」第 {i+1} 条指定的账号不可用或不是群成员。");
                chosen.Add(step with {Account=account,Position=i});
            }

            // Multiple groups are inserted atomically. If any validation
            // fails no partially created tasks can remain in the database.
            var id=Guid.NewGuid().ToString("N");
            await using(var job=c.CreateCommand())
            {
                job.Transaction=tx;
                job.CommandText="""
                    INSERT INTO v8_jobs(job_id,state,cursor,updated_at)
                    VALUES($j,'Running',0,$n);
                    """;
                job.Parameters.AddWithValue("$j",id);
                job.Parameters.AddWithValue("$n",now.ToUnixTimeSeconds());
                await job.ExecuteNonQueryAsync(ct);
            }
            await using(var plan=c.CreateCommand())
            {
                plan.Transaction=tx;
                plan.CommandText="""
                    INSERT INTO v8_preview_plans(
                        job_id,script_id,script_name,group_id,group_name,
                        steps_json,total_steps,next_due_ms,detail,created_at,updated_at)
                    VALUES($j,$script,$name,$g,$group,$steps,$count,$due,
                           '预演已创建：所有消息仅模拟，不会实际发出。',$n,$n);
                    """;
                plan.Parameters.AddWithValue("$j",id);
                plan.Parameters.AddWithValue("$script",script.ScriptId);
                plan.Parameters.AddWithValue("$name",script.Name);
                plan.Parameters.AddWithValue("$g",groupId);
                plan.Parameters.AddWithValue("$group",groupName);
                plan.Parameters.AddWithValue("$steps",JsonSerializer.Serialize(chosen));
                plan.Parameters.AddWithValue("$count",chosen.Count);
                plan.Parameters.AddWithValue("$due",now.ToUnixTimeMilliseconds());
                plan.Parameters.AddWithValue("$n",now.ToUnixTimeSeconds());
                await plan.ExecuteNonQueryAsync(ct);
            }
            await using(var log=c.CreateCommand())
            {
                log.Transaction=tx;
                log.CommandText="""
                    INSERT INTO v8_event_log(job_id,event_type,detail,created_at)
                    VALUES($j,'preview_created','预演任务创建（不会调用 Signal）',$n);
                    """;
                log.Parameters.AddWithValue("$j",id);
                log.Parameters.AddWithValue("$n",now.ToUnixTimeSeconds());
                await log.ExecuteNonQueryAsync(ct);
            }
            created.Add(id);
        }
        tx.Commit();
        return new PreviewTaskPlanResult(created.Count,created);
    }

    public async Task<IReadOnlyList<PreviewTaskItem>> ListPreviewTasksAsync(CancellationToken ct)
    {
        await InitializePreviewTasksAsync(ct);
        await using var c=Open();
        var results=new List<PreviewTaskItem>();
        await using var q=c.CreateCommand();
        q.CommandText="""
            SELECT p.job_id,p.script_name,p.script_id,p.group_id,p.group_name,
                   j.state,j.cursor,p.total_steps,p.next_due_ms,p.detail
            FROM v8_preview_plans p JOIN v8_jobs j ON j.job_id=p.job_id
            ORDER BY p.created_at DESC,p.job_id LIMIT 500;
            """;
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
            results.Add(new PreviewTaskItem(
                r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),
                r.GetString(4),r.GetString(5),r.GetInt64(6),r.GetInt32(7),
                r.GetInt64(8),r.GetString(9)));
        return results;
    }

    /// <summary>
    /// Pause/resume/stop are CAS transitions, and a recovery-needed send can
    /// never be bypassed by pressing resume. This applies only to preview jobs.
    /// All actions are persisted, including explicit user pauses.
    /// </summary>
    public async Task<PreviewTaskItem> ControlPreviewTaskAsync(
        PreviewTaskControlRequest request,CancellationToken ct)
    {
        if(request is null || string.IsNullOrWhiteSpace(request.JobId) ||
            request.JobId.Length>256 || request.Action is not
                ("pause" or "resume" or "stop"))
            throw new ArgumentException("任务编号或控制指令无效。");

        await using var c=Open();
        using var tx=c.BeginTransaction();
        string state;
        long cursor;
        int total;
        await using(var query=c.CreateCommand())
        {
            query.Transaction=tx;
            query.CommandText="""
                SELECT j.state,j.cursor,p.total_steps FROM v8_jobs j
                JOIN v8_preview_plans p ON p.job_id=j.job_id
                WHERE j.job_id=$j LIMIT 1;
                """;
            query.Parameters.AddWithValue("$j",request.JobId);
            await using var r=await query.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct)) throw new KeyNotFoundException("未找到预演任务。");
            state=r.GetString(0);
            cursor=r.GetInt64(1);
            total=r.GetInt32(2);
        }

        string next;
        switch(request.Action)
        {
            case "pause" when state=="Running":
                next="Paused";
                break;
            case "pause" when state=="Paused":
                next="Paused";
                break;
            case "resume" when state=="Paused" && cursor<total:
                next="Running";
                break;
            case "stop" when state is "Running" or "Paused" or "Created":
                next="Stopped";
                break;
            default:
                throw new InvalidOperationException(
                    $"当前状态为 {state}，不允许执行 {request.Action}。未知结果需要人工核对，不能通过恢复跳过。");
        }

        // A native message journal in an ambiguous state takes precedence.
        // Rehearsal tasks should not have one; this protects against corrupt
        // or hand-modified databases.
        await using(var unresolved=c.CreateCommand())
        {
            unresolved.Transaction=tx;
            unresolved.CommandText="""
                SELECT 1 FROM v8_dispatch_journal
                WHERE job_id=$j AND state IN (
                    'Prepared','Sending','Unknown','RecoveryRequired')
                LIMIT 1;
                """;
            unresolved.Parameters.AddWithValue("$j",request.JobId);
            if(await unresolved.ExecuteScalarAsync(ct) is not null)
                throw new InvalidOperationException("检测到待核对的发送记录，不允许直接变更任务状态。");
        }

        var now=DateTimeOffset.UtcNow;
        await using(var cmd=c.CreateCommand())
        {
            cmd.Transaction=tx;
            cmd.CommandText="""
                UPDATE v8_jobs SET state=$next,updated_at=$n
                WHERE job_id=$j AND state=$old AND cursor=$cursor;
                """;
            cmd.Parameters.AddWithValue("$j",request.JobId);
            cmd.Parameters.AddWithValue("$next",next);
            cmd.Parameters.AddWithValue("$old",state);
            cmd.Parameters.AddWithValue("$cursor",cursor);
            cmd.Parameters.AddWithValue("$n",now.ToUnixTimeSeconds());
            if(await cmd.ExecuteNonQueryAsync(ct)!=1)
                throw new InvalidOperationException("任务刚被其他操作改变，请刷新状态。");
        }
        var detail=request.Action switch{
            "pause"=>"用户手动暂停预演。",
            "resume"=>"用户手动继续预演（未发送任何消息）。",
            _=>"用户手动停止预演，不能再次继续。"
        };
        await using(var plan=c.CreateCommand())
        {
            plan.Transaction=tx;
            plan.CommandText="""
                UPDATE v8_preview_plans SET detail=$d,
                next_due_ms=CASE WHEN $action='resume' THEN $now ELSE next_due_ms END,
                updated_at=$n WHERE job_id=$j;
                """;
            plan.Parameters.AddWithValue("$j",request.JobId);
            plan.Parameters.AddWithValue("$d",detail);
            plan.Parameters.AddWithValue("$action",request.Action);
            plan.Parameters.AddWithValue("$now",now.ToUnixTimeMilliseconds());
            plan.Parameters.AddWithValue("$n",now.ToUnixTimeSeconds());
            await plan.ExecuteNonQueryAsync(ct);
        }
        await using(var log=c.CreateCommand())
        {
            log.Transaction=tx;
            log.CommandText="""
                INSERT INTO v8_event_log(job_id,event_type,detail,created_at)
                VALUES($j,$ev,$d,$n);
                """;
            log.Parameters.AddWithValue("$j",request.JobId);
            log.Parameters.AddWithValue("$ev","preview_"+request.Action);
            log.Parameters.AddWithValue("$d",detail);
            log.Parameters.AddWithValue("$n",now.ToUnixTimeSeconds());
            await log.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
        return (await ListPreviewTasksAsync(ct)).Single(x=>x.JobId==request.JobId);
    }

    /// <summary>
    /// Atomically advance one due rehearsal message without any transport.
    /// A concurrent Pause or Stop wins if committed first. The cursor moves
    /// exactly once; unexpected SQLite/task errors leave the old cursor.
    /// </summary>
    public async Task<bool> AdvanceOneDuePreviewAsync(
        string jobId,long nowMs,CancellationToken ct)
    {
        await using var c=Open();
        using var tx=c.BeginTransaction();
        string data;
        long cursor;
        int count;
        await using(var q=c.CreateCommand())
        {
            q.Transaction=tx;
            q.CommandText="""
                SELECT p.steps_json,j.cursor,p.total_steps FROM v8_jobs j
                JOIN v8_preview_plans p ON p.job_id=j.job_id
                WHERE j.job_id=$j AND j.state='Running' AND p.next_due_ms<=$now;
                """;
            q.Parameters.AddWithValue("$j",jobId);
            q.Parameters.AddWithValue("$now",nowMs);
            await using var r=await q.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct)) return false;
            data=r.GetString(0);
            cursor=r.GetInt64(1);
            count=r.GetInt32(2);
        }

        var steps=JsonSerializer.Deserialize<ScriptEditorStep[]>(data)
            ??throw new InvalidDataException("任务快照已损坏。");
        if(count!=steps.Length || cursor<0 || cursor>=count)
            throw new InvalidDataException("任务消息位置与快照不一致。");

        var current=steps[(int)cursor];
        var completed=cursor+1>=count;
        var pause=!completed && current.PauseAfter;
        var newState=completed?"Completed":pause?"Paused":"Running";
        var detail=completed
            ?"预演完成：没有向 Signal 发出任何消息。"
            :pause
                ?$"第 {cursor+1} 条已预演，按剧本设置暂停。{current.ReminderText}"
                :$"已预演第 {cursor+1} 条（仅模拟发送）。";
        await using(var job=c.CreateCommand())
        {
            job.Transaction=tx;
            job.CommandText="""
                UPDATE v8_jobs SET cursor=$next,state=$state,updated_at=$t
                WHERE job_id=$j AND cursor=$old AND state='Running';
                """;
            job.Parameters.AddWithValue("$j",jobId);
            job.Parameters.AddWithValue("$next",cursor+1);
            job.Parameters.AddWithValue("$state",newState);
            job.Parameters.AddWithValue("$old",cursor);
            job.Parameters.AddWithValue("$t",nowMs/1000);
            if(await job.ExecuteNonQueryAsync(ct)!=1) return false;
        }

        await using(var plan=c.CreateCommand())
        {
            plan.Transaction=tx;
            plan.CommandText="""
                UPDATE v8_preview_plans SET next_due_ms=$due,detail=$d,updated_at=$n
                WHERE job_id=$j;
                """;
            plan.Parameters.AddWithValue("$due",completed?0:
                nowMs+(long)current.TypingSeconds*1000+(long)current.DelayAfter*1000);
            plan.Parameters.AddWithValue("$d",detail);
            plan.Parameters.AddWithValue("$n",nowMs/1000);
            plan.Parameters.AddWithValue("$j",jobId);
            await plan.ExecuteNonQueryAsync(ct);
        }

        await using(var log=c.CreateCommand())
        {
            log.Transaction=tx;
            log.CommandText="""
                INSERT INTO v8_event_log(job_id,event_type,detail,created_at)
                VALUES($j,$ev,$detail,$now);
                """;
            log.Parameters.AddWithValue("$j",jobId);
            log.Parameters.AddWithValue("$ev",completed?"preview_completed":
                pause?"preview_reminder":"preview_step");
            log.Parameters.AddWithValue("$detail",detail);
            log.Parameters.AddWithValue("$now",nowMs/1000);
            await log.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
        return true;
    }

    public async Task<int> RunDuePreviewBatchAsync(long nowMs,int maxJobs,CancellationToken ct)
    {
        maxJobs=Math.Clamp(maxJobs,1,100);
        await InitializePreviewTasksAsync(ct);
        var ids=new List<string>();
        await using(var c=Open())
        {
            await using var q=c.CreateCommand();
            q.CommandText="""
                SELECT p.job_id FROM v8_preview_plans p
                JOIN v8_jobs j ON j.job_id=p.job_id
                WHERE j.state='Running' AND p.next_due_ms<=$now
                ORDER BY p.next_due_ms,p.job_id LIMIT $lim;
                """;
            q.Parameters.AddWithValue("$now",nowMs);
            q.Parameters.AddWithValue("$lim",maxJobs);
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) ids.Add(r.GetString(0));
        }
        var processed=0;
        foreach(var id in ids)
            if(await AdvanceOneDuePreviewAsync(id,nowMs,ct)) processed++;
        return processed;
    }
}
