using Microsoft.Data.Sqlite;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Persistence;

public sealed partial class StateStore
{
    /// <summary>
    /// A non-dispatching inspection of the exact selected script and groups.
    /// Never creates jobs, changes script contents, imports legacy media, or
    /// sends messages. StartLiveBatchAsync remains the final authority.
    /// </summary>
    public async Task<LiveBatchPreflightResult> PreflightLiveBatchAsync(
        LiveBatchPreflightRequest request,CancellationToken ct)
    {
        if(request is null || string.IsNullOrWhiteSpace(request.ScriptId) ||
           request.GroupIds is null || request.GroupIds.Count is <1 or >20 ||
           request.GroupIds.Any(g=>string.IsNullOrWhiteSpace(g) || g.Length>512))
            throw new ArgumentException("请选择剧本及 1～20 个群组。");

        var issues=new List<LiveBatchPreflightIssue>();
        void Error(string message)=>issues.Add(new("错误",message));
        void Warn(string message)=>issues.Add(new("提醒",message));

        var groups=request.GroupIds.Distinct(StringComparer.Ordinal).ToArray();
        if(groups.Length!=request.GroupIds.Count)
            Warn("选中的群组有重复，启动时只会为每个群建立一个任务。");

        var script=await ReadEditorScriptAsync(request.ScriptId,ct);
        if(script.Steps.Count>1500)
            Error("剧本超过 1500 条的编辑上限。");

        var sendable=0;
        foreach(var step in script.Steps)
        {
            var position=step.Position+1;
            var hasText=!string.IsNullOrWhiteSpace(step.Message);
            var hasImage=!string.IsNullOrWhiteSpace(step.Attachment);
            if((step.Message?.Length??0)>16000)
                Error($"第 {position} 条消息超过文字长度限制。");
            if(!hasText && !hasImage)continue;
            if(!hasImage)
            {
                sendable++;
                continue;
            }
            if(!step.Attachment.StartsWith("img:",StringComparison.Ordinal))
            {
                Warn($"第 {position} 条仍引用旧版图片路径，运行时可能无法迁移。");
                if(hasText)sendable++;
                continue;
            }
            try
            {
                await LookupImageAsync(new ImageLookupRequest(step.Attachment),ct);
                sendable++;
            }
            catch(Exception ex) when(ex is IOException or ArgumentException or
                UnauthorizedAccessException)
            {
                Warn($"第 {position} 条图片丢失或损坏，需要重新导入；"+
                    (hasText?"启动时可保留文字。":"该条只有图片，可能被跳过。"));
                if(hasText)sendable++;
            }
        }
        if(sendable==0)Error("剧本没有确定可以发送的文字或有效图片。");

        // Read-only queries mirror the account membership and job conflict
        // conditions that the transactional start command checks again.
        await using var c=Open();
        for(var i=0;i<groups.Length;i++)
        {
            var groupId=groups[i];
            var eligible=new HashSet<string>(StringComparer.Ordinal);
            string groupName=$"所选群组 {i+1}";
            try
            {
                await using(var q=c.CreateCommand())
                {
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
                        groupName=reader.GetString(1);
                    }
                }
                if(eligible.Count==0)
                {
                    Error($"群「{groupName}」没有已加入且在线、已启用的账号。");
                    continue;
                }
                // A fresh database has no batch jobs table yet; absence
                // means no conflict, not a failing health check.
                await using(var q=c.CreateCommand())
                {
                    q.CommandText="""
                        SELECT 1 FROM sqlite_master
                        WHERE type='table' AND name='v8_live_batch_jobs' LIMIT 1;
                        """;
                    if(await q.ExecuteScalarAsync(ct) is not null)
                    {
                        q.CommandText="""
                            SELECT 1 FROM v8_jobs j
                            JOIN v8_live_batch_jobs b ON b.job_id=j.job_id
                            WHERE b.group_id=$g AND j.state IN
                                ('Running','Paused','Sending','RecoveryRequired')
                            LIMIT 1;
                            """;
                        q.Parameters.AddWithValue("$g",groupId);
                        if(await q.ExecuteScalarAsync(ct) is not null)
                            Error($"群「{groupName}」已有未结束的任务，请先处理。");
                    }
                }
                var bad=script.Steps
                    .Where(x=>!string.IsNullOrWhiteSpace(x.Account) &&
                        ( !string.IsNullOrWhiteSpace(x.Message) ||
                          !string.IsNullOrWhiteSpace(x.Attachment)) &&
                         !eligible.Contains(x.Account.Trim()))
                    .Take(8).ToArray();
                foreach(var step in bad)
                    Error($"群「{groupName}」第 {step.Position+1} 条指定的账号不可用或未加入群组。");
            }
            catch(SqliteException)
            {
                Error($"群「{groupName}」的账号或任务目录尚未就绪，请刷新后重试。");
            }
        }

        return new LiveBatchPreflightResult(script.Name,groups.Length,sendable,
            issues,!issues.Any(x=>x.Level=="错误"));
    }
}
