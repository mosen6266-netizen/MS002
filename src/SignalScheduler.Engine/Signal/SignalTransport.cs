using System.Net.Http.Json;
using System.Text.Json;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Signal;

public interface ISignalTransport
{
    bool IsReady { get; }
    Task<SignalSendResult> SendAsync(
        DispatchIdentity dispatch,string payload,CancellationToken ct);
}

/// <summary>
/// HTTP JSON-RPC transport for strictly one-shot user-confirmed diagnostic
/// sends. Not used by the script preview runner; there is no background
/// path that invokes SendAsync automatically.
/// </summary>
public sealed class SignalCliTransport : ISignalTransport
{
    readonly HttpClient _http;
    readonly Func<bool> _healthy;

    public SignalCliTransport(SignalGuardian guardian)
        :this(new HttpClient
        {
            BaseAddress=new Uri("http://127.0.0.1:7583/"),
            Timeout=TimeSpan.FromSeconds(18)
        },()=>guardian.Snapshot.State=="healthy")
    {
    }

    // Separating the HTTP adapter allows deterministic testing of real RPC
    // semantics without sending to somebody's actual Signal group in CI.
    public SignalCliTransport(HttpClient http,Func<bool> healthy)
    {
        _http=http;
        _healthy=healthy;
    }

    public bool IsReady=>_healthy();

    public async Task<SignalSendResult> SendAsync(
        DispatchIdentity dispatch,string payload,CancellationToken ct)
    {
        if(!IsReady)
            return new SignalSendResult(SignalDeliveryOutcome.DefinitelyNotSent,
                Detail:"Signal 后台不健康，未执行 RPC 发送。");

        if(dispatch is null ||
           string.IsNullOrWhiteSpace(dispatch.AccountId) ||
           string.IsNullOrWhiteSpace(dispatch.GroupId) ||
           string.IsNullOrWhiteSpace(payload) || payload.Length>500 ||
           !payload.StartsWith("[SignalScheduler 实发测试]",StringComparison.Ordinal))
            throw new ArgumentException("仅允许经过确认、带显式测试标签的单条诊断消息。");

        var requestId=Guid.NewGuid().ToString("N");
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,"api/v1/rpc")
            {
                Content=JsonContent.Create(new
                {
                    jsonrpc="2.0",
                    method="send",
                    id=requestId,
                    @params=new
                    {
                        account=dispatch.AccountId,
                        groupId=dispatch.GroupId,
                        message=payload
                    }
                })
            };
            // Once this request leaves the process, a timeout or dropped
            // socket is ALWAYS ambiguous, even if the peer has no response.
            using var response=await _http.SendAsync(request,ct);
            var raw=await response.Content.ReadAsStringAsync(ct);
            if(!response.IsSuccessStatusCode)
                return new SignalSendResult(SignalDeliveryOutcome.Ambiguous,
                    Detail:$"Signal RPC 返回 HTTP {(int)response.StatusCode}；是否已发出未知，禁止自动重试。");

            using var doc=JsonDocument.Parse(raw);
            var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object ||
               !root.TryGetProperty("jsonrpc",out var ver) ||
               ver.GetString()!="2.0" ||
               !root.TryGetProperty("id",out var echoId) ||
               echoId.ValueKind!=JsonValueKind.String ||
               echoId.GetString()!=requestId)
                return new SignalSendResult(SignalDeliveryOutcome.Ambiguous,
                    Detail:"Signal RPC 返回值标识不匹配，无法证明发送状态。");

            if(root.TryGetProperty("error",out var error) &&
               error.ValueKind!=JsonValueKind.Null)
                return new SignalSendResult(SignalDeliveryOutcome.Ambiguous,
                    Detail:"Signal 返回 RPC 错误，可能部分发送，需手动核对。");

            if(!root.TryGetProperty("result",out var result) ||
               !result.TryGetProperty("timestamp",out var timestamp) ||
               !timestamp.TryGetInt64(out var messageTime) || messageTime<=0)
                return new SignalSendResult(SignalDeliveryOutcome.Ambiguous,
                    Detail:"Signal 没有返回有效的发送时间戳，需人工核对。");

            return new SignalSendResult(SignalDeliveryOutcome.Confirmed,
                messageTime.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "Signal JSON-RPC 已确认接受该测试消息（不代表每位群成员已读）。");
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            // DurableTaskEngine marks post-Sending cancellation as unknown.
            throw;
        }
        catch(Exception ex) when(ex is HttpRequestException or TaskCanceledException
            or IOException or JsonException or InvalidOperationException)
        {
            return new SignalSendResult(SignalDeliveryOutcome.Ambiguous,
                Detail:$"Signal 请求或响应异常（{ex.GetType().Name}），是否发出未知，必须人工核对。");
        }
    }
}
