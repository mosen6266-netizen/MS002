namespace SignalScheduler.Shared;

/// <summary>
/// Read-only classification for the task monitoring page. The task journal,
/// rather than this display helper, is authoritative for delivery outcomes.
/// </summary>
public static class LiveTaskMonitor
{
    public static bool NeedsAttention(LiveBatchItem item)
    {
        if(item.State is "RecoveryRequired" or "Failed")return true;
        if(item.State is not ("Paused" or "WaitingSignal" or "Stopping"))return false;
        return new[]{"异常","断开","失败","重启","授权","核对","不可用","超时"}
            .Any(word=>item.Detail.Contains(word,StringComparison.Ordinal));
    }

    /// <summary>
    /// A terminal task belongs in History, never in Current Running Tasks.
    /// Paused, WaitingSignal, Stopping and RecoveryRequired are NOT terminal:
    /// operators must still be able to inspect and handle them here.
    /// </summary>
    public static bool IsCurrent(LiveBatchItem item)=>item.State is
        "Created" or "Running" or "Paused" or "WaitingSignal"
        or "Stopping" or "RecoveryRequired";

    public static bool IsVisible(LiveBatchItem item,string filter)
    {
        if(!IsCurrent(item))return false;
        return filter switch
        {
            "active"=>true,
            "attention"=>NeedsAttention(item),
            "paused"=>item.State is "Paused" or "WaitingSignal"
                or "Stopping" or "RecoveryRequired",
            // "all", legacy "finished" and unrecognized filters must never
            // bring a terminal item back into the active monitor.
            _=>true
        };
    }
}
