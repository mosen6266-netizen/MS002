using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SignalScheduler.Engine.Signal;
using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class SignalSendRpcContractTests
{
    sealed class CaptureHandler : HttpMessageHandler
    {
        public JsonElement Captured {get;private set;}
        public int Requests {get;private set;}
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal(HttpMethod.Post,request.Method);
            Assert.Equal("/api/v1/rpc",request.RequestUri?.AbsolutePath);
            var raw=await request.Content!.ReadAsStringAsync(cancellationToken);
            using var json=JsonDocument.Parse(raw);
            Captured=json.RootElement.Clone();
            var id=Captured.GetProperty("id").GetString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content=new StringContent(JsonSerializer.Serialize(new
                {
                    jsonrpc="2.0",result=new{timestamp=1732345678901L},id
                }),Encoding.UTF8,"application/json")
            };
        }
    }

    [Fact]
    public async Task FirstScriptMessage_UsesSignalCliGroupIdsArray_NotDeprecatedGroupId()
    {
        var handler=new CaptureHandler();
        using var http=new HttpClient(handler)
        {
            BaseAddress=new Uri("http://127.0.0.1:7583/")
        };
        var transport=new SignalCliTransport(http,()=>true);
        var identity=new DispatchIdentity(
            "job1","run1",0,0,"selected-group","+491234567890","hash1");
        var result=await transport.SendAsync(identity,
            "signal-structured:{\"Message\":\"第一句发送测试\",\"AttachmentPath\":null}",
            CancellationToken.None);

        Assert.Equal(SignalDeliveryOutcome.Confirmed,result.Outcome);
        Assert.Equal(1,handler.Requests);
        var request=handler.Captured;
        Assert.Equal("send",request.GetProperty("method").GetString());
        var parameters=request.GetProperty("params");
        Assert.Equal("+491234567890",parameters.GetProperty("account").GetString());
        Assert.False(parameters.TryGetProperty("groupId",out _));
        var groups=parameters.GetProperty("groupIds");
        Assert.Equal(JsonValueKind.Array,groups.ValueKind);
        Assert.Equal("selected-group",Assert.Single(groups.EnumerateArray()).GetString());
        Assert.Equal("第一句发送测试",parameters.GetProperty("message").GetString());
    }
}
