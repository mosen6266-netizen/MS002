using Microsoft.Extensions.Hosting;
using SignalScheduler.Engine.Licensing;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Engine.Signal;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Engine;

/// <summary>
/// Supervised one-account/one-group automated live test only.
/// A pilot requires fresh manual confirmation, max 3 messages, >=15s
/// between confirmed messages. No automatic resumption after restart,
/// Guardian interruption, license/network failure or ambiguous delivery.
/// </summary>
public sealed class LivePilotRunner : BackgroundService
{
    readonly StateStore _store;
    readonly LicenseManager _license;
    readonly SignalGuardian _guardian;
    readonly DurableTaskEngine _engine;

    public LivePilotRunner(StateStore store,LicenseManager license,
        SignalGuardian guardian,DurableTaskEngine engine)
    {
        _store=store;
        _license=license;
        _guardian=guardian;
        _engine=engine;
    }

    public async Task<bool> RunOneDueAsync(long nowMs,CancellationToken ct)
    {
        var pending=await _store.FindDueLivePilotAsync(nowMs,ct);
        if(pending is null)return false;
        var jobId=pending.Dispatch.JobId;

        try
        {
            if(_guardian.Snapshot.State!="healthy")
            {
                await _store.PauseLivePilotForSafetyAsync(jobId,
                    "Signal 服务不健康，自动实发任务已暂停，没有发送下一条。",ct);
                return false;
            }
            var license=await _license.CheckAsync(ct);
            if(license.State!="active" || !license.ServerReachable ||
               license.LeaseUntil<=DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                await _store.PauseLivePilotForSafetyAsync(jobId,
                    "在线授权检查失败，自动实发任务已暂停，等待手动继续。",ct);
                return false;
            }

            // An explicit crash-safe Reserve/MarkSending/Send/Commit boundary.
            // A manual pause between selection and MarkSending prevents RPC.
            var result=await _engine.DispatchAsync(
                pending.Dispatch,pending.Text,ct);
            if(result.Outcome==SignalDeliveryOutcome.Confirmed)
            {
                await _store.ConfirmLivePilotStepAsync(
                    pending.Dispatch,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    CancellationToken.None);
            }
            else
            {
                // The durable journal already owns the result; only update
                // the UI detail, never advance an unconfirmed cursor.
                await _store.PauseLivePilotForSafetyAsync(jobId,
                    result.Detail??"真实发送未得到确认，禁止自动重试。",
                    CancellationToken.None);
            }
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            // A cancellation after Sending is journaled as ambiguous by the
            // durable engine and quarantined on next launch. No retry.
            throw;
        }
        catch(Exception ex)
        {
            try
            {
                await _store.PauseLivePilotForSafetyAsync(jobId,
                    $"自动实发异常：{ex.GetType().Name}。需人工检查发送日志。",
                    CancellationToken.None);
            }
            catch { }
        }
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try{await RunOneDueAsync(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),ct);}
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch{ /* Retain durable journal; never synthesize progress. */ }
            try{await Task.Delay(600,ct);}
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
        }
    }
}
