using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Persistence;

/// <summary>
/// One durable job per group; steps are frozen at the moment the operator
/// presses Start. Only explicitly selected groups may be targeted.
/// </summary>
public sealed partial class StateStore
{
    public sealed record DueLiveBatch(
        DispatchIdentity Dispatch,ScriptEditorStep Step,string ScriptName,string GroupName);

    public async Task InitializeLiveBatchAsync(CancellationToken ct)
    {
        await using var c=Open();
        await using var q=c.CreateCommand();
        q.CommandText="""
            CREATE TABLE IF NOT EXISTS v8_live_batch_jobs(
                job_id TEXT PRIMARY KEY REFERENCES v8_jobs(job_id) ON DELETE CASCADE,
                script_id TEXT NOT NULL,
                script_name TEXT NOT NULL,
                group_id TEXT NOT NULL,
                group_name TEXT NOT NULL,
                run_token TEXT NOT NULL,
                steps_json TEXT NOT NULL,
                total_steps INTEGER NOT NULL,
                next_due_ms INTEGER NOT NULL,
                detail TEXT NOT NULL,
                created_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_v8_live_batch_due
                ON v8_live_batch_jobs(next_due_ms,created_at);
            CREATE INDEX IF NOT EXISTS idx_v8_live_batch_group
                ON v8_live_batch_jobs(group_id);
            """;
        await q.ExecuteNonQueryAsync(ct);
    }

    private sealed record PreparedLiveBatchMedia(
        ScriptEditorStep[] Steps,IReadOnlyList<LiveBatchMediaIssue> Issues);

    public async Task<LiveBatchMediaInspection> InspectLiveBatchMediaAsync(
        LiveBatchMediaInspectionRequest request,CancellationToken ct)
    {
        if(request is null || string.IsNullOrWhiteSpace(request.ScriptId))
            throw new ArgumentException("请先选择剧本。");
        var script=await ReadEditorScriptAsync(request.ScriptId,ct);
        var media=await PrepareLiveBatchMediaAsync(script,true,ct);
        return new LiveBatchMediaInspection(
            script.Name,script.Steps.Count,media.Steps.Length,media.Issues);
    }

    // The old V7 scripts stored arbitrary local paths, while V8 requires
    // content-addressed img: references. If an old image is still reachable
    // on THIS computer, safely import it into the user-owned V8 attachments
    // directory; do not rewrite the source script.
    private async Task<PreparedLiveBatchMedia> PrepareLiveBatchMediaAsync(
        ScriptEditorDocument script,bool skipUnavailable,CancellationToken ct)
    {
        var steps=new List<ScriptEditorStep>();
        var issues=new List<LiveBatchMediaIssue>();
        foreach(var step in script.Steps)
        {
            if((step.Message?.Length??0)>16000)
                throw new ArgumentException(
                    $"剧本「{script.Name}」第 {step.Position+1} 条文字过长。");
            if(string.IsNullOrWhiteSpace(step.Message) &&
               string.IsNullOrWhiteSpace(step.Attachment))continue;
            if(string.IsNullOrWhiteSpace(step.Attachment))
            {
                steps.Add(step);
                continue;
            }

            try
            {
                string reference;
                if(step.Attachment.StartsWith("img:",StringComparison.Ordinal))
                {
                    var valid=await LookupImageAsync(
                        new ImageLookupRequest(step.Attachment),ct);
                    reference=valid.Reference;
                }
                else
                {
                    // Import accepts only an existing, fully-qualified image
                    // file and validates extension, signature, and SHA-256.
                    var imported=await ImportImageAsync(
                        new ImageImportRequest(step.Attachment),ct);
                    reference=imported.Reference;
                }
                steps.Add(step with {Attachment=reference});
            }
            catch(Exception ex) when(ex is ArgumentException or IOException or
                UnauthorizedAccessException or NotSupportedException)
            {
                var hasText=!string.IsNullOrWhiteSpace(step.Message);
                var action=hasText
                    ?"不发送旧图片，保留本条文字"
                    :"跳过仅含失效图片的这一条";
                issues.Add(new LiveBatchMediaIssue(step.Position+1,
                    $"第 {step.Position+1} 条图片不可用：{ex.Message}",action));
                if(!skipUnavailable)
                    throw new ArgumentException(
                        $"剧本「{script.Name}」第 {step.Position+1} 条图片不可用，"+
                        "请先查看运行前检查结果并确认如何处理。",ex);
                if(hasText)
                    steps.Add(step with {Attachment=""});
            }
        }
        if(steps.Count==0)
            throw new ArgumentException(
                $"剧本「{script.Name}」没有可发送的文字或有效图片，请先修复剧本。");
        return new PreparedLiveBatchMedia(steps.ToArray(),issues);
    }

    public async Task<LiveBatchStartResult> StartLiveBatchAsync(
        LiveBatchStartRequest request,CancellationToken ct)
    {
        if(request is null || !request.ConfirmRealSend ||
            string.IsNullOrWhiteSpace(request.ScriptId) ||
            request.GroupIds is null || request.GroupIds.Count is <1 or >20 ||
            request.GroupIds.Any(x=>string.IsNullOrWhiteSpace(x) || x.Length>512))
            throw new ArgumentException("请选择剧本、1～20 个已勾选群组并确认开始。");

        var groups=request.GroupIds.Distinct(StringComparer.Ordinal).ToArray();
        var script=await ReadEditorScriptAsync(request.ScriptId,ct);
        if(script.Steps.Count>1500)
            throw new ArgumentException($"剧本「{script.Name}」超过 1500 条的编辑上限。");

        // Empty editor bubbles are draft placeholders, not sendable messages.
        // Skip them only in a frozen task snapshot; never rewrite the draft.
        var resolved=await PrepareLiveBatchMediaAsync(
            script,request.SkipUnavailableImages,ct);
        var sendable=resolved.Steps;

        await InitializeLiveBatchAsync(ct);
        await InitializeAccountManagementAsync(ct);

        var now=DateTimeOffset.UtcNow;
        var created=new List<string>();
        await using var c=Open();
        using var tx=c.BeginTransaction();
        foreach(var groupId in groups)
        {
            var eligible=new List<string>();
            string? groupName=null;
            await using(var q=c.CreateCommand())
            {
                q.Transaction=tx;
                q.CommandText="""
                    SELECT a.account,g.name FROM v8_signal_groups g
                    JOIN v8_signal_accounts a ON a.account=g.account
                    LEFT JOIN v8_account_settings pref ON pref.account=a.account
                    WHERE g.group_id=$g AND g.is_member=1 AND a.enabled=1
                      AND a.online=1 AND COALESCE(pref.enabled,1)=1
                    ORDER BY a.account;
                    """;
                q.Parameters.AddWithValue("$g",groupId);
                await using var reader=await q.ExecuteReaderAsync(ct);
                while(await reader.ReadAsync(ct))
                {
                    eligible.Add(reader.GetString(0));
                    groupName??=reader.GetString(1);
                }
            }
            if(eligible.Count==0)
                throw new InvalidOperationException(
                     $"群组「{groupId}」没有可用的在线发送账号，或账号未加入该群。请刷新账号与群组。");

            // Parallel scripts may use disjoint groups; we never silently run
            // two independent scripts in one group at the same time.
            await using(var conflict=c.CreateCommand())
            {
                conflict.Transaction=tx;
                conflict.CommandText="""
                    SELECT 1 FROM v8_jobs j
                    JOIN v8_live_batch_jobs b ON b.job_id=j.job_id
                    WHERE b.group_id=$g AND j.state IN
                        ('Running','Paused','Sending','RecoveryRequired')
                    LIMIT 1;
                    """;
                conflict.Parameters.AddWithValue("$g",groupId);
                if(await conflict.ExecuteScalarAsync(ct) is not null)
                    throw new InvalidOperationException(
                        $"群「{groupName}」已有尚未结束的剧本任务，请先处理旧任务。");
            }

            // Preserve explicit per-step sender assignments in the immutable
            // task plan. A blank sender uses the first eligible group member;
            // it never silently substitutes for an explicitly named sender.
            var fallbackSender=eligible[0];
            var steps=sendable.Select((step,index)=>
            {
                var sender=string.IsNullOrWhiteSpace(step.Account)
                    ?fallbackSender:step.Account.Trim();
                if(!eligible.Contains(sender,StringComparer.Ordinal))
                    throw new ArgumentException(
                        $"群「{groupName}」剧本第 {step.Position+1} 条指定账号「{sender}」"+
                        "不在线、未启用或未加入该群，已取消本次启动。");
                return step with {Account=sender,Position=index};
            }).ToArray();

            var id=Guid.NewGuid().ToString("N");
            var token=Guid.NewGuid().ToString("N");
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
                    INSERT INTO v8_live_batch_jobs(
                        job_id,script_id,script_name,group_id,group_name,
                        run_token,steps_json,total_steps,next_due_ms,detail,created_at)
                    VALUES($j,$script,$name,$group,$groupname,
                        $token,$steps,$count,$due,'真实剧本已启动，等待首条发送。',$n);
                    """;
                plan.Parameters.AddWithValue("$j",id);
                plan.Parameters.AddWithValue("$script",script.ScriptId);
                plan.Parameters.AddWithValue("$name",script.Name);
                plan.Parameters.AddWithValue("$group",groupId);
                plan.Parameters.AddWithValue("$groupname",groupName!);
                plan.Parameters.AddWithValue("$token",token);
                plan.Parameters.AddWithValue("$steps",JsonSerializer.Serialize(steps));
                plan.Parameters.AddWithValue("$count",steps.Length);
                plan.Parameters.AddWithValue("$due",now.ToUnixTimeMilliseconds());
                plan.Parameters.AddWithValue("$n",now.ToUnixTimeSeconds());
                await plan.ExecuteNonQueryAsync(ct);
            }
            await using(var log=c.CreateCommand())
            {
                log.Transaction=tx;
                log.CommandText="""
                    INSERT INTO v8_event_log(job_id,event_type,detail,created_at)
                    VALUES($j,'batch_started','用户明确启动此群的真实剧本。',$n);
                    """;
                log.Parameters.AddWithValue("$j",id);
                log.Parameters.AddWithValue("$n",now.ToUnixTimeSeconds());
                await log.ExecuteNonQueryAsync(ct);
            }
            created.Add(id);
        }
        tx.Commit();
        return new LiveBatchStartResult(created,created.Count,sendable.Length);
    }

    public async Task<IReadOnlyList<LiveBatchItem>> ListLiveBatchAsync(CancellationToken ct)
    {
        await InitializeLiveBatchAsync(ct);
        await using var c=Open();
        var rows=new List<LiveBatchItem>();
        await using var q=c.CreateCommand();
        q.CommandText="""
            SELECT b.job_id,b.script_name,b.group_id,b.group_name,
                j.state,j.cursor,b.total_steps,b.next_due_ms,b.detail,b.steps_json
            FROM v8_live_batch_jobs b JOIN v8_jobs j ON j.job_id=b.job_id
            ORDER BY b.created_at DESC,b.job_id LIMIT 1000;
            """;
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
        {
            var state=r.GetString(4);
            var cursor=r.GetInt64(5);
            long estimated=0;
            if(state=="Running")
            {
                try
                {
                    var steps=JsonSerializer.Deserialize<ScriptEditorStep[]>(r.GetString(9));
                    if(steps is not null)
                    {
                        // First future step cannot begin before the persisted due time.
                        estimated=Math.Max(0,r.GetInt64(7)-DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                        for(var i=(int)Math.Clamp(cursor,0,steps.Length);i<steps.Length;i++)
                        {
                            estimated+=Math.Max(0,steps[i].TypingSeconds)*1000L;
                            if(i<steps.Length-1)
                                estimated+=Math.Max(5,steps[i].DelayAfter)*1000L;
                        }
                    }
                }
                catch(JsonException) { /* Unknown ETA rather than abort task listing. */ }
            }
            rows.Add(new LiveBatchItem(
                r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),
                state,cursor,r.GetInt32(6),r.GetInt64(7),
                r.GetString(8),estimated));
        }
        return rows;
    }

    public async Task<LiveBatchHistoryPage> ListLiveBatchHistoryPageAsync(
        LiveBatchHistoryPageRequest request,CancellationToken ct)
    {
        await InitializeLiveBatchAsync(ct);
        var page=Math.Max(0,request.Page);
        var size=Math.Clamp(request.PageSize,1,50);
        var search=(request.Search??"").Trim();
        if(search.Length>120)throw new ArgumentException("搜索内容过长。");
        var state=request.State??"";
        if(state.Length>40)throw new ArgumentException("状态筛选无效。");
        await using var c=Open();
        const string condition="""
            WHERE j.state IN ('Completed','Stopped','Failed')
              AND ($search='' OR instr(b.group_name,$search)>0
                OR instr(b.script_name,$search)>0)
              AND ($state='' OR j.state=$state)
            """;
        async Task AddParameters(Microsoft.Data.Sqlite.SqliteCommand q)
        {
            q.Parameters.AddWithValue("$search",search);
            q.Parameters.AddWithValue("$state",state);
            await Task.CompletedTask;
        }
        long total;
        await using(var count=c.CreateCommand())
        {
            count.CommandText="SELECT COUNT(*) FROM v8_live_batch_jobs b "+
                "JOIN v8_jobs j ON j.job_id=b.job_id "+condition;
            await AddParameters(count);
            total=Convert.ToInt64(await count.ExecuteScalarAsync(ct));
        }
        var jobs=new List<LiveBatchItem>();
        await using(var q=c.CreateCommand())
        {
            q.CommandText="""
                SELECT b.job_id,b.script_name,b.group_id,b.group_name,
                    j.state,j.cursor,b.total_steps,b.next_due_ms,b.detail
                FROM v8_live_batch_jobs b JOIN v8_jobs j ON j.job_id=b.job_id
                """+"\n"+condition+"\n"+"""
                ORDER BY b.created_at DESC,b.job_id DESC
                LIMIT $limit OFFSET $offset;
                """;
            await AddParameters(q);
            q.Parameters.AddWithValue("$limit",size);
            q.Parameters.AddWithValue("$offset",(long)page*size);
            await using var reader=await q.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))
                jobs.Add(new LiveBatchItem(
                    reader.GetString(0),reader.GetString(1),
                    reader.GetString(2),reader.GetString(3),
                    reader.GetString(4),reader.GetInt64(5),
                    reader.GetInt32(6),reader.GetInt64(7),reader.GetString(8)));
        }
        return new LiveBatchHistoryPage(jobs,total,page,size);
    }

    public async Task<LiveBatchHistoryDetail> GetLiveBatchHistoryDetailAsync(
        string jobId,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(jobId) || jobId.Length>128)
            throw new ArgumentException("无效的历史任务编号。");
        await using var c=Open();
        string scriptName,groupName,state,json;
        long cursor;
        int total;
        await using(var q=c.CreateCommand())
        {
            q.CommandText="""
                SELECT b.script_name,b.group_name,j.state,j.cursor,
                       b.total_steps,b.steps_json
                FROM v8_live_batch_jobs b JOIN v8_jobs j ON j.job_id=b.job_id
                WHERE b.job_id=$job LIMIT 1;
                """;
            q.Parameters.AddWithValue("$job",jobId);
            await using var r=await q.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct))
                throw new InvalidOperationException("历史记录不存在。");
            scriptName=r.GetString(0);
            groupName=r.GetString(1);
            state=r.GetString(2);
            cursor=r.GetInt64(3);
            total=r.GetInt32(4);
            json=r.GetString(5);
        }
        var steps=JsonSerializer.Deserialize<ScriptEditorStep[]>(json)
            ??throw new InvalidDataException("历史消息快照损坏。");
        var entries=new Dictionary<long,(string State,string Detail,long UpdatedAt)>();
        await using(var q=c.CreateCommand())
        {
            q.CommandText="""
                SELECT cursor,state,COALESCE(detail,''),updated_at
                FROM v8_dispatch_journal WHERE job_id=$job
                ORDER BY updated_at ASC,dispatch_key ASC;
                """;
            q.Parameters.AddWithValue("$job",jobId);
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                entries[r.GetInt64(0)]=(r.GetString(1),r.GetString(2),r.GetInt64(3));
        }
        var labels=new Dictionary<string,string>(StringComparer.Ordinal);
        await using(var q=c.CreateCommand())
        {
            q.CommandText="SELECT account,label FROM v8_account_settings;";
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                labels[r.GetString(0)]=r.GetString(1);
        }
        var messages=new List<LiveBatchHistoryMessage>(steps.Length);
        for(var i=0;i<steps.Length;i++)
        {
            var step=steps[i];
            entries.TryGetValue(i,out var info);
            // Journal evidence is about confirmed dispatch, not recipient delivery.
            // A message with no journal row must never be shown as sent.
            var status=StatusLabels.Delivery(info.State);
            messages.Add(new LiveBatchHistoryMessage(i+1,
                labels.GetValueOrDefault(step.Account,"未备注账号"),
                string.IsNullOrWhiteSpace(step.Message)
                    ?(string.IsNullOrWhiteSpace(step.Attachment)?"（空消息）":"（图片）")
                    :step.Message,
                status,info.Detail??"",
                info.UpdatedAt>0?DateTimeOffset.FromUnixTimeSeconds(info.UpdatedAt)
                    .ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"):"—"));
        }
        return new LiveBatchHistoryDetail(jobId,scriptName,groupName,
            state,cursor,total,messages);
    }

    public async Task<IReadOnlyList<DueLiveBatch>> FindDueLiveBatchAsync(
        long nowMs,int limit,CancellationToken ct)
    {
        await using var c=Open();
        var due=new List<DueLiveBatch>();
        await using var q=c.CreateCommand();
        q.CommandText="""
            SELECT b.job_id,b.run_token,b.group_id,b.steps_json,j.cursor,
                   b.total_steps,b.script_name,b.group_name
            FROM v8_live_batch_jobs b JOIN v8_jobs j ON j.job_id=b.job_id
            WHERE j.state='Running' AND b.next_due_ms<=$now
            ORDER BY b.next_due_ms,b.created_at LIMIT $limit;
            """;
        q.Parameters.AddWithValue("$now",nowMs);
        q.Parameters.AddWithValue("$limit",Math.Clamp(limit,1,20));
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
        {
            var id=r.GetString(0);
            var token=r.GetString(1);
            var groupId=r.GetString(2);
            var steps=JsonSerializer.Deserialize<ScriptEditorStep[]>(r.GetString(3));
            var cursor=r.GetInt64(4);
            var total=r.GetInt32(5);
            if(steps is null || steps.Length!=total || cursor<0 || cursor>=total)
                throw new InvalidDataException("任务消息快照或游标损坏，已停止后续发送。");
            var step=steps[(int)cursor];
            var data=string.Join("|",step.Message,step.Attachment,step.Account,groupId);
            var hash=Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
            due.Add(new DueLiveBatch(new DispatchIdentity(id,token,0,cursor,
                groupId,step.Account,hash),step,r.GetString(6),r.GetString(7)));
        }
        return due;
    }

    public async Task ConfirmLiveBatchStepAsync(
        DispatchIdentity d,long nowMs,CancellationToken ct)
    {
        await using var c=Open();
        using var tx=c.BeginTransaction();
        string json;
        int count;
        string state;
        await using(var q=c.CreateCommand())
        {
            q.Transaction=tx;
            q.CommandText="""
                SELECT b.steps_json,b.total_steps,j.state
                FROM v8_live_batch_jobs b JOIN v8_jobs j ON j.job_id=b.job_id
                WHERE b.job_id=$j AND j.cursor=$cursor LIMIT 1;
                """;
            q.Parameters.AddWithValue("$j",d.JobId);
            q.Parameters.AddWithValue("$cursor",d.Cursor+1);
            await using var r=await q.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct))
                throw new InvalidOperationException(
                    "发送确认已存，但任务位置不一致，需人工核对。");
            json=r.GetString(0);
            count=r.GetInt32(1);
            state=r.GetString(2);
        }
        var steps=JsonSerializer.Deserialize<ScriptEditorStep[]>(json)
            ??throw new InvalidDataException("消息快照损坏。");
        if(steps.Length!=count || d.Cursor<0 || d.Cursor>=count)
            throw new InvalidDataException("消息游标越界。");
        var step=steps[(int)d.Cursor];
        var done=d.Cursor+1==count;
        var reminder=!done&&step.PauseAfter;
        var next=done?"Completed":
            (state=="Paused"||reminder)?"Paused":"Running";
        var detail=done
            ?"剧本已完成，全部消息获得 Signal RPC 接受回执。"
            :reminder
                ?$"第 {d.Cursor+1} 条成功，按剧本提醒暂停。{step.ReminderText}"
                :$"第 {d.Cursor+1} 条获得发送回执，等待下一条。";
        await using(var job=c.CreateCommand())
        {
            job.Transaction=tx;
            job.CommandText="""
                UPDATE v8_jobs SET state=$state,updated_at=$now
                WHERE job_id=$j AND cursor=$cursor
                  AND state IN ('Running','Paused');
                """;
            job.Parameters.AddWithValue("$state",next);
            job.Parameters.AddWithValue("$now",nowMs/1000);
            job.Parameters.AddWithValue("$j",d.JobId);
            job.Parameters.AddWithValue("$cursor",d.Cursor+1);
            if(await job.ExecuteNonQueryAsync(ct)!=1)
                throw new InvalidOperationException("任务已被其他操作更改。");
        }
        await using(var plan=c.CreateCommand())
        {
            plan.Transaction=tx;
            plan.CommandText="""
                UPDATE v8_live_batch_jobs SET detail=$detail,next_due_ms=$due
                WHERE job_id=$j;
                """;
            plan.Parameters.AddWithValue("$detail",detail);
            plan.Parameters.AddWithValue("$due",done?0:
                nowMs+Math.Max(5000L,(long)step.DelayAfter*1000));
            plan.Parameters.AddWithValue("$j",d.JobId);
            await plan.ExecuteNonQueryAsync(ct);
        }
        await using(var log=c.CreateCommand())
        {
            log.Transaction=tx;
            log.CommandText="""
                INSERT INTO v8_event_log(job_id,dispatch_key,event_type,detail,created_at)
                VALUES($j,$key,$type,$detail,$now);
                """;
            log.Parameters.AddWithValue("$j",d.JobId);
            log.Parameters.AddWithValue("$key",d.DispatchKey);
            log.Parameters.AddWithValue("$type",done?"batch_completed":
                reminder?"batch_reminder_pause":"batch_step_confirmed");
            log.Parameters.AddWithValue("$detail",detail);
            log.Parameters.AddWithValue("$now",nowMs/1000);
            await log.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
    }

    public async Task PauseLiveBatchForSafetyAsync(
        string jobId,string detail,CancellationToken ct)
    {
        await using var c=Open();
        using var tx=c.BeginTransaction();
        await using(var job=c.CreateCommand())
        {
            job.Transaction=tx;
            job.CommandText="""
                UPDATE v8_jobs SET state='Paused',updated_at=$now
                WHERE job_id=$j AND state='Running';
                """;
            job.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            job.Parameters.AddWithValue("$j",jobId);
            await job.ExecuteNonQueryAsync(ct);
        }
        await using(var plan=c.CreateCommand())
        {
            plan.Transaction=tx;
            plan.CommandText="UPDATE v8_live_batch_jobs SET detail=$detail WHERE job_id=$j";
            plan.Parameters.AddWithValue("$j",jobId);
            plan.Parameters.AddWithValue("$detail",detail.Length>600?detail[..600]:detail);
            await plan.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
    }

    public async Task<LiveBatchItem> ControlLiveBatchAsync(
        LiveBatchControlRequest request,CancellationToken ct)
    {
        if(request is null || string.IsNullOrWhiteSpace(request.JobId) ||
            request.JobId.Length>256 || request.Action is not
                ("pause" or "resume" or "stop"))
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
                SELECT j.state,j.cursor,b.total_steps FROM v8_jobs j
                JOIN v8_live_batch_jobs b ON b.job_id=j.job_id
                WHERE j.job_id=$j LIMIT 1;
                """;
            q.Parameters.AddWithValue("$j",request.JobId);
            await using var r=await q.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct))
                throw new KeyNotFoundException("任务不存在。");
            state=r.GetString(0);cursor=r.GetInt64(1);count=r.GetInt32(2);
        }

        if(request.Action!="pause")
        {
            await using var uncertain=c.CreateCommand();
            uncertain.Transaction=tx;
            uncertain.CommandText="""
                SELECT 1 FROM v8_dispatch_journal WHERE job_id=$j AND
                  state IN ('Prepared','Sending','Unknown','RecoveryRequired')
                  LIMIT 1;
                """;
            uncertain.Parameters.AddWithValue("$j",request.JobId);
            if(await uncertain.ExecuteScalarAsync(ct) is not null)
                throw new InvalidOperationException(
                    "存在进行中或待核对的发送，不能继续或停止并跳过。");
        }

        var next=request.Action switch
        {
            "pause" when state=="Running"=>"Paused",
            "resume" when state=="Paused"&&cursor<count=>"Running",
            "stop" when state is "Running" or "Paused"=>"Stopped",
            _=>throw new InvalidOperationException($"当前任务状态 {state} 不允许执行 {request.Action}。")
        };

        var now=DateTimeOffset.UtcNow;
        await using(var job=c.CreateCommand())
        {
            job.Transaction=tx;
            job.CommandText="""
                UPDATE v8_jobs SET state=$state,updated_at=$now
                WHERE job_id=$j AND state=$old AND cursor=$cursor;
                """;
            job.Parameters.AddWithValue("$state",next);
            job.Parameters.AddWithValue("$now",now.ToUnixTimeSeconds());
            job.Parameters.AddWithValue("$j",request.JobId);
            job.Parameters.AddWithValue("$old",state);
            job.Parameters.AddWithValue("$cursor",cursor);
            if(await job.ExecuteNonQueryAsync(ct)!=1)
                throw new InvalidOperationException("任务状态刚被修改，请刷新。");
        }
        var detail=request.Action switch
        {
            "pause"=>"用户手动暂停，该群不会自动继续。",
            "resume"=>"用户手动继续，已确认的消息不会再次发送。",
            _=>"用户停止此群任务，必须重新创建才能启动。"
        };
        await using(var plan=c.CreateCommand())
        {
            plan.Transaction=tx;
            plan.CommandText="""
                UPDATE v8_live_batch_jobs SET detail=$detail,
                next_due_ms=CASE WHEN $action='resume'
                  THEN MAX(next_due_ms,$now,COALESCE((
                    SELECT MAX(updated_at)*1000+5000
                    FROM v8_dispatch_journal d
                    WHERE d.job_id=v8_live_batch_jobs.job_id AND d.state='Confirmed'
                  ),0)) ELSE next_due_ms END
                WHERE job_id=$j;
                """;
            plan.Parameters.AddWithValue("$j",request.JobId);
            plan.Parameters.AddWithValue("$action",request.Action);
            plan.Parameters.AddWithValue("$detail",detail);
            plan.Parameters.AddWithValue("$now",now.ToUnixTimeMilliseconds());
            await plan.ExecuteNonQueryAsync(ct);
        }
        await using(var ev=c.CreateCommand())
        {
            ev.Transaction=tx;
            ev.CommandText="""
                INSERT INTO v8_event_log(job_id,event_type,detail,created_at)
                VALUES($j,$action,$detail,$now);
                """;
            ev.Parameters.AddWithValue("$j",request.JobId);
            ev.Parameters.AddWithValue("$action","batch_"+request.Action);
            ev.Parameters.AddWithValue("$detail",detail);
            ev.Parameters.AddWithValue("$now",now.ToUnixTimeSeconds());
            await ev.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
        return (await ListLiveBatchAsync(ct)).Single(x=>x.JobId==request.JobId);
    }
}
