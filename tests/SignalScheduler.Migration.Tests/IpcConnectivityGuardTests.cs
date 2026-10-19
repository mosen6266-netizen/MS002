using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class IpcConnectivityGuardTests
{
    static readonly DateTimeOffset T0=new(2026,10,19,12,0,0,TimeSpan.Zero);

    [Fact]
    public void DashboardTimeoutWithLiveHeartbeatNeverTriggersCriticalAlarm()
    {
        var guard=new IpcConnectivityGuard();
        for(var i=0;i<30;i++)
            Assert.False(guard.DashboardFailed(T0.AddSeconds(i*5),true));
        Assert.Equal(0,guard.ConsecutiveHeartbeatFailures);
    }

    [Fact]
    public void SingleOrShortHeartbeatMissDoesNotInterruptWork()
    {
        var guard=new IpcConnectivityGuard();
        Assert.False(guard.DashboardFailed(T0,false));
        Assert.False(guard.DashboardFailed(T0.AddSeconds(5),false));
        Assert.False(guard.DashboardFailed(T0.AddSeconds(8),false));
        Assert.True(guard.DashboardFailed(T0.AddSeconds(12),false));
        Assert.False(guard.DashboardFailed(T0.AddSeconds(17),false));
    }

    [Fact]
    public void RealOutageRaisesOneAlertAndSuppressesFlappingDuplicates()
    {
        var guard=new IpcConnectivityGuard();
        guard.DashboardFailed(T0,false);
        guard.DashboardFailed(T0.AddSeconds(5),false);
        Assert.True(guard.DashboardFailed(T0.AddSeconds(12),false));
        guard.DashboardSucceeded(T0.AddSeconds(17));
        guard.DashboardSucceeded(T0.AddSeconds(22));
        guard.DashboardFailed(T0.AddSeconds(27),false);
        guard.DashboardFailed(T0.AddSeconds(32),false);
        Assert.False(guard.DashboardFailed(T0.AddSeconds(42),false));
        Assert.False(guard.DashboardFailed(T0.AddSeconds(50),false));
        Assert.True(guard.DashboardFailed(T0.AddMinutes(11),false));
    }

    [Fact]
    public void HealthyHeartbeatResetsFailureCountButNotAlertCooldown()
    {
        var guard=new IpcConnectivityGuard();
        guard.DashboardFailed(T0,false);
        guard.DashboardFailed(T0.AddSeconds(5),false);
        Assert.False(guard.DashboardFailed(T0.AddSeconds(8),true));
        Assert.Equal(0,guard.ConsecutiveHeartbeatFailures);
        Assert.False(guard.DashboardFailed(T0.AddSeconds(13),false));
    }

    [Fact]
    public void SustainedGoodDashboardCanClearOldIncident()
    {
        var guard=new IpcConnectivityGuard();
        guard.DashboardFailed(T0,false);
        guard.DashboardFailed(T0.AddSeconds(5),false);
        Assert.True(guard.DashboardFailed(T0.AddSeconds(11),false));
        guard.DashboardSucceeded(T0.AddSeconds(20));
        guard.DashboardSucceeded(T0.AddSeconds(81));
        guard.DashboardFailed(T0.AddSeconds(82),false);
        guard.DashboardFailed(T0.AddSeconds(88),false);
        Assert.True(guard.DashboardFailed(T0.AddSeconds(93),false));
    }
}
