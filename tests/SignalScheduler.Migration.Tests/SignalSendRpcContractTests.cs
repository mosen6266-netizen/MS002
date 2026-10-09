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
        public bool StringTimestamp {get;set;}
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
                    jsonrpc="2.0",result=new{timestamp=StringTimestamp ? (object)"1732345678901" : 1732345678901L},id
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
    [Fact]
    public async Task FirstScriptMessage_AcceptsDecimalStringTimestampAcknowledgement()
    {
        var handler=new CaptureHandler{StringTimestamp=true};
        using var http=new HttpClient(handler)
        {
            BaseAddress=new Uri("http://127.0.0.1:7583/")
        };
        var transport=new SignalCliTransport(http,()=>true);
        var identity=new DispatchIdentity(
            "job2","run2",0,0,"selected-group","+491234567890","hash2");
        var result=await transport.SendAsync(identity,
            "signal-structured:{\"Message\":\"第一句发送测试\",\"AttachmentPath\":null}",
            CancellationToken.None);
        Assert.Equal(SignalDeliveryOutcome.Confirmed,result.Outcome);
        Assert.Equal("1732345678901",result.ProviderMessageId);
        Assert.Equal(1,handler.Requests);
    }

    sealed class ErrorHandler : HttpMessageHandler
    {
        public string ErrorMessage {get;set;}="UntrustedIdentityException for +491234567890";
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,CancellationToken cancellationToken)
        {
            var raw=await request.Content!.ReadAsStringAsync(cancellationToken);
            using var json=JsonDocument.Parse(raw);
            var id=json.RootElement.GetProperty("id").GetString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content=new StringContent(JsonSerializer.Serialize(new
                {
                    jsonrpc="2.0",
                    error=new{code=-32603,message=ErrorMessage,data=(object?)null},
                    id
                }),Encoding.UTF8,"application/json")
            };
        }
    }

    [Fact]
    public async Task InternalRpcError_IsQuarantinedWithSafeClassification()
    {
        using var http=new HttpClient(new ErrorHandler())
        {
            BaseAddress=new Uri("http://127.0.0.1:7583/")
        };
        var transport=new SignalCliTransport(http,()=>true);
        var dispatch=new DispatchIdentity(
            "job3","run3",0,0,"selected-group","+491234567890","hash3");
        var result=await transport.SendAsync(dispatch,
            "signal-structured:{\"Message\":\"测试\",\"AttachmentPath\":null}",
            CancellationToken.None);
        Assert.Equal(SignalDeliveryOutcome.Ambiguous,result.Outcome);
        Assert.Contains("-32603",result.Detail!);
        Assert.Contains("身份密钥需要人工核验",result.Detail!);
        Assert.DoesNotContain("+491234567890",result.Detail!);
        Assert.Null(result.ProviderMessageId);
    }

    [Fact]
    public async Task UnknownRpcError_IsQuarantinedWithoutUntrustedErrorText()
    {
        using var http=new HttpClient(new ErrorHandler{
            ErrorMessage="Unexpected group field and secret 123456789"
        })
        {
            BaseAddress=new Uri("http://127.0.0.1:7583/")
        };
        var transport=new SignalCliTransport(http,()=>true);
        var dispatch=new DispatchIdentity(
            "job4","run4",0,0,"selected-group","+491234567890","hash4");
        var result=await transport.SendAsync(dispatch,
            "signal-structured:{\"Message\":\"测试\",\"AttachmentPath\":null}",
            CancellationToken.None);
        Assert.Equal(SignalDeliveryOutcome.Ambiguous,result.Outcome);
        Assert.Contains("未分类的 Signal 内部异常",result.Detail!);
        Assert.DoesNotContain("secret",result.Detail!);
    }

}
