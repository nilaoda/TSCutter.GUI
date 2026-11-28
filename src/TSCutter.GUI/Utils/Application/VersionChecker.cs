using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models.Application;

namespace TSCutter.GUI.Utils.Application;

public static class VersionChecker
{
    private const string ReleaseApiPrefix = "https://api.github.com/repos/nilaoda/TSCutter.GUI/releases/tags/";
    private const string ReleaseUrl = "https://github.com/nilaoda/TSCutter.GUI/releases/latest";
    private const string ReleaseUrlPrefix = "https://github.com/nilaoda/TSCutter.GUI/releases/tag/";
    private const string DownloadUrlPrefix = "https://github.com/nilaoda/TSCutter.GUI/releases/download/";
    private const string ManifestFileName = "update-manifest.json";
    private static readonly HttpClient RedirectClient = new(new HttpClientHandler { AllowAutoRedirect = false });
    private static readonly HttpClient MetadataClient = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "TSCutter.GUI" } }
    };
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static UpdateInfo? _cachedInfo;
    private static DateTimeOffset _cachedAt;
    private static bool _hasCached;

    /// <summary>RID used by the release pipeline, or null on an unsupported platform.</summary>
    public static string? CurrentRid { get; } = ResolveRid();

    internal static bool IsNewerTag(string candidateTag, string currentTag)
    {
        // 只接受版本化的发行标签，忽略旧的日期标签和不能识别的标签。
        return TryParseVersionTag(candidateTag, out var candidate)
               && TryParseVersionTag(currentTag, out var current)
               && candidate > current;
    }

    internal static bool TryParseVersionTag(string tag, out Version? version)
    {
        version = null;
        return !string.IsNullOrEmpty(tag) && tag[0] == 'v'
               && Version.TryParse(tag.AsSpan(1), out version)
               && version.Build >= 0 && version.Revision == -1;
    }

    /// <summary>
    /// Resolves the download URL for a manifest asset. The file name is trusted from
    /// the manifest but must be a plain .zip name and, when the API asset list is
    /// available, must match one of the actual release assets. This avoids assuming
    /// a particular naming pattern or release stage.
    /// </summary>
    internal static string? FindAssetUrl(GitHubVersionInfo? release, string fileName, string tag)
    {
        if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(".."))
            return null;

        var asset = release?.Assets?
            .FirstOrDefault(a => string.Equals(a.Name, fileName, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(asset?.BrowserDownloadUrl))
            return asset.BrowserDownloadUrl;
        // If the API list is present but does not contain the file, do not download it.
        if (release?.Assets is { Count: > 0 })
            return null;
        // Fallback when the API response was unavailable: use the release tag.
        return DownloadUrlPrefix + tag + "/" + fileName;
    }

    private static async Task<string?> GetLatestTagAsync(HttpClient client)
    {
        try
        {
            var releaseUri = new Uri(ReleaseUrl);
            using var response = await client.GetAsync(releaseUri, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Found || response.Headers.Location is not { } location)
                return null;

            var target = new Uri(releaseUri, location).GetLeftPart(UriPartial.Path);
            return target.Length > ReleaseUrlPrefix.Length && target.StartsWith(ReleaseUrlPrefix, StringComparison.Ordinal)
                ? target[ReleaseUrlPrefix.Length..]
                : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get latest tag: {ex.Message}");
            return null;
        }
    }

    private static async Task<GitHubVersionInfo?> GetReleaseAsync(HttpClient client, string tag)
    {
        try
        {
            var json = await client.GetStringAsync(ReleaseApiPrefix + tag).ConfigureAwait(false);
            var release = JsonSerializer.Deserialize(json, AppJsonContext.Default.GitHubVersionInfo);
            return release?.TagName == tag ? release : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get GitHub version info: {ex.Message}");
            return null;
        }
    }

    /// <summary>先比较重定向中的发行标签，发现新版本时再获取发布说明和更新清单。</summary>
    public static async Task<(UpdateInfo? Update, bool CheckFailed)> CheckForUpdateAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && HasFreshCache())
            return (_cachedInfo, false);
        await CacheGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!forceRefresh && HasFreshCache())
                return (_cachedInfo, false);
            var result = await LoadUpdateInfoAsync(RedirectClient, MetadataClient, App.CurrentTag, CurrentRid)
                .ConfigureAwait(false);
            if (!result.CheckFailed)
            {
                _cachedInfo = result.Update;
                _cachedAt = DateTimeOffset.UtcNow;
                _hasCached = true;
            }
            return result;
        }
        finally
        {
            CacheGate.Release();
        }
    }

    internal static void ClearCache()
    {
        _cachedInfo = null;
        _cachedAt = default;
        _hasCached = false;
    }

    private static bool HasFreshCache() =>
        _hasCached && DateTimeOffset.UtcNow - _cachedAt < CacheLifetime;

    internal static async Task<(UpdateInfo? Update, bool CheckFailed)> LoadUpdateInfoAsync(
        HttpClient redirectClient, HttpClient metadataClient, string currentTag, string? rid)
    {
        try
        {
            var tag = await GetLatestTagAsync(redirectClient).ConfigureAwait(false);
            if (tag is null)
                return (null, true);
            if (!IsNewerTag(tag, currentTag))
                return (null, false);

            var release = await GetReleaseAsync(metadataClient, tag).ConfigureAwait(false);
            var info = new UpdateInfo
            {
                Tag = tag,
                Version = Version.Parse(tag.AsSpan(1)).ToString(3),
                Notes = release?.Body,
                ReleaseUrl = release?.HtmlUrl ?? ReleaseUrlPrefix + tag
            };

            if (rid is null)
                return (info, false);

            var manifest = await GetManifestAsync(metadataClient, tag).ConfigureAwait(false);
            if (manifest is null || manifest.Tag != tag || manifest.Version != info.Version
                || manifest.Assets is null
                || !manifest.Assets.TryGetValue(rid, out var asset)
                || string.IsNullOrEmpty(asset.File) || string.IsNullOrEmpty(asset.Sha256))
                return (info, false);

            var assetUrl = FindAssetUrl(release, asset.File, tag);
            if (assetUrl is null)
                return (info, false);

            info.Asset = new UpdateAsset
            {
                FileName = asset.File,
                Url = assetUrl,
                Size = asset.Size,
                Sha256 = asset.Sha256
            };
            return (info, false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to prepare update info: {ex.Message}");
            return (null, true);
        }
    }

    private static async Task<UpdateManifest?> GetManifestAsync(HttpClient client, string tag)
    {
        try
        {
            var json = await client.GetStringAsync(DownloadUrlPrefix + tag + "/" + ManifestFileName)
                .ConfigureAwait(false);
            return JsonSerializer.Deserialize<UpdateManifest>(json, AppJsonContext.Default.UpdateManifest);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get update manifest: {ex.Message}");
            return null;
        }
    }

    private static string? ResolveRid()
    {
        var arch = RuntimeInformation.ProcessArchitecture;
        if (OperatingSystem.IsWindows())
            return arch == Architecture.X64 ? "win-x64" : arch == Architecture.Arm64 ? "win-arm64" : null;
        if (OperatingSystem.IsLinux())
            return arch == Architecture.X64 ? "linux-x64" : arch == Architecture.Arm64 ? "linux-arm64" : null;
        if (OperatingSystem.IsMacOS())
            return arch == Architecture.Arm64 ? "osx-arm64" : arch == Architecture.X64 ? "osx-x64" : null;
        return null;
    }
}
