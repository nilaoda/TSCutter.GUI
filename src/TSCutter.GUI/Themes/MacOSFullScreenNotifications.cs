using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace TSCutter.GUI.Themes;

/// <summary>监听 AppKit 全屏切换完成，补足托管窗口状态早于原生动画完成的时序。</summary>
internal sealed class MacOSFullScreenNotifications : IDisposable
{
    private static readonly ConcurrentDictionary<IntPtr, Action<bool>> Callbacks = new();
    private readonly IntPtr center;
    private IntPtr observer;

    private MacOSFullScreenNotifications(IntPtr window, Action<bool> completed)
    {
        center = Native.GetObject(Native.GetClass("NSNotificationCenter"), Native.Selector("defaultCenter"));
        observer = Native.GetObject(Native.ObserverClass, Native.Selector("new"));
        Callbacks[observer] = completed;
        foreach (var notification in new[] { "NSWindowDidEnterFullScreenNotification", "NSWindowDidExitFullScreenNotification" })
        {
            var name = Native.GetString(Native.GetClass("NSString"), Native.Selector("stringWithUTF8String:"), notification);
            Native.AddObserver(center, Native.Selector("addObserver:selector:name:object:"), observer,
                Native.CallbackSelector, name, window);
        }
    }

    public static IDisposable? Observe(Window window, Action<bool> completed) =>
        OperatingSystem.IsMacOS() && window.TryGetPlatformHandle() is { HandleDescriptor: "NSWindow" } handle
            ? new MacOSFullScreenNotifications(handle.Handle, completed) : null;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnTransitionCompleted(IntPtr observer, IntPtr selector, IntPtr notification)
    {
        try
        {
            if (Callbacks.TryGetValue(observer, out var callback))
            {
                var name = Native.GetObject(notification, Native.Selector("name"));
                var text = Native.GetObject(name, Native.Selector("UTF8String"));
                callback(Marshal.PtrToStringUTF8(text) == "NSWindowDidEnterFullScreenNotification");
            }
        }
        catch (Exception exception)
        {
            // 不能让托管异常穿过 AppKit 的通知分发边界。
            Console.WriteLine($"Full-screen notification failed: {exception}");
        }
    }

    public void Dispose()
    {
        if (observer == IntPtr.Zero)
            return;
        Native.RemoveObserver(center, Native.Selector("removeObserver:"), observer);
        Callbacks.TryRemove(observer, out _);
        Native.GetObject(observer, Native.Selector("release"));
        observer = IntPtr.Zero;
    }

    // 延迟到真实 NSWindow 可用时才加载 Objective-C，其他平台和无头测试不触发原生调用。
    private static class Native
    {
        private const string Library = "/usr/lib/libobjc.dylib";
        public static readonly IntPtr CallbackSelector = Selector("tscutterWindowFullScreenCompleted:");
        public static readonly IntPtr ObserverClass = CreateClass();

        private static unsafe IntPtr CreateClass()
        {
            const string name = "TSCutterFullScreenObserver";
            var existing = GetClass(name);
            if (existing != IntPtr.Zero)
                return existing;
            var type = AllocateClass(GetClass("NSObject"), name, 0);
            AddMethod(type, CallbackSelector,
                (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&OnTransitionCompleted, "v@:@");
            RegisterClass(type);
            return type;
        }

        [DllImport(Library, EntryPoint = "objc_getClass")]
        public static extern IntPtr GetClass(string name);
        [DllImport(Library, EntryPoint = "sel_registerName")]
        public static extern IntPtr Selector(string name);
        [DllImport(Library, EntryPoint = "objc_allocateClassPair")]
        private static extern IntPtr AllocateClass(IntPtr superclass, string name, nuint extraBytes);
        [DllImport(Library, EntryPoint = "class_addMethod")]
        private static extern byte AddMethod(IntPtr type, IntPtr selector, IntPtr implementation, string signature);
        [DllImport(Library, EntryPoint = "objc_registerClassPair")]
        private static extern void RegisterClass(IntPtr type);
        [DllImport(Library, EntryPoint = "objc_msgSend")]
        public static extern IntPtr GetObject(IntPtr target, IntPtr selector);
        [DllImport(Library, EntryPoint = "objc_msgSend")]
        public static extern IntPtr GetString(IntPtr target, IntPtr selector,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        [DllImport(Library, EntryPoint = "objc_msgSend")]
        public static extern void AddObserver(IntPtr target, IntPtr selector, IntPtr observer,
            IntPtr callback, IntPtr name, IntPtr window);
        [DllImport(Library, EntryPoint = "objc_msgSend")]
        public static extern void RemoveObserver(IntPtr target, IntPtr selector, IntPtr observer);
    }
}
