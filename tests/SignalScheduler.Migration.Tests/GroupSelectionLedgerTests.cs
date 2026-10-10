using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class GroupSelectionLedgerTests
{
    [Fact]
    public void LaunchingScriptAndRebuildingRowsDoesNotClearChosenGroupIds()
    {
        var selected=new GroupSelectionLedger();
        Assert.True(selected.Set("group-a",true));
        Assert.True(selected.Set("group-b",true));
        var targetIds=selected.SelectedIds.OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        // Launch sends a copied set of IDs. UI refresh must not alter them.
        var launched=targetIds.ToArray();
        selected.RetainAvailable(new[]{"group-a","group-b","group-c"});
        Assert.Equal(new[]{"group-a","group-b"},selected.SelectedIds.OrderBy(x=>x,StringComparer.Ordinal));
        Assert.Equal(targetIds,launched);
    }

    [Fact]
    public void ExplicitTogglingAndClearingIsHonoured()
    {
        var selected=new GroupSelectionLedger();
        Assert.True(selected.Set("group-a",true));
        Assert.True(selected.Set("group-a",true));
        Assert.Equal(1,selected.Count);
        Assert.True(selected.Set("group-a",false));
        Assert.Equal(0,selected.Count);
        selected.Set("group-b",true);
        selected.Clear();
        Assert.Empty(selected.SelectedIds);
    }

    [Fact]
    public void ClickingTwentyFirstGroupIsRejectedWithoutLosingExistingChoices()
    {
        var selected=new GroupSelectionLedger();
        foreach(var n in Enumerable.Range(1,20))
            Assert.True(selected.Set("group-"+n,true));
        Assert.False(selected.Set("group-extra",true));
        Assert.Equal(20,selected.Count);
        Assert.False(selected.Contains("group-extra"));
        Assert.True(selected.Set("group-1",false));
        Assert.True(selected.Set("group-extra",true));
        Assert.Equal(20,selected.Count);
    }

    [Fact]
    public void MissingGroupsAreRemovedWithoutClearingOtherSelections()
    {
        var selected=new GroupSelectionLedger();
        selected.Set("group-a",true);
        selected.Set("group-b",true);
        selected.RetainAvailable(new[]{"group-b","group-c"});
        Assert.False(selected.Contains("group-a"));
        Assert.True(selected.Contains("group-b"));
    }
}
