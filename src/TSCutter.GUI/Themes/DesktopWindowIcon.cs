using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace TSCutter.GUI.Themes;

/// <summary>显示窗口图标，并在图标更新或标题模板移除时释放解码后的位图。</summary>
public sealed class DesktopWindowIcon : Control
{
    public static readonly StyledProperty<WindowIcon?> IconProperty =
        AvaloniaProperty.Register<DesktopWindowIcon, WindowIcon?>(nameof(Icon));

    public static readonly StyledProperty<WindowIcon?> SmallIconProperty =
        AvaloniaProperty.Register<DesktopWindowIcon, WindowIcon?>(nameof(SmallIcon));

    private Bitmap? _bitmap;
    private bool _attached;

    public WindowIcon? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public WindowIcon? SmallIcon
    {
        get => GetValue(SmallIconProperty);
        set => SetValue(SmallIconProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if ((change.Property == IconProperty || change.Property == SmallIconProperty) && _attached)
            UpdateIcon();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        UpdateIcon();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private void UpdateIcon()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        if ((SmallIcon ?? Icon) is { } icon)
        {
            using var stream = new MemoryStream();
            icon.Save(stream);
            stream.Position = 0;
            // 无图标数据的后端不绘制图标，仍正常显示标题和按钮。
            if (stream.Length > 0)
                _bitmap = new Bitmap(stream);
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_bitmap != null)
            context.DrawImage(_bitmap, new Rect(Bounds.Size));
    }
}
