using System;
using Avalonia;
using Avalonia.Controls;
using Classic.Avalonia.Theme;

namespace TSCutter.GUI.Themes;

/// <summary>窗口首次显示时，按完整的系统窗口尺寸修正居中位置。</summary>
internal sealed class MacOSWindowPlacement : AvaloniaObject
{
    private static readonly AttachedProperty<bool> PendingProperty =
        AvaloniaProperty.RegisterAttached<MacOSWindowPlacement, ClassicWindow, bool>("Pending");

    public static void Prepare(ClassicWindow window)
    {
        // Avalonia.Native 11.3 在 Show 之前不返回外框尺寸，首次居中只能使用客户区尺寸。
        // 对于外框与客户区尺寸不同的窗口，需要在外框尺寸就绪后修正位置。
        window.SetValue(PendingProperty, CustomTheme.UsesNativeTitleBar(window.ActualThemeVariant) &&
            window.WindowStartupLocation != WindowStartupLocation.Manual && window.FrameSize == null &&
            window.TryGetPlatformHandle() is { HandleDescriptor: "NSWindow" });
    }

    public static void Complete(ClassicWindow window)
    {
        if (!window.GetValue(PendingProperty) || window.FrameSize is not { } frame)
            return;
        window.ClearValue(PendingProperty);
        if (!CustomTheme.UsesNativeTitleBar(window.ActualThemeVariant) || window.WindowState != WindowState.Normal ||
            window.WindowStartupLocation == WindowStartupLocation.Manual)
            return;

        var owner = window.Owner as Window;
        var screen = window.Screens.ScreenFromWindow(owner ?? window) ?? window.Screens.Primary;
        var bounds = window.WindowStartupLocation == WindowStartupLocation.CenterOwner &&
                     owner is { WindowState: not WindowState.Minimized }
            ? new PixelRect(owner.Position, PixelSize.FromSize(owner.FrameSize ?? owner.ClientSize, owner.DesktopScaling))
            : screen?.WorkingArea;
        if (bounds is not { } area)
            return;
        var size = PixelSize.FromSize(frame, window.DesktopScaling);
        var position = area.CenterRect(new PixelRect(size)).Position;
        if (screen is { WorkingArea: var work })
            position = new PixelPoint(
                Math.Clamp(position.X, work.X, Math.Max(work.X, work.Right - size.Width)),
                Math.Clamp(position.Y, work.Y, Math.Max(work.Y, work.Bottom - size.Height)));
        // FrameSize 在 OnOpened 期间同步发布，直接修正位置。
        // 不延迟到下一轮调度或布局，避免窗口已经可交互后再次移动。
        window.Position = position;
    }
}
