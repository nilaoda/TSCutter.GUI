using Avalonia;
using Avalonia.Media;
using Classic.CommonControls;

namespace TSCutter.GUI.Themes;

/// <summary>为工具栏提供矢量图标，Classic 仍使用原有的位图属性及模板。</summary>
public sealed class ThemeIcons : AvaloniaObject
{
    public static readonly AttachedProperty<IImage?> SourceProperty =
        AvaloniaProperty.RegisterAttached<ThemeIcons, ToolBarButton, IImage?>("Source");

    public static IImage? GetSource(ToolBarButton button) => button.GetValue(SourceProperty);
    public static void SetSource(ToolBarButton button, IImage? value) => button.SetValue(SourceProperty, value);
}
