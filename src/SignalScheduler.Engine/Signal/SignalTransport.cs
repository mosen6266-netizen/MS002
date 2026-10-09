using System.Net.Http.Json;
using System.Text.Json;
using SignalScheduler.Engine;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Signal;

public interface ISignalTransport
{
    bool IsReady {get;}
    Task<SignalSendResult> SendAsync(
        DispatchIdentity dispatch,string payload,CancellationToken ct);
}

public interface ISignalTypingTransport
{
    Task SendTypingAsync(string account,string groupId,bool stop,CancellationToken ct);
}

/// <summary>
/// Signal JSON-RPC transport shared by manual probe, short pilot and full
/// scripts. The caller MUST go through DurableTaskEngine's journal boundary.
/// </summary>
public sealed class SignalCliTransport : ISignalTransport, ISignalTypingTransport
{
    readonly HttpClient _http;
    readonly Func<bool> _healthy;
    readonly string? _imageRoot;

    public SignalCliTransport(SignalGuardian guardian,RuntimePaths paths)
        :this(new HttpClient{
            BaseAddress=new Uri("http://127.0.0.1:7583/"),
            Timeout=TimeSpan.FromSeconds(40)
        },()=>string.Equals(guardian.Snapshot.State,"healthy",
            StringComparison.OrdinalIgnoreCase),
          Path.Combine(paths.DataRoot,"attachments","images"))
    {
    }

    public SignalCliTransport(HttpClient http,Func<bool> healthy,
        string? imageRoot=null)
    {
        _http=http;
        _healthy=healthy;
        _imageRoot=imageRoot;
    }

    public bool IsReady=>_healthy();

    public async Task<SignalSendResult> SendAsync(
        DispatchIdentity dispatch,string payload,CancellationToken ct)
    {
        if(!IsReady)
            return new SignalSendResult(SignalDeliveryOutcome.DefinitelyNotSent,
                Detail:"Signal 服务未就绪，未调用发送接口。");

        if(dispatch is null || string.IsNullOrWhiteSpace(dispatch.AccountId) ||
           string.IsNullOrWhiteSpace(dispatch.GroupId))
            throw new ArgumentException("发送账号或群组编号无效。");

        string text;
        string[] attachments=Array.Empty<string>();

        if(payload.StartsWith("signal-structured:",StringComparison.Ordinal))
        {
            var decoded=JsonSerializer.Deserialize<SignalMessagePayload>(
                payload["signal-structured:".Length..])
                ??throw new ArgumentException("消息内容格式错误。");
            text=decoded.Message??"";
            if(!string.IsNullOrWhiteSpace(decoded.AttachmentPath))
            {
                var source=Path.GetFullPath(decoded.AttachmentPath);
                if(_imageRoot is null)
                    throw new InvalidOperationException("当前发送端未配置图片存储目录。");
                var relative=Path.GetRelativePath(
                    Path.GetFullPath(_imageRoot),source);
                if(Path.IsPathRooted(relative) || relative==".." ||
                   relative.StartsWith(".."+Path.DirectorySeparatorChar,
                       StringComparison.Ordinal) ||
                   !File.Exists(source) || new FileInfo(source).Length>15*1024*1024)
                    throw new ArgumentException("图片不是受信任的本地附件，禁止发送。");
                attachments=new[]{source};
            }
        }
        else
        {
            // Keep the old explicitly consented diagnostic path compatible.
            if(!payload.StartsWith("[SignalScheduler 实发测试]",StringComparison.Ordinal))
                throw new ArgumentException("未经过剧本调度的原始发送内容已被拒绝。");
            text=payload;
        }

        if(text.Length>16000 ||
           (string.IsNullOrWhiteSpace(text) && attachments.Length==0))
            throw new ArgumentException("发送内容为空或超过 Signal 文字长度限制。");

        var requestId=Guid.NewGuid().ToString("N");
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,"api/v1/rpc")
            {
                Content=JsonContent.Create(new
                {
                    jsonrpc="2.0",method="send",id=requestId,
                    @params=new{
                        account=dispatch.AccountId,
                        groupIds=new[]{dispatch.GroupId},
                        message=text,
                        attachments
                    }
                })
            };
            using var response=await _http.SendAsync(request,ct);
            var raw=await response.Content.ReadAsStringAsync(ct);
            if(!response.IsSuccessStatusCode)
                return new SignalSendResult(SignalDeliveryOutcome.Ambiguous,
                    Detail:$"Signal HTTP {(int)response.StatusCode}，是否发送成功未知。");

            using var doc=JsonDocument.Parse(raw);
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object ||
               !root.TryGetProperty("jsonrpc",out var version) ||
               version.GetString()!="2.0" ||
               !root.TryGetProperty("id",out var echoId) ||
               echoId.GetString()!=requestId)
                return new SignalSendResult(SignalDeliveryOutcome.Ambiguous,
                    Detail:"Signal 回执未匹配请求编号，禁止自动重发。");

            if(root.TryGetProperty("error",out var error) &&
               error.ValueKind!=JsonValueKind.Null)
            {
                // InvalidParams is rejected before any transport send attempt.
                var code=error.ValueKind==JsonValueKind.Object &&
                    error.TryGetProperty("code",out var number) &&
                    number.TryGetInt32(out var errorCode)?errorCode:0;
                if(code is -32600 or -32601 or -32602)
                    return new SignalSendResult(
                        SignalDeliveryOutcome.DefinitelyNotSent,
                        Detail:$"Signal JSON-RPC 拒绝参数（{code}），确认未发出。");
                return new SignalSendResult(SignalDeliveryOutcome.Ambiguous,
                    Detail:$"Signal 返回错误码 {code}，是否已经局部发送未知。");
            }

            if(!root.TryGetProperty("result",out var result) ||
               result.ValueKind!=JsonValueKind.Object ||
               !result.TryGetProperty("timestamp",out var ts) ||
               !ts.TryGetInt64(out var timestamp) || timestamp<=0)
                return new SignalSendResult(SignalDeliveryOutcome.Ambiguous,
                    Detail:"发送回执无有效时间戳，需在群中人工核对。");

            return new SignalSendResult(SignalDeliveryOutcome.Confirmed,
                timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Signal RPC 已接受该消息，不代表所有成员已读。");
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            // DurableTaskEngine records any cancelled Sending as ambiguous.
            throw;
        }
        catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException
            or IOException or JsonException or InvalidOperationException)
        {
            return new SignalSendResult(SignalDeliveryOutcome.Ambiguous,
                Detail:$"发送或回执异常：{ex.GetType().Name}；禁止自动重试。");
        }
    }

    public async Task SendTypingAsync(
        string account,string groupId,bool stop,CancellationToken ct)
    {
        if(!IsReady) return;
        // Typing is ephemeral and NEVER advances the actual message journal.
        using var request=new HttpRequestMessage(HttpMethod.Post,"api/v1/rpc"){
            Content=JsonContent.Create(new{
                jsonrpc="2.0",method="sendTyping",id=Guid.NewGuid().ToString("N"),
                @params=new{account,groupIds=new[]{groupId},stop}
            })
        };
        using var response=await _http.SendAsync(request,ct);
        response.EnsureSuccessStatusCode();
    }
}
