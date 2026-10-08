using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SignalScheduler.Engine;
using SignalScheduler.Engine.Licensing;
using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class LicenseManagerTests
{
    static readonly CancellationToken Ct=CancellationToken.None;

    [Fact]
    public async Task ActivateAndCheckKeepLicenseAndDeviceIdEncryptedAcrossRestart()
    {
        var root=Path.Combine(Path.GetTempPath(),"SignalLicenseTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths=RuntimePaths.ForTesting(root,Path.Combine(root,"data.db"));
        using var server=new FakeServer();
        using var client=new HttpClient(server)
        {
            BaseAddress=new Uri("https://example.invalid/"),
            Timeout=TimeSpan.FromSeconds(5)
        };
        var manager=new LicenseManager(paths,client);
        var active=await manager.ActivateAsync(
            new LicenseActivationRequest("ABC-12345"),Ct);
        Assert.Equal("active",active.State);
        Assert.True(active.HasSavedLicense);
        Assert.Equal("月卡",active.TypeName);

        var raw=await File.ReadAllBytesAsync(
            Path.Combine(root,"license-v8.dpapi"),Ct);
        Assert.DoesNotContain("ABC-12345",Encoding.UTF8.GetString(raw));
        Assert.NotEmpty(await File.ReadAllBytesAsync(
            Path.Combine(root,"device-v8.dpapi"),Ct));

        Assert.Equal("active",(await manager.CheckAsync(Ct)).State);
        var restarted=new LicenseManager(paths,client);
        var after=await restarted.CheckAsync(Ct);
        Assert.Equal("active",after.State);
        Assert.Equal(1,server.ActivateRequests);
        Assert.Equal(2,server.CheckRequests);
        Assert.Single(server.SeenDeviceIds.Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task InvalidSignatureNeverSavesAnActivatedCard()
    {
        var root=Path.Combine(Path.GetTempPath(),"SignalLicenseTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths=RuntimePaths.ForTesting(root,Path.Combine(root,"data.db"));
        using var server=new FakeServer { CorruptSignature=true };
        using var client=new HttpClient(server)
        {
            BaseAddress=new Uri("https://example.invalid/")
        };
        var manager=new LicenseManager(paths,client);
        await Assert.ThrowsAsync<CryptographicException>(()=>
            manager.ActivateAsync(new LicenseActivationRequest("ABC-12345"),Ct));
        Assert.False(File.Exists(Path.Combine(root,"license-v8.dpapi")));
    }

    sealed class FakeServer : HttpMessageHandler
    {
        readonly ECDsa _key=ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public int ActivateRequests {get;private set;}
        public int CheckRequests {get;private set;}
        public bool CorruptSignature {get;set;}
        public List<string> SeenDeviceIds {get;}=new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,CancellationToken ct)
        {
            var json=await request.Content!.ReadAsStringAsync(ct);
            using var doc=JsonDocument.Parse(json);
            var dev=doc.RootElement.GetProperty("device_id").GetString()!;
            SeenDeviceIds.Add(dev);
            if(request.RequestUri!.AbsolutePath.EndsWith("/activate",
                StringComparison.Ordinal))ActivateRequests++;
            else if(request.RequestUri.AbsolutePath.EndsWith("/check",
                StringComparison.Ordinal))CheckRequests++;
            else throw new InvalidOperationException("Unexpected request");

            var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var lease=B64(JsonSerializer.SerializeToUtf8Bytes(new
            {
                v=1,license_id="test-license",card_type="month",
                device_hash=dev,issued_at=now,expires_at=now+86400L,
                lease_until=now+3600L,max_devices=2,server_version="unit"
            }));
            var sig=B64(_key.SignData(Encoding.UTF8.GetBytes(lease),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            if(CorruptSignature)sig=B64(new byte[64]);
            var pub=_key.ExportParameters(false);
            var body=JsonSerializer.Serialize(new
            {
                ok=true,card_type="month",type_name="月卡",
                activated_at=now,expires_at=now+86400L,
                lease,signature=sig,
                public_key=new
                {
                    kty="EC",crv="P-256",x=B64(pub.Q.X!),y=B64(pub.Q.Y!)
                }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content=new StringContent(body,Encoding.UTF8,"application/json")
            };
        }

        static string B64(byte[] bytes)=>Convert.ToBase64String(bytes)
            .TrimEnd('=').Replace('+','-').Replace('/','_');
        protected override void Dispose(bool disposing)
        {
            if(disposing)_key.Dispose();
            base.Dispose(disposing);
        }
    }
}
