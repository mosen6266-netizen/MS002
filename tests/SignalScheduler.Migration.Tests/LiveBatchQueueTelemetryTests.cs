using SignalScheduler.Engine.Engine;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class LiveBatchQueueTelemetryTests
{
    [Fact]
    public void AccountQueueFlagIsEphemeralScopedAndIdempotent()
    {
        var tracker=new LiveBatchQueueTelemetry();
        Assert.False(tracker.IsWaitingForAccount("group-job-1"));
        tracker.WaitingForAccount("group-job-1");
        Assert.True(tracker.IsWaitingForAccount("group-job-1"));
        Assert.False(tracker.IsWaitingForAccount("group-job-2"));
        tracker.Clear("group-job-1");
        tracker.Clear("group-job-1");
        Assert.False(tracker.IsWaitingForAccount("group-job-1"));
    }
}
