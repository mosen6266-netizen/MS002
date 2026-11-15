using System.Net.Http.Json;
using System.Text.Json;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Signal;

public sealed class SignalLinkManager
{
    const string RpcUrl="http://127.0.0.1:7583/api/v1/rpc";

    readonly HttpClient _http=new(){Timeout=Timeout.InfiniteTimeSpan};
    readonly SignalGuardian _guardian;
    readonly StateStore _store;
    readonly object _gate=new();

    CancellationTokenSource? _sessionCts;
    SignalLinkSnapshot _snapshot=new("idle","","","",DateTimeOffset.MinValue);

    public SignalLinkManager(SignalGuardian guardian,StateStore store)
    {
        _guardian=guardian;
        _store=store;
    }

    public SignalLinkSnapshot Snapshot
    {
        get { lock(_gate) return _snapshot; }
    }

    void Set(string state,string uri,string account,string detail,DateTimeOffset startedAt)
    {
        lock(_gate) _snapshot=new SignalLinkSnapshot(state,uri,account,detail,startedAt);
    }

    public async Task<SignalLinkSnapshot> StartAsync(string deviceName,CancellationToken ct)
    {
        var guardian=_guardian.Snapshot;
        if(!string.Equals(guardian.State,"healthy",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Signal 服务尚未正常，不能开始扫码登录。");

        lock(_gate)
        {
            if(_snapshot.State is "starting" or "waiting")
                return _snapshot;

            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts=new CancellationTokenSource();
        }

        var started=DateTimeOffset.UtcNow;
        Set("starting","","","正在创建 Signal 设备链接…",started);

        var result=await RpcAsync("startLink",null,TimeSpan.FromSeconds(15),ct);
        if(result.ValueKind!=JsonValueKind.Object ||
           !result.TryGetProperty("deviceLinkUri",out var uriNode) ||
           uriNode.ValueKind!=JsonValueKind.String ||
           string.IsNullOrWhiteSpace(uriNode.GetString()))
        {
            Set("failed","","","signal-cli 未返回有效设备链接。",started);
            throw new InvalidOperationException("signal-cli 未返回有效设备链接。");
        }

        var uri=uriNode.GetString()!;
        Set("waiting",uri,"","请使用手机 Signal 扫描二维码。",started);

        var sessionToken=_sessionCts!.Token;
        _=Task.Run(()=>FinishAsync(uri,deviceName,started,sessionToken),CancellationToken.None);

        return Snapshot;
    }

    public void Cancel()
    {
        CancellationTokenSource? cts;
        lock(_gate) cts=_sessionCts;
        try { cts?.Cancel(); } catch { }
        var old=Snapshot;
        Set("cancelled","","","已取消扫码登录。",old.StartedAt);
    }

    async Task FinishAsync(string uri,string deviceName,DateTimeOffset started,CancellationToken ct)
    {
        try
        {
            var result=await RpcAsync(
                "finishLink",
                new{deviceLinkUri=uri,deviceName},
                TimeSpan.FromMinutes(2),
                ct);

            string account="";
            if(result.ValueKind==JsonValueKind.Object)
            {
                if(result.TryGetProperty("number",out var number) && number.ValueKind==JsonValueKind.String)
                    account=number.GetString()??"";
                if(string.IsNullOrWhiteSpace(account) &&
                   result.TryGetProperty("aci",out var aci) && aci.ValueKind==JsonValueKind.String)
                    account=aci.GetString()??"";
            }

            // A previously removed account is visible again only after an
            // explicitly completed QR link for that exact Signal identity.
            if(!string.IsNullOrWhiteSpace(account))
                await _store.RestoreRemovedAccountAsync(account,ct);
            Set("complete","",account,string.IsNullOrWhiteSpace(account)?"Signal 账号已链接。":$"已链接：{account}",started);
        }
        catch(OperationCanceledException)
        {
            if(Snapshot.State!="cancelled")
                Set("expired","","","二维码已取消或过期，请重新生成。",started);
        }
        catch(Exception ex)
        {
            Set("failed","","",$"扫码登录失败：{Short(ex.Message)}",started);
        }
    }

    /// <summary>
    /// Delete only the selected linked device's local signal-cli credentials.
    /// This MUST NOT invoke unregister or removeDevice: those can affect the
    /// server-side registration and other devices. The UI confirms separately.
    /// </summary>
    public async Task RemoveLocalAccountAsync(string account,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(account) || account.Length>128)
            throw new ArgumentException("账号编号无效。");
        if(!string.Equals(_guardian.Snapshot.State,"healthy",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Signal 后台未就绪；未删除任何本机登录数据。");

        var catalog=await RpcAsync("listAccounts",null,TimeSpan.FromSeconds(8),ct);
        if(catalog.ValueKind!=JsonValueKind.Array)
            throw new InvalidOperationException("无法验证 Signal 本机账号列表，已停止删除。");

        static bool Matches(JsonElement node,string expected)
        {
            if(node.ValueKind==JsonValueKind.String)
                return string.Equals(node.GetString(),expected,StringComparison.Ordinal);
            if(node.ValueKind!=JsonValueKind.Object)return false;
            foreach(var key in new[]{"number","aci","username"})
            {
                if(node.TryGetProperty(key,out var v) && v.ValueKind==JsonValueKind.String &&
                    string.Equals(v.GetString(),expected,StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
        if(!catalog.EnumerateArray().Any(item=>Matches(item,account)))
            return; // Already disconnected locally; only stale catalog metadata remains.

        await RpcAsync("deleteLocalAccountData",
            new{account,ignoreRegistered=true},TimeSpan.FromSeconds(20),ct);
        // Do not delete SQL records when signal-cli reports an error. The
        // in-memory daemon may need a probe cycle before its list refreshes.
    }

    async Task<JsonElement> RpcAsync(string method,object? parameters,TimeSpan timeout,CancellationToken ct)
    {
        var requestMap=new Dictionary<string,object?>
        {
            ["jsonrpc"]="2.0",
            ["method"]=method,
            ["id"]=Guid.NewGuid().ToString("N")
        };
        if(parameters is not null) requestMap["params"]=parameters;

        using var req=new HttpRequestMessage(HttpMethod.Post,RpcUrl)
        {
            Content=JsonContent.Create(requestMap)
        };
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);

        using var response=await _http.SendAsync(req,linked.Token);
        response.EnsureSuccessStatusCode();
        var text=await response.Content.ReadAsStringAsync(linked.Token);
        using var doc=JsonDocument.Parse(text);
        var root=doc.RootElement;

        if(root.TryGetProperty("error",out var error))
        {
            var message=error.TryGetProperty("message",out var m)?m.GetString():error.ToString();
            throw new InvalidOperationException(message??"Signal JSON-RPC error");
        }

        if(!root.TryGetProperty("result",out var result))
            throw new InvalidOperationException("Signal JSON-RPC result missing");

        return result.Clone();
    }

    static string Short(string text)
    {
        text=text.Replace("\r"," ").Replace("\n"," ").Trim();
        return text.Length<=220?text:text[..220];
    }
}
