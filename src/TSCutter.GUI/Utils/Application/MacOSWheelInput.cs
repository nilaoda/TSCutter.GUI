using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace TSCutter.GUI.Utils.Application;

/// <summary>读取与 Avalonia 滚轮事件对应的 AppKit 精细滚动标记。</summary>
internal static class MacOSWheelInput
{
    public static bool IsPrecise(Control control, ulong timestamp)
    {
        if (!OperatingSystem.IsMacOS() || TopLevel.GetTopLevel(control)?.TryGetPlatformHandle() is not { HandleDescriptor: "NSWindow" } handle)
            return false;
        var app = Native.GetObject(Native.ApplicationClass, Native.SharedApplicationSelector);
        return IsPreciseEvent(Native.GetObject(app, Native.CurrentEventSelector), handle.Handle, timestamp);
    }

    internal static bool IsPreciseEvent(IntPtr ev, IntPtr window, ulong timestamp) =>
        ev != IntPtr.Zero && Native.GetInteger(ev, Native.TypeSelector) == 22 &&
        Native.GetObject(ev, Native.WindowSelector) == window &&
        (ulong)(Native.GetDouble(ev, Native.TimestampSelector) * 1000) == timestamp &&
        Native.GetBool(ev, Native.PreciseSelector) != 0;

    // AppKit 同步分发滚轮输入；核对窗口和时间戳，避免将其他原生事件用于合成输入。
    private static class Native
    {
        private const string Library = "/usr/lib/libobjc.dylib";
        public static readonly IntPtr ApplicationClass = GetClass("NSApplication");
        public static readonly IntPtr SharedApplicationSelector = Selector("sharedApplication");
        public static readonly IntPtr CurrentEventSelector = Selector("currentEvent");
        public static readonly IntPtr TypeSelector = Selector("type");
        public static readonly IntPtr WindowSelector = Selector("window");
        public static readonly IntPtr TimestampSelector = Selector("timestamp");
        public static readonly IntPtr PreciseSelector = Selector("hasPreciseScrollingDeltas");

        [DllImport(Library, EntryPoint = "objc_getClass")]
        private static extern IntPtr GetClass(string name);
        [DllImport(Library, EntryPoint = "sel_registerName")]
        private static extern IntPtr Selector(string name);
        [DllImport(Library, EntryPoint = "objc_msgSend")]
        public static extern IntPtr GetObject(IntPtr target, IntPtr selector);
        [DllImport(Library, EntryPoint = "objc_msgSend")]
        public static extern double GetDouble(IntPtr target, IntPtr selector);
        [DllImport(Library, EntryPoint = "objc_msgSend")]
        public static extern byte GetBool(IntPtr target, IntPtr selector);
        [DllImport(Library, EntryPoint = "objc_msgSend")]
        public static extern ulong GetInteger(IntPtr target, IntPtr selector);
    }
}
