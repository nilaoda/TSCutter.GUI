using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform.Surfaces;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.TextFormatting;
using Avalonia.Platform;
using Avalonia.Skia.Helpers;
using SkiaSharp;

namespace TSCutter.GUI.Web;

// 只替换远程进程的屏幕输出；字体、位图、几何与离屏图层仍交给原有 Skia 后端。
internal static class WebUiRenderBackend
{
    private static readonly ConditionalWeakTable<object, WebUiRecordedWindow> Windows = new();
    private static readonly Histogram<double> RecordDuration = WebUiSession.FrameMeter.CreateHistogram<double>("webui.render.record.duration", "ms");
    private static long sceneVersion;
    internal static long SceneVersion => Volatile.Read(ref sceneVersion);
    internal static void InvalidateScene() => Interlocked.Increment(ref sceneVersion);

    internal static AppBuilder UseWebUiRendering(this AppBuilder builder)
    {
        var initialize = builder.RenderingSubsystemInitializer!;
        return builder.UseRenderingSubsystem(() => { initialize(); Initialize(); }, "RemoteSkia");
    }

    private static void Initialize()
    {
        var original = AvaloniaLocator.Current.GetRequiredService<IPlatformRenderInterface>();
        AvaloniaLocator.CurrentMutable.Bind<IPlatformRenderInterface>().ToConstant(new RenderInterface(original));
    }

    internal static WebUiRecordedWindow GetWindow(Window window) =>
        Windows.GetValue(window.PlatformImpl!.Surfaces.OfType<IFramebufferPlatformSurface>().First(), _ => new());

    private sealed class RenderInterface(IPlatformRenderInterface inner) : IPlatformRenderInterface
    {
        public IPlatformRenderInterfaceContext CreateBackendContext(IPlatformGraphicsContext? context) =>
            new BackendContext(inner.CreateBackendContext(context), inner);
        public IGeometryImpl CreateEllipseGeometry(Rect rect) => inner.CreateEllipseGeometry(rect);
        public IGeometryImpl CreateLineGeometry(Point p1, Point p2) => inner.CreateLineGeometry(p1, p2);
        public IGeometryImpl CreateRectangleGeometry(Rect rect) => inner.CreateRectangleGeometry(rect);
        public IStreamGeometryImpl CreateStreamGeometry() => inner.CreateStreamGeometry();
        public IGeometryImpl CreateGeometryGroup(FillRule fillRule, IReadOnlyList<IGeometryImpl> children) => inner.CreateGeometryGroup(fillRule, children);
        public IGeometryImpl CreateCombinedGeometry(GeometryCombineMode combineMode, IGeometryImpl g1, IGeometryImpl g2) => inner.CreateCombinedGeometry(combineMode, g1, g2);
        public IGeometryImpl BuildGlyphRunGeometry(GlyphRun glyphRun) => inner.BuildGlyphRunGeometry(glyphRun);
        public IRenderTargetBitmapImpl CreateRenderTargetBitmap(PixelSize size, Vector dpi) => inner.CreateRenderTargetBitmap(size, dpi);
        public IWriteableBitmapImpl CreateWriteableBitmap(PixelSize size, Vector dpi, PixelFormat format, AlphaFormat alphaFormat) => inner.CreateWriteableBitmap(size, dpi, format, alphaFormat);
        public IBitmapImpl LoadBitmap(string fileName) => inner.LoadBitmap(fileName);
        public IBitmapImpl LoadBitmap(Stream stream) => inner.LoadBitmap(stream);
        public IWriteableBitmapImpl LoadWriteableBitmapToWidth(Stream stream, int width, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality) => inner.LoadWriteableBitmapToWidth(stream, width, interpolationMode);
        public IWriteableBitmapImpl LoadWriteableBitmapToHeight(Stream stream, int height, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality) => inner.LoadWriteableBitmapToHeight(stream, height, interpolationMode);
        public IWriteableBitmapImpl LoadWriteableBitmap(string fileName) => inner.LoadWriteableBitmap(fileName);
        public IWriteableBitmapImpl LoadWriteableBitmap(Stream stream) => inner.LoadWriteableBitmap(stream);
        public IBitmapImpl LoadBitmapToWidth(Stream stream, int width, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality) => inner.LoadBitmapToWidth(stream, width, interpolationMode);
        public IBitmapImpl LoadBitmapToHeight(Stream stream, int height, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality) => inner.LoadBitmapToHeight(stream, height, interpolationMode);
        public IBitmapImpl ResizeBitmap(IBitmapImpl bitmapImpl, PixelSize destinationSize, BitmapInterpolationMode interpolationMode = BitmapInterpolationMode.HighQuality) => inner.ResizeBitmap(bitmapImpl, destinationSize, interpolationMode);
        public IBitmapImpl LoadBitmap(PixelFormat format, AlphaFormat alphaFormat, IntPtr data, PixelSize size, Vector dpi, int stride) => inner.LoadBitmap(format, alphaFormat, data, size, dpi, stride);
        public IGlyphRunImpl CreateGlyphRun(IGlyphTypeface glyphTypeface, double fontRenderingEmSize, IReadOnlyList<GlyphInfo> glyphInfos, Point baselineOrigin) => inner.CreateGlyphRun(glyphTypeface, fontRenderingEmSize, glyphInfos, baselineOrigin);
        public bool IsSupportedBitmapPixelFormat(PixelFormat format) => inner.IsSupportedBitmapPixelFormat(format);
        public IPlatformRenderInterfaceRegion CreateRegion() => inner.CreateRegion();
        public bool SupportsIndividualRoundRects => inner.SupportsIndividualRoundRects;
        public AlphaFormat DefaultAlphaFormat => inner.DefaultAlphaFormat;
        public PixelFormat DefaultPixelFormat => inner.DefaultPixelFormat;
        public bool SupportsRegions => inner.SupportsRegions;
    }

    private sealed class BackendContext(IPlatformRenderInterfaceContext inner, IPlatformRenderInterface factory) : IPlatformRenderInterfaceContext
    {
        public IRenderTarget CreateRenderTarget(IEnumerable<object> surfaces)
        {
            var candidates = surfaces.ToArray();
            var surface = candidates.OfType<IFramebufferPlatformSurface>().FirstOrDefault();
            return surface is null ? inner.CreateRenderTarget(candidates) :
                new RecordingTarget(Windows.GetValue(surface, _ => new()), factory);
        }
        public IDrawingContextLayerImpl CreateOffscreenRenderTarget(PixelSize size, double scaling) => inner.CreateOffscreenRenderTarget(size, scaling);
        public bool IsLost => inner.IsLost;
        public IReadOnlyDictionary<Type, object> PublicFeatures => inner.PublicFeatures;
        public object? TryGetFeature(Type type) => inner.TryGetFeature(type);
        public void Dispose() => inner.Dispose();
    }

    private sealed class RecordingTarget(WebUiRecordedWindow window, IPlatformRenderInterface factory) : IRenderTarget2
    {
        public bool IsCorrupted => false;
        // 后台窗口缓冲按顺序应用脏区，保留上一帧内容；不再创建额外的整窗栅格图层。
        public RenderTargetProperties Properties => new()
        {
            RetainsPreviousFrameContents = true,
            IsSuitableForDirectRendering = true
        };
        public IDrawingContextImpl CreateDrawingContext(bool useScaledDrawing) => CreateDrawingContext(window.Size, out _);
        public IDrawingContextImpl CreateDrawingContext(PixelSize size, out RenderTargetDrawingContextProperties properties)
        {
            properties = new()
            {
                PreviousFrameIsRetained = true
            };
            var start = RecordDuration.Enabled ? Stopwatch.GetTimestamp() : 0;
            var recorder = new SKPictureRecorder();
            var canvas = recorder.BeginRecording(new SKRect(0, 0, size.Width, size.Height));
            RecordingContext? context = null;
            context = new RecordingContext(DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96)), factory, !window.NeedsRefresh, size, () =>
            {
                try
                {
                    window.Publish(recorder.EndRecording(), size, context!.Replacement);
                }
                finally
                {
                    recorder.Dispose();
                    if (start != 0)
                        RecordDuration.Record(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                }
            });
            return context;
        }
        public void Dispose() => window.Reset();
    }

    // 图层走通用位图绘制，避免 Skia 的直接 Blit 强制转换录制上下文。
    private sealed class DrawingLayer(IDrawingContextLayerImpl inner) : IDrawingContextLayerImpl
    {
        internal IDrawingContextLayerImpl Source => inner;
        public bool CanBlit => false;
        public bool IsCorrupted => inner.IsCorrupted;
        public PixelSize PixelSize => inner.PixelSize;
        public Vector Dpi => inner.Dpi;
        public int Version => inner.Version;
        public IDrawingContextImpl CreateDrawingContext(bool useScaledDrawing) => inner.CreateDrawingContext(useScaledDrawing);
        public void Blit(IDrawingContextImpl context)
        {
            var bounds = new Rect(PixelSize.ToSize(1));
            context.DrawBitmap(inner, 1, bounds, bounds);
        }
        public void Save(string path, int? quality = null) => inner.Save(path, quality);
        public void Save(Stream stream, int? quality = null) => inner.Save(stream, quality);
        public void Dispose() => inner.Dispose();
    }

    private sealed class RecordingContext(IDrawingContextImpl inner, IPlatformRenderInterface factory, bool freezeBitmaps, PixelSize recordingSize, Action finish) : IDrawingContextImplWithEffects, IDrawingContextWithAcrylicLikeSupport
    {
        private bool disposed;
        private bool painted;
        private int clipDepth;
        private int replacementDepth;
        private bool replacementScopeEnded;
        private Rect[]? rootClip;
        internal Rect[]? Replacement
        {
            get; private set;
        }

        private void Drawing()
        {
            painted = true;
            // 外层脏区裁剪弹出后再绘制的叠加内容不属于已记录的替换范围。
            if (replacementScopeEnded || clipDepth < replacementDepth)
                Replacement = null;
        }

        private void RootClip(Rect[]? rectangles)
        {
            if (clipDepth == 0 && !painted)
                rootClip = Transform == Matrix.Identity ? rectangles : null;
            ++clipDepth;
        }

        private readonly Dictionary<IBitmapImpl, IBitmapImpl> snapshots = new(ReferenceEqualityComparer.Instance);
        private IBitmapImpl Freeze(IBitmapImpl source)
        {
            if (source is DrawingLayer layer)
                source = layer.Source;
            if (!freezeBitmaps || source is not IWriteableBitmapImpl writable)
                return source;
            if (snapshots.TryGetValue(source, out var saved))
                return saved;
            // 只快照本次实际绘制的可写位图；录制结束后 Skia 图片独立持有像素。
            using var pixels = writable.Lock();
            var snapshot = factory.LoadBitmap(pixels.Format, writable.AlphaFormat ?? AlphaFormat.Premul,
                pixels.Address, pixels.Size, pixels.Dpi, pixels.RowBytes);
            snapshots.Add(source, snapshot);
            return snapshot;
        }
        public Matrix Transform
        {
            get => inner.Transform; set => inner.Transform = value;
        }
        public void Clear(Color color)
        {
            // Avalonia 先裁剪脏区，再清空并绘制根节点；只有这种独立替换才能安全合并。
            if (!painted)
            {
                Replacement = clipDepth == 0 ? [new Rect(recordingSize.ToSize(1))] : clipDepth == 1 ? rootClip : null;
                replacementDepth = clipDepth;
            }
            Drawing();
            inner.Clear(color);
        }
        public void DrawBitmap(IBitmapImpl source, double opacity, Rect sourceRect, Rect destRect)
        {
            Drawing();
            inner.DrawBitmap(Freeze(source), opacity, sourceRect, destRect);
        }
        public void DrawBitmap(IBitmapImpl source, IBrush opacityMask, Rect opacityMaskRect, Rect destRect)
        {
            Drawing();
            inner.DrawBitmap(Freeze(source), opacityMask, opacityMaskRect, destRect);
        }
        public void DrawLine(IPen? pen, Point p1, Point p2)
        {
            Drawing();
            inner.DrawLine(pen, p1, p2);
        }
        public void DrawGeometry(IBrush? brush, IPen? pen, IGeometryImpl geometry)
        {
            Drawing();
            inner.DrawGeometry(brush, pen, geometry);
        }
        public void DrawRectangle(IBrush? brush, IPen? pen, RoundedRect rect, BoxShadows boxShadows = default)
        {
            Drawing();
            inner.DrawRectangle(brush, pen, rect, boxShadows);
        }
        public void DrawRegion(IBrush? brush, IPen? pen, IPlatformRenderInterfaceRegion region)
        {
            Drawing();
            inner.DrawRegion(brush, pen, region);
        }
        public void DrawEllipse(IBrush? brush, IPen? pen, Rect rect)
        {
            Drawing();
            inner.DrawEllipse(brush, pen, rect);
        }
        public void DrawGlyphRun(IBrush? foreground, IGlyphRunImpl glyphRun)
        {
            Drawing();
            inner.DrawGlyphRun(foreground, glyphRun);
        }
        public IDrawingContextLayerImpl CreateLayer(PixelSize size) => new DrawingLayer(inner.CreateLayer(size));
        public void PushClip(Rect clip)
        {
            // 脏区是像素对齐的矩形；分数坐标和圆角边缘不能当作完整像素覆盖。
            var aligned = clipDepth == 0 && !painted && clip.Left == Math.Floor(clip.Left) && clip.Top == Math.Floor(clip.Top) &&
                clip.Right == Math.Floor(clip.Right) && clip.Bottom == Math.Floor(clip.Bottom);
            RootClip(aligned ? [clip] : null);
            inner.PushClip(clip);
        }
        public void PushClip(RoundedRect clip)
        {
            RootClip(null);
            inner.PushClip(clip);
        }
        public void PushClip(IPlatformRenderInterfaceRegion region)
        {
            // 跟踪器会在绘制完成后复用并清空区域，必须复制具体矩形。
            RootClip(clipDepth == 0 && !painted ? region.Rects.Select(rect => new Rect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)).ToArray() : null);
            inner.PushClip(region);
        }
        public void PopClip()
        {
            if (--clipDepth < replacementDepth)
                replacementScopeEnded = true;
            inner.PopClip();
        }
        public void PushLayer(Rect bounds)
        {
            Drawing();
            inner.PushLayer(bounds);
        }
        public void PopLayer()
        {
            Drawing();
            inner.PopLayer();
        }
        public void PushOpacity(double opacity, Rect? bounds)
        {
            Drawing();
            inner.PushOpacity(opacity, bounds);
        }
        public void PopOpacity()
        {
            Drawing();
            inner.PopOpacity();
        }
        public void PushOpacityMask(IBrush mask, Rect bounds)
        {
            Drawing();
            inner.PushOpacityMask(mask, bounds);
        }
        public void PopOpacityMask()
        {
            Drawing();
            inner.PopOpacityMask();
        }
        public void PushGeometryClip(IGeometryImpl clip)
        {
            RootClip(null);
            inner.PushGeometryClip(clip);
        }
        public void PopGeometryClip()
        {
            if (--clipDepth < replacementDepth)
                replacementScopeEnded = true;
            inner.PopGeometryClip();
        }
        public void PushRenderOptions(RenderOptions renderOptions) => inner.PushRenderOptions(renderOptions);
        public void PopRenderOptions() => inner.PopRenderOptions();
        public object? GetFeature(Type t)
        {
            var feature = inner.GetFeature(t);
            // 原生绘图租约可能直接写入画布，也要纳入外层裁剪保护。
            if (feature is not null)
                Drawing();
            return feature;
        }
        public void DrawRectangle(IExperimentalAcrylicMaterial material, RoundedRect rect)
        {
            Drawing();
            ((IDrawingContextWithAcrylicLikeSupport)inner).DrawRectangle(material, rect);
        }
        public void PushEffect(Rect? clipRect, IEffect effect)
        {
            Drawing();
            ((IDrawingContextImplWithEffects)inner).PushEffect(clipRect, effect);
        }
        public void PopEffect()
        {
            Drawing();
            ((IDrawingContextImplWithEffects)inner).PopEffect();
        }
        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            try
            {
                inner.Dispose();
            }
            finally
            {
                try
                {
                    finish();
                }
                finally
                {
                    foreach (var snapshot in snapshots.Values)
                        snapshot.Dispose();
                }
            }
        }
    }
}
