using Avalonia;
using Avalonia.Media;

namespace TSCutter.GUI.Themes;

/// <summary>将渲染选项暴露给主题样式；样式撤销后恢复控件原有设置。</summary>
public sealed class ThemeRenderOptions : AvaloniaObject
{
    // Avalonia 的渲染选项是普通属性，不能直接在样式 Setter 中设置。
    public static readonly AttachedProperty<EdgeMode?> EdgeModeProperty =
        AvaloniaProperty.RegisterAttached<ThemeRenderOptions, Visual, EdgeMode?>("EdgeMode");

    private static readonly AttachedProperty<EdgeMode?> OriginalEdgeModeProperty =
        AvaloniaProperty.RegisterAttached<ThemeRenderOptions, Visual, EdgeMode?>("OriginalEdgeMode");

    static ThemeRenderOptions()
    {
        EdgeModeProperty.Changed.AddClassHandler<Visual>((visual, change) =>
        {
            if (change.GetNewValue<EdgeMode?>() is { } mode)
            {
                if (visual.GetValue(OriginalEdgeModeProperty) == null)
                    visual.SetValue(OriginalEdgeModeProperty, RenderOptions.GetEdgeMode(visual));
                RenderOptions.SetEdgeMode(visual, mode);
            }
            else if (visual.GetValue(OriginalEdgeModeProperty) is { } original)
            {
                RenderOptions.SetEdgeMode(visual, original);
                visual.ClearValue(OriginalEdgeModeProperty);
            }
        });
    }

    public static EdgeMode? GetEdgeMode(Visual visual) => visual.GetValue(EdgeModeProperty);
    public static void SetEdgeMode(Visual visual, EdgeMode? value) => visual.SetValue(EdgeModeProperty, value);
}
