using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TSCutter.GUI.Models.Application;

public class GitHubVersionInfo
{
    [JsonPropertyName("url")]
    public required string Version { get; set; }
    [JsonPropertyName("id")]
    public required long Id { get; set; }
    [JsonPropertyName("body")]
    public required string Body { get; set; }
    [JsonPropertyName("name")]
    public required string Name { get; set; }
    [JsonPropertyName("tag_name")]
    public required string TagName { get; set; }
    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
    [JsonPropertyName("assets")]
    public List<GitHubReleaseAsset>? Assets { get; set; }
}
