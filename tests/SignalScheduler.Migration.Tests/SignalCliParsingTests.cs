using System.Text.Json;
using SignalScheduler.Engine.Signal;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class SignalCliParsingTests
{
    [Fact]
    public void ParsesNumberedAndNumberlessAccounts()
    {
        using var doc=JsonDocument.Parse("[{\"number\":\"+491111111\",\"aci\":\"11111111-1111-4111-8111-111111111111\"},{\"number\":null,\"aci\":\"22222222-2222-4222-8222-222222222222\"}]");
        var accounts=SignalCliClient.ParseAccounts(doc.RootElement);
        Assert.Equal(2,accounts.Count);
        Assert.Equal("+491111111",accounts[0].Account);
        Assert.Equal("22222222-2222-4222-8222-222222222222",accounts[1].Account);
    }

    [Fact]
    public void ParsesGroupsWithoutDroppingMemberMetadata()
    {
        using var doc=JsonDocument.Parse("[{\"id\":\"abc=\",\"name\":\"Group A\",\"isMember\":true,\"isBlocked\":false,\"members\":[{\"number\":\"+491111111\",\"uuid\":\"u1\"}]}]");
        var groups=SignalCliClient.ParseGroups("+491111111",doc.RootElement);
        Assert.Single(groups);
        Assert.Equal("abc=",groups[0].GroupId);
        Assert.Contains("+491111111",groups[0].MembersJson);
        Assert.True(groups[0].IsMember);
    }
}
