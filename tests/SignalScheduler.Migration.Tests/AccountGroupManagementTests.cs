using Microsoft.Data.Sqlite;
using SignalScheduler.Engine;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class AccountGroupManagementTests
{
    static readonly CancellationToken C=CancellationToken.None;

    [Fact]
    public async Task AccountPreferencesSurviveLiveSyncAndRestart_AndBlockSendingWhenDisabled()
    {
        var (store,db)=await CreateAsync();
        var acct="+49123456789";
        var group="group-1";
        var item=new SignalGroupCatalogItem(acct,group,"群聊一",true,Array.Empty<string>());
        await store.SyncSignalCatalogAsync(new[]{acct},new[]{item},new[]{acct},C);
        var before=await store.GetAccountGroupOverviewAsync(C);
        Assert.Single(before.Accounts);
        Assert.True(before.Accounts[0].Online);
        Assert.Equal(0,before.Accounts[0].Revision);

        var saved=await store.UpdateManagedAccountAsync(
            new UpdateManagedAccount(acct,"备用账号",false,0),C);
        Assert.False(saved.Enabled);
        Assert.Equal(1,saved.Revision);

        // Even a newly fetched account catalog must not re-enable this account.
        await store.SyncSignalCatalogAsync(new[]{acct},new[]{item},new[]{acct},C);
        Assert.Equal("0",await ScalarAsync(db,
            $"SELECT enabled FROM v8_signal_accounts WHERE account='{acct}'"));
        var overview=await store.GetAccountGroupOverviewAsync(C);
        Assert.Equal("备用账号",overview.Accounts[0].Label);
        Assert.False(overview.Accounts[0].Enabled);

        // Use a correctly prepared job and verify the final irreversible gate.
        await using(var c=new SqliteConnection($"Data Source={db}"))
        {
            await c.OpenAsync();
            await using var q=c.CreateCommand();
            q.CommandText="""
                INSERT INTO v8_jobs(job_id,state,cursor,updated_at)
                VALUES('job','Running',0,1);
                """;
            await q.ExecuteNonQueryAsync();
        }
        var dispatch=new DispatchIdentity("job","run",0,0,group,acct,"hash");
        await store.ReserveAsync(dispatch,C);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.MarkSendingAsync(dispatch,C));
        Assert.Equal("DefinitelyNotSent",await ScalarAsync(db,
            "SELECT state FROM v8_dispatch_journal"));
        Assert.Equal("Paused",await ScalarAsync(db,"SELECT state FROM v8_jobs"));

        var reopened=new StateStore(RuntimePaths.ForTesting(Path.GetDirectoryName(db)!,db));
        await reopened.InitializeAsync(C);
        var after=await reopened.GetAccountGroupOverviewAsync(C);
        Assert.Equal("备用账号",after.Accounts.Single().Label);
        Assert.False(after.Accounts.Single().Enabled);
    }

    [Fact]
    public async Task GroupSelectionIsById_NotByName_AndSurvivesRestart()
    {
        var (store,db)=await CreateAsync();
        var acct="+49123";
        var groups=new[]{
            new SignalGroupCatalogItem(acct,"g1","同名群",true,Array.Empty<string>()),
            new SignalGroupCatalogItem(acct,"g2","同名群",true,Array.Empty<string>())
        };
        await store.SyncSignalCatalogAsync(new[]{acct},groups,new[]{acct},C);
        var before=await store.GetAccountGroupOverviewAsync(C);
        Assert.Equal(2,before.Groups.Count);
        Assert.Equal(2,await store.SetSelectedGroupsAsync(
            new UpdateGroupSelection(new[]{"g1","g2","g1"}),C));
        var after=await store.GetAccountGroupOverviewAsync(C);
        Assert.Equal(2,after.Groups.Count(x=>x.Selected));

        var reopened=new StateStore(RuntimePaths.ForTesting(Path.GetDirectoryName(db)!,db));
        await reopened.InitializeAsync(C);
        var saved=await reopened.GetAccountGroupOverviewAsync(C);
        Assert.Equal(2,saved.Groups.Count(x=>x.Selected));
        await Assert.ThrowsAsync<ArgumentException>(()=>
            reopened.SetSelectedGroupsAsync(new UpdateGroupSelection(new[]{"nonexistent"}),C));

        Assert.Equal(0,await reopened.SetSelectedGroupsAsync(
            new UpdateGroupSelection(Array.Empty<string>()),C));
        Assert.All((await reopened.GetAccountGroupOverviewAsync(C)).Groups,x=>Assert.False(x.Selected));
    }

    [Fact]
    public async Task UpdatingAccountWithOutdatedRevisionIsRejected()
    {
        var (store,db)=await CreateAsync();
        await store.SyncSignalCatalogAsync(new[]{"+49123"},
            Array.Empty<SignalGroupCatalogItem>(),Array.Empty<string>(),C);
        await store.UpdateManagedAccountAsync(
            new UpdateManagedAccount("+49123","新备注",true,0),C);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>
            store.UpdateManagedAccountAsync(
                new UpdateManagedAccount("+49123","过期备注",true,0),C));
        Assert.Equal("新备注",(await store.GetAccountGroupOverviewAsync(C))
            .Accounts.Single().Label);
    }

    static async Task<(StateStore,string)> CreateAsync()
    {
        var dir=Path.Combine(Path.GetTempPath(),"SignalV8AccountTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var db=Path.Combine(dir,"data.db");
        var store=new StateStore(RuntimePaths.ForTesting(dir,db));
        await store.InitializeAsync(C);
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
