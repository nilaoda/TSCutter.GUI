using Avalonia.Styling;

namespace TSCutter.GUI.Themes;

public class CustomTheme : Styles
{
    public static ThemeVariant DarkStandard { get; } = new("DarkStandard", ThemeVariant.Dark);
}