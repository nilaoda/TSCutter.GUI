using System.Text.Json.Serialization;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.FrameSearch;

namespace TSCutter.GUI.Utils.Application;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    Converters = [typeof(JsonStringEnumConverter)] // 让 enum 输出为字符串
)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(GitHubVersionInfo))]
[JsonSerializable(typeof(CutterProject))]
[JsonSerializable(typeof(SavedFrameLibrary))]
public partial class AppJsonContext : JsonSerializerContext
{
}
