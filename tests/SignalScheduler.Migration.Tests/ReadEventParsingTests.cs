using System.Reflection;
using System.Text.Json;
using SignalScheduler.Engine.Signal;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class ReadEventParsingTests
{
    static (bool Ok,string Account,string Group,string Author,long Timestamp) Parse(string json)
    {
        using var doc=JsonDocument.Parse(json);
        var method=typeof(SignalReadCoordinator).GetMethod("TryReadEvent",
            BindingFlags.Static|BindingFlags.NonPublic)
            ??throw new InvalidOperationException("Missing Signal event parser");
        object?[] args={doc.RootElement,"","","",0L};
        var ok=(bool)method.Invoke(null,args)!;
        return (ok,(string)args[1]!, (string)args[2]!,
            (string)args[3]!, (long)args[4]!);
    }

    [Fact]
    public void WrappedGroupEvent_UsesAccountFromOuterNotification()
    {
        var ev=Parse("""
            {"jsonrpc":"2.0","method":"receive","params":{
              "account":"+12025550123","result":{"envelope":{
                "sourceUuid":"author-uuid","timestamp":1234567890000,
                "dataMessage":{"groupInfo":{"groupId":"group-id"}}
              }}
            }}
            """);
        Assert.True(ev.Ok);
        Assert.Equal("+12025550123",ev.Account);
        Assert.Equal("group-id",ev.Group);
        Assert.Equal("author-uuid",ev.Author);
        Assert.Equal(1234567890000L,ev.Timestamp);
    }

    [Fact]
    public void DirectMessage_ProducesReceiptableEmptyGroupKey()
    {
        var ev=Parse("""
            {"account":"+12025550123","envelope":{
              "sourceNumber":"+12025550999","timestamp":1234567890010,
              "dataMessage":{"message":"test"}
            }}
            """);
        Assert.True(ev.Ok);
        Assert.Equal("",ev.Group);
        Assert.Equal("+12025550999",ev.Author);
    }

    [Fact]
    public void ReceiptOnlyEnvelope_IsNotMistakenForIncomingMessage()
    {
        var ev=Parse("""
            {"account":"+12025550123","envelope":{
              "sourceNumber":"+12025550999","timestamp":1234567890020,
              "receiptMessage":{"type":"READ","timestamps":[1234567890000]}
            }}
            """);
        Assert.False(ev.Ok);
    }
}
