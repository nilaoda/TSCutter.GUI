using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Classic.Avalonia.Theme;

namespace TSCutter.GUI.Themes;

/// <summary>为融合到客户区的 macOS 标题补齐拖动和系统双击行为。</summary>
internal static class MacOSWindowHeader
{
    internal enum DoubleClickAction { Zoom, Minimize, None }

    public static void OnPointerPressed(ClassicWindow window, PointerPressedEventArgs e)
    {
        if (e.Handled || !window.Classes.Contains("macos-native-titlebar") ||
            window.WindowState == WindowState.FullScreen ||
            e.GetCurrentPoint(window).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed ||
            e.Source is not Visual source)
            return;

        var header = source.GetSelfAndVisualAncestors().OfType<Border>()
            .FirstOrDefault(control => control.Name == "MacOSHeader" && control.TemplatedParent == window);
        // 左侧预留区域由原生红黄绿接管；不把正文或其他主题的点击当作标题操作。
        if (header is not { IsEffectivelyVisible: true } || e.GetPosition(header).X < 84)
            return;

        e.Handled = true;
        // 客户区标题的首击也应激活窗口，否则 AppKit 可能忽略后台窗口的拖动请求。
        window.Activate();
        if (e.ClickCount == 2)
            ApplyDoubleClick(window, GetDoubleClickAction());
        else if (e.ClickCount == 1)
            BeginMoveDrag(window, e);
    }

    private static void BeginMoveDrag(Window window, PointerPressedEventArgs e)
    {
        if (OperatingSystem.IsMacOS() && window.TryGetPlatformHandle() is { HandleDescriptor: "NSWindow" } handle)
        {
            // 原生标题区域的点击可能直接从 NSWindow 转发，未经过 Avalonia 缓存鼠标事件的 NSView。
            // 使用当前按下事件启动 AppKit 拖动，避免 BeginMoveDrag 因没有缓存事件而无效。
            var app = GetObject(GetClass("NSApplication"), Selector("sharedApplication"));
            var current = GetObject(app, Selector("currentEvent"));
            if (current != IntPtr.Zero && GetInteger(current, Selector("type")) == 1 &&
                GetObject(current, Selector("window")) == handle.Handle)
            {
                SendObject(handle.Handle, Selector("performWindowDragWithEvent:"), current);
                return;
            }
        }
        window.BeginMoveDrag(e);
    }

    internal static void ApplyDoubleClick(Window window, DoubleClickAction action)
    {
        if (window.WindowState == WindowState.FullScreen) return;
        if (action == DoubleClickAction.Minimize && window.CanMinimize)
            window.WindowState = WindowState.Minimized;
        else if (action == DoubleClickAction.Zoom && window.CanResize && window.CanMaximize)
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private static DoubleClickAction GetDoubleClickAction()
    {
        if (!OperatingSystem.IsMacOS()) return DoubleClickAction.Zoom;
        var defaults = GetObject(GetClass("NSUserDefaults"), Selector("standardUserDefaults"));
        var key = GetString(GetClass("NSString"), Selector("stringWithUTF8String:"), "AppleActionOnDoubleClick");
        var value = GetObjectWithArgument(defaults, Selector("stringForKey:"), key);
        var action = value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(GetObject(value, Selector("UTF8String")));
        if (string.Equals(action, "Minimize", StringComparison.OrdinalIgnoreCase)) return DoubleClickAction.Minimize;
        if (string.Equals(action, "None", StringComparison.OrdinalIgnoreCase)) return DoubleClickAction.None;
        if (action == null)
        {
            // 旧版 macOS 使用布尔偏好；没有设置时沿用系统默认的缩放行为。
            var legacyKey = GetString(GetClass("NSString"), Selector("stringWithUTF8String:"), "AppleMiniaturizeOnDoubleClick");
            if (GetBool(defaults, Selector("boolForKey:"), legacyKey) != 0) return DoubleClickAction.Minimize;
        }
        return DoubleClickAction.Zoom;
    }

    private const string ObjCLibrary = "/usr/lib/libobjc.dylib";
    [DllImport(ObjCLibrary, EntryPoint = "objc_getClass")]
    private static extern IntPtr GetClass(string name);
    [DllImport(ObjCLibrary, EntryPoint = "sel_registerName")]
    private static extern IntPtr Selector(string name);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr GetObject(IntPtr target, IntPtr selector);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr GetObjectWithArgument(IntPtr target, IntPtr selector, IntPtr argument);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint GetInteger(IntPtr target, IntPtr selector);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendObject(IntPtr target, IntPtr selector, IntPtr argument);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern byte GetBool(IntPtr target, IntPtr selector, IntPtr argument);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr GetString(IntPtr target, IntPtr selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
}
