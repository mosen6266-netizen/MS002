using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Signal;

public sealed class SignalCatalogSyncService : BackgroundService
{
    const string RpcUrl="http://127.0.0.1:7583/api/v1/rpc";

    readonly SignalGuardian _guardian;
    readonly StateStore _store;
    readonly HttpClient _http=new(){Timeout=TimeSpan.FromSeconds(12)};

    public SignalCatalogSyncService(SignalGuardian guardian,StateStore store)
    {
        _guardian=guardian;
        _store=store;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8),stoppingToken);
                var guardian=_guardian.Snapshot;
                if(!string.Equals(guardian.State,"healthy",StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(TimeSpan.FromSeconds(15),stoppingToken);
                    continue;
                }

                var accounts=guardian.LiveAccounts;
                var groups=new List<SignalGroupCatalogItem>();
                var completeGroupAccounts=new List<string>();

                foreach(var account in accounts)
                {
                    try
                    {
                        var result=await RpcAsync("listGroups",new{account},stoppingToken);
                        if(result.ValueKind!=JsonValueKind.Array) continue;
                        completeGroupAccounts.Add(account);

                        foreach(var g in result.EnumerateArray())
                        {
                            if(g.ValueKind!=JsonValueKind.Object) continue;
                            if(!g.TryGetProperty("id",out var id) || id.ValueKind!=JsonValueKind.String) continue;

                            var groupId=id.GetString()!;
                            var name=g.TryGetProperty("name",out var n) && n.ValueKind==JsonValueKind.String
                                ? n.GetString()!
                                : groupId;
                            var isMember=!g.TryGetProperty("isMember",out var m) || m.ValueKind!=JsonValueKind.False;
                            var members=new List<string>();
                            if(g.TryGetProperty("members",out var ms) && ms.ValueKind==JsonValueKind.Array)
                            {
                                foreach(var item in ms.EnumerateArray())
                                {
                                    if(item.ValueKind==JsonValueKind.String)
                                        members.Add(item.GetString()!);
                                }
                            }

                            groups.Add(new SignalGroupCatalogItem(account,groupId,name,isMember,members));
                        }
                    }
                    catch
                    {
                        // One account's group refresh must not break other accounts or the Guardian.
                    }
                }

                await _store.SyncSignalCatalogAsync(accounts,groups,completeGroupAccounts,stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(45),stoppingToken);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try { await Task.Delay(TimeSpan.FromSeconds(10),stoppingToken); } catch { }
            }
        }
    }

    async Task<JsonElement> RpcAsync(string method,object parameters,CancellationToken ct)
    {
        // Build the envelope explicitly so the JSON-RPC key is exactly "params".
        var envelope=JsonSerializer.SerializeToElement(new Dictionary<string,object?>
        {
            ["jsonrpc"]="2.0",
            ["method"]=method,
            ["params"]=parameters,
            ["id"]=Guid.NewGuid().ToString("N")
        });
        using var req=new HttpRequestMessage(HttpMethod.Post,RpcUrl)
        {
            Content=JsonContent.Create(envelope)
        };

        using var response=await _http.SendAsync(req,ct);
        response.EnsureSuccessStatusCode();
        var text=await response.Content.ReadAsStringAsync(ct);
        using var doc=JsonDocument.Parse(text);
        var root=doc.RootElement;

        if(root.TryGetProperty("error",out var error))
            throw new InvalidOperationException(error.ToString());
        if(!root.TryGetProperty("result",out var result))
            throw new InvalidOperationException("JSON-RPC result missing");

        return result.Clone();
    }
}
