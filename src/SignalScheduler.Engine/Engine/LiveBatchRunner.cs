using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using SignalScheduler.Engine.Licensing;
using SignalScheduler.Engine.Persistence;
using SignalScheduler.Engine.Signal;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Engine;

/// <summary>
/// Bounded, independent per-group durable runner. Scripts only start through
/// a user-confirmed plan; they never resume themselves after an OS restart.
/// Signal RPC send acknowledgements must be durable before cursor advances.
/// </summary>
public sealed class LiveBatchRunner : BackgroundService
{
    readonly StateStore _store;
    readonly LicenseManager _license;
    readonly SignalGuardian _guardian;
    readonly DurableTaskEngine _engine;
    readonly ISignalTypingTransport _typing;
    readonly SignalReadCoordinator _read;
    readonly LiveBatchQueueTelemetry _queueTelemetry;
    readonly ConcurrentDictionary<string,SemaphoreSlim> _accountLocks=new();
    readonly SemaphoreSlim _slots=new(4,4);

    public LiveBatchRunner(StateStore store,LicenseManager license,
        SignalGuardian guardian,DurableTaskEngine engine,ISignalTypingTransport typing,
        SignalReadCoordinator read,LiveBatchQueueTelemetry queueTelemetry)
    {
        _store=store;
        _license=license;
        _guardian=guardian;
        _engine=engine;
        _typing=typing;
        _read=read;
        _queueTelemetry=queueTelemetry;
    }

    async Task SimulateTypingAsync(StateStore.DueLiveBatch due,CancellationToken ct)
    {
        var remaining=Math.Clamp(due.Step.TypingSeconds,0,300);
        if(remaining==0) return;

        while(remaining>0 && !ct.IsCancellationRequested)
        {
            try
            {
                await _typing.SendTypingAsync(
                    due.Dispatch.AccountId,due.Dispatch.GroupId,false,ct);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch { /* Typing failures never imply a Signal message was sent. */ }

            var seconds=Math.Min(remaining,10);
            await Task.Delay(TimeSpan.FromSeconds(seconds),ct);
            remaining-=seconds;
        }
        try
        {
            await _typing.SendTypingAsync(
                due.Dispatch.AccountId,due.Dispatch.GroupId,true,ct);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch { }
    }

    async Task ProcessGroupAsync(StateStore.DueLiveBatch due,CancellationToken ct)
    {
        var accountGate=_accountLocks.GetOrAdd(
            due.Dispatch.AccountId,_=>new SemaphoreSlim(1,1));
        var acquired=await accountGate.WaitAsync(0,ct);
        if(!acquired)
        {
            // The lock wait is UI telemetry only. The runner's existing
            // account FIFO/dispatch behavior remains authoritative.
            _queueTelemetry.WaitingForAccount(due.Dispatch.JobId);
            try{await accountGate.WaitAsync(ct);}
            finally{_queueTelemetry.Clear(due.Dispatch.JobId);}
        }
        try
        {
            var jobId=due.Dispatch.JobId;
            if(!string.Equals(_guardian.Snapshot.State,"healthy",
                StringComparison.OrdinalIgnoreCase))
            {
                await _store.PauseLiveBatchForSafetyAsync(jobId,
                    "Signal 服务断开或不健康，该群已暂停，等待人工继续。",CancellationToken.None);
                return;
            }

            LicensePublicStatus license;
            try{license=await _license.CheckAsync(ct);}
            catch(Exception ex)
            {
                await _store.PauseLiveBatchForSafetyAsync(jobId,
                    $"在线授权检查异常：{ex.GetType().Name}，群组已暂停。",
                    CancellationToken.None);
                return;
            }
            if(license.State!="active" || !license.ServerReachable ||
               license.LeaseUntil<=DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                await _store.PauseLiveBatchForSafetyAsync(jobId,
                    "在线授权无效或服务器不可用，群组已暂停。",
                    CancellationToken.None);
                return;
            }

            string? imagePath=null;
            if(!string.IsNullOrWhiteSpace(due.Step.Attachment))
            {
                try
                {
                    var image=await _store.LookupImageAsync(
                        new ImageLookupRequest(due.Step.Attachment),ct);
                    imagePath=image.AbsolutePath;
                }
                catch(Exception ex)
                {
                    await _store.PauseLiveBatchForSafetyAsync(jobId,
                        $"图片附件校验失败：{ex.Message}，未发送下一条。",
                        CancellationToken.None);
                    return;
                }
            }

            await SimulateTypingAsync(due,ct);

            // Typing can last several minutes. A healthy Signal connection or
            // authorization at the start of that delay is not permission to
            // dispatch after it expires or disconnects.
            if(!string.Equals(_guardian.Snapshot.State,"healthy",
                StringComparison.OrdinalIgnoreCase))
            {
                await _store.PauseLiveBatchForSafetyAsync(jobId,
                    "输入等待期间 Signal 已断开或不健康，未发送下一条；请手动核对后继续。",
                    CancellationToken.None);
                return;
            }
            try
            {
                license=await _license.CheckAsync(ct);
            }
            catch(Exception ex)
            {
                await _store.PauseLiveBatchForSafetyAsync(jobId,
                    $"发送前授权复核异常：{ex.GetType().Name}，未发送下一条。",
                    CancellationToken.None);
                return;
            }
            if(license.State!="active" || !license.ServerReachable ||
               license.LeaseUntil<=DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                await _store.PauseLiveBatchForSafetyAsync(jobId,
                    "输入等待期间在线授权已失效或服务器不可用，未发送下一条。",
                    CancellationToken.None);
                return;
            }

            // Send read receipts immediately before speaking, AFTER typing.
            // Messages arriving during the typing animation must not be missed.
            // Only this exact account/group is read; other accounts remain unread.
            try
            {
                using var receiptTimeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
                receiptTimeout.CancelAfter(ReadReceiptRetryPolicy.MaxPhase);
                var read=await _read.TrySendForGroupAsync(
                    due.Dispatch.AccountId,due.Dispatch.GroupId,receiptTimeout.Token);
                if(!read.Ready)
                {
                    await _store.PauseLiveBatchForSafetyAsync(jobId,
                        $"发言前已读未完成（{read.Code}，已请求 {read.Accepted}/{read.Selected}，待处理 {read.Remaining}）。"+
                        "已暂停本群下一句，其他群和账号不受影响。请检查已读诊断后手动继续。",
                        CancellationToken.None);
                    return;
                }
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
            catch(OperationCanceledException)
            {
                await _store.PauseLiveBatchForSafetyAsync(jobId,
                    "发言前已读处理超时（READ_PHASE_TIMEOUT），未发送下一句；请核对已读诊断。",
                    CancellationToken.None);
                return;
            }
            catch(Exception ex)
            {
                await _store.PauseLiveBatchForSafetyAsync(jobId,
                    $"发言前已读处理异常（READ_PHASE_EXCEPTION:{ex.GetType().Name}），"+
                    "未发送下一句；请检查已读诊断。",
                    CancellationToken.None);
                return;
            }

            // Important: Pause during typing is checked in MarkSendingAsync,
            // inside the same SQLite transaction as the Sending transition.
            var wire="signal-structured:"+JsonSerializer.Serialize(
                new SignalMessagePayload(due.Step.Message,imagePath));

            try
            {
                var send=await _engine.DispatchAsync(
                    due.Dispatch,wire,ct);
                if(send.Outcome==SignalDeliveryOutcome.Confirmed)
                    await _store.ConfirmLiveBatchStepAsync(
                        due.Dispatch,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        CancellationToken.None);
                else
                    await _store.PauseLiveBatchForSafetyAsync(jobId,
                        send.Detail??"未获得可靠的发送结果，等待人工核对。",
                        CancellationToken.None);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested)
            {
                throw;
            }
            catch(Exception ex)
            {
                // The durable dispatch journal quarantines an in-flight send.
                // Pausing does not mark an ambiguous delivery as unsent.
                try
                {
                    await _store.PauseLiveBatchForSafetyAsync(jobId,
                        $"发送异常：{ex.GetType().Name}；此群已停止自动推进。",
                        CancellationToken.None);
                }
                catch { }
            }
        }
        finally{accountGate.Release();}
    }

    public async Task<int> RunDueOnceAsync(long nowMs,CancellationToken ct)
    {
        var due=await _store.FindDueLiveBatchAsync(nowMs,12,ct);
        var tasks=new List<Task>();
        foreach(var job in due)
        {
            await _slots.WaitAsync(ct);
            tasks.Add(ProcessSafelyAsync(job,ct));
        }
        await Task.WhenAll(tasks);
        return due.Count;
    }

    async Task ProcessSafelyAsync(StateStore.DueLiveBatch due,CancellationToken ct)
    {
        try
        {
            await ProcessGroupAsync(due,ct);
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested)
        {
            throw;
        }
        catch(Exception ex)
        {
            // A worker failure must not leave the group Running and therefore
            // eligible for the next polling cycle. Never advance its cursor
            // or guess the outcome of an in-flight Signal request.
            try
            {
                await _store.PauseLiveBatchForSafetyAsync(
                    due.Dispatch.JobId,
                    "异常停止：后台调度发生未处理错误（"+
                        ex.GetType().Name+"），请检查恢复记录并手动决定是否继续。",
                    CancellationToken.None);
            }
            catch
            {
                // If SQLite itself is unavailable, journal recovery on next
                // startup remains fail-closed; never retry in this worker.
            }
        }
        finally{_slots.Release();}
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while(!ct.IsCancellationRequested)
        {
            try
            {
                await RunDueOnceAsync(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),ct);
            }
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
            catch
            {
                // A storage fault cannot authorize a send or invent a cursor.
                // The next cycle only ever reads committed task state.
            }
            try{await Task.Delay(800,ct);}
            catch(OperationCanceledException) when(ct.IsCancellationRequested){break;}
        }
    }
}
