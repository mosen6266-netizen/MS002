using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Signal;

public interface ISignalTransport
{
    bool IsReady { get; }
    Task<SignalSendResult> SendAsync(DispatchIdentity dispatch,string payload,CancellationToken ct);
}

public sealed class SignalCliTransport : ISignalTransport
{
    public bool IsReady=>false;
    public Task<SignalSendResult> SendAsync(DispatchIdentity dispatch,string payload,CancellationToken ct)=>
        throw new InvalidOperationException("Signal transport is intentionally disabled until Stage 3.");
}
