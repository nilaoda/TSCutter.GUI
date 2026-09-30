using TSCutter.GUI.Utils;
using Xunit;

namespace TSCutter.GUI.Tests;

public class VersionCheckerTests
{
    [Theory]
    [InlineData("v0.1.0", "v0.1.0", false)]
    [InlineData("v0.1.1", "v0.1.0", true)]
    [InlineData("v0.2.0", "v0.1.0", true)]
    [InlineData("v1.0.0", "v0.1.0", true)]
    [InlineData("v0.10.0", "v0.9.0", true)]
    [InlineData("v0.9.0", "v0.10.0", false)]
    [InlineData("v0.1.0", "v0.1.1", false)]
    [InlineData("alphabuild_20260928", "v0.1.0", false)]
    [InlineData("alphabuild_20990101", "v0.1.0", false)]
    [InlineData("", "v0.1.0", false)]
    [InlineData("unknown", "v0.1.0", false)]
    [InlineData("v0.2", "v0.1.0", false)]
    [InlineData("v0.2.0.0", "v0.1.0", false)]
    [InlineData("v0.2.0-beta.1", "v0.1.0", false)]
    [InlineData("v0.2.0", "", false)]
    [InlineData("v0.2.0", "unknown", false)]
    public void IsNewerTag_OnlyAcceptsHigherVersionedReleases(
        string candidateTag, string currentTag, bool expected)
    {
        Assert.Equal(expected, VersionChecker.IsNewerTag(candidateTag, currentTag));
    }

    [Fact]
    public void ReleaseMetadata_MatchesCompiledVersion()
    {
        var assemblyVersion = typeof(VersionChecker).Assembly.GetName().Version!;

        Assert.Equal(assemblyVersion.ToString(3), ReleaseInfo.Version);
        Assert.Equal($"v{ReleaseInfo.Version}", App.CurrentTag);
        Assert.Equal("Beta", ReleaseInfo.Stage);
        Assert.Contains(ReleaseInfo.Version, ReleaseInfo.WindowTitle);
        Assert.Contains(ReleaseInfo.Stage, ReleaseInfo.AboutVersion);
    }
}
