using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Classic.Avalonia.Theme;

namespace TSCutter.GUI.Themes;

/// <summary>绘制桌面主题外框，并裁剪窗口和拖入提示的圆角。</summary>
public sealed class DesktopWindowChrome : AvaloniaObject
{
    public static readonly AttachedProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.RegisterAttached<DesktopWindowChrome, Control, CornerRadius>("CornerRadius");

    public static readonly AttachedProperty<WindowFramePalette?> FramePaletteProperty =
        AvaloniaProperty.RegisterAttached<DesktopWindowChrome, ClassicBorderDecorator, WindowFramePalette?>("FramePalette");

    // 空值保留装饰器原有边框布局；设置后只绘制圆角外框，不占用正文布局空间。
    public static readonly AttachedProperty<Thickness?> FrameThicknessProperty =
        AvaloniaProperty.RegisterAttached<DesktopWindowChrome, ClassicBorderDecorator, Thickness?>("FrameThickness");

    // Aero 侧框与标题栏共用渐变坐标，避免按整窗高度拉伸后在接缝处出现色差。
    public static readonly AttachedProperty<double> CaptionHeightProperty =
        AvaloniaProperty.RegisterAttached<DesktopWindowChrome, ClassicBorderDecorator, double>("CaptionHeight");

    static DesktopWindowChrome()
    {
        // 类处理器不持有窗口实例，无需为每个窗口订阅事件。
        FrameThicknessProperty.Changed.AddClassHandler<ClassicBorderDecorator>((border, _) =>
        {
            UpdateClip(border);
            UpdateFrame(border);
        });
        FramePaletteProperty.Changed.AddClassHandler<ClassicBorderDecorator>((border, _) => UpdateFrame(border));
        CaptionHeightProperty.Changed.AddClassHandler<ClassicBorderDecorator>((border, _) => UpdateFrame(border));
        Visual.BoundsProperty.Changed.AddClassHandler<ClassicBorderDecorator>((border, _) =>
        {
            if (GetFramePalette(border) != null)
                UpdateFrame(border);
        });
        CornerRadiusProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            UpdateClip(control);
            if (control is ClassicBorderDecorator border && GetFramePalette(border) != null)
                UpdateFrame(border);
        });
        Visual.BoundsProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            if (GetCornerRadius(control) != default)
                UpdateClip(control);
        });
    }

    public static CornerRadius GetCornerRadius(Control control) => control.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(Control control, CornerRadius value) => control.SetValue(CornerRadiusProperty, value);

    public static WindowFramePalette? GetFramePalette(ClassicBorderDecorator border) => border.GetValue(FramePaletteProperty);
    public static void SetFramePalette(ClassicBorderDecorator border, WindowFramePalette? value) => border.SetValue(FramePaletteProperty, value);

    public static Thickness? GetFrameThickness(ClassicBorderDecorator border) => border.GetValue(FrameThicknessProperty);
    public static void SetFrameThickness(ClassicBorderDecorator border, Thickness? value) => border.SetValue(FrameThicknessProperty, value);

    public static double GetCaptionHeight(ClassicBorderDecorator border) => border.GetValue(CaptionHeightProperty);
    public static void SetCaptionHeight(ClassicBorderDecorator border, double value) => border.SetValue(CaptionHeightProperty, value);

    private static void UpdateFrame(ClassicBorderDecorator border)
    {
        var palette = GetFramePalette(border);
        if (palette == null)
        {
            // 撤销主题样式时延后清除绘制值，避免修改正在遍历的样式属性集合。
            Dispatcher.UIThread.Post(() =>
            {
                if (GetFramePalette(border) == null)
                {
                    border.ClearValue(ClassicBorderDecorator.BackgroundProperty);
                    border.ClearValue(ClassicBorderDecorator.BorderBrushProperty);
                }
            });
            return;
        }

        var width = border.Bounds.Width;
        var height = border.Bounds.Height;
        if (width <= 0 || height <= 0)
            return;

        var brushes = new[] { palette.Outer, palette.Middle, palette.Inner };
        var corners = GetCornerRadius(border);
        var thickness = GetFrameThickness(border);
        var contentRect = new Rect(0, 0, width, height).Deflate(thickness ?? default);
        var drawing = new DrawingGroup();
        if (thickness == null || thickness.Value != default)
        {
            using var context = drawing.Open();
            // 内侧高光沿圆弧绘制，窗口统一控制外轮廓，避免重复裁剪。
            // 装饰器只承载画刷，不参与鼠标命中，也不改变标题栏和正文的布局。
            for (var inset = 0; inset < brushes.Length && width > inset * 2 && height > inset * 2; inset++)
            {
                var rect = new Rect(inset, inset, width - inset * 2, height - inset * 2);
                // 细边框到达正文区域后停止绘制，避免一像素外框被后续内层画成两像素。
                if (thickness != null && contentRect.Contains(rect))
                    break;
                // 最外层交给窗口或原生图层裁剪，只生成一次外缘覆盖率；内侧高光仍按圆弧绘制。
                Geometry outer = thickness == null || inset == 0 ? new RectangleGeometry(rect) :
                    CreateRoundedGeometry(rect, DeflateCorners(corners, inset));
                Geometry? inner = null;
                if (inset < brushes.Length - 1 && rect.Width > 2 && rect.Height > 2)
                    inner = thickness == null ? new RectangleGeometry(rect.Deflate(1)) :
                        CreateRoundedGeometry(rect.Deflate(1), DeflateCorners(corners, inset + 1));
                else if (thickness != null && contentRect.Width > 0 && contentRect.Height > 0)
                    inner = new RectangleGeometry(contentRect);

                var brush = brushes[inset];
                var captionHeight = GetCaptionHeight(border);
                if (inset == brushes.Length - 1 && thickness is { } frame && captionHeight > 0 &&
                    brush is LinearGradientBrush gradient)
                {
                    // 与标题栏在窗口内的位置对齐，正文以下沿用渐变末端颜色。
                    RelativePoint Align(RelativePoint point) => point.Unit == RelativeUnit.Absolute ? point :
                        new RelativePoint(frame.Left + point.Point.X * contentRect.Width,
                            frame.Top + point.Point.Y * captionHeight, RelativeUnit.Absolute);
                    brush = new LinearGradientBrush
                    {
                        StartPoint = Align(gradient.StartPoint), EndPoint = Align(gradient.EndPoint),
                        GradientStops = gradient.GradientStops, SpreadMethod = gradient.SpreadMethod,
                        Opacity = gradient.Opacity, Transform = gradient.Transform, TransformOrigin = gradient.TransformOrigin
                    };
                }
                context.DrawGeometry(brush, null, inner == null ? outer :
                    new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner));
            }
        }
        // 有正文的拖入提示继续使用原有边框布局，窗口装饰层则直接绘制完整圆角环。
        border.SetCurrentValue(thickness == null ? ClassicBorderDecorator.BorderBrushProperty : ClassicBorderDecorator.BackgroundProperty,
            new DrawingBrush(drawing) { Stretch = Stretch.Fill });
    }

    private static CornerRadius DeflateCorners(CornerRadius corners, double inset) => new(
        Math.Max(0, corners.TopLeft - inset), Math.Max(0, corners.TopRight - inset),
        Math.Max(0, corners.BottomRight - inset), Math.Max(0, corners.BottomLeft - inset));

    private static void UpdateClip(Control control)
    {
        // 窗口装饰层已绘制圆角几何，外轮廓由窗口统一裁剪；再次裁剪会叠乘边缘透明度。
        // 拖入提示仍需裁剪其正文，继续保留原有裁剪。
        if (control is ClassicBorderDecorator border && GetFrameThickness(border) != null)
        {
            control.ClearValue(Visual.ClipProperty);
            return;
        }

        var width = control.Bounds.Width;
        var height = control.Bounds.Height;
        var corners = GetCornerRadius(control);
        if (corners == default || width <= 0 || height <= 0)
        {
            // 与外框一样，等主题样式撤销完成后再清除裁剪。
            Dispatcher.UIThread.Post(() =>
            {
                if (GetCornerRadius(control) == default || control.Bounds.Width <= 0 || control.Bounds.Height <= 0)
                    control.ClearValue(Visual.ClipProperty);
            });
            return;
        }

        control.SetCurrentValue(Visual.ClipProperty, CreateRoundedGeometry(new Rect(0, 0, width, height), corners));
    }

    private static StreamGeometry CreateRoundedGeometry(Rect rect, CornerRadius corners)
    {
        var limit = Math.Min(rect.Width, rect.Height) / 2;
        var topLeft = Math.Min(corners.TopLeft, limit);
        var topRight = Math.Min(corners.TopRight, limit);
        var bottomRight = Math.Min(corners.BottomRight, limit);
        var bottomLeft = Math.Min(corners.BottomLeft, limit);
        var clip = new StreamGeometry();
        using (var context = clip.Open())
        {
            context.BeginFigure(new Point(rect.Left + topLeft, rect.Top), true);
            context.LineTo(new Point(rect.Right - topRight, rect.Top));
            RoundTo(context, new Point(rect.Right, rect.Top + topRight), topRight);
            context.LineTo(new Point(rect.Right, rect.Bottom - bottomRight));
            RoundTo(context, new Point(rect.Right - bottomRight, rect.Bottom), bottomRight);
            context.LineTo(new Point(rect.Left + bottomLeft, rect.Bottom));
            RoundTo(context, new Point(rect.Left, rect.Bottom - bottomLeft), bottomLeft);
            context.LineTo(new Point(rect.Left, rect.Top + topLeft));
            RoundTo(context, new Point(rect.Left + topLeft, rect.Top), topLeft);
            context.EndFigure(true);
        }
        return clip;
    }

    private static void RoundTo(StreamGeometryContext context, Point end, double radius)
    {
        if (radius > 0)
            context.ArcTo(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise);
        else
            context.LineTo(end);
    }
}

/// <summary>外框由外向内的三层画刷，每套配色分别提供活动和失焦状态。</summary>
public sealed class WindowFramePalette
{
    public IBrush Outer { get; set; } = Brushes.Transparent;
    public IBrush Middle { get; set; } = Brushes.Transparent;
    public IBrush Inner { get; set; } = Brushes.Transparent;
}
