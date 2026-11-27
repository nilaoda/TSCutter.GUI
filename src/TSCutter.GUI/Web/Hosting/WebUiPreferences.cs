using System;
using System.Text.Json;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.Web;

internal static class WebUiPreferences
{
    internal const string EnvironmentVariable = "TSCUTTER_WEB_CONFIG";

    internal static string CreateSnapshot(AppConfig source, ClipCutMode? cutMode = null, ClipListMode? clipMode = null)
    {
        // 使用现有配置格式保留所有编辑偏好，清除桌面文件历史和目录后再传给子进程。
        var format = AppJsonContext.Default.AppConfig;
        var snapshot = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(source, format), format)!;
        snapshot.ClipCutMode = cutMode ?? snapshot.ClipCutMode;
        snapshot.ClipListMode = clipMode ?? snapshot.ClipListMode;
        snapshot.AutoDetectLanguage = false;
        snapshot.ShowTimecode = false;
        snapshot.AutoCheckForUpdates = false;
        snapshot.EnableInPlaceUpdates = false;
        snapshot.RememberRecentItems = false;
        snapshot.RecentItems = [];
        snapshot.LastImportFolder = null;
        snapshot.LastExportFolder = null;
        snapshot.WebUiInputDirectory = null;
        snapshot.WebUiOutputDirectory = null;
        snapshot.WebUiPort = 5360;
        snapshot.WebUiAllowLan = false;
        snapshot.WebUiEnableIpv6 = false;
        snapshot.WebUiAccounts = [];
        snapshot.WebUiMaxWorkers = 4;
        snapshot.AutoSaveRecovery = false;
        snapshot.ShowSystemProgress = false;
        return JsonSerializer.Serialize(snapshot, format);
    }

    internal static AppConfig? ReadSnapshot()
    {
        var json = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig);
    }
}
