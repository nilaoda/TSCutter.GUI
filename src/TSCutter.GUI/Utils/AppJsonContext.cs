using System.Text.Json.Serialization;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.Utils;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    Converters = [typeof(JsonStringEnumConverter)] // 让 enum 输出为字符串
)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(GitHubVersionInfo))]
public partial class AppJsonContext : JsonSerializerContext
{
}