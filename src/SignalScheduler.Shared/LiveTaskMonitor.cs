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

    public static bool IsVisible(LiveBatchItem item,string filter)=>filter switch
    {
        // Default overview includes stopped and failed jobs so a red
        // status marker is not hidden by the filter itself.
        "active"=>item.State is "Running" or "Paused" or "WaitingSignal"
            or "Stopping" or "RecoveryRequired" or "Stopped" or "Failed",
        "attention"=>NeedsAttention(item),
        "finished"=>item.State is "Completed" or "Stopped" or "Failed",
        _=>true
    };
}
