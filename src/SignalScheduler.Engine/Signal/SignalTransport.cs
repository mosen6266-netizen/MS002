using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Signal;

public interface ISignalTransport
{
    bool IsReady { get; }
    Task<SignalSendResult> SendAsync(DispatchIdentity dispatch,string payload,CancellationToken ct);
}

/// <summary>
/// Alpha.3 validates the runtime and Guardian but deliberately keeps irreversible sends disabled.
/// The transport will be enabled only after the V7 dispatch/recovery contract has been ported.
/// </summary>
public sealed class SignalCliTransport : ISignalTransport
{
    public bool IsReady=>false;

    public Task<SignalSendResult> SendAsync(DispatchIdentity dispatch,string payload,CancellationToken ct)=>
        throw new InvalidOperationException("Signal send is disabled in V8 alpha.3 until durable send/recovery migration is complete.");
}
