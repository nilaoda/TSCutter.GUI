using System.Linq;
using System.Text.Json.Serialization;

namespace TSCutter.GUI.Models;

public class AppConfig
{
    // 系统当前是否处于深色模式
    [JsonIgnore]
    public static bool IsSystemDarkMode { get; set; }
    // 系统当前是否处于深色模式
    [JsonIgnore]
    public static string SystemLocName { get; set; }

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
}