using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using Avalonia;
using SkiaSharp;

namespace TSCutter.GUI.Web;

internal sealed class WebUiFrameBuffers : IDisposable
{
    private static readonly Histogram<double> RasterDuration = WebUiSession.FrameMeter.CreateHistogram<double>("webui.capture.raster.duration", "ms");
    private static readonly Histogram<double> CompareDuration = WebUiSession.FrameMeter.CreateHistogram<double>("webui.capture.compare.duration", "ms");
    private static readonly Counter<long> ReusedFrames = WebUiSession.FrameMeter.CreateCounter<long>("webui.capture.reused.frames");
    private static readonly Histogram<double> PictureCount = WebUiSession.FrameMeter.CreateHistogram<double>("webui.capture.pictures");
    private readonly record struct WindowLayout(WebUiRecordedWindow Window, PixelSize Size, SKPoint Offset, SKPoint Scale);
    private WindowLayout[] layout = [];
    private SKSurface? drawing;
    private SKSurface? previous;
    private PixelSize size;
    private SKRectI? pendingDamage;
    private SKRectI? synchronize;
    private readonly SKPaint copyPaint = new() { BlendMode = SKBlendMode.Src };
    private readonly Dictionary<WebUiRecordedWindow, (PixelSize Size, SKSurface Surface)> windows = new();
    private SKRectI FullBounds => new(0, 0, size.Width, size.Height);

    private SKSurface EnsureFrame(PixelSize frameSize)
    {
        if (size != frameSize)
        {
            ReleaseSurfaces();
            size = frameSize;
        }
        return drawing ??= SKSurface.Create(new SKImageInfo(
            size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
    }

    internal bool Rasterize(WebUiCapturedFrame frame)
    {
        if (PictureCount.Enabled)
            PictureCount.Record(frame.Windows.Sum(window => window.Pictures.Length));
        var sameLayout = (drawing is not null || previous is not null) && size == frame.Size && layout.Length == frame.Windows.Count &&
            frame.Windows.Select((window, index) =>
                layout[index] == new WindowLayout(window.Window, window.Size, window.Offset, window.Scale)).All(same => same);
        if (sameLayout && frame.Windows.All(window => window.Pictures.Length == 0))
        {
            ReusedFrames.Add(1);
            return false;
        }
        var start = RasterDuration.Enabled ? Stopwatch.GetTimestamp() : 0;
        var surface = EnsureFrame(frame.Size);
        SKRectI? damage = sameLayout ? null : FullBounds;
        foreach (var window in frame.Windows)
        {
            if (!windows.TryGetValue(window.Window, out var cached) || cached.Size != window.Size)
            {
                cached.Surface?.Dispose();
                cached = (window.Size, SKSurface.Create(new SKImageInfo(window.Size.Width, window.Size.Height,
                    SKColorType.Bgra8888, SKAlphaType.Premul)));
                cached.Surface.Canvas.Clear(SKColors.Transparent);
                windows[window.Window] = cached;
            }
            // 同一窗口的增量依次应用到复用缓冲，静止窗口直接复用已有像素。
            using (var restore = new SKAutoCanvasRestore(cached.Surface.Canvas))
            {
                cached.Surface.Canvas.Scale(window.Scale.X, window.Scale.Y);
                foreach (var picture in window.Pictures)
                    cached.Surface.Canvas.DrawPicture(picture);
            }
            if (sameLayout && window.Pictures.Length > 0)
                damage = Union(damage, DamageBounds(window));
        }
        if (damage is { } repaint)
        {
            // 交换双缓冲后，只同步上次改变的区域；本轮完全覆盖该区域时省去复制。
            if (synchronize is { } stale && !repaint.Contains(stale))
                SynchronizeDrawing();
            synchronize = null;
            using (var restore = new SKAutoCanvasRestore(surface.Canvas))
            {
                surface.Canvas.ClipRect(repaint);
                surface.Canvas.Clear(SKColors.Black);
                // 遮挡和透明混合仍按完整层级重放，只把输出限制在受影响的像素内。
                foreach (var window in frame.Windows)
                {
                    using var image = windows[window.Window].Surface.Snapshot();
                    surface.Canvas.DrawImage(image, window.Offset);
                }
            }
            surface.Canvas.Flush();
            pendingDamage = Union(pendingDamage, repaint);
        }
        foreach (var removed in windows.Keys.Where(key => !frame.Windows.Any(window => window.Window == key)).ToArray())
        {
            windows[removed].Surface.Dispose();
            windows.Remove(removed);
        }
        layout = frame.Windows.Select(window => new WindowLayout(window.Window, window.Size, window.Offset, window.Scale)).ToArray();
        if (start != 0)
            RasterDuration.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        return damage is not null;
    }

    internal SKImage? SnapshotIfChanged(bool force, out bool changed)
    {
        changed = false;
        // 编码拥塞可能跳过上轮快照，不能因本轮没有新指令而丢掉尚未提交的变化。
        if (pendingDamage is not { } damage)
            return force ? previous?.Snapshot() : null;
        changed = true;
        if (previous is not null)
        {
            using var currentPixels = drawing!.PeekPixels();
            using var previousPixels = previous.PeekPixels();
            var compareStart = CompareDuration.Enabled ? Stopwatch.GetTimestamp() : 0;
            changed = !PixelsEqual(currentPixels, previousPixels, damage);
            if (compareStart != 0)
                CompareDuration.Record(Stopwatch.GetElapsedTime(compareStart).TotalMilliseconds);
        }
        pendingDamage = null;
        if (!changed)
            return force ? previous!.Snapshot() : null;

        (drawing, previous) = (previous, drawing);
        synchronize = drawing is null ? FullBounds : damage;
        return previous!.Snapshot();
    }

    private void SynchronizeDrawing()
    {
        if (synchronize is not { } area || previous is null)
            return;
        using var pixels = previous.PeekPixels();
        using var subset = pixels.ExtractSubset(area);
        // 独立包装需要同步的像素，不取得编码线程仍持有的同一份托管快照。
        using var image = SKImage.FromPixels(subset);
        drawing!.Canvas.DrawImage(image, area.Left, area.Top, copyPaint);
        synchronize = null;
    }

    private SKRectI? DamageBounds(WebUiWindowFrame window)
    {
        // 裁剪来自窗口逻辑坐标；向外取整并留出边缘，覆盖缩放与分数位置的采样。
        var rect = window.Damage is { } region
            ? new SKRect((float)region.Left * window.Scale.X, (float)region.Top * window.Scale.Y,
                (float)region.Right * window.Scale.X, (float)region.Bottom * window.Scale.Y)
            : new SKRect(0, 0, window.Size.Width, window.Size.Height);
        var bounds = new SKRectI(Math.Max(0, (int)Math.Floor(rect.Left + window.Offset.X) - 2),
            Math.Max(0, (int)Math.Floor(rect.Top + window.Offset.Y) - 2),
            Math.Min(size.Width, (int)Math.Ceiling(rect.Right + window.Offset.X) + 2),
            Math.Min(size.Height, (int)Math.Ceiling(rect.Bottom + window.Offset.Y) + 2));
        return bounds.Width > 0 && bounds.Height > 0 ? bounds : null;
    }

    private static SKRectI? Union(SKRectI? first, SKRectI? second) => first is not { } a ? second :
        second is not { } b ? first : new SKRectI(Math.Min(a.Left, b.Left), Math.Min(a.Top, b.Top),
            Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    private bool PixelsEqual(SKPixmap current, SKPixmap before, SKRectI area)
    {
        var left = current.GetPixelSpan();
        var right = before.GetPixelSpan();
        if (area == FullBounds)
            return left.SequenceEqual(right);
        var bytes = area.Width * 4;
        for (var y = area.Top; y < area.Bottom; ++y)
            if (!left.Slice(y * current.RowBytes + area.Left * 4, bytes)
                .SequenceEqual(right.Slice(y * before.RowBytes + area.Left * 4, bytes)))
                return false;
        return true;
    }

    public void Dispose()
    {
        ReleaseSurfaces();
        foreach (var cached in windows.Values)
            cached.Surface.Dispose();
        windows.Clear();
        copyPaint.Dispose();
    }

    private void ReleaseSurfaces()
    {
        drawing?.Dispose();
        previous?.Dispose();
        drawing = previous = null;
        pendingDamage = synchronize = null;
        layout = [];
    }
}
