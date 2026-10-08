using SignalScheduler.Engine.Persistence;
using SignalScheduler.Engine.Signal;
using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Engine;

public sealed class DurableTaskEngine
{
    readonly StateStore _store; readonly ISignalTransport _signal;
    public DurableTaskEngine(StateStore store,ISignalTransport signal){_store=store;_signal=signal;}

    public async Task<SignalSendResult> DispatchAsync(DispatchIdentity d,string payload,CancellationToken ct)
    {
        if(!_signal.IsReady) throw new InvalidOperationException("Signal unavailable; dispatch blocked fail-closed.");
        await _store.ReserveAsync(d,ct);
        await _store.MarkSendingAsync(d,ct);
        SignalSendResult result;
        try{result=await _signal.SendAsync(d,payload,ct);}
        catch(OperationCanceledException){await _store.MarkRecoveryAsync(d,"Cancelled after entering Sending.",CancellationToken.None);throw;}
        catch(Exception ex){await _store.MarkRecoveryAsync(d,ex.Message,CancellationToken.None);throw;}

        if(result.Outcome==SignalDeliveryOutcome.Confirmed) await _store.CommitConfirmedAsync(d,result,ct);
        else if(result.Outcome==SignalDeliveryOutcome.DefinitelyNotSent) await _store.MarkDefinitelyNotSentAsync(d,result.Detail,ct);
        else await _store.MarkRecoveryAsync(d,result.Detail,ct);
        return result;
    }
}
