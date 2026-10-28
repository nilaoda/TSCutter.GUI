using System;
using System.Linq;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Utils.Editing;

namespace TSCutter.GUI.Models.Application;

public class AppConfig : ObservableObject
{
    private bool _showImageNavigator = true;
    private bool _showKeyFrameStrip = true;
    private bool _enableSdrToneMapping = true;
    private bool _preferHardwareDecoding = IsHardwareDecodingSupported;

    // 系统当前是否处于深色模式
    [JsonIgnore]
    public static bool IsSystemDarkMode { get; set; }
    [JsonIgnore]
    public ThemeModel ThemeModel => ThemeModel.AllThemes.FirstOrDefault(x => x.Name == ThemeName) ?? ThemeModel.AllThemes[0];
    [JsonIgnore]
    public ThemeModel DarkThemeModel => ThemeModel.AllDarkThemes.FirstOrDefault(x => x.Name == DarkThemeName) ?? ThemeModel.AllDarkThemes[0];

    public string Language { get; set; } = "en-US";
    public string ThemeName { get; set; } = ThemeModel.AllThemes[0].Name;
    public string DarkThemeName { get; set; } = ThemeModel.AllDarkThemes[0].Name;
    public ThemeVariantMode ThemeVariantMode { get; set; } = ThemeVariantMode.Automatic;
    public bool AutoDetectLanguage { get; set; } = true;
    public bool AutoCheckForUpdates { get; set; } = true;
    public bool ShowImageNavigator
    {
        get => _showImageNavigator;
        set => SetProperty(ref _showImageNavigator, value);
    }
    public bool ShowKeyFrameStrip
    {
        get => _showKeyFrameStrip;
        set => SetProperty(ref _showKeyFrameStrip, value);
    }
    public bool EnableSdrToneMapping
    {
        get => _enableSdrToneMapping;
        set => SetProperty(ref _enableSdrToneMapping, value);
    }
    public string ClipOutputTemplate { get; set; } = Utils.Editing.ClipOutputNaming.DefaultTemplate;
    public bool TsBatchCheckSaveLogs { get; set; }
    public bool ExportCurrentProgramOnly { get; set; }
    // 导出过程中对最终输出字节同步执行 TS 检测；默认开启，可在设置中关闭。
    public bool TsCheckOnExport { get; set; } = true;
    public bool PreferHardwareDecoding
    {
        get => IsHardwareDecodingSupported && _preferHardwareDecoding;
        set => _preferHardwareDecoding = NormalizeHardwareDecodingPreference(
            value,
            IsHardwareDecodingSupported);
    }
    public string? FFmpegRootPath { get; set; }
    public ThumbnailSheetPreferences ThumbnailSheetPreferences { get; set; } = new();

    [JsonIgnore]
    public static bool IsHardwareDecodingSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    internal static bool NormalizeHardwareDecodingPreference(bool requested, bool supported) => requested && supported;
}
