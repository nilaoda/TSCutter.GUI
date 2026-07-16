using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TSCutter.GUI.Controls;

/// <summary>
/// 用一个简单的实心矩形表示 PID 在 TS 总包数中的占比，避免复用 ProgressBar 的分段主题样式。
/// </summary>
public sealed class PacketShareBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<PacketShareBar, double>(nameof(Value));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<PacketShareBar, IBrush?>(nameof(Fill));

    static PacketShareBar()
    {
        AffectsRender<PacketShareBar>(ValueProperty, FillProperty);
    }

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        var ratio = Math.Clamp(Value, 0, 100) / 100.0;
        if (ratio <= 0)
            return;

        context.FillRectangle(
            Fill ?? Brushes.Black,
            new Rect(0, 0, Bounds.Width * ratio, Bounds.Height));
    }
}
