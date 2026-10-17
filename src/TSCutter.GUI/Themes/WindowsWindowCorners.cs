using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Classic.Avalonia.Theme;

namespace TSCutter.GUI.Themes;

/// <summary>让 Windows 11 的窗口、材质与阴影共用 DWM 圆角，离开圆角主题时恢复原值。</summary>
internal sealed class WindowsWindowCorners : AvaloniaObject
{
    private static readonly AttachedProperty<int?> OriginalPreferenceProperty =
        AvaloniaProperty.RegisterAttached<WindowsWindowCorners, ClassicWindow, int?>("OriginalPreference");

    public static void Update(ClassicWindow window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ||
            window.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" })
        {
            window.Classes.Remove("windows-native-corners");
            WindowsLegacyWindowChrome.UpdateNonClientRendering(window);
            return;
        }

        // ClassicWindow 和 Avalonia 会在装饰区、窗口状态变化时重设 DWM 属性，等它们完成后再应用。
        Dispatcher.UIThread.Post(() => Apply(window));
    }

    private static void Apply(ClassicWindow window)
    {
        if (!window.IsLoaded || window.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle)
            return;

        var original = window.GetValue(OriginalPreferenceProperty);
        var rounded = CustomTheme.IsWindows11(window.ActualThemeVariant) || CustomTheme.IsAero(window.ActualThemeVariant);
        if (!rounded)
        {
            if (original is { } saved && DwmSetWindowAttribute(handle.Handle, CornerPreferenceAttribute,
                    ref saved, sizeof(int)) == 0)
                window.ClearValue(OriginalPreferenceProperty);
            window.Classes.Remove("windows-native-corners");
            return;
        }

        if (original == null)
        {
            if (DwmGetWindowAttribute(handle.Handle, CornerPreferenceAttribute, out var saved, sizeof(int)) != 0)
                return;
            original = saved;
        }
        var preference = window.WindowState is WindowState.Maximized or WindowState.FullScreen ? DoNotRound : Round;
        if (DwmSetWindowAttribute(handle.Handle, CornerPreferenceAttribute, ref preference, sizeof(int)) == 0)
        {
            window.SetValue(OriginalPreferenceProperty, original);
            window.Classes.Set("windows-native-corners", true);
        }
    }

    private const int CornerPreferenceAttribute = 33; // DWMWA_WINDOW_CORNER_PREFERENCE
    private const int DoNotRound = 1; // DWMWCP_DONOTROUND
    private const int Round = 2; // DWMWCP_ROUND

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
