using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class GroupAccountResolverTests
{
    static readonly Dictionary<string,string> Remarks=new(StringComparer.Ordinal)
    {
        ["sender-a"]="1号",
        ["sender-b"]="1号",
        ["sender-c"]="2号"
    };

    [Fact]
    public void ImportedRepresentativeIsReplacedOnlyBySameRemarkInSelectedGroup()
    {
        Assert.Equal("sender-b",GroupAccountResolver.Resolve("sender-a",
            Remarks,new[]{"sender-b","sender-c"}));
    }

    [Fact]
    public void OtherSelectedGroupKeepsOriginalMember()
    {
        Assert.Equal("sender-a",GroupAccountResolver.Resolve("sender-a",
            Remarks,new[]{"sender-a","sender-c"}));
    }

    [Fact]
    public void GroupWithoutSameRemarkFailsInsteadOfUsingWrongRole()
    {
        Assert.Throws<InvalidOperationException>(()=>
            GroupAccountResolver.Resolve("sender-a",Remarks,new[]{"sender-c"}));
    }

    [Fact]
    public void TwoEligibleIdenticallyNamedMembersMustBeDisambiguated()
    {
        Assert.Throws<InvalidOperationException>(()=>
            GroupAccountResolver.Resolve("absent-but-known",
                new Dictionary<string,string>(Remarks){
                    ["absent-but-known"]="1号"
                },new[]{"sender-a","sender-b"}));
    }

    [Fact]
    public void BlankSenderUsesSortedEligibleMemberDeterministically()
    {
        Assert.Equal("sender-b",GroupAccountResolver.Resolve("",
            Remarks,new[]{"sender-c","sender-b"}));
    }
}
