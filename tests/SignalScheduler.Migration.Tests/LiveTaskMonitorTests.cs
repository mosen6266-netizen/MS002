using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class LiveTaskMonitorTests
{
    static LiveBatchItem Item(string state,string detail="")=>
        new("job","剧本","group","群",state,1,3,0,detail);

    [Fact]
    public void CurrentMonitorOnlyShowsTasksThatHaveNotReachedTerminalState()
    {
        foreach(var state in new[]{"Created","Running","Paused","WaitingSignal",
                     "Stopping","RecoveryRequired"})
            Assert.True(LiveTaskMonitor.IsVisible(Item(state),"active"));
        foreach(var state in new[]{"Stopped","Completed","Failed"})
            Assert.False(LiveTaskMonitor.IsVisible(Item(state),"active"));
    }

    [Fact]
    public void AttentionFilterOnlySurfacesMeaningfulFaults()
    {
        Assert.True(LiveTaskMonitor.NeedsAttention(Item("RecoveryRequired")));
        Assert.True(LiveTaskMonitor.NeedsAttention(Item("Failed")));
        Assert.True(LiveTaskMonitor.NeedsAttention(Item("Paused","Signal 后台重启")));
        Assert.True(LiveTaskMonitor.NeedsAttention(Item("Paused","授权无法连接")));
        Assert.True(LiveTaskMonitor.IsVisible(Item("Paused","等待人工核对"),"attention"));
        Assert.False(LiveTaskMonitor.IsVisible(Item("Failed","已彻底失败"),"attention"));
        Assert.False(LiveTaskMonitor.NeedsAttention(Item("Paused","用户手动暂停")));
        Assert.False(LiveTaskMonitor.NeedsAttention(Item("Completed")));
        Assert.False(LiveTaskMonitor.IsVisible(Item("Running"),"attention"));
    }

    [Fact]
    public void FinishedJobsNeverReappearInCurrentMonitorForAnyFilter()
    {
        foreach(var filter in new[]{"active","attention","paused","all","finished","unexpected"})
        foreach(var state in new[]{"Completed","Stopped","Failed"})
            Assert.False(LiveTaskMonitor.IsVisible(Item(state),filter));
        Assert.True(LiveTaskMonitor.IsVisible(Item("Paused"),"paused"));
        Assert.True(LiveTaskMonitor.IsVisible(Item("RecoveryRequired"),"paused"));
        Assert.True(LiveTaskMonitor.IsVisible(Item("Running"),"all"));
        Assert.False(LiveTaskMonitor.IsVisible(Item("Running"),"paused"));
    }
}
