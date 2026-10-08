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

    static string B64(byte[] bytes)=>Convert.ToBase64String(bytes)
        .TrimEnd('=').Replace('+','-').Replace('/','_');
}
