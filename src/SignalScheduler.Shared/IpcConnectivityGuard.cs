namespace SignalScheduler.Shared;

/// <summary>
/// UI-only connectivity state. A delayed dashboard is not evidence that the
/// sending engine stopped. Require sustained failures of an independent
/// heartbeat before escalating; suppress duplicate incidents across flaps.
/// This class never changes scheduling or delivery state.
/// </summary>
public sealed class IpcConnectivityGuard
{
    static readonly TimeSpan ConfirmationWindow=TimeSpan.FromSeconds(10);
    static readonly TimeSpan DuplicateCooldown=TimeSpan.FromMinutes(10);
    static readonly TimeSpan StableRecovery=TimeSpan.FromMinutes(1);
    DateTimeOffset? _firstHeartbeatFailure;
    DateTimeOffset? _lastCriticalAlert;
    DateTimeOffset? _stableDashboardSince;
    int _consecutiveHeartbeatFailures;

    public int ConsecutiveHeartbeatFailures=>_consecutiveHeartbeatFailures;

    public void DashboardSucceeded(DateTimeOffset now)
    {
        _firstHeartbeatFailure=null;
        _consecutiveHeartbeatFailures=0;
        _stableDashboardSince??=now;
        if(now-_stableDashboardSince.Value>=StableRecovery)
            _lastCriticalAlert=null;
    }

    /// <summary>
    /// Returns true only when an actual engine-heartbeat outage should cause
    /// a new critical notification. Dashboard query timeouts with a healthy
    /// heartbeat are normal load shedding, not Signal task failures.
    /// </summary>
    public bool DashboardFailed(DateTimeOffset now,bool heartbeatReachable)
    {
        _stableDashboardSince=null;
        if(heartbeatReachable)
        {
            _firstHeartbeatFailure=null;
            _consecutiveHeartbeatFailures=0;
            return false;
        }

        _firstHeartbeatFailure??=now;
        _consecutiveHeartbeatFailures++;
        if(_consecutiveHeartbeatFailures<3 ||
           now-_firstHeartbeatFailure.Value<ConfirmationWindow ||
           (_lastCriticalAlert.HasValue &&
            now-_lastCriticalAlert.Value<DuplicateCooldown))
            return false;
        _lastCriticalAlert=now;
        return true;
    }
}
