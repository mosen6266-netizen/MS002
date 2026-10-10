using System.Net;
using System.Net.Http;
using System.Text;
using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class UpdateManifestTests
{
    static VerifiedUpdateManifest Valid()=>new(
        "8.0.0-beta.8",551,38000000001,
        new string('a',40),new string('b',64));

    [Fact]
    public void ValidatedManifestAlwaysDerivesAnOfficialActionsUrl()
    {
        var value=Valid();
        var parsed=UpdateManifestCodec.Parse(UpdateManifestCodec.Serialize(value));
        Assert.NotNull(parsed);
        Assert.Equal(value,parsed);
        Assert.Equal("https://github.com/mosen6266-netizen/MS002/actions/runs/38000000001",
            parsed!.RunUrl);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"version\":\"8.0.0-beta.8\",\"build\":\"fake\",\"runId\":42}")]
    [InlineData("{\"version\":\"https://evil.invalid\",\"build\":551,\"runId\":42}")]
    [InlineData("{\"version\":\"8.0.0-beta.8\",\"build\":-2,\"runId\":42}")]
    public void InvalidOrUntrustedManifestIsNotAccepted(string json)
    {
        Assert.Null(UpdateManifestCodec.Parse(json));
    }

    sealed class StaticFeedHandler : HttpMessageHandler
    {
        public int RawRequests {get;private set;}
        public int RestRequests {get;private set;}
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,CancellationToken ct)
        {
            if(request.RequestUri!.Host=="raw.githubusercontent.com")
            {
                RawRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){
                    Content=new StringContent(UpdateManifestCodec.Serialize(Valid()),
                        Encoding.UTF8,"application/json")
                });
            }
            RestRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden){
                Content=new StringContent("API rate limit exceeded")
            });
        }
    }

    [Fact]
    public async Task PrimaryUpdateCheckStillWorksWhenGithubRestWouldReturn403()
    {
        var handler=new StaticFeedHandler();
        using var http=new HttpClient(handler);
        var result=await UpdateFeedClient.FetchAsync(http,CancellationToken.None);
        Assert.Equal(551,result.Build);
        Assert.Equal("8.0.0-beta.8",result.Version);
        Assert.Equal(1,handler.RawRequests);
        Assert.Equal(0,handler.RestRequests);
    }

    [Fact]
    public async Task BadPrimaryFeedFailsWithoutInventingAVersion()
    {
        using var http=new HttpClient(new InvalidHandler());
        await Assert.ThrowsAsync<InvalidDataException>(()=>
            UpdateFeedClient.FetchAsync(http,CancellationToken.None));
    }

    sealed class InvalidHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,CancellationToken ct)=>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){
                Content=new StringContent("{\"error\":\"not published\"}")
            });
    }
}
