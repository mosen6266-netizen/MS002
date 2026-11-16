using System.Text.Json;
using Microsoft.Data.Sqlite;
using SignalScheduler.Engine;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class SenderRoleRoutingTests
{
    static readonly CancellationToken Ct=CancellationToken.None;
    const string A="+49111001";
    const string B="+49111002";
    const string C="+49111003";

    [Fact]
    public async Task DuplicateRemarkRoutesToTheOnlyEligibleMemberOfEachGroup()
    {
        var (store,db)=await NewStoreAsync();
        await SeedAsync(store,
            new(A,"g1","群一",true,Array.Empty<string>()),
            new(B,"g2","群二",true,Array.Empty<string>()),
            new(A,"g3","共同群",true,Array.Empty<string>()),
            new(B,"g3","共同群",true,Array.Empty<string>()));
        await store.UpdateManagedAccountAsync(new(A,"水军1",true,0),Ct);
        await store.UpdateManagedAccountAsync(new(B,"水军1",true,0),Ct);
        var script=await SaveAsync(store,
            Step(0,A,"旧剧本仍选择账号 A"),
            Step(1,SenderRoleSelector.ForLabel("水军1"),"新的同备注角色"));
        var groups=new[]{"g1","g2","g3"};
        var preflight=await store.PreflightLiveBatchAsync(
            new(script.ScriptId,groups),Ct);
        Assert.True(preflight.CanStart,string.Join("; ",preflight.Issues.Select(x=>x.Message)));

        var started=await store.StartLiveBatchAsync(
            new(script.ScriptId,groups,true),Ct);
        Assert.Equal(3,started.GroupCount);
        Assert.Equal(new[]{A,A},await SendersAsync(db,"g1"));
        Assert.Equal(new[]{B,B},await SendersAsync(db,"g2"));
        // Both A and B are eligible in g3; the original A preference stays
        // stable, and the role chooses the lowest (deterministic) account.
        Assert.Equal(new[]{A,A},await SendersAsync(db,"g3"));

        // Change member lists after Start. No existing task may reroute.
        await store.SyncSignalCatalogAsync(new[]{A,B},
            new[]{
                new SignalGroupCatalogItem(B,"g1","群一",true,Array.Empty<string>()),
                new SignalGroupCatalogItem(B,"g2","群二",true,Array.Empty<string>()),
                new SignalGroupCatalogItem(A,"g3","共同群",true,Array.Empty<string>()),
                new SignalGroupCatalogItem(B,"g3","共同群",true,Array.Empty<string>())
            },new[]{A,B},Ct);
        Assert.Equal(new[]{A,A},await SendersAsync(db,"g1"));
    }

    [Fact]
    public async Task SingleRemarkStillPinsItsExactAccount_AndDoesNotSubstitute()
    {
        var (store,db)=await NewStoreAsync();
        await SeedAsync(store,new(B,"g2","群二",true,Array.Empty<string>()));
        await store.UpdateManagedAccountAsync(new(A,"水军1",true,0),Ct);
        await store.UpdateManagedAccountAsync(new(B,"水军2",true,0),Ct);
        var script=await SaveAsync(store,Step(0,A,"不允许换其他备注"));
        var checkedResult=await store.PreflightLiveBatchAsync(
            new(script.ScriptId,new[]{"g2"}),Ct);
        Assert.False(checkedResult.CanStart);
        await Assert.ThrowsAsync<ArgumentException>(()=>store.StartLiveBatchAsync(
            new(script.ScriptId,new[]{"g2"},true),Ct));
        Assert.Equal("0",await ScalarAsync(db,"SELECT COUNT(*) FROM v8_live_batch_jobs"));
    }

    [Fact]
    public async Task SameRemarkButAccountNotOnlineOrEnabledNeverSends()
    {
        var (store,db)=await NewStoreAsync();
        await SeedAsync(store,new(B,"g2","群二",true,Array.Empty<string>()));
        await store.UpdateManagedAccountAsync(new(A,"水军1",true,0),Ct);
        await store.UpdateManagedAccountAsync(new(B,"水军1",false,0),Ct);
        var script=await SaveAsync(store,Step(0,A,"不使用已停用的同备注号"));
        var result=await store.PreflightLiveBatchAsync(
            new(script.ScriptId,new[]{"g2"}),Ct);
        Assert.False(result.CanStart);
        await Assert.ThrowsAnyAsync<Exception>(()=>store.StartLiveBatchAsync(
            new(script.ScriptId,new[]{"g2"},true),Ct));
        Assert.Equal("0",await ScalarAsync(db,"SELECT COUNT(*) FROM v8_live_batch_jobs"));
    }

    [Theory]
    [InlineData("水军1","水军1",true)]
    [InlineData("水军1","水军2",false)]
    public void ExplicitLegacyAccountFallsBackOnlyToExactDuplicateLabel(
        string originalRemark,string otherRemark,bool expected)
    {
        var eligible=new[]{B};
        var labels=new Dictionary<string,string>(StringComparer.Ordinal)
        {
            [A]=originalRemark,
            [B]=otherRemark
        };
        Assert.Equal(expected?B:null,
            SenderRoleSelector.Resolve(A,eligible,labels));
    }

    [Fact]
    public void RoleSelectionUsesOnlyEligibleGroupMemberAndNeverInventsAnAccount()
    {
        var labels=new Dictionary<string,string>{{A,"角色甲"},{B,"角色甲"},{C,"其他"}};
        Assert.Equal(B,SenderRoleSelector.Resolve(
            SenderRoleSelector.ForLabel("角色甲"),new[]{C,B},labels));
        Assert.Null(SenderRoleSelector.Resolve(
            SenderRoleSelector.ForLabel("角色甲"),new[]{C},labels));
        Assert.Equal(C,SenderRoleSelector.Resolve("",new[]{C,B},labels));
        Assert.False(SenderRoleSelector.TryGetLabel("role:",out _));
    }

    static ScriptEditorStep Step(int position,string sender,string text)=>
        new(position,sender,text,"",false,"",0,0);

    static Task<ScriptEditorDocument> SaveAsync(StateStore store,
        params ScriptEditorStep[] steps)=>
        store.SaveEditorScriptAsync(new(null,"相同备注群匹配","",0,steps),Ct);

    static async Task<(StateStore,string)> NewStoreAsync()
    {
        var folder=Path.Combine(Path.GetTempPath(),"MS002RoleRouting",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var db=Path.Combine(folder,"data.db");
        var store=new StateStore(RuntimePaths.ForTesting(folder,db));
        await store.InitializeAsync(Ct);
        await store.InitializeLiveBatchAsync(Ct);
        return(store,db);
    }

    static async Task SeedAsync(StateStore store,params SignalGroupCatalogItem[] members)
    {
        await store.SyncSignalCatalogAsync(new[]{A,B},
            members,new[]{A,B},Ct);
    }

    static async Task<string[]> SendersAsync(string db,string groupId)
    {
        await using var connection=new SqliteConnection($"Data Source={db}");
        await connection.OpenAsync();
        await using var query=connection.CreateCommand();
        query.CommandText="SELECT steps_json FROM v8_live_batch_jobs WHERE group_id=$group";
        query.Parameters.AddWithValue("$group",groupId);
        var json=(string?)await query.ExecuteScalarAsync();
        Assert.NotNull(json);
        return JsonSerializer.Deserialize<ScriptEditorStep[]>(json!)!
            .Select(step=>step.Account).ToArray();
    }

    static async Task<string?> ScalarAsync(string db,string sql)
    {
        await using var connection=new SqliteConnection($"Data Source={db}");
        await connection.OpenAsync();
        await using var query=connection.CreateCommand();
        query.CommandText=sql;
        return Convert.ToString(await query.ExecuteScalarAsync());
    }
}
