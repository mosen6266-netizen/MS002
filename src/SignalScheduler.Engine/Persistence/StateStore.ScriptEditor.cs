using Microsoft.Data.Sqlite;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Persistence;

public sealed partial class StateStore
{
    /// <summary>
    /// Non-destructive native script authoring store. Legacy V7 metadata is
    /// copied exactly once into a separate editable V8 table. Editing a legacy
    /// script never writes back to V7 or overwrites its original data.
    /// </summary>
    public async Task InitializeScriptEditorAsync(CancellationToken ct)
    {
        await using var c=Open();
        using var tx=c.BeginTransaction();
        await using(var schema=c.CreateCommand())
        {
            schema.Transaction=tx;
            schema.CommandText="""
                CREATE TABLE IF NOT EXISTS v8_editor_scripts(
                    script_id TEXT PRIMARY KEY,
                    legacy_id INTEGER UNIQUE,
                    name TEXT NOT NULL,
                    target_group_id TEXT NOT NULL DEFAULT '',
                    revision INTEGER NOT NULL,
                    created_at INTEGER NOT NULL,
                    updated_at INTEGER NOT NULL
                );
                CREATE TABLE IF NOT EXISTS v8_editor_steps(
                    step_id INTEGER PRIMARY KEY AUTOINCREMENT,
                    script_id TEXT NOT NULL REFERENCES v8_editor_scripts(script_id)
                      ON DELETE CASCADE,
                    position INTEGER NOT NULL,
                    account TEXT NOT NULL DEFAULT '',
                    message TEXT NOT NULL DEFAULT '',
                    attachment TEXT NOT NULL DEFAULT '',
                    pause_after INTEGER NOT NULL DEFAULT 0,
                    reminder_text TEXT NOT NULL DEFAULT '',
                    delay_after INTEGER NOT NULL DEFAULT 0,
                    typing_seconds INTEGER NOT NULL DEFAULT 5
                );
                CREATE INDEX IF NOT EXISTS idx_v8_editor_steps_order
                  ON v8_editor_steps(script_id,position);
                CREATE TABLE IF NOT EXISTS v8_editor_versions(
                    script_id TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    name TEXT NOT NULL,
                    target_group_id TEXT NOT NULL,
                    steps_json TEXT NOT NULL,
                    saved_at INTEGER NOT NULL,
                    PRIMARY KEY(script_id,revision)
                );
                                CREATE TABLE IF NOT EXISTS v8_editor_deleted_scripts(
                    script_id TEXT PRIMARY KEY,
                    deleted_at INTEGER NOT NULL
                );
                """;
            await schema.ExecuteNonQueryAsync(ct);
        }

        async Task<bool> TableExistsAsync(string name)
        {
            await using var cmd=c.CreateCommand();
            cmd.Transaction=tx;
            cmd.CommandText="""
                SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$n",name);
            return await cmd.ExecuteScalarAsync(ct) is not null;
        }

        if(await TableExistsAsync("v8_scripts") &&
           await TableExistsAsync("v8_script_steps"))
        {
            var legacy=new List<(long Id,string Name,string Target,long Created,long Updated)>();
            await using(var query=c.CreateCommand())
            {
                query.Transaction=tx;
                query.CommandText="""
                    SELECT legacy_id,name,target_group_id,created_at,updated_at
                    FROM v8_scripts ORDER BY legacy_id;
                    """;
                await using var r=await query.ExecuteReaderAsync(ct);
                while(await r.ReadAsync(ct))
                    legacy.Add((r.GetInt64(0),r.GetString(1),r.GetString(2),
                        r.GetInt64(3),r.GetInt64(4)));
            }

            foreach(var script in legacy)
            {
                var id=$"legacy:{script.Id}";
                await using var insert=c.CreateCommand();
                insert.Transaction=tx;
                insert.CommandText="""
                    INSERT OR IGNORE INTO v8_editor_scripts(
                        script_id,legacy_id,name,target_group_id,revision,
                        created_at,updated_at)
                    SELECT $id,$legacy,$name,$group,1,$created,$updated
                    WHERE NOT EXISTS(
                        SELECT 1 FROM v8_editor_deleted_scripts
                        WHERE script_id=$id
                    );
                    """;
                insert.Parameters.AddWithValue("$id",id);
                insert.Parameters.AddWithValue("$legacy",script.Id);
                insert.Parameters.AddWithValue("$name",script.Name);
                insert.Parameters.AddWithValue("$group",script.Target);
                insert.Parameters.AddWithValue("$created",script.Created);
                insert.Parameters.AddWithValue("$updated",script.Updated);
                // Only copy steps for a newly imported script. Reopening the
                // editor must never undo user changes or restore deleted steps.
                if(await insert.ExecuteNonQueryAsync(ct)!=1) continue;

                await using var steps=c.CreateCommand();
                steps.Transaction=tx;
                steps.CommandText="""
                    INSERT INTO v8_editor_steps(
                        script_id,position,account,message,attachment,
                        pause_after,reminder_text,delay_after,typing_seconds)
                    SELECT $id,sort_order,account,message,attachment,pause_after,
                           reminder_text,delay_after,typing_seconds
                    FROM v8_script_steps WHERE script_id=$legacy
                    ORDER BY sort_order,legacy_id;
                    """;
                steps.Parameters.AddWithValue("$id",id);
                steps.Parameters.AddWithValue("$legacy",script.Id);
                await steps.ExecuteNonQueryAsync(ct);
            }
        }
        tx.Commit();
    }

    public async Task<IReadOnlyList<ScriptEditorSummary>> ListEditorScriptsAsync(CancellationToken ct)
    {
        await InitializeScriptEditorAsync(ct);
        await using var c=Open();
        var scripts=new List<ScriptEditorSummary>();
        await using var q=c.CreateCommand();
        q.CommandText="""
            SELECT s.script_id,s.name,s.revision,COUNT(st.step_id),s.legacy_id
            FROM v8_editor_scripts s
            LEFT JOIN v8_editor_steps st ON st.script_id=s.script_id
            GROUP BY s.script_id,s.name,s.revision,s.legacy_id
            ORDER BY s.updated_at DESC,s.name LIMIT 500;
            """;
        await using var r=await q.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct))
            scripts.Add(new ScriptEditorSummary(
                r.GetString(0),r.GetString(1),r.GetInt32(2),
                r.GetInt32(3),!r.IsDBNull(4)));
        return scripts;
    }

    public async Task<ScriptEditorDocument> ReadEditorScriptAsync(
        string scriptId,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(scriptId) || scriptId.Length>256)
            throw new ArgumentException("剧本编号无效。",nameof(scriptId));
        await InitializeScriptEditorAsync(ct);
        await using var c=Open();
        string name,target;
        int revision;
        bool imported;
        await using(var q=c.CreateCommand())
        {
            q.CommandText="""
                SELECT name,target_group_id,revision,legacy_id
                FROM v8_editor_scripts WHERE script_id=$id LIMIT 1;
                """;
            q.Parameters.AddWithValue("$id",scriptId);
            await using var r=await q.ExecuteReaderAsync(ct);
            if(!await r.ReadAsync(ct))
                throw new KeyNotFoundException("没有找到这个剧本。");
            name=r.GetString(0);
            target=r.GetString(1);
            revision=r.GetInt32(2);
            imported=!r.IsDBNull(3);
        }

        var steps=new List<ScriptEditorStep>();
        await using(var q=c.CreateCommand())
        {
            q.CommandText="""
                SELECT position,account,message,attachment,pause_after,
                       reminder_text,delay_after,typing_seconds
                FROM v8_editor_steps WHERE script_id=$id
                ORDER BY position,step_id;
                """;
            q.Parameters.AddWithValue("$id",scriptId);
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
                steps.Add(new ScriptEditorStep(
                    r.GetInt32(0),r.GetString(1),r.GetString(2),r.GetString(3),
                    r.GetInt64(4)!=0,r.GetString(5),r.GetInt32(6),r.GetInt32(7)));
        }
        return new ScriptEditorDocument(scriptId,name,target,revision,steps,imported);
    }

    public async Task<ScriptEditorDocument> SaveEditorScriptAsync(
        ScriptSaveRequest request,CancellationToken ct)
    {
        if(request is null) throw new ArgumentException("剧本数据为空。");
        if(string.IsNullOrWhiteSpace(request.Name) || request.Name.Length>120)
            throw new ArgumentException("剧本名称必须在 1～120 个字符之间。");
        if(request.TargetGroupId is null || request.TargetGroupId.Length>500)
            throw new ArgumentException("目标群组编号过长。");
        if(request.Steps is null || request.Steps.Count>1500)
            throw new ArgumentException("单个剧本最多支持 1500 条消息。");
        foreach(var step in request.Steps)
        {
            if(step is null ||
               step.Account is null || step.Account.Length>128 ||
               step.Message is null || step.Message.Length>16000 ||
               step.Attachment is null || step.Attachment.Length>1024 ||
               step.ReminderText is null || step.ReminderText.Length>1000 ||
               step.DelayAfter is <0 or >3600 || step.TypingSeconds is <0 or >300)
                throw new ArgumentException("某条消息的内容、间隔或输入时长超出允许范围。");
        }
        await InitializeScriptEditorAsync(ct);

        var isNew=string.IsNullOrWhiteSpace(request.ScriptId);
        var id=isNew?Guid.NewGuid().ToString("N"):request.ScriptId!;
        if(id.Length>256) throw new ArgumentException("剧本编号无效。");
        var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using var c=Open();
        using var tx=c.BeginTransaction();
        await using(var cmd=c.CreateCommand())
        {
            cmd.Transaction=tx;
            if(isNew)
            {
                cmd.CommandText="""
                    INSERT INTO v8_editor_scripts(
                        script_id,legacy_id,name,target_group_id,revision,
                        created_at,updated_at)
                    VALUES($id,NULL,$name,$group,1,$now,$now);
                    """;
            }
            else
            {
                cmd.CommandText="""
                    UPDATE v8_editor_scripts
                    SET name=$name,target_group_id=$group,
                        revision=revision+1,updated_at=$now
                    WHERE script_id=$id AND revision=$rev;
                    """;
                cmd.Parameters.AddWithValue("$rev",request.Revision);
            }
            cmd.Parameters.AddWithValue("$id",id);
            cmd.Parameters.AddWithValue("$name",request.Name.Trim());
            cmd.Parameters.AddWithValue("$group",request.TargetGroupId);
            cmd.Parameters.AddWithValue("$now",now);
            if(await cmd.ExecuteNonQueryAsync(ct)!=1)
                throw new InvalidOperationException(
                    "剧本已在其他窗口被修改，当前保存已取消。请重新读取后再编辑。");
        }

        // Snapshot the previous complete revision in the same write transaction.
        // An optimistic-concurrency failure above rolls back without creating history.
        if(!isNew)
        {
            string oldName,oldGroup;
            await using(var previous=c.CreateCommand())
            {
                previous.Transaction=tx;
                previous.CommandText="SELECT name,target_group_id FROM v8_editor_scripts WHERE script_id=$id;";
                previous.Parameters.AddWithValue("$id",id);
                await using var reader=await previous.ExecuteReaderAsync(ct);
                if(!await reader.ReadAsync(ct))
                    throw new InvalidOperationException("无法读取旧剧本。");
                oldName=reader.GetString(0);
                oldGroup=reader.GetString(1);
            }
            var earlier=new List<ScriptEditorStep>();
            await using(var previousSteps=c.CreateCommand())
            {
                previousSteps.Transaction=tx;
                previousSteps.CommandText="""
                    SELECT position,account,message,attachment,pause_after,
                           reminder_text,delay_after,typing_seconds
                    FROM v8_editor_steps WHERE script_id=$id ORDER BY position,step_id;
                    """;
                previousSteps.Parameters.AddWithValue("$id",id);
                await using var reader=await previousSteps.ExecuteReaderAsync(ct);
                while(await reader.ReadAsync(ct))
                    earlier.Add(new ScriptEditorStep(reader.GetInt32(0),
                        reader.GetString(1),reader.GetString(2),reader.GetString(3),
                        reader.GetInt64(4)!=0,reader.GetString(5),
                        reader.GetInt32(6),reader.GetInt32(7)));
            }
            await using var history=c.CreateCommand();
            history.Transaction=tx;
            history.CommandText="""
                INSERT OR IGNORE INTO v8_editor_versions(
                    script_id,revision,name,target_group_id,steps_json,saved_at)
                VALUES($id,$rev,$name,$group,$steps,$now);
                """;
            history.Parameters.AddWithValue("$id",id);
            history.Parameters.AddWithValue("$rev",request.Revision);
            history.Parameters.AddWithValue("$name",oldName);
            history.Parameters.AddWithValue("$group",oldGroup);
            history.Parameters.AddWithValue("$steps",
                System.Text.Json.JsonSerializer.Serialize(earlier));
            history.Parameters.AddWithValue("$now",now);
            await history.ExecuteNonQueryAsync(ct);
        }

        await using(var clear=c.CreateCommand())
        {
            clear.Transaction=tx;
            clear.CommandText="DELETE FROM v8_editor_steps WHERE script_id=$id";
            clear.Parameters.AddWithValue("$id",id);
            await clear.ExecuteNonQueryAsync(ct);
        }
        var position=0;
        foreach(var step in request.Steps)
        {
            await using var insert=c.CreateCommand();
            insert.Transaction=tx;
            insert.CommandText="""
                INSERT INTO v8_editor_steps(
                    script_id,position,account,message,attachment,pause_after,
                    reminder_text,delay_after,typing_seconds)
                VALUES($id,$pos,$account,$message,$attachment,$pause,$reminder,$delay,$typing);
                """;
            insert.Parameters.AddWithValue("$id",id);
            insert.Parameters.AddWithValue("$pos",position++);
            insert.Parameters.AddWithValue("$account",step.Account);
            insert.Parameters.AddWithValue("$message",step.Message);
            insert.Parameters.AddWithValue("$attachment",step.Attachment);
            insert.Parameters.AddWithValue("$pause",step.PauseAfter?1:0);
            insert.Parameters.AddWithValue("$reminder",step.ReminderText);
            insert.Parameters.AddWithValue("$delay",step.DelayAfter);
            insert.Parameters.AddWithValue("$typing",step.TypingSeconds);
            await insert.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
        return await ReadEditorScriptAsync(id,ct);
    }
}
