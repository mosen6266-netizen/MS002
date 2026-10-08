using System.Collections.Concurrent;
using System.Text.Json;

namespace SignalScheduler.Engine.Signal;

public sealed class SignalLinkCoordinator
{
    readonly SignalCliClient _client;
    readonly SignalCliSupervisor _supervisor;
    readonly ConcurrentDictionary<string,SignalLinkSession> _sessions=new();

    public SignalLinkCoordinator(SignalCliClient client,SignalCliSupervisor supervisor)
    {
        _client=client;
        _supervisor=supervisor;
    }

    public async Task<SignalLinkSession> StartAsync(string deviceName,CancellationToken ct)
    {
        if(_supervisor.Snapshot.State!=SignalHealthState.Healthy)
            throw new InvalidOperationException("Signal 服务当前未就绪，无法扫码登录。");

        var result=await _client.StartLinkAsync(ct);
        if(!result.TryGetProperty("deviceLinkUri",out var uriNode))
            throw new InvalidOperationException("Signal 未返回设备链接。");
        var uri=uriNode.GetString();
        if(string.IsNullOrWhiteSpace(uri)) throw new InvalidOperationException("Signal 设备链接为空。");

        var now=DateTimeOffset.UtcNow;
        var session=new SignalLinkSession(Guid.NewGuid().ToString("N"),uri,SignalLinkState.WaitingForScan,"请使用手机 Signal 扫描二维码。",null,now,now);
        _sessions[session.SessionId]=session;

        _=Task.Run(async()=>{
            try
            {
                var finish=await _client.FinishLinkAsync(uri,deviceName,CancellationToken.None);
                string? number=finish.TryGetProperty("number",out var n)&&n.ValueKind!=JsonValueKind.Null?n.GetString():null;
                string? aci=finish.TryGetProperty("aci",out var a)&&a.ValueKind!=JsonValueKind.Null?a.GetString():null;
                var account=number??aci;
                _sessions[session.SessionId]=session with
                {
                    State=SignalLinkState.Linked,
                    Detail="扫码登录成功。",
                    Account=account,
                    UpdatedAt=DateTimeOffset.UtcNow
                };
                _supervisor.RequestImmediateSync();
            }
            catch(OperationCanceledException)
            {
                _sessions[session.SessionId]=session with
                {
                    State=SignalLinkState.Expired,
                    Detail="二维码已过期，请重新生成。",
                    UpdatedAt=DateTimeOffset.UtcNow
                };
            }
            catch(Exception ex)
            {
                _sessions[session.SessionId]=session with
                {
                    State=SignalLinkState.Failed,
                    Detail=ex.Message,
                    UpdatedAt=DateTimeOffset.UtcNow
                };
            }
        });

        return session;
    }

    public SignalLinkSession? Get(string sessionId)=>
        _sessions.TryGetValue(sessionId,out var session)?session:null;
}
