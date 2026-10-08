using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace SignalScheduler.Engine.Signal;

public sealed class SignalRpcException : Exception
{
    public int? Code { get; }
    public SignalRpcException(string message,int? code=null) : base(message)=>Code=code;
}

public sealed class SignalCliClient
{
    readonly HttpClient _http;
    readonly Uri _rpc=new("http://127.0.0.1:7583/api/v1/rpc");
    readonly Uri _check=new("http://127.0.0.1:7583/api/v1/check");

    public SignalCliClient()
    {
        _http=new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout=TimeSpan.FromSeconds(2),
            PooledConnectionLifetime=TimeSpan.FromMinutes(2)
        });
    }

    public async Task<bool> CheckAsync(TimeSpan timeout,CancellationToken ct)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        try
        {
            using var response=await _http.GetAsync(_check,linked.Token);
            return response.StatusCode==HttpStatusCode.OK;
        }
        catch{return false;}
    }

    public async Task<JsonElement> RpcAsync(string method,object? parameters,TimeSpan timeout,CancellationToken ct)
    {
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeout);
        var request=new
        {
            jsonrpc="2.0",
            id=Guid.NewGuid().ToString("N"),
            method,
            @params=parameters
        };
        using var response=await _http.PostAsJsonAsync(_rpc,request,linked.Token);
        var text=await response.Content.ReadAsStringAsync(linked.Token);
        if(!response.IsSuccessStatusCode)
            throw new SignalRpcException($"HTTP {(int)response.StatusCode}: {Trim(text)}");

        using var doc=JsonDocument.Parse(text);
        var root=doc.RootElement;
        if(root.TryGetProperty("error",out var error))
        {
            int? code=error.TryGetProperty("code",out var c)&&c.TryGetInt32(out var cv)?cv:null;
            var message=error.TryGetProperty("message",out var m)?m.GetString():"Signal RPC error";
            throw new SignalRpcException(message??"Signal RPC error",code);
        }
        if(!root.TryGetProperty("result",out var result))
            throw new SignalRpcException("Signal RPC response has no result.");
        return result.Clone();
    }

    public async Task<IReadOnlyList<SignalAccountInfo>> ListAccountsAsync(CancellationToken ct)
    {
        var result=await RpcAsync("listAccounts",new{},TimeSpan.FromSeconds(8),ct);
        return ParseAccounts(result);
    }

    public async Task<IReadOnlyList<SignalGroupInfo>> ListGroupsAsync(string account,CancellationToken ct)
    {
        var result=await RpcAsync("listGroups",new{account},TimeSpan.FromSeconds(15),ct);
        return ParseGroups(account,result);
    }

    public async Task<string> VersionAsync(CancellationToken ct)
    {
        try
        {
            var result=await RpcAsync("version",new{},TimeSpan.FromSeconds(5),ct);
            if(result.ValueKind==JsonValueKind.String) return result.GetString()??"unknown";
            if(result.ValueKind==JsonValueKind.Object && result.TryGetProperty("version",out var v))
                return v.GetString()??"unknown";
            return result.ToString();
        }
        catch{return "unknown";}
    }

    public Task<JsonElement> StartLinkAsync(CancellationToken ct)=>
        RpcAsync("startLink",new{},TimeSpan.FromSeconds(30),ct);

    public Task<JsonElement> FinishLinkAsync(string uri,string deviceName,CancellationToken ct)=>
        RpcAsync("finishLink",new{deviceLinkUri=uri,deviceName},TimeSpan.FromMinutes(3),ct);

    public static IReadOnlyList<SignalAccountInfo> ParseAccounts(JsonElement result)
    {
        var list=new List<SignalAccountInfo>();
        if(result.ValueKind!=JsonValueKind.Array) return list;
        foreach(var item in result.EnumerateArray())
        {
            if(item.ValueKind==JsonValueKind.String)
            {
                var value=item.GetString();
                if(!string.IsNullOrWhiteSpace(value)) list.Add(new(value,value,null));
                continue;
            }
            if(item.ValueKind!=JsonValueKind.Object) continue;
            string? number=item.TryGetProperty("number",out var n)&&n.ValueKind!=JsonValueKind.Null?n.GetString():null;
            string? aci=item.TryGetProperty("aci",out var a)&&a.ValueKind!=JsonValueKind.Null?a.GetString():null;
            var account=!string.IsNullOrWhiteSpace(number)?number:aci;
            if(!string.IsNullOrWhiteSpace(account)) list.Add(new(account!,number,aci));
        }
        return list;
    }

    public static IReadOnlyList<SignalGroupInfo> ParseGroups(string account,JsonElement result)
    {
        var list=new List<SignalGroupInfo>();
        if(result.ValueKind!=JsonValueKind.Array) return list;
        foreach(var item in result.EnumerateArray())
        {
            if(item.ValueKind!=JsonValueKind.Object) continue;
            var id=item.TryGetProperty("id",out var i)?i.GetString():null;
            if(string.IsNullOrWhiteSpace(id)) continue;
            var name=item.TryGetProperty("name",out var n)?n.GetString()??"(未命名群组)":"(未命名群组)";
            var member=!item.TryGetProperty("isMember",out var im)||im.ValueKind!=JsonValueKind.False;
            var blocked=item.TryGetProperty("isBlocked",out var ib)&&ib.ValueKind==JsonValueKind.True;
            var members=item.TryGetProperty("members",out var ms)?ms.GetRawText():"[]";
            list.Add(new(account,id!,name,member,blocked,members));
        }
        return list;
    }

    static string Trim(string text)=>text.Length<=300?text:text[..300];
}
