using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class VerifiedInstallerDownloaderTests
{
    const string Url="https://github.com/mosen6266-netizen/MS002/releases/download/v8.0.0-ui2.3/SignalScheduler_Setup_V8.0.0-ui2.3.exe";
    sealed class TestHandler : HttpMessageHandler
    {
        readonly byte[] _bytes;
        readonly bool _alwaysCorrupt;
        public int Calls;
        public TestHandler(byte[] bytes,bool alwaysCorrupt=false)
        { _bytes=bytes;_alwaysCorrupt=alwaysCorrupt; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Calls==1&&!_alwaysCorrupt
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content=new ByteArrayContent(_alwaysCorrupt?new byte[]{4,3,2}:_bytes)
                });
        }
    }

    [Fact]
    public async Task TransientFailedGetIsRetriedAndShaValidated()
    {
        var data=new byte[]{1,2,3,4,5,6};
        var expected=Convert.ToHexString(SHA256.HashData(data));
        var folder=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var handler=new TestHandler(data);
            using var http=new HttpClient(handler);
            var final=Path.Combine(folder,"installer.exe");
            await VerifiedInstallerDownloader.DownloadAsync(http,Url,
                final+".partial",final,expected,null,CancellationToken.None);
            Assert.Equal(2,handler.Calls);
            Assert.Equal(data,await File.ReadAllBytesAsync(final));
            Assert.False(File.Exists(final+".partial"));
        }
        finally{Directory.Delete(folder,true);}
    }

    [Fact]
    public async Task IncorrectHashNeverPublishesAnInstaller()
    {
        var data=new byte[]{1,2,3};
        var expected=Convert.ToHexString(SHA256.HashData(data));
        var folder=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var final=Path.Combine(folder,"installer.exe");
            using var http=new HttpClient(new TestHandler(data,true));
            await Assert.ThrowsAsync<IOException>(()=>
                VerifiedInstallerDownloader.DownloadAsync(http,Url,
                    final+".partial",final,expected,null,CancellationToken.None));
            Assert.False(File.Exists(final));
            Assert.False(File.Exists(final+".partial"));
        }
        finally{Directory.Delete(folder,true);}
    }
}
