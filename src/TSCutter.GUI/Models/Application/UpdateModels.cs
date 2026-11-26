using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TSCutter.GUI.Models.Application;

/// <summary>An asset attached to a GitHub release.</summary>
public sealed class GitHubReleaseAsset
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string? BrowserDownloadUrl { get; set; }
}

/// <summary>The download selected for the current platform.</summary>
public sealed class UpdateAsset
{
    public required string FileName { get; set; }
    public required string Url { get; set; }
    public long Size { get; set; }
    public string? Sha256 { get; set; }
}

/// <summary>Release information prepared for the update dialog.</summary>
public sealed class UpdateInfo
{
    public required string Tag { get; set; }
    public required string Version { get; set; }
    public string? Notes { get; set; }
    public string? ReleaseUrl { get; set; }
    public UpdateAsset? Asset { get; set; }

    /// <summary>In-place install requires a verified asset with a checksum.</summary>
    public bool CanInstallInPlace => Asset is not null
                                     && !string.IsNullOrEmpty(Asset.Sha256)
                                     && !string.IsNullOrEmpty(Asset.Url);
}

/// <summary>Optional release manifest published with the packages.</summary>
public sealed class UpdateManifest
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("tag")]
    public string? Tag { get; set; }

    [JsonPropertyName("assets")]
    public Dictionary<string, UpdateManifestAsset>? Assets { get; set; }
}

public sealed class UpdateManifestAsset
{
    [JsonPropertyName("file")]
    public string? File { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }
}
