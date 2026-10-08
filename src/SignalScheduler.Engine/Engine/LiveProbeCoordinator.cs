using SignalScheduler.Engine.Licensing;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Engine.Signal;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Engine;

/// <summary>
/// The ONLY caller of the real Signal transport in alpha.12. A real message
/// can be sent only from a fresh, user-confirmed IPC command, for a selected
/// group with a linked and enabled sender. No recurring or unattended sends.
/// </summary>
public sealed class LiveProbeCoordinator
{
    readonly StateStore _store;
    readonly LicenseManager _license;
    readonly SignalGuardian _guardian;
    readonly DurableTaskEngine _engine;
    readonly SemaphoreSlim _singleProbe=new(1,1);

    public LiveProbeCoordinator(StateStore store,LicenseManager license,
        SignalGuardian guardian,DurableTaskEngine engine)
    {
        _store=store;
        _license=license;
        _guardian=guardian;
        _engine=engine;
    }

    public async Task<LiveProbeResult> SendOnceAsync(
        LiveProbeRequest request,CancellationToken ct)
    {
        if(request is null || !request.ConfirmRealSend)
            throw new ArgumentException("未确认实际发送；没有发送任何消息。");
        if(!await _singleProbe.WaitAsync(0,ct))
            throw new InvalidOperationException("已有一条实发测试正在运行。");

        string? jobId=null;
        try
        {
            // An online check is required for the diagnostic stage, so a
            // disabled/expired card cannot be used with a stale offline lease.
            var license=await _license.CheckAsync(ct);
            if(license.State!="active" || !license.ServerReachable ||
               license.LeaseUntil<=DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                throw new InvalidOperationException(
                    "请先在授权设置中激活并完成联网校验。离线租约暂不允许实发测试。");

            if(_guardian.Snapshot.State!="healthy")
                throw new InvalidOperationException(
                    "Signal 服务尚未健康，无法进行真实测试。");

            var plan=await _store.CreateLiveProbeAsync(request,ct);
            jobId=plan.Dispatch.JobId;
            try
            {
                var result=await _engine.DispatchAsync(
                    plan.Dispatch,plan.Message,ct);
                if(result.Outcome==SignalDeliveryOutcome.Confirmed)
                    await _store.FinishLiveProbeAsync(jobId,true,
                        result.Detail??"已收到 Signal RPC 确认。",CancellationToken.None);
                else
                    await _store.FinishLiveProbeAsync(jobId,false,
                        result.Detail??"未确认发送结果。",CancellationToken.None);
            }
            catch(Exception ex)
            {
                // MarkRecoveryAsync already quarantines any send that crossed
                // the Sending boundary. This call may only fail the job when
                // no real send was attempted; never advance the cursor here.
                try
                {
                    await _store.FinishLiveProbeAsync(jobId,false,
                        $"实发测试中断：{ex.GetType().Name}",CancellationToken.None);
                }
                catch { }
                throw;
            }
            return await _store.GetLiveProbeResultAsync(jobId,CancellationToken.None);
        }
        finally
        {
            _singleProbe.Release();
        }
    }
}
