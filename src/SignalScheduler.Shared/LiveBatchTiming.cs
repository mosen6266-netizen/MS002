namespace SignalScheduler.Shared;

/// <summary>
/// Display-only information derived from a durable task snapshot.
/// Signal acceptance, dispatch latency and account-lock contention are unknown;
/// never present these time estimates as guaranteed completion deadlines.
/// </summary>
public sealed record LiveBatchTimingDisplay(
    string Phase,string NextCountdown,string RemainingEstimate,
    string EarliestFinish,string NextSend);

public static class LiveBatchTiming
{
    static string Span(long seconds)
    {
        seconds=Math.Clamp(seconds,0,365L*24*60*60);
        var duration=TimeSpan.FromSeconds(seconds);
        return duration.Days>0
            ?$"{duration.Days}天 {duration.Hours:00}:{duration.Minutes:00}:{duration.Seconds:00}"
            :$"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    public static LiveBatchTimingDisplay Format(
        LiveBatchItem item,long receivedAtMs,long nowMs)
    {
        if(item.State=="Completed")
            return new("已完成","已完成","已完成","—","—");
        if(item.State=="Stopped")
            return new("已停止","已停止","不再计算","—","—");
        if(item.State=="RecoveryRequired")
            return new("待人工核对","禁止自动续发","暂停估算","—","—");
        if(item.State=="Paused")
            return new("已暂停","已暂停","恢复后重算","—","—");
        if(item.State=="WaitingSignal")
            return new("等待 Signal","网络等待","恢复后重算","—","—");
        if(item.State=="Failed")
            return new("异常停止","异常停止","不再计算","—","—");
        if(item.State!="Running")
            return new(StatusLabels.Task(item.State),"—","—","—","—");

        // The journal has a stronger claim about actual delivery stage than
        // the scheduler due clock. In flight, no duration can be guaranteed.
        if(item.DispatchState=="Sending")
            return new("请求发送中","等待 Signal 响应",
                "发送中，时间不确定","—","—");
        if(item.DispatchState=="Prepared")
            return new("准备发送","正在准备",
                "准备中，时间不确定","—","—");
        if(item.DispatchState=="Confirmed")
            return new("已获发送回执","正在记录下一步",
                "等待进度更新","—","—");
        if(item.DispatchState is "Unknown" or "RecoveryRequired")
            return new("发送结果待核对","禁止自动续发","暂停估算","—","—");

        // A disconnected engine may leave the desktop showing a previously
        // running task. Old timer samples must not keep counting down forever.
        if(nowMs-receivedAtMs>15000)
            return new("状态等待刷新","状态已过期",
                "重新连接后估算","—","—");

        var waitMs=item.NextDueMs-nowMs;
        var waiting=item.NextDueMs>0 && waitMs>0;
        var phase=waiting?"等待间隔":"等待调度或账号";
        var countdown=waiting
            ?$"{Math.Max(1,(waitMs+999)/1000)} 秒"
            :"等待调度";
        string next="—";
        if(waiting)
        {
            try
            {
                next=DateTimeOffset.FromUnixTimeMilliseconds(item.NextDueMs)
                    .ToLocalTime().ToString("MM-dd HH:mm:ss");
            }
            catch(ArgumentOutOfRangeException){}
        }

        if(item.EstimatedRemainingMs<=0)
            return new(phase,countdown,"无法准确预计","—",next);
        var elapsed=Math.Clamp(nowMs-receivedAtMs,0,365L*24*60*60*1000);
        var lowerBoundMs=Math.Max(0,item.EstimatedRemainingMs-elapsed);
        if(lowerBoundMs==0)
            return new(phase,countdown,"时间已到，等待确认","—",next);
        var seconds=(lowerBoundMs+999)/1000;
        var remaining="至少 "+Span(seconds);
        string finish="—";
        try
        {
            finish="不早于 "+DateTimeOffset.FromUnixTimeMilliseconds(
                checked(nowMs+lowerBoundMs)).ToLocalTime()
                .ToString("MM-dd HH:mm");
        }
        catch(ArgumentOutOfRangeException){}
        catch(OverflowException){}
        return new(phase,countdown,remaining,finish,next);
    }
}
