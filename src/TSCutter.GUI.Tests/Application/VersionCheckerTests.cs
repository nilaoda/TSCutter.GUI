using System.Net;
using Xunit;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.Tests.Application;

public class VersionCheckerTests
{
    private const string LatestUrl = "https://github.com/nilaoda/TSCutter.GUI/releases/latest";
    private const string TagUrl = "https://github.com/nilaoda/TSCutter.GUI/releases/tag/";
    private const string DownloadUrl = "https://github.com/nilaoda/TSCutter.GUI/releases/download/";

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

    [Theory]
    [InlineData(HttpStatusCode.Found, TagUrl + "v1.0.0", false)]
    [InlineData(HttpStatusCode.Found, TagUrl + "v0.9.0", false)]
    [InlineData(HttpStatusCode.Found, TagUrl + "v1.1.0-beta.1", false)]
    [InlineData(HttpStatusCode.Found, "https://github.com/example/other/releases/tag/v1.1.0", true)]
    [InlineData(HttpStatusCode.OK, null, true)]
    public async Task LoadUpdateInfoAsync_OnlyRequestsRedirectWithoutANewerTag(
        HttpStatusCode statusCode, string? location, bool checkFailed)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(statusCode)
        {
            Headers = { Location = location is null ? null : new Uri(location) }
        });
        using var client = new HttpClient(handler);

        var result = await VersionChecker.LoadUpdateInfoAsync(client, client, "v1.0.0", "linux-x64");

        Assert.Null(result.Update);
        Assert.Equal(checkFailed, result.CheckFailed);
        Assert.Equal([LatestUrl], handler.Requests);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task LoadUpdateInfoAsync_FetchesMetadataForTheRedirectedTag(bool apiAvailable, bool manifestAvailable)
    {
        const string tag = "v1.1.0";
        const string releasePath = "/nilaoda/TSCutter.GUI/releases/tag/" + tag + "?source=latest";
        const string apiUrl = "https://api.github.com/repos/nilaoda/TSCutter.GUI/releases/tags/" + tag;
        const string manifestUrl = DownloadUrl + tag + "/update-manifest.json";
        const string file = "TSCutter.GUI_Beta_linux-x64_1.1.0.zip";
        const string assetUrl = DownloadUrl + tag + "/" + file;
        var handler = new RecordingHandler(url => url switch
        {
            LatestUrl => new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri(releasePath, UriKind.Relative) }
            },
            apiUrl when apiAvailable => Json($$"""
                {"url":"","id":1,"body":"Release notes","name":"Beta 1.1.0","tag_name":"{{tag}}",
                 "assets":[{"name":"{{file}}","browser_download_url":"{{assetUrl}}"}]}
                """),
            manifestUrl when manifestAvailable => Json($$"""
                {"tag":"{{tag}}","version":"1.1.0",
                 "assets": {"linux-x64": {"file":"{{file}}","size":42,"sha256":"{{new string('a', 64)}}" } } }
                """),
            _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        });
        using var client = new HttpClient(handler);

        var (update, checkFailed) = await VersionChecker.LoadUpdateInfoAsync(client, client, "v1.0.0", "linux-x64");

        Assert.False(checkFailed);
        Assert.NotNull(update);
        Assert.Equal(tag, update.Tag);
        Assert.Equal(apiAvailable ? "Release notes" : null, update.Notes);
        Assert.Equal(TagUrl + tag, update.ReleaseUrl);
        Assert.Equal(manifestAvailable, update.CanInstallInPlace);
        Assert.Equal(manifestAvailable ? assetUrl : null, update.Asset?.Url);
        Assert.Equal([LatestUrl, apiUrl, manifestUrl], handler.Requests);
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
    public void FindAssetUrl_AcceptsManifestNamePresentInRelease()
    {
        const string name = "TSCutter.GUI_Beta_win-x64_0.10.1.zip";
        var release = new GitHubVersionInfo
        {
            Version = "url", Id = 1, Body = "", Name = "Beta 0.10.1", TagName = "v0.10.1",
            Assets = [new GitHubReleaseAsset { Name = name, BrowserDownloadUrl = "https://github.com/a.zip" }]
        };
        Assert.Equal("https://github.com/a.zip", VersionChecker.FindAssetUrl(release, name, "v0.10.1"));
    }

    [Fact]
    public void FindAssetUrl_RejectsUnknownNameWhenReleaseAssetsAreKnown()
    {
        var release = new GitHubVersionInfo
        {
            Version = "url", Id = 1, Body = "", Name = "Beta 0.10.1", TagName = "v0.10.1",
            Assets = [new GitHubReleaseAsset { Name = "other.zip", BrowserDownloadUrl = "https://github.com/other.zip" }]
        };
        Assert.Null(VersionChecker.FindAssetUrl(release, "TSCutter.GUI_Beta_win-x64_0.10.1.zip", "v0.10.1"));
        Assert.Null(VersionChecker.FindAssetUrl(release, "../unsafe.zip", "v0.10.1"));
    }

    [Fact]
    public void FindAssetUrl_FallsBackToTagWhenApiResponseIsMissing()
    {
        Assert.Equal("https://github.com/nilaoda/TSCutter.GUI/releases/download/v0.10.1/a.zip",
            VersionChecker.FindAssetUrl(null, "a.zip", "v0.10.1"));
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

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json)
    };

    private sealed class RecordingHandler(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requests.Add(url);
            return Task.FromResult(respond(url));
        }
    }
}
