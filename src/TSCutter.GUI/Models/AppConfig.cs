using System;
using System.Linq;
using System.Text.Json.Serialization;

namespace TSCutter.GUI.Models;

public class AppConfig
{
    private bool _preferHardwareDecoding = IsHardwareDecodingSupported;

    // 系统当前是否处于深色模式
    [JsonIgnore]
    public static bool IsSystemDarkMode { get; set; }
    [JsonIgnore]
    public ThemeModel ThemeModel => ThemeModel.AllThemes.FirstOrDefault(x => x.Name == ThemeName)!;
    [JsonIgnore]
    public ThemeModel DarkThemeModel => ThemeModel.AllDarkThemes.FirstOrDefault(x => x.Name == DarkThemeName)!;

    public string Language { get; set; } = "en-US";
    public string ThemeName { get; set; } = ThemeModel.AllThemes[0].Name;
    public string DarkThemeName { get; set; } = ThemeModel.AllDarkThemes[0].Name;
    public ThemeVariantMode ThemeVariantMode { get; set; } = ThemeVariantMode.Automatic;
    public bool AutoDetectLanguage { get; set; } = true;
    public bool AutoCheckForUpdates { get; set; } = true;
    public bool PreferHardwareDecoding
    {
        get => IsHardwareDecodingSupported && _preferHardwareDecoding;
        set => _preferHardwareDecoding = NormalizeHardwareDecodingPreference(
            value,
            IsHardwareDecodingSupported);
    }
    public string? FFmpegRootPath { get; set; }

    [JsonIgnore]
    public static bool IsHardwareDecodingSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    internal static bool NormalizeHardwareDecodingPreference(bool requested, bool supported) => requested && supported;
}
