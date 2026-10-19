using SignalScheduler.Shared;
using Xunit;

namespace SignalScheduler.Migration.Tests;

public sealed class InstalledBuildInfoTests
{
    [Theory]
    [InlineData("8.0.0-ui2.4",58)]
    [InlineData("8.0.0-ui2.5",60)]
    [InlineData("8.0.0-ui2.6",62)]
    public void InstalledUi2VersionTracksBuildMetadata(string version,int build)
    {
        var parsed=InstalledBuildInfo.Parse(
            $$"""{"version":"{{version}}","build":{{build}},"sourceSha":"abc"}""");
        Assert.NotNull(parsed);
        Assert.Equal(version,parsed!.Version);
        Assert.Equal(build,parsed.Build);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"version\":\"8.0.0-ui2.5\",\"build\":0}")]
    [InlineData("{\"version\":\"8.0.0-beta.17\",\"build\":673}")]
    [InlineData("{\"version\":\"8.0.0-ui2.5\",\"build\":\"60\"}")]
    [InlineData("{\"version\":\"<script>\",\"build\":60}")]
    [InlineData("broken")]
    public void DoesNotInventInstalledVersionForInvalidMetadata(string json)
    {
        Assert.Null(InstalledBuildInfo.Parse(json));
    }

    [Fact]
    public void MissingBuildInfoDoesNotClaimAnOldOrNewVersion()
    {
        Assert.Null(InstalledBuildInfo.ReadFromDirectory(
            Path.Combine(Path.GetTempPath(),"ms002-version-test-nonexistent",
                Guid.NewGuid().ToString("N"))));
    }
}
