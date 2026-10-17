using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Classic.Avalonia.Theme;

namespace TSCutter.GUI.Themes;

/// <summary>旧系统使用直角窗口，保留原生边框、阴影、缩放及系统菜单。</summary>
internal sealed class WindowsLegacyWindowChrome : AvaloniaObject
{
    private static readonly AttachedProperty<bool> MenuCallbackInstalledProperty =
        AvaloniaProperty.RegisterAttached<WindowsLegacyWindowChrome, ClassicWindow, bool>("MenuCallbackInstalled");

    private static readonly AttachedProperty<bool> NonClientRenderingEnabledProperty =
        AvaloniaProperty.RegisterAttached<WindowsLegacyWindowChrome, ClassicWindow, bool>("NonClientRenderingEnabled");

    private static readonly AttachedProperty<bool> UpdatePendingProperty =
        AvaloniaProperty.RegisterAttached<WindowsLegacyWindowChrome, ClassicWindow, bool>("UpdatePending");

    public static void Update(ClassicWindow window)
    {
        var legacy = OperatingSystem.IsWindows() && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) &&
                     (CustomTheme.IsAero(window.ActualThemeVariant) || CustomTheme.IsWindows11(window.ActualThemeVariant));
        var installed = window.GetValue(MenuCallbackInstalledProperty);
        if (legacy && !installed && window.TryGetPlatformHandle() is { HandleDescriptor: "HWND" })
        {
            // BorderOnly 会移除 WS_SYSMENU。先安装样式回调，保留 Alt+Space 和任务栏系统菜单。
            Win32Properties.AddWindowStylesCallback(window, PreserveSystemMenu);
            window.SetValue(MenuCallbackInstalledProperty, true);
            window.Closed += OnClosed;
        }
        else if (!legacy && installed)
            RemoveMenuCallback(window);

        window.Classes.Set("windows-legacy-frame", legacy);
        UpdateNonClientRendering(window);
    }

    internal static (uint style, uint exStyle) PreserveSystemMenu(uint style, uint exStyle) =>
        (style | 0x00080000u, exStyle); // BorderOnly 已移除原生标题栏，仅补回 WS_SYSMENU。

    public static void UpdateNonClientRendering(ClassicWindow window)
    {
        if (!OperatingSystem.IsWindows() || OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ||
            window.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } || window.GetValue(UpdatePendingProperty))
            return;

        // 等待 Avalonia 更新装饰区和窗口样式，避免最大化时被框架的后续调用覆盖。
        window.SetValue(UpdatePendingProperty, true);
        Dispatcher.UIThread.Post(() =>
        {
            window.SetValue(UpdatePendingProperty, false);
            ApplyNonClientRendering(window);
        });
    }

    private static void ApplyNonClientRendering(ClassicWindow window)
    {
        if (!window.IsLoaded || window.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle)
            return;

        var legacy = window.Classes.Contains("windows-legacy-frame");
        if (!legacy && !window.GetValue(NonClientRenderingEnabledProperty))
            return;

        // 直角轮廓与系统外框一致，保留 DWM 边框和阴影，不再使用 HWND 区域或应用圆角裁剪。
        var policy = legacy ? 2 : 0; // DWMNCRP_ENABLED / DWMNCRP_USEWINDOWSTYLE
        if (DwmSetWindowAttribute(handle.Handle, 2, ref policy, sizeof(int)) == 0) // DWMWA_NCRENDERING_POLICY
            window.SetValue(NonClientRenderingEnabledProperty, legacy);
    }

    private static void OnClosed(object? sender, EventArgs e) => RemoveMenuCallback((ClassicWindow)sender!);

    private static void RemoveMenuCallback(ClassicWindow window)
    {
        Win32Properties.RemoveWindowStylesCallback(window, PreserveSystemMenu);
        window.ClearValue(MenuCallbackInstalledProperty);
        window.Closed -= OnClosed;
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
