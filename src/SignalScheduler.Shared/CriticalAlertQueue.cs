namespace SignalScheduler.Shared;

/// <summary>
/// FIFO for human-visible critical alerts. Presentations are serialized and
/// unacknowledged items survive temporary window unload/reload; no task,
/// Signal delivery or persisted journal state is affected.
/// </summary>
public sealed class CriticalAlertQueue
{
    readonly LinkedList<string> _waiting=new();
    string? _presenting;

    public static string DialogText(string message)=>
        "发现任务或 Signal 异常，请及时处理。\n\n"+message;

    public int PendingCount=>_waiting.Count+(_presenting is null?0:1);
    public bool IsPresenting=>_presenting is not null;

    public bool Enqueue(string message)
    {
        if(string.IsNullOrWhiteSpace(message))return false;
        _waiting.AddLast(message);
        return true;
    }

    public bool TryBegin(out string message)
    {
        if(_presenting is not null || _waiting.First is null)
        {
            message="";
            return false;
        }
        message=_waiting.First.Value;
        _waiting.RemoveFirst();
        _presenting=message;
        return true;
    }

    public void DeferCurrent()
    {
        if(_presenting is null)return;
        // Preserve FIFO when a modal cannot yet be shown.
        _waiting.AddFirst(_presenting);
        _presenting=null;
    }

    public bool CompleteCurrent()
    {
        if(_presenting is null)return false;
        _presenting=null;
        return true;
    }
}
