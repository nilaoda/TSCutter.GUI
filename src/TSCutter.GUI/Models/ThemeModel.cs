using System.Collections.Generic;
using System.Linq;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Themes;

namespace TSCutter.GUI.Models;

public enum ThemeVariantMode
{
    Automatic,
    Light,
    Dark
}

public class ThemeModel
{
    public string Name { get; set; }
    public bool IsDarkTheme { get; set; }
    public Avalonia.Styling.ThemeVariant Variant { get; set; }

    public ThemeModel(Avalonia.Styling.ThemeVariant variant)
    {
        Name = $"{variant.Key}";
        Variant = variant;
    }

    public ThemeModel(string name, Avalonia.Styling.ThemeVariant variant, bool isDarkTheme)
    {
        Name = name;
        Variant = variant;
        IsDarkTheme = isDarkTheme;
    }

    private static ThemeModel DarkTheme { get; } = new("Standard (Dark)", CustomTheme.DarkStandard, true);
    public static List<ThemeModel> AllThemes { get; } = ClassicTheme.AllVariants.Select(x => new ThemeModel(x)).ToList();
    public static List<ThemeModel> AllDarkThemes { get; } = [DarkTheme];
}