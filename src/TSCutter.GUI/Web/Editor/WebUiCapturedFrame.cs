using System;
using System.Collections.Generic;
using Avalonia;

namespace TSCutter.GUI.Web;

// 只持有绘图指令，不把 Avalonia 控件或位图锁交给后台线程。
internal sealed class WebUiCapturedFrame(IReadOnlyList<WebUiWindowFrame> windows,
    PixelSize size, PixelSize viewport, int jpegQuality, int videoBitrate) : IDisposable
{
    internal IReadOnlyList<WebUiWindowFrame> Windows { get; } = windows;
    internal bool Rendered => Windows.Count > 0;
    internal PixelSize Size { get; } = size;
    internal PixelSize Viewport { get; } = viewport;
    internal int JpegQuality { get; } = jpegQuality;
    internal int VideoBitrate { get; } = videoBitrate;

    public void Dispose()
    {
        foreach (var window in Windows)
            foreach (var picture in window.Pictures)
                picture.Dispose();
    }
}
