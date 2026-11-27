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
    private const string VersionUrl = "https://api.github.com/repos/nilaoda/TSCutter.GUI/releases/latest";
    private const string ReleaseUrl = "https://github.com/nilaoda/TSCutter.GUI/releases/latest";
    private const string ReleaseUrlPrefix = "https://github.com/nilaoda/TSCutter.GUI/releases/tag/";
    private const string DownloadUrlPrefix = "https://github.com/nilaoda/TSCutter.GUI/releases/download/";
    private const string ManifestFileName = "update-manifest.json";
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static UpdateInfo? _cachedInfo;
    private static DateTimeOffset _cachedAt;
    private static bool _hasCached;

    /// <summary>RID used by the release pipeline, or null on an unsupported platform.</summary>
    public static string? CurrentRid { get; } = ResolveRid();

    internal static bool IsNewerTag(string candidateTag, string currentTag)
    {
        // Versioned Beta releases supersede the dated Alpha releases. Ignore legacy
        // and unrecognized tags so a stale /latest response cannot suggest a downgrade.
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

    public static async Task<string> GetLatestTagAsync()
    {
        try
        {
            var redirctUrl = await Get302Async(ReleaseUrl);
            return redirctUrl.StartsWith(ReleaseUrlPrefix, StringComparison.Ordinal)
                ? redirctUrl[ReleaseUrlPrefix.Length..]
                : "";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get latest tag: {ex.Message}");
            return "";
        }
    }

    public static async Task<GitHubVersionInfo?> CheckUpdateAsync()
    {
        try
        {
            var jsonStr = await GetContentAsync(VersionUrl);
            return JsonSerializer.Deserialize<GitHubVersionInfo>(jsonStr, AppJsonContext.Default.GitHubVersionInfo);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to get GitHub version info: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Gathers the information needed by the update dialog. The existing tag-based
    /// check remains the fallback when the API or the release manifest is unavailable;
    /// in that case <see cref="UpdateInfo.CanInstallInPlace"/> is false and the caller
    /// keeps the original "open the download page" behavior.
    /// </summary>
    public static async Task<UpdateInfo?> CheckForUpdateAsync(bool forceRefresh = false)
    {
        if (!forceRefresh && HasFreshCache())
            return _cachedInfo;
        await CacheGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!forceRefresh && HasFreshCache())
                return _cachedInfo;
            var info = await LoadUpdateInfoAsync().ConfigureAwait(false);
            _cachedInfo = info;
            _cachedAt = DateTimeOffset.UtcNow;
            _hasCached = true;
            return info;
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

    private static async Task<UpdateInfo?> LoadUpdateInfoAsync()
    {
        try
        {
            var release = await CheckUpdateAsync();
            var tag = release?.TagName;
            if (string.IsNullOrEmpty(tag))
                tag = await GetLatestTagAsync();
            if (string.IsNullOrEmpty(tag) || !TryParseVersionTag(tag, out var parsed) || parsed is null)
                return null;

            var info = new UpdateInfo
            {
                Tag = tag,
                Version = parsed.ToString(3),
                Notes = release?.Body,
                ReleaseUrl = release?.HtmlUrl ?? ReleaseUrl
            };

            var rid = CurrentRid;
            if (rid is null)
                return info;

            var manifest = await GetManifestAsync(tag);
            if (manifest is null || manifest.Tag != tag || manifest.Version != info.Version
                || manifest.Assets is null
                || !manifest.Assets.TryGetValue(rid, out var asset)
                || string.IsNullOrEmpty(asset.File) || string.IsNullOrEmpty(asset.Sha256))
                return info;

            var assetUrl = FindAssetUrl(release, asset.File, tag);
            if (assetUrl is null)
                return info;

            info.Asset = new UpdateAsset
            {
                FileName = asset.File,
                Url = assetUrl,
                Size = asset.Size,
                Sha256 = asset.Sha256
            };
            return info;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to prepare update info: {ex.Message}");
            return null;
        }
    }

    private static async Task<UpdateManifest?> GetManifestAsync(string tag)
    {
        try
        {
            var json = await GetContentAsync(DownloadUrlPrefix + tag + "/" + ManifestFileName);
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
    
    // 获取网页源码
    private static async Task<string> GetContentAsync(string url)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("User-Agent", "TSCutter.GUI");
        using var response = await client.GetAsync(url);
        using var content = response.Content;
        return await content.ReadAsStringAsync();
    }
    
    // 重定向
    private static async Task<string> Get302Async(string url)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false
        };
        var redirectedUrl = "";
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        using var content = response.Content;
        if (response.StatusCode != HttpStatusCode.Found) return redirectedUrl;
        
        var headers = response.Headers;
        if (headers.Location != null)
        {
            redirectedUrl = headers.Location.AbsoluteUri;
        }

        return redirectedUrl;
    }
}
