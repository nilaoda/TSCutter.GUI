using System.Text.Json.Serialization;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.Utils;

[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(GitHubVersionInfo))]
public partial class AppJsonContext : JsonSerializerContext
{
}