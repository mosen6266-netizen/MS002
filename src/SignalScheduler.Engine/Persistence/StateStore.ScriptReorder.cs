using Microsoft.Data.Sqlite;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Persistence;

public sealed partial class StateStore
{
    public async Task<bool> ReorderEditorScriptsAsync(ScriptReorderRequest request,
        CancellationToken ct)
    {
        if(request?.ScriptIds is not { Count: >0 and <=500 } ||
           request.ScriptIds.Any(x=>string.IsNullOrWhiteSpace(x)||x.Length>256) ||
           request.ScriptIds.Distinct(StringComparer.Ordinal).Count()!=request.ScriptIds.Count)
            throw new ArgumentException("剧本排序数据无效，请刷新列表后重试。");
        await InitializeScriptEditorAsync(ct);
        await using var c=Open();
        using var tx=c.BeginTransaction();
        var existing=new HashSet<string>(StringComparer.Ordinal);
        await using(var q=c.CreateCommand())
        {
            q.Transaction=tx;
            q.CommandText="SELECT script_id FROM v8_editor_scripts;";
            await using var reader=await q.ExecuteReaderAsync(ct);
            while(await reader.ReadAsync(ct))existing.Add(reader.GetString(0));
        }
        // Reject concurrent creations/deletions rather than rewriting an
        // incomplete list and accidentally losing someone's sort preferences.
        if(existing.Count!=request.ScriptIds.Count ||
           request.ScriptIds.Any(x=>!existing.Contains(x)))
            throw new InvalidOperationException(
                "剧本列表发生变化，排序未保存；请刷新列表后重试。");
        for(var i=0;i<request.ScriptIds.Count;i++)
        {
            await using var cmd=c.CreateCommand();
            cmd.Transaction=tx;
            cmd.CommandText="""
                INSERT INTO v8_editor_script_order(script_id,sort_position)
                VALUES($id,$position)
                ON CONFLICT(script_id) DO UPDATE SET sort_position=excluded.sort_position;
                """;
            cmd.Parameters.AddWithValue("$id",request.ScriptIds[i]);
            cmd.Parameters.AddWithValue("$position",i+1);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
        return true;
    }
}
