using Microsoft.Data.Sqlite;
using SignalScheduler.Engine;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class ScriptEditorTests
{
    [Fact]
    public async Task NativeScript_SaveLoadEdit_PersistsAllFields()
    {
        var (store,db)=await NewAsync();
        var steps=new[]{
            new ScriptEditorStep(0,"","第一条消息","",false,"",10,5),
            new ScriptEditorStep(1,"+49123","第二条消息","assets/image.png",
                true,"请检查后继续",45,12)
        };
        var created=await store.SaveEditorScriptAsync(
            new ScriptSaveRequest(null,"测试剧本","group-1",0,steps),CancellationToken.None);
        Assert.Equal(1,created.Revision);
        Assert.Equal(2,created.Steps.Count);
        Assert.Equal("请检查后继续",created.Steps[1].ReminderText);
        Assert.Equal(12,created.Steps[1].TypingSeconds);
        Assert.Equal("assets/image.png",created.Steps[1].Attachment);
        Assert.Single(await store.ListEditorScriptsAsync(CancellationToken.None));

        // A relaunch must not discard script edits or import any duplicates.
        var reopened=new StateStore(RuntimePaths.ForTesting(Path.GetDirectoryName(db)!,db));
        await reopened.InitializeAsync(CancellationToken.None);
        var copy=await reopened.ReadEditorScriptAsync(created.ScriptId,CancellationToken.None);
        Assert.Equal("第一条消息",copy.Steps[0].Message);

        var edited=await reopened.SaveEditorScriptAsync(new ScriptSaveRequest(
            copy.ScriptId,"更名后","group-1",copy.Revision,
            new[]{copy.Steps[1],copy.Steps[0]}),CancellationToken.None);
        Assert.Equal(2,edited.Revision);
        Assert.Equal("第二条消息",edited.Steps[0].Message);
        Assert.Equal(0,edited.Steps[0].Position);

        // A stale editor window must not silently overwrite newer changes.
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            reopened.SaveEditorScriptAsync(new ScriptSaveRequest(
                copy.ScriptId,"旧版本覆盖","group-1",copy.Revision,steps),
                CancellationToken.None));
        var versions=await reopened.ListScriptVersionsAsync(copy.ScriptId,CancellationToken.None);
        Assert.Single(versions);
        Assert.Equal(1,versions[0].Revision);
        var original=await reopened.ReadScriptVersionAsync(
            new ScriptVersionRequest(copy.ScriptId,1),CancellationToken.None);
        Assert.Equal("测试剧本",original.Name);
        Assert.Equal("第一条消息",original.Steps[0].Message);
        Assert.Equal("更名后",
            (await reopened.ReadEditorScriptAsync(copy.ScriptId,CancellationToken.None)).Name);
    }

    [Fact]
    public async Task LegacyImport_LeavesOriginalTablesUntouched_AndNeverOverwritesEdits()
    {
        var (store,db)=await NewAsync();
        await using(var c=new SqliteConnection($"Data Source={db}"))
        {
            await c.OpenAsync();
            await using var q=c.CreateCommand();
            q.CommandText="""
                CREATE TABLE v8_scripts(
                    legacy_id INTEGER PRIMARY KEY,name TEXT NOT NULL,
                    target_group_id TEXT NOT NULL,created_at INTEGER NOT NULL,
                    updated_at INTEGER NOT NULL);
                CREATE TABLE v8_script_steps(
                    legacy_id INTEGER PRIMARY KEY,script_id INTEGER NOT NULL,
                    sort_order INTEGER NOT NULL,account TEXT NOT NULL,
                    message TEXT NOT NULL,attachment TEXT NOT NULL,
                    pause_after INTEGER NOT NULL,reminder_text TEXT NOT NULL,
                    delay_after INTEGER NOT NULL,typing_seconds INTEGER NOT NULL);
                INSERT INTO v8_scripts VALUES(7,'旧版剧本','g7',1,1);
                INSERT INTO v8_script_steps VALUES(
                    11,7,0,'+49123','原始消息','a/photo.png',1,'操作提醒',35,5);
                """;
            await q.ExecuteNonQueryAsync();
        }
        await store.InitializeScriptEditorAsync(CancellationToken.None);
        var legacy=await store.ReadEditorScriptAsync("legacy:7",CancellationToken.None);
        Assert.True(legacy.ImportedFromV7);
        Assert.Equal("原始消息",legacy.Steps.Single().Message);
        Assert.Equal("a/photo.png",legacy.Steps.Single().Attachment);

        await store.SaveEditorScriptAsync(new ScriptSaveRequest(
            legacy.ScriptId,"修改后的剧本","g7",legacy.Revision,
            Array.Empty<ScriptEditorStep>()),CancellationToken.None);
        await store.InitializeScriptEditorAsync(CancellationToken.None);
        var edited=await store.ReadEditorScriptAsync("legacy:7",CancellationToken.None);
        Assert.Empty(edited.Steps);
        Assert.Equal("修改后的剧本",edited.Name);

        Assert.Equal("旧版剧本",
            await ScalarAsync(db,"SELECT name FROM v8_scripts WHERE legacy_id=7"));
        Assert.Equal("原始消息",
            await ScalarAsync(db,"SELECT message FROM v8_script_steps WHERE legacy_id=11"));
        Assert.Equal("1",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_editor_scripts WHERE legacy_id=7"));
    }

    [Fact]
    public async Task InvalidScript_IsRejectedWithoutCreatingPartialRows()
    {
        var (store,db)=await NewAsync();
        await Assert.ThrowsAsync<ArgumentException>(()=>
            store.SaveEditorScriptAsync(new ScriptSaveRequest(null,"","",0,
                Array.Empty<ScriptEditorStep>()),CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(()=>
            store.SaveEditorScriptAsync(new ScriptSaveRequest(null,"good","",0,
                new[]{new ScriptEditorStep(0,"","test","",false,"",3601,5)}),
                CancellationToken.None));
        Assert.Equal("0",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_editor_scripts"));
    }

    static async Task<(StateStore,string)> NewAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"SignalV8ScriptTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db=Path.Combine(root,"data.db");
        var store=new StateStore(RuntimePaths.ForTesting(root,db));
        await store.InitializeAsync(CancellationToken.None);
        await store.InitializeScriptEditorAsync(CancellationToken.None);
        return (store,db);
    }

    static async Task<string?> ScalarAsync(string db,string sql)
    {
        await using var c=new SqliteConnection($"Data Source={db}");
        await c.OpenAsync();
        await using var q=c.CreateCommand();
        q.CommandText=sql;
        return Convert.ToString(await q.ExecuteScalarAsync());
    }
}
