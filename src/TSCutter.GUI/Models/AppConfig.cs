using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Themes;

namespace TSCutter.GUI.Models;

public class AppConfig
{
    [JsonIgnore]
    public static ThemeModel DarkTheme { get; } = new("Standard (Dark)", CustomTheme.DarkStandard);
    [JsonIgnore]
    public static List<ThemeModel> AllThemes { get; } = [..ClassicTheme.AllVariants.Select(x => new ThemeModel(x)), DarkTheme];
    [JsonIgnore]
    public ThemeModel ThemeModel => AllThemes.FirstOrDefault(x => x.Name == ThemeName)!;

    public string Language { get; set; } = "en-US";
    public string ThemeName { get; set; } = ClassicTheme.AllVariants[0].Key.ToString()!;
    public bool AutoDetectDarkMode { get; set; } = true;
    public bool AutoDetectLanguage { get; set; } = true;
}