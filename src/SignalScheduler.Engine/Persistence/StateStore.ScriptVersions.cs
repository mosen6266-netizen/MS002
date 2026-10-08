using System.Text.Json;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Persistence;

public sealed partial class StateStore
{
    public async Task<IReadOnlyList<ScriptVersionSummary>> ListScriptVersionsAsync(
        string scriptId,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(scriptId)||scriptId.Length>256)
            throw new ArgumentException("剧本编号无效。");
        await InitializeScriptEditorAsync(ct);
        var result=new List<ScriptVersionSummary>();
        await using var c=Open();
        await using var q=c.CreateCommand();
        q.CommandText="""
            SELECT revision,name,saved_at FROM v8_editor_versions
            WHERE script_id=$id ORDER BY revision DESC LIMIT 100;
            """;
        q.Parameters.AddWithValue("$id",scriptId);
        await using var reader=await q.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
            result.Add(new ScriptVersionSummary(reader.GetInt32(0),
                reader.GetString(1),reader.GetInt64(2)));
        return result;
    }

    public async Task<ScriptEditorDocument> ReadScriptVersionAsync(
        ScriptVersionRequest request,CancellationToken ct)
    {
        if(request is null ||string.IsNullOrWhiteSpace(request.ScriptId) ||
           request.ScriptId.Length>256||request.Revision<1)
            throw new ArgumentException("旧版剧本参数无效。");
        await InitializeScriptEditorAsync(ct);
        await using var c=Open();
        await using var q=c.CreateCommand();
        q.CommandText="""
            SELECT name,target_group_id,steps_json
            FROM v8_editor_versions WHERE script_id=$id AND revision=$revision;
            """;
        q.Parameters.AddWithValue("$id",request.ScriptId);
        q.Parameters.AddWithValue("$revision",request.Revision);
        await using var reader=await q.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))
            throw new KeyNotFoundException("没有找到该历史版本。");
        var steps=JsonSerializer.Deserialize<ScriptEditorStep[]>(reader.GetString(2))
            ??throw new InvalidDataException("历史版本内容损坏。");
        return new ScriptEditorDocument(request.ScriptId,reader.GetString(0),
            reader.GetString(1),request.Revision,steps,false);
    }
}
