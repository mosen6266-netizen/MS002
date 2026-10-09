using System.Collections.Concurrent;

namespace SignalScheduler.Engine.Engine;

/// <summary>
/// Ephemeral UI-only state for work queued behind an account already in use.
/// Durable dispatch state and retry decisions are never derived from this.
/// </summary>
public sealed class LiveBatchQueueTelemetry
{
    readonly ConcurrentDictionary<string,string> _phases=new(StringComparer.Ordinal);

    public void WaitingForAccount(string jobId)
    {
        if(!string.IsNullOrWhiteSpace(jobId))
            _phases[jobId]="WaitingAccount";
    }

    public void Clear(string jobId)=>_phases.TryRemove(jobId,out _);

    public bool IsWaitingForAccount(string jobId)=>
        _phases.TryGetValue(jobId,out var value) && value=="WaitingAccount";
}
