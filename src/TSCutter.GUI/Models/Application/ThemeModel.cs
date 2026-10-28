using System;
using System.Collections.Generic;
using System.Linq;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Themes;

namespace TSCutter.GUI.Models.Application;

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
    public string Family => CustomTheme.IsMacOS(Variant) ? "macOS" :
        CustomTheme.IsWindows11(Variant) ? "Windows 11" :
        CustomTheme.IsWindows10(Variant) ? "Windows 10" :
        CustomTheme.IsWindows8(Variant) ? "Windows 8" :
        CustomTheme.IsLuna(Variant) ? "Windows XP" :
        CustomTheme.IsAero(Variant) ? "Windows 7" : "Windows Classic";
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

    // 同系列的浅色配色共用对应的深色主题，例如 Luna Silver 与 Luna (Dark)。
    public static ThemeModel? GetPairedDarkTheme(ThemeModel? lightTheme) =>
        lightTheme is { IsDarkTheme: false }
            ? AllDarkThemes.FirstOrDefault(theme => theme.Family == lightTheme.Family)
            : null;

    private static ThemeModel DarkTheme { get; } = new("Standard (Dark)", CustomTheme.DarkStandard, true);
    // macOS 外观只在 macOS 上提供，其他平台没有原生标题栏可依附。
    private static bool ShowMacOSFamily => OperatingSystem.IsMacOS();
    public static List<ThemeModel> AllThemes { get; } =
        [
            .. ClassicTheme.AllVariants.Select(x => new ThemeModel(x)),
            new("Luna Blue", CustomTheme.LunaBlue, false),
            new("Luna Silver", CustomTheme.LunaSilver, false),
            new("Luna Olive Green", CustomTheme.LunaOliveGreen, false),
            new("Windows 7 Aero", CustomTheme.Aero, false),
            new("Windows 8", CustomTheme.Windows8, false),
            new("Windows 10", CustomTheme.Windows10, false),
            new("Windows 11", CustomTheme.Windows11, false),
            .. ShowMacOSFamily ? [new("macOS", CustomTheme.MacOS, false)] : Array.Empty<ThemeModel>()
        ];
    public static List<ThemeModel> AllDarkThemes { get; } =
        [
            DarkTheme,
            new("Luna (Dark)", CustomTheme.LunaDark, true),
            new("Windows 7 Aero (Dark)", CustomTheme.AeroDark, true),
            new("Windows 8 (Dark)", CustomTheme.Windows8Dark, true),
            new("Windows 10 (Dark)", CustomTheme.Windows10Dark, true),
            new("Windows 11 (Dark)", CustomTheme.Windows11Dark, true),
            .. ShowMacOSFamily ? [new("macOS (Dark)", CustomTheme.MacOSDark, true)] : Array.Empty<ThemeModel>()
        ];
}