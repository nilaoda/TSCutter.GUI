using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TSCutter.GUI.Services.Application.SystemProgress;

/// <summary>
/// 在 Dock 图标上叠加进度条：Dock 图标换成自定义视图，底层显示应用图标，上层显示绘制好的进度条图片。
/// 不修改应用图标本身。只能在界面线程调用。
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOSDockProgressIndicator : ISystemProgressIndicator
{
    private const int ImageSize = 256;
    private const double MinimumValueStep = 0.005;
    private static readonly TimeSpan MinimumRedrawInterval = TimeSpan.FromMilliseconds(250);

    private readonly Stopwatch _sinceRedraw = new();
    private SystemProgressSnapshot _shown = SystemProgressSnapshot.None;
    private nint _iconView;
    private nint _barView;

    public bool TracksWindows => false;

    public void Show(nint windowHandle, SystemProgressSnapshot snapshot)
    {
        if (snapshot.State == _shown.State)
        {
            if (snapshot.State == SystemProgressState.None)
                return;
            // 状态不变时限制重画频率，Dock 每次重画的开销不小。
            if (Math.Abs(snapshot.Value - _shown.Value) < MinimumValueStep ||
                _sinceRedraw.IsRunning && _sinceRedraw.Elapsed < MinimumRedrawInterval)
                return;
        }

        var pool = objc_autoreleasePoolPush();
        try
        {
            if (snapshot.State == SystemProgressState.None)
                RemoveViews();
            else
                ShowBar(DockProgressBarRenderer.Render(snapshot, ImageSize));
            Send(DockTile(), Selectors.Display);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
        _shown = snapshot;
        _sinceRedraw.Restart();
    }

    public void Forget(nint windowHandle)
    {
    }

    public void Dispose()
    {
        if (_iconView == 0)
            return;
        var pool = objc_autoreleasePoolPush();
        try
        {
            RemoveViews();
            Send(DockTile(), Selectors.Display);
        }
        finally
        {
            objc_autoreleasePoolPop(pool);
        }
        _shown = SystemProgressSnapshot.None;
    }

    private void ShowBar(byte[] png)
    {
        var image = CreateImage(png);
        try
        {
            if (_iconView != 0)
            {
                SendObject(_barView, Selectors.SetImage, image);
                return;
            }

            var dockTile = DockTile();
            var size = GetSize(dockTile, Selectors.Size);
            var frame = new CGRect(0, 0, size.Width, size.Height);

            _iconView = CreateImageView(GetObject(SharedApplication(), Selectors.ApplicationIconImage), frame);
            _barView = CreateImageView(image, frame);
            SendObject(_iconView, Selectors.AddSubview, _barView);
            SendObject(dockTile, Selectors.SetContentView, _iconView);
        }
        finally
        {
            Send(image, Selectors.Release);
        }
    }

    private void RemoveViews()
    {
        SendObject(DockTile(), Selectors.SetContentView, 0);
        if (_barView != 0)
            Send(_barView, Selectors.Release);
        if (_iconView != 0)
            Send(_iconView, Selectors.Release);
        _barView = 0;
        _iconView = 0;
    }

    private static nint CreateImageView(nint image, CGRect frame)
    {
        var view = GetObject(GetObject(objc_getClass("NSImageView"), Selectors.Alloc), Selectors.Init);
        SendRect(view, Selectors.SetFrame, frame);
        // NSImageScaleProportionallyUpOrDown：图片随 Dock 图标大小等比缩放。
        SendInteger(view, Selectors.SetImageScaling, 3);
        SendObject(view, Selectors.SetImage, image);
        return view;
    }

    private static nint CreateImage(byte[] png)
    {
        var data = GetDataWithBytes(objc_getClass("NSData"), Selectors.DataWithBytesLength, png, png.Length);
        return GetObjectWithArgument(GetObject(objc_getClass("NSImage"), Selectors.Alloc), Selectors.InitWithData, data);
    }

    private static nint SharedApplication() => GetObject(objc_getClass("NSApplication"), Selectors.SharedApplication);

    private static nint DockTile() => GetObject(SharedApplication(), Selectors.DockTile);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGRect(double X, double Y, double Width, double Height);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGSize(double Width, double Height);

    private static class Selectors
    {
        public static readonly nint Alloc = sel_registerName("alloc");
        public static readonly nint Init = sel_registerName("init");
        public static readonly nint Release = sel_registerName("release");
        public static readonly nint SharedApplication = sel_registerName("sharedApplication");
        public static readonly nint ApplicationIconImage = sel_registerName("applicationIconImage");
        public static readonly nint DockTile = sel_registerName("dockTile");
        public static readonly nint Size = sel_registerName("size");
        public static readonly nint Display = sel_registerName("display");
        public static readonly nint SetContentView = sel_registerName("setContentView:");
        public static readonly nint AddSubview = sel_registerName("addSubview:");
        public static readonly nint SetFrame = sel_registerName("setFrame:");
        public static readonly nint SetImage = sel_registerName("setImage:");
        public static readonly nint SetImageScaling = sel_registerName("setImageScaling:");
        public static readonly nint DataWithBytesLength = sel_registerName("dataWithBytes:length:");
        public static readonly nint InitWithData = sel_registerName("initWithData:");
    }

    private const string ObjCLibrary = "/usr/lib/libobjc.dylib";
    [DllImport(ObjCLibrary)]
    private static extern nint objc_getClass(string name);
    [DllImport(ObjCLibrary)]
    private static extern nint sel_registerName(string name);
    [DllImport(ObjCLibrary)]
    private static extern nint objc_autoreleasePoolPush();
    [DllImport(ObjCLibrary)]
    private static extern void objc_autoreleasePoolPop(nint pool);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint GetObject(nint target, nint selector);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint GetObjectWithArgument(nint target, nint selector, nint argument);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern nint GetDataWithBytes(nint target, nint selector, byte[] bytes, nint length);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern CGSize GetSize(nint target, nint selector);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void Send(nint target, nint selector);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendObject(nint target, nint selector, nint argument);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendInteger(nint target, nint selector, nint argument);
    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendRect(nint target, nint selector, CGRect rect);
}
