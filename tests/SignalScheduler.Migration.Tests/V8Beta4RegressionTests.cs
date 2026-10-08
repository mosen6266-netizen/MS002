using Microsoft.Data.Sqlite;
using SignalScheduler.Engine;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class V8Beta4RegressionTests
{
    static readonly CancellationToken Ct=CancellationToken.None;
    static readonly byte[] ValidPng=Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/g2QAAAAASUVORK5CYII=");

    static async Task<(StateStore Store,string Db,string Root)> NewAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"SignalBeta4",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db=Path.Combine(root,"data.db");
        var store=new StateStore(RuntimePaths.ForTesting(root,db));
        await store.InitializeAsync(Ct);
        await store.InitializeScriptEditorAsync(Ct);
        await store.InitializeLiveBatchAsync(Ct);
        return (store,db,root);
    }

    static async Task<ScriptEditorDocument> SaveAsync(StateStore store,
        params ScriptEditorStep[] steps)=>
        await store.SaveEditorScriptAsync(new ScriptSaveRequest(
            null,"旧图片兼容测试","",0,steps),Ct);

    static async Task SeedAsync(StateStore store)
    {
        await store.SyncSignalCatalogAsync(new[]{"+49123"},
            new[]{new SignalGroupCatalogItem(
                "+49123","test-g","测试授权群",true,Array.Empty<string>())},
            new[]{"+49123"},Ct);
    }

    [Fact]
    public async Task LostLegacyImageIsReviewedBeforeTextOnlyRun()
    {
        var (store,_,root)=await NewAsync();
        await SeedAsync(store);
        var missing=Path.Combine(root,"old-photo.jpg");
        var script=await SaveAsync(store,
            new(0,"","带旧图片的文字",missing,false,"",12,0),
            new(1,"","第二句话","",false,"",15,0));

        var check=await store.InspectLiveBatchMediaAsync(
            new(script.ScriptId),Ct);
        Assert.Equal(2,check.SendableRows);
        Assert.Single(check.Issues);
        Assert.Equal(1,check.Issues[0].Position);

        await Assert.ThrowsAsync<ArgumentException>(()=>
            store.StartLiveBatchAsync(
                new(script.ScriptId,new[]{"test-g"},true),Ct));

        var run=await store.StartLiveBatchAsync(
            new(script.ScriptId,new[]{"test-g"},true,true),Ct);
        Assert.Equal(2,run.MessageCount);
        var due=await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000,10,Ct);
        Assert.Single(due);
        Assert.Equal("带旧图片的文字",due[0].Step.Message);
        Assert.Equal("",due[0].Step.Attachment);
        Assert.Equal(missing,(await store.ReadEditorScriptAsync(
            script.ScriptId,Ct)).Steps[0].Attachment);
    }

    [Fact]
    public async Task ExistingV7ImagePathImportsSafelyAndKeepsOriginal()
    {
        var (store,_,root)=await NewAsync();
        await SeedAsync(store);
        var path=Path.Combine(root,"legacy.png");
        await File.WriteAllBytesAsync(path,ValidPng,Ct);
        var script=await SaveAsync(store,
            new(0,"","图片通知",path,false,"",12,0));
        var check=await store.InspectLiveBatchMediaAsync(
            new(script.ScriptId),Ct);
        Assert.Empty(check.Issues);
        Assert.True(File.Exists(path));

        await store.StartLiveBatchAsync(
            new(script.ScriptId,new[]{"test-g"},true),Ct);
        var due=await store.FindDueLiveBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000,10,Ct);
        Assert.Single(due);
        Assert.StartsWith("img:",due[0].Step.Attachment);
        Assert.True(File.Exists(path));
        Assert.Equal(path,(await store.ReadEditorScriptAsync(
            script.ScriptId,Ct)).Steps[0].Attachment);
    }

    [Fact]
    public async Task LostImageOnlyRowIsSkippedWithExplicitApproval()
    {
        var (store,_,root)=await NewAsync();
        await SeedAsync(store);
        var script=await SaveAsync(store,
            new(0,"","",Path.Combine(root,"missing.png"),false,"",3,0),
            new(1,"","仍可发布的公告","",false,"",6,0));
        var inspect=await store.InspectLiveBatchMediaAsync(
            new(script.ScriptId),Ct);
        Assert.Single(inspect.Issues);
        Assert.Equal(1,inspect.SendableRows);
        Assert.Contains("跳过",inspect.Issues[0].Action);
        var run=await store.StartLiveBatchAsync(new(
            script.ScriptId,new[]{"test-g"},true,true),Ct);
        Assert.Equal(1,run.MessageCount);
    }

    [Fact]
    public async Task DeleteScriptIsRevisionGuarded_AndPersistsAfterRestart()
    {
        var (store,db,root)=await NewAsync();
        var script=await SaveAsync(store,
            new(0,"","可以删的剧本","",false,"",4,0));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.DeleteEditorScriptAsync(
                new(script.ScriptId,script.Revision+1),Ct));
        var deleted=await store.DeleteEditorScriptAsync(
            new(script.ScriptId,script.Revision),Ct);
        Assert.True(deleted.Deleted);
        Assert.Empty(await store.ListEditorScriptsAsync(Ct));

        var reopened=new StateStore(RuntimePaths.ForTesting(root,db));
        await reopened.InitializeAsync(Ct);
        Assert.Empty(await reopened.ListEditorScriptsAsync(Ct));
    }

    [Fact]
    public async Task DeletingLegacyEditableCopyDoesNotRestoreItOrDeleteV7()
    {
        var (store,db,root)=await NewAsync();
        await using(var connection=new SqliteConnection($"Data Source={db}"))
        {
            await connection.OpenAsync(Ct);
            await using var cmd=connection.CreateCommand();
            cmd.CommandText="""
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
                INSERT INTO v8_scripts VALUES(77,'只读旧原件','',1,1);
                INSERT INTO v8_script_steps VALUES(
                  1,77,0,'','旧内容','',0,'',0,0);
                """;
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        await store.InitializeScriptEditorAsync(Ct);
        var old=await store.ReadEditorScriptAsync("legacy:77",Ct);
        await store.DeleteEditorScriptAsync(new(
            old.ScriptId,old.Revision),Ct);
        await store.InitializeScriptEditorAsync(Ct);
        Assert.DoesNotContain(await store.ListEditorScriptsAsync(Ct),
            x=>x.ScriptId=="legacy:77");
        await using(var dbconn=new SqliteConnection($"Data Source={db}"))
        {
            await dbconn.OpenAsync(Ct);
            await using var cmd=dbconn.CreateCommand();
            cmd.CommandText="SELECT name FROM v8_scripts WHERE legacy_id=77";
            Assert.Equal("只读旧原件",await cmd.ExecuteScalarAsync(Ct));
        }
    }

    [Fact]
    public async Task ActiveJobPreventsScriptDeletion()
    {
        var (store,_,_)=await NewAsync();
        await SeedAsync(store);
        var script=await SaveAsync(store,
            new(0,"","待发送内容","",false,"",10,0));
        await store.StartLiveBatchAsync(new(
            script.ScriptId,new[]{"test-g"},true),Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.DeleteEditorScriptAsync(
                new(script.ScriptId,script.Revision),Ct));
        Assert.Single(await store.ListEditorScriptsAsync(Ct));
    }
}
