using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class LiveTaskMonitorTests
{
    static LiveBatchItem Item(string state,string detail="")=>
        new("job","剧本","group","群",state,1,3,0,detail);

    [Fact]
    public void DefaultFilterShowsRunningPausedStoppedAndFailedForImmediateAttention()
    {
        foreach(var state in new[]{"Running","Paused","WaitingSignal","Stopping",
                     "RecoveryRequired","Stopped","Failed"})
            Assert.True(LiveTaskMonitor.IsVisible(Item(state),"active"));
        Assert.False(LiveTaskMonitor.IsVisible(Item("Completed"),"active"));
    }

    [Fact]
    public void AttentionFilterOnlySurfacesMeaningfulFaults()
    {
        Assert.True(LiveTaskMonitor.NeedsAttention(Item("RecoveryRequired")));
        Assert.True(LiveTaskMonitor.NeedsAttention(Item("Failed")));
        Assert.True(LiveTaskMonitor.NeedsAttention(Item("Paused","Signal 后台重启")));
        Assert.True(LiveTaskMonitor.NeedsAttention(Item("Paused","授权无法连接")));
        Assert.True(LiveTaskMonitor.IsVisible(Item("Paused","等待人工核对"),"attention"));
        Assert.False(LiveTaskMonitor.NeedsAttention(Item("Paused","用户手动暂停")));
        Assert.False(LiveTaskMonitor.NeedsAttention(Item("Completed")));
        Assert.False(LiveTaskMonitor.IsVisible(Item("Running"),"attention"));
    }

    [Fact]
    public void FinishedFilterIsSeparateFromTasksThatCanStillRun()
    {
        Assert.True(LiveTaskMonitor.IsVisible(Item("Completed"),"finished"));
        Assert.True(LiveTaskMonitor.IsVisible(Item("Stopped"),"finished"));
        Assert.True(LiveTaskMonitor.IsVisible(Item("Failed"),"finished"));
        Assert.False(LiveTaskMonitor.IsVisible(Item("Paused"),"finished"));
        Assert.True(LiveTaskMonitor.IsVisible(Item("Paused"),"all"));
    }
}
