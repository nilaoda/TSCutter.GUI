namespace TSCutter.GUI.Models;

public class ThemeModel
{
    public string Name { get; set; }
    public Avalonia.Styling.ThemeVariant Variant { get; set; }

    public ThemeModel(Avalonia.Styling.ThemeVariant variant)
    {
        Name = $"{variant.Key}";
        Variant = variant;
    }

    public ThemeModel(string name, Avalonia.Styling.ThemeVariant variant)
    {
        Name = name;
        Variant = variant;
    }
}