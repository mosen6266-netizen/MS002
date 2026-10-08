using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Persistence;

public sealed partial class StateStore
{
    public sealed record DueLivePilot(
        DispatchIdentity Dispatch,string Text,string ScriptName,string GroupName);

    public async Task InitializeLivePilotAsync(CancellationToken ct)
    {
        await using var c=Open();
        await using var q=c.CreateCommand();
        q.CommandText="""
            CREATE TABLE IF NOT EXISTS v8_live_pilot_plans(
                job_id TEXT PRIMARY KEY REFERENCES v8_jobs(job_id) ON DELETE CASCADE,
                script_id TEXT NOT NULL,
                script_name TEXT NOT NULL,
                group_id TEXT NOT NULL,
                group_name TEXT NOT NULL,
                account_id TEXT NOT NULL,
                run_token TEXT NOT NULL,
                steps_json TEXT NOT NULL,
                total_steps INTEGER NOT NULL,
                next_due_ms INTEGER NOT NULL,
                detail TEXT NOT NULL,
                created_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_v8_live_pilot_due
              ON v8_live_pilot_plans(next_due_ms);
            """;
        await q.ExecuteNonQueryAsync(ct);
    }

    public async Task<LivePilotItem> PlanLivePilotAsync(
        LivePilotPlanRequest request,CancellationToken ct)
    {
        if(request is null || !request.ConfirmRealSend ||
           string.IsNullOrWhiteSpace(request.ScriptId) ||
           string.IsNullOrWhiteSpace(request.Account) ||
           string.IsNullOrWhiteSpace(request.GroupId))
            throw new ArgumentException("必须手动选择剧本、单个测试群、单个账号，并同意实发。");

        var doc=await ReadEditorScriptAsync(request.ScriptId,ct);
        if(doc.Steps.Count is <1 or >3)
            throw new ArgumentException("受控实发一次最多 3 条文本。请另存一份测试剧本。");
        if(doc.Steps.Any(x=>string.IsNullOrWhiteSpace(x.Message) ||
            x.Message.Length>300 || !string.IsNullOrWhiteSpace(x.Attachment)))
            throw new ArgumentException(
                "测试剧本仅支持 1～3 条文本（每条最多 300 字），暂不支持图片实发。");
        if(doc.Steps.Any(x=>!string.IsNullOrWhiteSpace(x.Account) &&
                            x.Account!=request.Account))
            throw new ArgumentException(
                "本轮仅允许一个账号，请将剧本中各条消息的账号设置为所选账号或留空。");

        await InitializeLivePilotAsync(ct);
        var now=DateTimeOffset.UtcNow;
        await using var c=Open();
        using var tx=c.BeginTransaction();
        string name;
        await using(var group=c.CreateCommand())
        {
            group.Transaction=tx;
            group.CommandText="""
                SELECT g.name FROM v8_signal_groups g
                JOIN v8_signal_accounts a ON a.account=g.account
                JOIN v8_selected_groups s ON s.group_id=g.group_id
                LEFT JOIN v8_account_settings setting ON setting.account=g.account
                WHERE g.group_id=$group AND g.account=$account AND g.is_member=1
                  AND a.online=1 AND a.enabled=1 AND COALESCE(setting.enabled,1)=1
                LIMIT 1;
                """;
            group.Parameters.AddWithValue("$group",request.GroupId);
            group.Parameters.AddWithValue("$account",request.Account);
            name=(string?)await group.ExecuteScalarAsync(ct)
                ??throw new ArgumentException("测试群尚未勾选，或发送账号未启用/未加入群组。");
        }
        await using(var unresolved=c.CreateCommand())
        {
            unresolved.Transaction=tx;
            unresolved.CommandText="""
                SELECT 1 FROM v8_dispatch_journal
                WHERE state IN ('Prepared','Sending','Unknown','RecoveryRequired')
                LIMIT 1;
                """;
            if(await unresolved.ExecuteScalarAsync(ct) is not null)
                throw new InvalidOperationException("存在未确认发送记录，禁止再创建自动实发任务。");
        }
        await using(var running=c.CreateCommand())
        {
            running.Transaction=tx;
            running.CommandText="""
                SELECT 1 FROM v8_jobs j
                JOIN v8_live_pilot_plans p ON j.job_id=p.job_id
                WHERE j.state IN ('Running','Paused','RecoveryRequired') LIMIT 1;
                """;
            if(await running.ExecuteScalarAsync(ct) is not null)
                throw new InvalidOperationException(
                    "已有实发剧本任务尚未结束；必须先完成、停止或人工核对。");
        }
        await using(var cooldown=c.CreateCommand())
        {
            cooldown.Transaction=tx;
            cooldown.CommandText="""
                SELECT 1 FROM (
                  SELECT created_at FROM v8_live_pilot_plans
                  UNION ALL SELECT created_at FROM v8_live_probe_jobs
                ) WHERE created_at>=$last LIMIT 1;
                """;
            cooldown.Parameters.AddWithValue("$last",now.ToUnixTimeSeconds()-90);
            if(await cooldown.ExecuteScalarAsync(ct) is not null)
                throw new InvalidOperationException(
                    "最近 90 秒已有实发测试，避免短时间内重复发送。");
        }

        var jobId=Guid.NewGuid().ToString("N");
        var token=Guid.NewGuid().ToString("N");
        var steps=doc.Steps.Select((x,i)=>x with {
            Position=i,Account=request.Account
        }).ToArray();
        await using(var job=c.CreateCommand())
        {
            job.Transaction=tx;
            job.CommandText="""
                INSERT INTO v8_jobs(job_id,state,cursor,updated_at)
                VALUES($job,'Running',0,$now);
                """;
            job.Parameters.AddWithValue("$job",jobId);
            job.Parameters.AddWithValue("$now",now.ToUnixTimeSeconds());
            await job.ExecuteNonQueryAsync(ct);
        }
        await using(var plan=c.CreateCommand())
        {
            plan.Transaction=tx;
            plan.CommandText="""
                INSERT INTO v8_live_pilot_plans(
                    job_id,script_id,script_name,group_id,group_name,account_id,
                    run_token,steps_json,total_steps,next_due_ms,detail,created_at)
                VALUES($job,$script,$scriptname,$group,$name,$account,
                    $token,$steps,$count,$due,'受控自动实发：最多 3 条文本',
                    $now);
                """;
            plan.Parameters.AddWithValue("$job",jobId);
            plan.Parameters.AddWithValue("$script",request.ScriptId);
            plan.Parameters.AddWithValue("$scriptname",doc.Name);
            plan.Parameters.AddWithValue("$group",request.GroupId);
            plan.Parameters.AddWithValue("$name",name);
            plan.Parameters.AddWithValue("$account",request.Account);
            plan.Parameters.AddWithValue("$token",token);
            plan.Parameters.AddWithValue("$steps",JsonSerializer.Serialize(steps));
            plan.Parameters.AddWithValue("$count",steps.Length);
            plan.Parameters.AddWithValue("$due",now.ToUnixTimeMilliseconds());
            plan.Parameters.AddWithValue("$now",now.ToUnixTimeSeconds());
            await plan.ExecuteNonQueryAsync(ct);
        }
        await using(var ev=c.CreateCommand())
        {
            ev.Transaction=tx;
            ev.CommandText="""
                INSERT INTO v8_event_log(job_id,event_type,detail,created_at)
                VALUES($job,'live_pilot_approved',
                    '操作者二次确认：单账号、单群、最多3条真实测试文本。',$now);
                """;
            ev.Parameters.AddWithValue("$job",jobId);
            ev.Parameters.AddWithValue("$now",now.ToUnixTimeSeconds());
            await ev.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
        return (await ListLivePilotsAsync(ct)).Single(x=>x.JobId==jobId);
    }

    static string TestMessage(string message,int number,string jobId)=>
        $"[SignalScheduler 实发测试] [{number}] {message}\n测试编号：{jobId[..8]}";

    public async Task<DueLivePilot?> FindDueLivePilotAsync(
        long currentMs,CancellationToken ct)
    {
        await using var c=Open();
        await using var q=c.CreateCommand();
        q.CommandText="""
            SELECT p.job_id,p.run_token,p.group_id,p.account_id,p.steps_json,
                j.cursor,p.total_steps,p.script_name,p.group_name
            FROM v8_live_pilot_plans p JOIN v8_jobs j ON j.job_id=p.job_id
            WHERE j.state='Running' AND p.next_due_ms<=$now
            ORDER BY p.next_due_ms,p.created_at LIMIT 1;
            """;
        q.Parameters.AddWithValue("$now",currentMs);
        await using var r=await q.ExecuteReaderAsync(ct);
        if(!await r.ReadAsync(ct))return null;
        var id=r.GetString(0);
        var token=r.GetString(1);
        var groupId=r.GetString(2);
        var account=r.GetString(3);
        var steps=JsonSerializer.Deserialize<ScriptEditorStep[]>(r.GetString(4));
        var cursor=r.GetInt64(5);
        var total=r.GetInt32(6);
        if(steps is null || steps.Length!=total || cursor<0 || cursor>=total)
            throw new InvalidDataException("任务快照和进度不匹配，禁止发送。");
        var step=steps[(int)cursor];
        var message=TestMessage(step.Message,(int)cursor+1,id);
        var hash=Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
        var identity=new DispatchIdentity(
            id,token,0,cursor,groupId,account,hash);
        return new DueLivePilot(identity,message,r.GetString(7),r.GetString(8));
    }

    public async Task ConfirmLivePilotStepAsync(
        DispatchIdentity dispatch,long nowMs,CancellationToken ct)
    {
        await using var c=Open();
        using var tx=c.BeginTransaction();
        string json;
        int total;
        string state;
        await using(var query=c.CreateCommand())
        {
            query.Transaction=tx;
            query.CommandText="""
                SELECT p.steps_json,p.total_steps,j.state FROM v8_live_pilot_plans p
                JOIN v8_jobs j ON j.job_id=p.job_id
                WHERE j.job_id=$job AND j.cursor=$cursor;
                """;
            query.Parameters.AddWithValue("$job",dispatch.JobId);
            query.Parameters.AddWithValue("$cursor",dispatch.Cursor+1);
            await using var r=await query.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct))
                throw new InvalidOperationException("发送结果提交后任务游标异常，要求人工核对。");
            json=r.GetString(0);total=r.GetInt32(1);state=r.GetString(2);
        }

        var steps=JsonSerializer.Deserialize<ScriptEditorStep[]>(json)
            ??throw new InvalidDataException("任务快照损坏。");
        if(steps.Length!=total || dispatch.Cursor>=steps.Length)
            throw new InvalidDataException("任务顺序数据异常。");
        var step=steps[(int)dispatch.Cursor];
        var done=dispatch.Cursor+1==total;
        var manualPause=!done&&step.PauseAfter;
        var nextState=done?"Completed":state=="Paused"||manualPause?"Paused":"Running";
        var detail=done
            ?"受控真实剧本测试完成；仅代表每条有 Signal RPC 确认。"
            :manualPause
                ?$"第 {dispatch.Cursor+1} 条真实发送已确认，按剧本设置暂停。{step.ReminderText}"
                :$"第 {dispatch.Cursor+1} 条真实发送已确认，按预定间隔等待下一条。";

        await using(var job=c.CreateCommand())
        {
            job.Transaction=tx;
            job.CommandText="""
                UPDATE v8_jobs SET state=$state,updated_at=$now
                WHERE job_id=$job AND cursor=$cursor
                  AND state IN ('Running','Paused');
                """;
            job.Parameters.AddWithValue("$state",nextState);
            job.Parameters.AddWithValue("$now",nowMs/1000);
            job.Parameters.AddWithValue("$job",dispatch.JobId);
            job.Parameters.AddWithValue("$cursor",dispatch.Cursor+1);
            if(await job.ExecuteNonQueryAsync(ct)!=1)
                throw new InvalidOperationException("任务在确认后被并发修改，请人工核对。");
        }
        await using(var p=c.CreateCommand())
        {
            p.Transaction=tx;
            p.CommandText="""
                UPDATE v8_live_pilot_plans
                SET detail=$detail,next_due_ms=$due WHERE job_id=$job;
                """;
            p.Parameters.AddWithValue("$detail",detail);
            p.Parameters.AddWithValue("$due",done?0:
                nowMs+Math.Max(15000L,
                    (long)(step.TypingSeconds+step.DelayAfter)*1000));
            p.Parameters.AddWithValue("$job",dispatch.JobId);
            await p.ExecuteNonQueryAsync(ct);
        }
        await using(var ev=c.CreateCommand())
        {
            ev.Transaction=tx;
            ev.CommandText="""
                INSERT INTO v8_event_log(job_id,dispatch_key,event_type,detail,created_at)
                VALUES($job,$key,$type,$detail,$now);
                """;
            ev.Parameters.AddWithValue("$job",dispatch.JobId);
            ev.Parameters.AddWithValue("$key",dispatch.DispatchKey);
            ev.Parameters.AddWithValue("$type",done?"live_pilot_completed":
                manualPause?"live_pilot_paused":"live_pilot_step_confirmed");
            ev.Parameters.AddWithValue("$detail",detail);
            ev.Parameters.AddWithValue("$now",nowMs/1000);
            await ev.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
    }

    public async Task PauseLivePilotForSafetyAsync(
        string jobId,string detail,CancellationToken ct)
    {
        await using var c=Open();
        using var tx=c.BeginTransaction();
        await using(var job=c.CreateCommand())
        {
            job.Transaction=tx;
            job.CommandText="""
                UPDATE v8_jobs SET state='Paused',updated_at=$now
                WHERE job_id=$job AND state='Running';
                """;
            job.Parameters.AddWithValue("$job",jobId);
            job.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await job.ExecuteNonQueryAsync(ct);
        }
        await using(var p=c.CreateCommand())
        {
            p.Transaction=tx;
            p.CommandText="UPDATE v8_live_pilot_plans SET detail=$d WHERE job_id=$job";
            p.Parameters.AddWithValue("$d",detail);
            p.Parameters.AddWithValue("$job",jobId);
            await p.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
    }

    public async Task<LivePilotItem> ControlLivePilotAsync(
        LivePilotControlRequest request,CancellationToken ct)
    {
        if(request is null || string.IsNullOrWhiteSpace(request.JobId) ||
           request.Action is not ("pause" or "resume" or "stop"))
            throw new ArgumentException("任务编号或操作无效。");

        await using var c=Open();
        using var tx=c.BeginTransaction();
        string state;
        long cursor;
        int count;
        await using(var q=c.CreateCommand())
        {
            q.Transaction=tx;
            q.CommandText="""
                SELECT j.state,j.cursor,p.total_steps FROM v8_jobs j
                JOIN v8_live_pilot_plans p ON p.job_id=j.job_id
                WHERE j.job_id=$id;
                """;
            q.Parameters.AddWithValue("$id",request.JobId);
            await using var r=await q.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct))
                throw new KeyNotFoundException("没有找到这个实发测试任务。");
            state=r.GetString(0);cursor=r.GetInt64(1);count=r.GetInt32(2);
        }
        await using(var inFlight=c.CreateCommand())
        {
            inFlight.Transaction=tx;
            inFlight.CommandText="""
                SELECT 1 FROM v8_dispatch_journal
                WHERE job_id=$id AND state IN (
                    'Prepared','Sending','Unknown','RecoveryRequired') LIMIT 1;
                """;
            inFlight.Parameters.AddWithValue("$id",request.JobId);
            if(await inFlight.ExecuteScalarAsync(ct) is not null)
                throw new InvalidOperationException(
                    "这条任务正在发送或发送结果未知，禁止跳过消息；请在恢复中心核对。");
        }

        var next=request.Action switch
        {
            "pause" when state=="Running"=>"Paused",
            "resume" when state=="Paused"&&cursor<count=>"Running",
            "stop" when state is "Running" or "Paused"=>"Stopped",
            _=>throw new InvalidOperationException(
                $"任务当前为 {state}，不能执行 {request.Action}。")
        };
        var now=DateTimeOffset.UtcNow;
        await using(var job=c.CreateCommand())
        {
            job.Transaction=tx;
            job.CommandText="""
                UPDATE v8_jobs SET state=$next,updated_at=$now
                WHERE job_id=$id AND state=$old AND cursor=$cursor;
                """;
            job.Parameters.AddWithValue("$next",next);
            job.Parameters.AddWithValue("$now",now.ToUnixTimeSeconds());
            job.Parameters.AddWithValue("$id",request.JobId);
            job.Parameters.AddWithValue("$old",state);
            job.Parameters.AddWithValue("$cursor",cursor);
            if(await job.ExecuteNonQueryAsync(ct)!=1)
                throw new InvalidOperationException("任务状态已经变化，请刷新。");
        }
        var detail=request.Action switch{
            "pause"=>"操作者暂停真实剧本测试。",
            "resume"=>"操作者手动继续真实剧本测试（没有重发已确认消息）。",
            _=>"操作者停止本次真实剧本测试；不会自动恢复。"
        };
        await using(var p=c.CreateCommand())
        {
            p.Transaction=tx;
            p.CommandText="""
                UPDATE v8_live_pilot_plans
                SET detail=$detail,
                    next_due_ms=CASE WHEN $action='resume' THEN $due ELSE next_due_ms END
                WHERE job_id=$id;
                """;
            p.Parameters.AddWithValue("$detail",detail);
            p.Parameters.AddWithValue("$action",request.Action);
            p.Parameters.AddWithValue("$due",now.ToUnixTimeMilliseconds());
            p.Parameters.AddWithValue("$id",request.JobId);
            await p.ExecuteNonQueryAsync(ct);
        }
        await using(var ev=c.CreateCommand())
        {
            ev.Transaction=tx;
            ev.CommandText="""
                INSERT INTO v8_event_log(job_id,event_type,detail,created_at)
                VALUES($id,$type,$detail,$now);
                """;
            ev.Parameters.AddWithValue("$id",request.JobId);
            ev.Parameters.AddWithValue("$type","live_pilot_"+request.Action);
            ev.Parameters.AddWithValue("$detail",detail);
            ev.Parameters.AddWithValue("$now",now.ToUnixTimeSeconds());
            await ev.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
        return (await ListLivePilotsAsync(ct)).Single(x=>x.JobId==request.JobId);
    }

    public async Task<IReadOnlyList<LivePilotItem>> ListLivePilotsAsync(CancellationToken ct)
    {
        await InitializeLivePilotAsync(ct);
        await using var c=Open();
        var rows=new List<LivePilotItem>();
        await using var q=c.CreateCommand();
        q.CommandText="""
            SELECT p.job_id,p.script_name,p.group_name,p.account_id,
                j.state,j.cursor,p.total_steps,p.next_due_ms,p.detail
            FROM v8_live_pilot_plans p JOIN v8_jobs j ON j.job_id=p.job_id
            ORDER BY p.created_at DESC,p.job_id LIMIT 100;
            """;
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
            rows.Add(new LivePilotItem(
                r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),
                r.GetString(4),r.GetInt64(5),r.GetInt32(6),r.GetInt64(7),
                r.GetString(8)));
        return rows;
    }
}
