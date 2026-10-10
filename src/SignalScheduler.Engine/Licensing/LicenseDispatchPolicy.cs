using SignalScheduler.Shared;

namespace SignalScheduler.Engine.Licensing;

/// <summary>
/// Authorize a single pending dispatch. The manager creates "active-offline"
/// ONLY after verifying the server-signed, device-bound cached lease.
/// Temporary connection loss must not interrupt a still valid signed lease.
/// Revoked, invalid, expired, malformed and unactivated states always deny.
/// </summary>
public static class LicenseDispatchPolicy
{
    public static bool CanDispatch(LicensePublicStatus? status,long nowUnixSeconds)
    {
        if(status is null || !status.HasSavedLicense ||
           status.LeaseUntil<=nowUnixSeconds ||
           status.ExpiresAt<=nowUnixSeconds)
            return false;

        return (status.State=="active" && status.ServerReachable) ||
               (status.State=="active-offline" && !status.ServerReachable);
    }
}
