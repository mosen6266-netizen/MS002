using SignalScheduler.Engine.Persistence;
using SignalScheduler.Engine.Signal;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Engine;

/// <summary>
/// Durable, fail-closed dispatch boundary. The transport remains disabled
/// until the full runner, recovery UI, and Guardian handshake are verified.
/// </summary>
public sealed class DurableTaskEngine
{
    readonly StateStore _store;
    readonly ISignalTransport _signal;

    public DurableTaskEngine(StateStore store,ISignalTransport signal)
    {
        _store=store;
        _signal=signal;
    }

    public async Task<SignalSendResult> DispatchAsync(
        DispatchIdentity d,string payload,CancellationToken ct)
    {
        if(!_signal.IsReady)
            throw new InvalidOperationException("Signal unavailable; dispatch blocked fail-closed.");

        await _store.ReserveAsync(d,ct);

        // MarkSendingAsync is the final atomic authorization check. If it
        // rejects the request, it records DefinitelyNotSent and pauses the job.
        await _store.MarkSendingAsync(d,ct);

        SignalSendResult result;
        try
        {
            result=await _signal.SendAsync(d,payload,ct);
        }
        catch(OperationCanceledException)
        {
            // A cancellation after entering Sending has an ambiguous outcome.
            // Never let a cancelled caller token prevent recovery persistence.
            await RecordRecoveryBestEffortAsync(d,"Cancelled during Sending.");
            throw;
        }
        catch(Exception ex)
        {
            await RecordRecoveryBestEffortAsync(d,
                $"Transport failed during Sending: {ex.GetType().Name}");
            throw;
        }

        try
        {
            // Persistence must complete even if the caller cancels at the
            // exact moment the Signal server acknowledges delivery.
            switch(result.Outcome)
            {
                case SignalDeliveryOutcome.Confirmed:
                    await _store.CommitConfirmedAsync(d,result,CancellationToken.None);
                    break;
                case SignalDeliveryOutcome.DefinitelyNotSent:
                    await _store.MarkDefinitelyNotSentAsync(d,result.Detail,CancellationToken.None);
                    break;
                default:
                    await _store.MarkRecoveryAsync(d,result.Detail,CancellationToken.None);
                    break;
            }
        }
        catch
        {
            // If confirmation could not be persisted, keep the message
            // ambiguous instead of silently retrying or advancing.
            await RecordRecoveryBestEffortAsync(d,
                "Dispatch result could not be persisted; manual verification required.");
            throw;
        }

        return result;
    }

    async Task RecordRecoveryBestEffortAsync(DispatchIdentity d,string detail)
    {
        try
        {
            await _store.MarkRecoveryAsync(d,detail,CancellationToken.None);
        }
        catch
        {
            // The journal is already Sending or terminal, so a restart will
            // quarantine an in-flight record without inventing delivery.
            // Do not mask the original transport/persistence exception.
        }
    }
}
