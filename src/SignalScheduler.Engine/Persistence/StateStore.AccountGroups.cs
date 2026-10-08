using Microsoft.Data.Sqlite;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Persistence;

public sealed partial class StateStore
{
    public async Task InitializeAccountManagementAsync(CancellationToken ct)
    {
        await using var c=Open();
        await using var cmd=c.CreateCommand();
        cmd.CommandText="""
            CREATE TABLE IF NOT EXISTS v8_account_settings(
              account TEXT PRIMARY KEY,
              label TEXT NOT NULL,
              enabled INTEGER NOT NULL,
              revision INTEGER NOT NULL DEFAULT 1,
              updated_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS v8_selected_groups(
              group_id TEXT PRIMARY KEY,
              selected_at INTEGER NOT NULL
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Live and migrated account identities are unified by account identifier.
    /// Local preference overrides are independent of immutable V7 metadata.
    /// Groups are de-duplicated by Signal group ID, not by name.
    /// </summary>
    public async Task<AccountGroupOverview> GetAccountGroupOverviewAsync(CancellationToken ct)
    {
        await InitializeAccountManagementAsync(ct);
        await using var c=Open();

        async Task<bool> TableExistsAsync(string name)
        {
            await using var q=c.CreateCommand();
            q.CommandText="SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n LIMIT 1";
            q.Parameters.AddWithValue("$n",name);
            return await q.ExecuteScalarAsync(ct) is not null;
        }

        var accounts=new Dictionary<string,ManagedAccount>(StringComparer.Ordinal);
        if(await TableExistsAsync("v8_accounts"))
        {
            await using var q=c.CreateCommand();
            q.CommandText="SELECT account,label,enabled FROM v8_accounts";
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                var key=r.GetString(0);
                accounts[key]=new ManagedAccount(key,r.GetString(1),
                    r.GetInt64(2)!=0,false,0);
            }
        }
        if(await TableExistsAsync("v8_signal_accounts"))
        {
            await using var q=c.CreateCommand();
            q.CommandText="SELECT account,label,enabled,online FROM v8_signal_accounts";
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                var key=r.GetString(0);
                var old=accounts.GetValueOrDefault(key);
                accounts[key]=new ManagedAccount(key,
                    old?.Label??r.GetString(1),
                    old?.Enabled??(r.GetInt64(2)!=0),
                    r.GetInt64(3)!=0,0);
            }
        }
        await using(var q=c.CreateCommand())
        {
            q.CommandText="SELECT account,label,enabled,revision FROM v8_account_settings";
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                var key=r.GetString(0);
                if(!accounts.TryGetValue(key,out var prior)) continue;
                accounts[key]=prior with {
                    Label=r.GetString(1),
                    Enabled=r.GetInt64(2)!=0,
                    Revision=r.GetInt64(3)
                };
            }
        }

        var groups=new Dictionary<string,(string Name,HashSet<string> Members)>(
            StringComparer.Ordinal);
        if(await TableExistsAsync("v8_groups"))
        {
            await using var q=c.CreateCommand();
            q.CommandText="SELECT account,group_id,name,enabled FROM v8_groups";
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                var key=r.GetString(1);
                var value=groups.GetValueOrDefault(key);
                if(value.Members is null)
                    value=(r.GetString(2),new HashSet<string>(StringComparer.Ordinal));
                if(r.GetInt64(3)!=0) value.Members.Add(r.GetString(0));
                groups[key]=value;
            }
        }
        if(await TableExistsAsync("v8_signal_groups"))
        {
            await using var q=c.CreateCommand();
            q.CommandText="SELECT account,group_id,name,is_member FROM v8_signal_groups";
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct))
            {
                var key=r.GetString(1);
                var value=groups.GetValueOrDefault(key);
                if(value.Members is null)
                    value=(r.GetString(2),new HashSet<string>(StringComparer.Ordinal));
                else
                    value.Name=r.GetString(2);
                if(r.GetInt64(3)!=0) value.Members.Add(r.GetString(0));
                else value.Members.Remove(r.GetString(0));
                groups[key]=value;
            }
        }
        var selected=new HashSet<string>(StringComparer.Ordinal);
        await using(var q=c.CreateCommand())
        {
            q.CommandText="SELECT group_id FROM v8_selected_groups";
            await using var r=await q.ExecuteReaderAsync(ct);
            while(await r.ReadAsync(ct)) selected.Add(r.GetString(0));
        }

        return new AccountGroupOverview(
            accounts.Values.OrderBy(x=>x.Label,StringComparer.Ordinal)
                .ThenBy(x=>x.Account,StringComparer.Ordinal).ToArray(),
            groups.Select(g=>new ManagedGroup(g.Key,g.Value.Name,
                    g.Value.Members.Count,selected.Contains(g.Key)))
                .OrderBy(g=>g.Name,StringComparer.Ordinal)
                .ThenBy(g=>g.GroupId,StringComparer.Ordinal).ToArray());
    }

    public async Task<ManagedAccount> UpdateManagedAccountAsync(
        UpdateManagedAccount input,CancellationToken ct)
    {
        if(input is null || string.IsNullOrWhiteSpace(input.Account)
            || input.Account.Length>128 || string.IsNullOrWhiteSpace(input.Label)
            || input.Label.Trim().Length>80 || input.ExpectedRevision<0)
            throw new ArgumentException("账号编号、备注或修订号无效。");

        var snapshot=await GetAccountGroupOverviewAsync(ct);
        var known=snapshot.Accounts.FirstOrDefault(x=>x.Account==input.Account);
        if(known is null) throw new KeyNotFoundException("账号不存在，请先刷新或扫码登录。");

        var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using var c=Open();
        using var tx=c.BeginTransaction();
        await using(var q=c.CreateCommand())
        {
            q.Transaction=tx;
            q.CommandText=input.ExpectedRevision==0
                ? """
                  INSERT OR IGNORE INTO v8_account_settings(
                    account,label,enabled,revision,updated_at)
                  VALUES($a,$l,$e,1,$n);
                  """
                : """
                  UPDATE v8_account_settings
                  SET label=$l,enabled=$e,revision=revision+1,updated_at=$n
                  WHERE account=$a AND revision=$rev;
                  """;
            q.Parameters.AddWithValue("$a",input.Account);
            q.Parameters.AddWithValue("$l",input.Label.Trim());
            q.Parameters.AddWithValue("$e",input.Enabled?1:0);
            q.Parameters.AddWithValue("$n",now);
            q.Parameters.AddWithValue("$rev",input.ExpectedRevision);
            if(await q.ExecuteNonQueryAsync(ct)!=1)
                throw new InvalidOperationException("这个账号的设置已经在另一个窗口修改，请刷新后重试。");
        }

        await using(var mirror=c.CreateCommand())
        {
            mirror.Transaction=tx;
            mirror.CommandText="""
                UPDATE v8_signal_accounts SET label=$l,enabled=$e
                WHERE account=$a;
                """;
            mirror.Parameters.AddWithValue("$a",input.Account);
            mirror.Parameters.AddWithValue("$l",input.Label.Trim());
            mirror.Parameters.AddWithValue("$e",input.Enabled?1:0);
            await mirror.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
        return new ManagedAccount(input.Account,input.Label.Trim(),
            input.Enabled,known.Online,input.ExpectedRevision+1);
    }

    public async Task<int> SetSelectedGroupsAsync(UpdateGroupSelection input,CancellationToken ct)
    {
        if(input?.GroupIds is null || input.GroupIds.Count>1000 ||
            input.GroupIds.Any(x=>string.IsNullOrWhiteSpace(x)||x.Length>512))
            throw new ArgumentException("群组选择数据无效。");

        var known=(await GetAccountGroupOverviewAsync(ct)).Groups
            .Select(g=>g.GroupId).ToHashSet(StringComparer.Ordinal);
        var selected=input.GroupIds.ToHashSet(StringComparer.Ordinal);
        if(!selected.IsSubsetOf(known))
            throw new ArgumentException("包含未识别的群组，请刷新列表后再保存。");

        await using var c=Open();
        using var tx=c.BeginTransaction();
        await using(var reset=c.CreateCommand())
        {
            reset.Transaction=tx;
            reset.CommandText="DELETE FROM v8_selected_groups";
            await reset.ExecuteNonQueryAsync(ct);
        }
        var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach(var id in selected)
        {
            await using var insert=c.CreateCommand();
            insert.Transaction=tx;
            insert.CommandText="""
                INSERT INTO v8_selected_groups(group_id,selected_at) VALUES($g,$n);
                """;
            insert.Parameters.AddWithValue("$g",id);
            insert.Parameters.AddWithValue("$n",now);
            await insert.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
        return selected.Count;
    }
}
