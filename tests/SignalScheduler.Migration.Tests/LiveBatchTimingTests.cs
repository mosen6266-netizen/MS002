using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class LiveBatchTimingTests
{
    const long Now=1760000000000;
    static LiveBatchItem Job(
        string state,long due=0,long remaining=0,string phase="")=>
        new("job-1","测试剧本","group-1","测试群",state,1,3,due,
            "测试状态",remaining,phase);

    [Fact]
    public void RunningCountdownDecreasesAndEarliestFinishIsOnlyALowerBound()
    {
        var snapshot=Job("Running",Now+11000,Now>0?75000:0);
        var first=LiveBatchTiming.Format(snapshot,Now,Now);
        Assert.Equal("等待间隔",first.Phase);
        Assert.Equal("11 秒",first.NextCountdown);
        Assert.Equal("至少 00:01:15",first.RemainingEstimate);
        Assert.StartsWith("不早于 ",first.EarliestFinish);

        var later=LiveBatchTiming.Format(snapshot,Now,Now+6000);
        Assert.Equal("5 秒",later.NextCountdown);
        Assert.Equal("至少 00:01:09",later.RemainingEstimate);
    }

    [Theory]
    [InlineData("Paused","已暂停","恢复后重算")]
    [InlineData("RecoveryRequired","禁止自动续发","暂停估算")]
    [InlineData("WaitingSignal","网络等待","恢复后重算")]
    [InlineData("Stopped","已停止","不再计算")]
    [InlineData("Completed","已完成","已完成")]
    public void InactiveJobsNeverShowRunningCompletionCountdown(
        string state,string next,string remainder)
    {
        var snapshot=Job(state,Now+30000,120000);
        var first=LiveBatchTiming.Format(snapshot,Now,Now);
        var later=LiveBatchTiming.Format(snapshot,Now,Now+120000);
        Assert.Equal(next,first.NextCountdown);
        Assert.Equal(next,later.NextCountdown);
        Assert.Equal(remainder,later.RemainingEstimate);
        Assert.Equal("—",later.EarliestFinish);
    }

    [Theory]
    [InlineData("Prepared","准备发送","正在准备")]
    [InlineData("Sending","请求发送中","等待 Signal 响应")]
    [InlineData("Confirmed","已获发送回执","正在记录下一步")]
    [InlineData("Unknown","发送结果待核对","禁止自动续发")]
    public void InFlightDispatchNeverClaimsZeroSecondsOrKnownCompletion(
        string dispatch,string phase,string next)
    {
        var state=LiveBatchTiming.Format(
            Job("Running",Now-1000,0,dispatch),Now,Now+10000);
        Assert.Equal(phase,state.Phase);
        Assert.Equal(next,state.NextCountdown);
        Assert.Equal("—",state.EarliestFinish);
    }

    [Fact]
    public void OverdueAndStaleRunningJobsAreNotMisrepresentedAsSent()
    {
        var snapshot=Job("Running",Now-1000,9000);
        var overdue=LiveBatchTiming.Format(snapshot,Now,Now+2000);
        Assert.Equal("等待调度或账号",overdue.Phase);
        Assert.Equal("等待调度",overdue.NextCountdown);
        Assert.NotEqual("已完成",overdue.RemainingEstimate);

        var stale=LiveBatchTiming.Format(snapshot,Now,Now+16000);
        Assert.Equal("状态等待刷新",stale.Phase);
        Assert.Equal("状态已过期",stale.NextCountdown);
        Assert.Equal("重新连接后估算",stale.RemainingEstimate);
    }

    [Fact]
    public void EstimatesOverOneDayDoNotWrapAtTwentyFourHours()
    {
        var state=LiveBatchTiming.Format(
            Job("Running",Now+5000,26L*60*60*1000),Now,Now);
        Assert.Equal("至少 1天 02:00:00",state.RemainingEstimate);
    }

    [Fact]
    public void RestoringPausedJobRequiresNewRunningSnapshotBeforeAnyCountdown()
    {
        var paused=LiveBatchTiming.Format(
            Job("Paused",Now+20000,30000),Now,Now+1000);
        Assert.Equal("已暂停",paused.NextCountdown);
        var resumed=LiveBatchTiming.Format(
            Job("Running",Now+22000,52000),Now+2000,Now+2000);
        Assert.Equal("20 秒",resumed.NextCountdown);
        Assert.StartsWith("至少 ",resumed.RemainingEstimate);
    }
}
