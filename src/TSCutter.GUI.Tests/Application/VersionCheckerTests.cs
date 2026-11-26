using Xunit;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.Tests.Application;

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

    [Fact]
    public void ExpectedAssetName_UsesReleaseNaming()
    {
        Assert.Equal("TSCutter.GUI_Beta_0.10.1_win-x64.zip",
            VersionChecker.GetExpectedAssetName("win-x64", "0.10.1"));
    }

    [Fact]
    public void CurrentRid_IsSupportedOnKnownPlatforms()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            Assert.NotNull(VersionChecker.CurrentRid);
    }

    [Fact]
    public void UpdateInfo_AllowsInstallOnlyWithVerifiedAsset()
    {
        var info = new UpdateInfo { Tag = "v1.0.0", Version = "1.0.0" };
        Assert.False(info.CanInstallInPlace);

        info.Asset = new UpdateAsset { FileName = "a.zip", Url = "https://example.test/a.zip" };
        Assert.False(info.CanInstallInPlace);

        info.Asset.Sha256 = new string('a', 64);
        Assert.True(info.CanInstallInPlace);
    }
}
