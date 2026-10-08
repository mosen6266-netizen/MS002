using Microsoft.Data.Sqlite;
using SignalScheduler.Engine;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class PreviewTaskTests
{
    static readonly CancellationToken Ct=CancellationToken.None;

    [Fact]
    public async Task TwentyGroupsHaveIndependentDurableCursors_NoMessagesOrJournal()
    {
        var (store,db)=await NewStoreAsync();
        var account="+4911111";
        var groups=Enumerable.Range(1,20)
            .Select(i=>new SignalGroupCatalogItem(account,$"group-{i}",
                $"群组 {i}",true,Array.Empty<string>())).ToArray();
        await store.SyncSignalCatalogAsync(new[]{account},groups,new[]{account},Ct);
        await store.SetSelectedGroupsAsync(new UpdateGroupSelection(
            groups.Select(x=>x.GroupId).ToArray()),Ct);

        var doc=await CreateScriptAsync(store,new[]{
            Step(0,"第一条",false),Step(1,"第二条",false),Step(2,"最后一条",false)
        });
        var planned=await store.PlanPreviewTasksAsync(new PreviewTaskPlanRequest(
            doc.ScriptId,groups.Select(g=>g.GroupId).ToArray()),Ct);
        Assert.Equal(20,planned.Created);
        Assert.Equal(20,planned.JobIds.Distinct().Count());

        var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000;
        Assert.Equal(20,await store.RunDuePreviewBatchAsync(now,50,Ct));
        var snapshot=await store.ListPreviewTasksAsync(Ct);
        Assert.Equal(20,snapshot.Count);
        Assert.All(snapshot,x=>Assert.Equal(1,x.Cursor));

        // Pausing one group does not affect any of the other nineteen groups.
        var paused=planned.JobIds[0];
        await store.ControlPreviewTaskAsync(new(paused,"pause"),Ct);
        Assert.Equal(19,await store.RunDuePreviewBatchAsync(now+1000,50,Ct));
        snapshot=await store.ListPreviewTasksAsync(Ct);
        Assert.Equal(1,snapshot.Single(x=>x.JobId==paused).Cursor);
        Assert.Equal(19,snapshot.Count(x=>x.Cursor==2));

        Assert.Equal(19,await store.RunDuePreviewBatchAsync(now+2000,50,Ct));
        Assert.Equal(19,(await store.ListPreviewTasksAsync(Ct))
            .Count(x=>x.State=="Completed"));
        Assert.Equal("0",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_dispatch_journal"));

        // Restart never advances a paused task without an explicit resume.
        var reopened=new StateStore(RuntimePaths.ForTesting(
            Path.GetDirectoryName(db)!,db));
        await reopened.InitializeAsync(Ct);
        Assert.Equal("Paused",(await reopened.ListPreviewTasksAsync(Ct))
            .Single(x=>x.JobId==paused).State);
        Assert.Equal(0,await reopened.RunDuePreviewBatchAsync(now+3000,50,Ct));
        await reopened.ControlPreviewTaskAsync(new(paused,"resume"),Ct);
        Assert.Equal(1,await reopened.RunDuePreviewBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+2000,50,Ct));
    }

    [Fact]
    public async Task ManualReminderPausesAfterItsMessage_ThenResumesFromNextCursor()
    {
        var (store,db,account,group)=await WithOneGroupAsync();
        var doc=await CreateScriptAsync(store,new[]{
            Step(0,"第一条",true,"人工停顿"),
            Step(1,"第二条",false)
        });
        var id=(await store.PlanPreviewTasksAsync(
            new PreviewTaskPlanRequest(doc.ScriptId,new[]{group}),Ct)).JobIds.Single();

        var now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+1000;
        Assert.True(await store.AdvanceOneDuePreviewAsync(id,now,Ct));
        var paused=(await store.ListPreviewTasksAsync(Ct)).Single();
        Assert.Equal("Paused",paused.State);
        Assert.Equal(1,paused.Cursor);
        Assert.Contains("人工停顿",paused.Detail);
        Assert.False(await store.AdvanceOneDuePreviewAsync(id,now+10000,Ct));

        await store.ControlPreviewTaskAsync(new(id,"resume"),Ct);
        Assert.True(await store.AdvanceOneDuePreviewAsync(
            id,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+2000,Ct));
        var complete=(await store.ListPreviewTasksAsync(Ct)).Single();
        Assert.Equal("Completed",complete.State);
        Assert.Equal(2,complete.Cursor);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.ControlPreviewTaskAsync(new(id,"resume"),Ct));
        Assert.Equal("0",await ScalarAsync(db,
            "SELECT COUNT(*) FROM v8_dispatch_journal"));
    }

    [Fact]
    public async Task StopIsPermanent_AndExistingScriptEditsCannotChangeTaskSnapshot()
    {
        var (store,db,_,group)=await WithOneGroupAsync();
        var initial=await CreateScriptAsync(store,new[]{
            Step(0,"原第一条"),Step(1,"原第二条")});
        var id=(await store.PlanPreviewTasksAsync(
            new(initial.ScriptId,new[]{group}),Ct)).JobIds.Single();
        await store.SaveEditorScriptAsync(new ScriptSaveRequest(
            initial.ScriptId,"已经修改的剧本","",initial.Revision,
            new[]{Step(0,"新内容")}),Ct);
        var preview=(await store.ListPreviewTasksAsync(Ct)).Single();
        Assert.Equal(2,preview.TotalSteps);

        await store.ControlPreviewTaskAsync(new(id,"stop"),Ct);
        var stopped=(await store.ListPreviewTasksAsync(Ct)).Single();
        Assert.Equal("Stopped",stopped.State);
        Assert.Equal(0,stopped.Cursor);
        Assert.Equal(0,await store.RunDuePreviewBatchAsync(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+999999,50,Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.ControlPreviewTaskAsync(new(id,"resume"),Ct));
    }

    [Fact]
    public async Task InvalidGroupCausesFullRollback_AndOfflineAccountIsRejected()
    {
        var (store,db,acct,group)=await WithOneGroupAsync();
        var doc=await CreateScriptAsync(store,new[]{Step(0,"测试")});
        await Assert.ThrowsAsync<ArgumentException>(()=>
            store.PlanPreviewTasksAsync(new(doc.ScriptId,new[]{group,"unknown"}),Ct));
        Assert.Equal("0",await ScalarAsync(db,"SELECT COUNT(*) FROM v8_jobs"));

        await store.UpdateManagedAccountAsync(
            new UpdateManagedAccount(acct,"关闭账户",false,0),Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.PlanPreviewTasksAsync(new(doc.ScriptId,new[]{group}),Ct));
        Assert.Equal("0",await ScalarAsync(db,"SELECT COUNT(*) FROM v8_jobs"));
    }

    [Fact]
    public async Task PreviewCannotBypassAmbiguousJournal()
    {
        var (store,db,acct,group)=await WithOneGroupAsync();
        var doc=await CreateScriptAsync(store,new[]{Step(0,"测试"),Step(1,"另一个")});
        var id=(await store.PlanPreviewTasksAsync(
            new(doc.ScriptId,new[]{group}),Ct)).JobIds.Single();
        await using(var c=new SqliteConnection($"Data Source={db}"))
        {
            await c.OpenAsync(Ct);
            await using var q=c.CreateCommand();
            q.CommandText="""
                INSERT INTO v8_dispatch_journal(
                    dispatch_key,job_id,run_token,run_cycle,cursor,
                    group_id,account_id,payload_hash,state,created_at,updated_at)
                VALUES('manual',$j,'run',0,0,$g,$a,'hash','RecoveryRequired',1,1);
                """;
            q.Parameters.AddWithValue("$j",id);
            q.Parameters.AddWithValue("$g",group);
            q.Parameters.AddWithValue("$a",acct);
            await q.ExecuteNonQueryAsync(Ct);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.ControlPreviewTaskAsync(new(id,"stop"),Ct));
        await store.InitializeAsync(Ct);
        Assert.Equal("RecoveryRequired",(await store.ListPreviewTasksAsync(Ct)).Single().State);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.ControlPreviewTaskAsync(new(id,"resume"),Ct));
    }

    static ScriptEditorStep Step(int index,string message,bool pause=false,
        string reminder="")=>new(index,"",message,"",pause,reminder,0,0);

    static Task<ScriptEditorDocument> CreateScriptAsync(
        StateStore store,ScriptEditorStep[] steps)=>
        store.SaveEditorScriptAsync(new ScriptSaveRequest(
            null,"预演测试剧本","",0,steps),Ct);

    static async Task<(StateStore Store,string Db)> NewStoreAsync()
    {
        var root=Path.Combine(Path.GetTempPath(),"SignalSchedulerPreview",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var db=Path.Combine(root,"data.db");
        var store=new StateStore(RuntimePaths.ForTesting(root,db));
        await store.InitializeAsync(Ct);
        await store.InitializePreviewTasksAsync(Ct);
        return (store,db);
    }

    static async Task<(StateStore Store,string Db,string Account,string Group)>
        WithOneGroupAsync()
    {
        var (store,db)=await NewStoreAsync();
        const string account="+49123",group="preview-group";
        await store.SyncSignalCatalogAsync(new[]{account},
            new[]{new SignalGroupCatalogItem(account,group,"测试群",true,
                Array.Empty<string>())},new[]{account},Ct);
        await store.SetSelectedGroupsAsync(
            new UpdateGroupSelection(new[]{group}),Ct);
        return (store,db,account,group);
    }

    static async Task<string?> ScalarAsync(string db,string sql)
    {
        await using var c=new SqliteConnection($"Data Source={db}");
        await c.OpenAsync(Ct);
        await using var q=c.CreateCommand();
        q.CommandText=sql;
        return Convert.ToString(await q.ExecuteScalarAsync(Ct));
    }
}
