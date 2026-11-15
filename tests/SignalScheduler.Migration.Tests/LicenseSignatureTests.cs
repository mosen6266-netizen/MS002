using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SignalScheduler.Engine.Licensing;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class LicenseSignatureTests
{
    [Fact]
    public void ValidP256LeaseIsAccepted_AndTamperIsRejected()
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string device="test-device-id";
        var issued=DateTimeOffset.UtcNow.ToUnixTimeSeconds()-30;
        var data=new
        {
            v=1,license_id="license-1",card_type="month",
            device_hash=device,issued_at=issued,expires_at=issued+2592000,
            lease_until=issued+3600,max_devices=1,server_version="test"
        };
        var lease=B64(JsonSerializer.SerializeToUtf8Bytes(data));
        var signature=B64(key.SignData(Encoding.UTF8.GetBytes(lease),
            HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var parts=key.ExportParameters(false);
        using var publicKey=JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            kty="EC",crv="P-256",x=B64(parts.Q.X!),y=B64(parts.Q.Y!)
        }));
        var claims=LicenseLeaseVerifier.Verify(
            lease,signature,publicKey.RootElement,device,null,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.Equal("month",claims.CardType);
        Assert.True(claims.LeaseUntil>DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        Assert.Throws<CryptographicException>(()=>
            LicenseLeaseVerifier.Verify(lease+"x",signature,
                publicKey.RootElement,device,null,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        Assert.Throws<CryptographicException>(()=>
            LicenseLeaseVerifier.Verify(lease,signature,
                publicKey.RootElement,"wrong-device",null,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        Assert.Throws<CryptographicException>(()=>
            LicenseLeaseVerifier.Verify(lease,signature,
                publicKey.RootElement,device,new string('0',64),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    [Fact]
    public void ExpiredOrExcessivelyLongSignedLeaseIsRejected()
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload=new
        {
            v=1,license_id="license-2",card_type="permanent",
            device_hash="device",issued_at=now-100,
            expires_at=0L,lease_until=now+12*3600,
            max_devices=1,server_version="test"
        };
        var lease=B64(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signature=B64(key.SignData(Encoding.UTF8.GetBytes(lease),
            HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var parts=key.ExportParameters(false);
        using var pub=JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            kty="EC",crv="P-256",x=B64(parts.Q.X!),y=B64(parts.Q.Y!)
        }));
        Assert.Throws<CryptographicException>(()=>
            LicenseLeaseVerifier.Verify(lease,signature,pub.RootElement,
                "device",null,now));
        Assert.Throws<CryptographicException>(()=>
            LicenseLeaseVerifier.Verify(lease,signature,pub.RootElement,
                "device",null,now+24*3600));
    }

    [Theory]
    [InlineData(-86400L)]
    [InlineData(86400L)]
    public void SignedOnlineLeaseIgnoresWindowsClockDrift_OfflineDoesNot(long driftSeconds)
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issued=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const string device="clock-drift-device";
        var payload=new
        {
            v=1,license_id="license-clock-test",card_type="month",
            device_hash=device,issued_at=issued,expires_at=issued+2592000,
            lease_until=issued+6*3600,max_devices=1,server_version="1.4.1"
        };
        var lease=B64(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signature=B64(key.SignData(Encoding.UTF8.GetBytes(lease),
            HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var ec=key.ExportParameters(false);
        using var pub=JsonDocument.Parse(JsonSerializer.Serialize(new{
            kty="EC",crv="P-256",x=B64(ec.Q.X!),y=B64(ec.Q.Y!)
        }));
        // Activation and online refresh must succeed even when a user has
        // accidentally set their Windows clock a day ahead or behind UTC.
        var claims=LicenseLeaseVerifier.Verify(
            lease,signature,pub.RootElement,device,null,
            issued+driftSeconds,trustedOnlineResponse:true);
        Assert.Equal("license-clock-test",claims.LicenseId);
        // An offline cache MUST NOT be extended by the signed issue time:
        // the desktop clock still bounds the offline lease's expiration.
        Assert.Throws<CryptographicException>(()=>
            LicenseLeaseVerifier.Verify(lease,signature,pub.RootElement,
                device,null,issued+driftSeconds));
    }

    [Fact]
    public void OnlineLeaseStillRejectsSignatureValidButExpiredCard()
    {
        using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var issued=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload=new{
            v=1,license_id="expired-card",card_type="test",
            device_hash="device",issued_at=issued,
            expires_at=issued-10,lease_until=issued+3600
        };
        var lease=B64(JsonSerializer.SerializeToUtf8Bytes(payload));
        var sig=B64(key.SignData(Encoding.UTF8.GetBytes(lease),
            HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var ec=key.ExportParameters(false);
        using var pub=JsonDocument.Parse(JsonSerializer.Serialize(new{
            kty="EC",crv="P-256",x=B64(ec.Q.X!),y=B64(ec.Q.Y!)
        }));
        Assert.Throws<CryptographicException>(()=>
            LicenseLeaseVerifier.Verify(lease,sig,pub.RootElement,
                "device",null,issued+86400,trustedOnlineResponse:true));
    }

    static string B64(byte[] bytes)=>Convert.ToBase64String(bytes)
        .TrimEnd('=').Replace('+','-').Replace('/','_');
}
