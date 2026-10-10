using SignalScheduler.Engine.Licensing;
using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class LicenseDispatchPolicyTests
{
    const long Now=1_800_000_000;
    static LicensePublicStatus Valid(string state="active",bool reachable=true)=>
        new(state,"", "月卡",Now-3600,Now+86400,Now+120,reachable,true);

    [Fact]
    public void OnlineSignedLeaseAllowsDispatch()=>
        Assert.True(LicenseDispatchPolicy.CanDispatch(Valid(),Now));

    [Fact]
    public void TemporaryNetworkOutageAllowsOnlyPreviouslyVerifiedSignedLease()=>
        Assert.True(LicenseDispatchPolicy.CanDispatch(
            Valid("active-offline",false),Now));

    [Theory]
    [InlineData("invalid",false)]
    [InlineData("offline-expired",false)]
    [InlineData("unactivated",false)]
    [InlineData("error",false)]
    [InlineData("active",false)]
    [InlineData("active-offline",true)]
    public void RejectedOrInconsistentStateNeverAllowsDispatch(
        string state,bool reachable)=>
        Assert.False(LicenseDispatchPolicy.CanDispatch(
            Valid(state,reachable),Now));

    [Fact]
    public void LeaseExpiryDeniesImmediatelyEvenWhenServerOffline()=>
        Assert.False(LicenseDispatchPolicy.CanDispatch(
            Valid("active-offline",false),Now+120));

    [Fact]
    public void PerpetualCardExpiryZeroStillHonorsServerSignedLease()
    {
        Assert.True(LicenseDispatchPolicy.CanDispatch(
            Valid() with {ExpiresAt=0},Now));
        Assert.True(LicenseDispatchPolicy.CanDispatch(
            Valid("active-offline",false) with {ExpiresAt=0},Now));
        Assert.False(LicenseDispatchPolicy.CanDispatch(
            Valid() with {ExpiresAt=0,LeaseUntil=Now},Now));
    }

    [Fact]
    public void CardExpiryAndMissingLicenseCannotBeOverridden() 
    {
        Assert.False(LicenseDispatchPolicy.CanDispatch(
            Valid("active-offline",false) with {ExpiresAt=Now},Now));
        Assert.False(LicenseDispatchPolicy.CanDispatch(
            Valid("active-offline",false) with {HasSavedLicense=false},Now));
        Assert.False(LicenseDispatchPolicy.CanDispatch(null,Now));
    }
}
