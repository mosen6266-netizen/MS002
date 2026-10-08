using Microsoft.Data.Sqlite;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Persistence;

public sealed partial class StateStore
{
    /// <summary>
    /// Deletes only the editable V8 copy. Never removes a V7 original or
    /// the immutable snapshot of any historical sending task.
    /// </summary>
    public async Task<ScriptDeleteResult> DeleteEditorScriptAsync(
        ScriptDeleteRequest request,CancellationToken ct)
    {
        if(request is null || string.IsNullOrWhiteSpace(request.ScriptId) ||
           request.ScriptId.Length>256 || request.ExpectedRevision<1)
            throw new ArgumentException("请选择有效剧本并刷新后重试。");

        await InitializeScriptEditorAsync(ct);
        await InitializeLiveBatchAsync(ct);

        await using var c=Open();
        using var tx=c.BeginTransaction();
        await using(var active=c.CreateCommand())
        {
            active.Transaction=tx;
            active.CommandText="""
                SELECT COUNT(*) FROM v8_live_batch_jobs b
                JOIN v8_jobs j ON j.job_id=b.job_id
                WHERE b.script_id=$id
                  AND j.state IN ('Running','Sending','Paused','RecoveryRequired');
                """;
            active.Parameters.AddWithValue("$id",request.ScriptId);
            if(Convert.ToInt64(await active.ExecuteScalarAsync(ct))>0)
                throw new InvalidOperationException(
                    "这个剧本仍有未完成的运行任务，请先停止或处理这些任务。");
        }

        int actualRevision;
        await using(var lookup=c.CreateCommand())
        {
            lookup.Transaction=tx;
            lookup.CommandText="SELECT revision FROM v8_editor_scripts WHERE script_id=$id";
            lookup.Parameters.AddWithValue("$id",request.ScriptId);
            var found=await lookup.ExecuteScalarAsync(ct);
            if(found is null)throw new KeyNotFoundException("剧本已经不存在。");
            actualRevision=Convert.ToInt32(found);
        }
        if(actualRevision!=request.ExpectedRevision)
            throw new InvalidOperationException(
                "剧本已被修改，请重新刷新列表后再删除。");

        await using(var tombstone=c.CreateCommand())
        {
            tombstone.Transaction=tx;
            tombstone.CommandText="""
                INSERT OR REPLACE INTO v8_editor_deleted_scripts(script_id,deleted_at)
                VALUES($id,$now);
                """;
            tombstone.Parameters.AddWithValue("$id",request.ScriptId);
            tombstone.Parameters.AddWithValue("$now",
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await tombstone.ExecuteNonQueryAsync(ct);
        }
        await using(var steps=c.CreateCommand())
        {
            steps.Transaction=tx;
            steps.CommandText="DELETE FROM v8_editor_steps WHERE script_id=$id";
            steps.Parameters.AddWithValue("$id",request.ScriptId);
            await steps.ExecuteNonQueryAsync(ct);
        }
        await using(var script=c.CreateCommand())
        {
            script.Transaction=tx;
            script.CommandText="""
                DELETE FROM v8_editor_scripts
                WHERE script_id=$id AND revision=$rev;
                """;
            script.Parameters.AddWithValue("$id",request.ScriptId);
            script.Parameters.AddWithValue("$rev",request.ExpectedRevision);
            if(await script.ExecuteNonQueryAsync(ct)!=1)
                throw new InvalidOperationException("剧本已发生更改，本次删除已取消。");
        }
        tx.Commit();
        return new ScriptDeleteResult(request.ScriptId,true);
    }
}
