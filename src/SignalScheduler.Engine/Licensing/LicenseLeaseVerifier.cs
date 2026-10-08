using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SignalScheduler.Engine.Licensing;

public sealed record VerifiedLicenseLease(
    string LicenseId,string CardType,string DeviceId,long IssuedAt,
    long ExpiresAt,long LeaseUntil,string PublicKeyPin);

/// <summary>
/// Verifies the existing Cloudflare license Worker's ECDSA P-256 JWS-like
/// response (base64url payload plus base64url IEEE-P1363 signature).
/// No private signing key or admin credential is embedded in Windows builds.
/// </summary>
public static class LicenseLeaseVerifier
{
    public static VerifiedLicenseLease Verify(
        string lease,string signature,JsonElement publicKey,
        string expectedDevice,string? pinnedPublicKey,long nowSeconds)
    {
        if(string.IsNullOrWhiteSpace(lease) || lease.Length>16000 ||
            string.IsNullOrWhiteSpace(signature) || signature.Length>1024)
            throw new InvalidDataException("授权凭据格式错误。");

        if(publicKey.ValueKind!=JsonValueKind.Object ||
            publicKey.GetProperty("kty").GetString()!="EC" ||
            publicKey.GetProperty("crv").GetString()!="P-256")
            throw new CryptographicException("不是受支持的服务器签名公钥。");

        var x=Decode(publicKey.GetProperty("x").GetString()!);
        var y=Decode(publicKey.GetProperty("y").GetString()!);
        var sig=Decode(signature);
        if(x.Length!=32 || y.Length!=32 || sig.Length!=64)
            throw new CryptographicException("服务器公钥或签名长度不正确。");

        var pin=Convert.ToHexString(SHA256.HashData(
            x.Concat(y).ToArray())).ToLowerInvariant();
        if(pinnedPublicKey is not null &&
            !string.Equals(pin,pinnedPublicKey,StringComparison.Ordinal))
            throw new CryptographicException("服务器签名公钥与首次激活时不一致。");

        using(var ecdsa=ECDsa.Create(new ECParameters
        {
            Curve=ECCurve.NamedCurves.nistP256,
            Q=new ECPoint{X=x,Y=y}
        }))
        {
            if(!ecdsa.VerifyData(Encoding.UTF8.GetBytes(lease),sig,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw new CryptographicException("授权凭据签名不正确。");
        }

        using var doc=JsonDocument.Parse(Decode(lease));
        var data=doc.RootElement;
        if(data.GetProperty("v").GetInt32()!=1)
            throw new InvalidDataException("授权凭据版本不支持。");

        var device=data.GetProperty("device_hash").GetString()!;
        if(string.IsNullOrWhiteSpace(expectedDevice) || device!=expectedDevice)
            throw new CryptographicException("此卡密绑定的不是当前设备。");
        var issued=data.GetProperty("issued_at").GetInt64();
        var until=data.GetProperty("lease_until").GetInt64();
        var expires=data.GetProperty("expires_at").GetInt64();
        if(issued>nowSeconds+300 || until<=issued ||
           until>issued+6*3600+300 || until<=nowSeconds ||
           (expires>0 && (expires<=nowSeconds || until>expires)))
            throw new CryptographicException("授权凭据过期或时间信息异常。");

        return new VerifiedLicenseLease(
            data.GetProperty("license_id").GetString()!,
            data.GetProperty("card_type").GetString()!,
            device,issued,expires,until,pin);
    }

    static byte[] Decode(string text)
    {
        var s=text.Replace('-','+').Replace('_','/');
        if(s.Length%4==1) throw new FormatException("无效的 Base64URL 编码。");
        s=s.PadRight((s.Length+3)/4*4,'=');
        return Convert.FromBase64String(s);
    }
}
