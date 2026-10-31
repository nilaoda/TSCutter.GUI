using System;
using System.Threading;
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Extensions;
using FFmpeg.AutoGen.Abstractions;

namespace TSCutter.GUI.Rendering.Gpu;

/// <summary>不可变的 YUV 着色器帧；界面租约和在途绘制分别持有引用。</summary>
internal sealed class SkiaVideoFrame : IGpuFrameLease
{
    private readonly SkiaVideoFramePresenter.ShaderPair? softwareShaders;
    private readonly SkiaVideoFramePresenter.ShaderParameters? hardwareParameters;
    private readonly Action validated;
    private bool gpuValidated, gpuFailed;
    private readonly Frame source;
    private readonly bool toneMapping;
    private readonly PixelSize fallbackSize;
    private readonly Action<bool, bool> rendered;
    private readonly Action dropped;
    private readonly Action? submitted;
    private readonly bool renderAtRequestedSize;
    private readonly Action<string> failure;
    private HostFramePresenter? fallbackPresenter;
    private IBitmapFrameLease? fallback;
    private int references = 1;
    private int ownerReleased;
    private int wasRendered;
    private int wasSubmitted;

    internal SkiaVideoFrame(SkiaVideoFramePresenter.ShaderPair? softwareShaders,
        SkiaVideoFramePresenter.ShaderParameters? hardwareParameters, Frame source, bool toneMapping, PixelSize fallbackSize,
        bool renderAtRequestedSize, Action<bool, bool> rendered, Action dropped, Action<string> failure, Action validated,
        Action? submitted = null)
    {
        this.softwareShaders = softwareShaders;
        this.hardwareParameters = hardwareParameters;
        this.validated = validated;
        this.source = source.CloneReference();
        this.toneMapping = toneMapping;
        this.fallbackSize = fallbackSize;
        this.rendered = rendered;
        this.renderAtRequestedSize = renderAtRequestedSize;
        this.dropped = dropped;
        this.submitted = submitted;
        this.failure = failure;
        PixelSize = new(source.Width, source.Height);
    }

    public PixelSize PixelSize { get; }
    public string Backend => hardwareParameters is null ? "Skia GPU" : "VideoToolbox / IOSurface";
    public bool TryAttach(Avalonia.Visual host) => Volatile.Read(ref ownerReleased) == 0;
    public void UpdateViewport(Rect destination) { }

    internal void Draw(DrawingContext context, Rect destination, Rect clip)
    {
        var count = Volatile.Read(ref references);
        while (count > 0 && Volatile.Read(ref ownerReleased) == 0)
        {
            var observed = Interlocked.CompareExchange(ref references, count + 1, count);
            if (observed == count)
            {
                try { context.Custom(new DrawOperation(this, destination, clip)); }
                catch { Release(); throw; }
                // DrawOperation 已持有帧引用；UI 现在可以准备下一帧，实际绘制计数仍在 Render 中更新。
                if (Interlocked.Exchange(ref wasSubmitted, 1) == 0)
                    submitted?.Invoke();
                return;
            }
            count = observed;
        }
    }

    internal void RenderShader(SKCanvas canvas, SKRect destination, SKRect clip, float opacity = 1, SKShader? frameShader = null)
    {
        var saveCount = canvas.Save();
        try
        {
            canvas.ClipRect(clip);
            canvas.Translate(destination.Left, destination.Top);
            canvas.Scale(destination.Width / PixelSize.Width, destination.Height / PixelSize.Height);
            // Runtime shader 的输入颜色也会传给子着色器；透明度在合成层应用，避免改变 YUV/LUT 采样。
            using var paint = new SKPaint { Shader = frameShader ?? softwareShaders?.Shader };
            if (opacity < 1)
            {
                using var layerPaint = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * Math.Clamp(opacity, 0, 1))) };
                canvas.SaveLayer(layerPaint);
                canvas.DrawRect(0, 0, PixelSize.Width, PixelSize.Height, paint);
                canvas.Restore();
            }
            else
                canvas.DrawRect(0, 0, PixelSize.Width, PixelSize.Height, paint);
        }
        finally { canvas.RestoreToCount(saveCount); }
    }

    private void Render(ImmediateDrawingContext context, Rect destination, Rect clip)
    {
        var feature = gpuFailed ? null : context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
        if (feature is not null)
        {
            try
            {
                using var lease = feature.Lease();
                if (lease.GrContext is not null && !lease.GrContext.IsAbandoned)
                {
                    if (hardwareParameters is not null)
                        RenderHardware(lease, destination, clip);
                    else
                    {
                        RenderWithShaders(lease, softwareShaders!, destination, clip);
                        ReportRendered(true);
                    }
                    return;
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                failure(exception.Message);
            }
        }
        gpuFailed = true;
        failure("The current drawing surface has no compatible GPU context.");
        fallbackPresenter ??= new HostFramePresenter();
        if (fallback is null && !fallbackPresenter.TryConvert(source, fallbackSize.Width, fallbackSize.Height,
                out fallback, toneMapping))
            return;
        using (context.PushClip(clip))
            context.DrawBitmap(fallback!.Bitmap, destination);
        ReportRendered(false);
    }

    private void RenderWithShaders(ISkiaSharpApiLease lease, SkiaVideoFramePresenter.ShaderPair shaders,
        Rect destination, Rect clip)
    {
        ValidateGpu(lease.GrContext!, shaders.Validation);
        if (renderAtRequestedSize)
            RenderOffscreen(lease, shaders.Shader, destination, clip);
        else
            RenderShader(lease.SkCanvas, ToSkia(destination), ToSkia(clip), (float)lease.CurrentOpacity, shaders.Shader);
    }

    private unsafe void RenderHardware(ISkiaSharpApiLease lease, Rect destination, Rect clip)
    {
        try
        {
            using var textures = MacOSVideoTexture.Import(lease.GrContext!, source);
            using var shaders = hardwareParameters!.Create(textures.Y, textures.Uv, textures.Uv,
                !gpuValidated && hardwareParameters.NeedsValidation);
            try { RenderWithShaders(lease, shaders, destination, clip); }
            finally
            {
                // 先提交采样命令，再由纹理租约的 GPU 栅栏异步归还表面。
                lease.SkCanvas.Flush();
                lease.GrContext!.Flush();
                lease.GrContext.Submit(false);
            }
            ReportRendered(true, true);
            return;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            MacOSVideoTexture.Disable(exception.Message);
        }
        // 共享纹理不可用时，当前在途硬件帧也能立即走原来的 YUV 上传路径。
        using var software = source.TransferToSoftwareFrame();
        AVFrame* raw = software;
        var descriptor = NativeMethods.av_pix_fmt_desc_get((AVPixelFormat)raw->format);
        using var y = SkiaVideoFramePresenter.CreatePlane(software, descriptor, 0);
        using var uv = SkiaVideoFramePresenter.CreatePlane(software, descriptor, 1);
        using var fallbackShaders = hardwareParameters!.Create(y, uv, uv, !gpuValidated && hardwareParameters.NeedsValidation);
        RenderWithShaders(lease, fallbackShaders, destination, clip);
        ReportRendered(true);
    }

    private void RenderOffscreen(ISkiaSharpApiLease lease, SKShader shader, Rect destination, Rect clip)
    {
        // 自检预览很小，但必须按用户选择的分辨率完成 GPU 运算后再缩小显示。
        var size = fallbackSize.Width > 0 && fallbackSize.Height > 0
            ? TSCutter.GUI.Utils.Media.ImageUtil.CalculateDecodeSize(PixelSize, fallbackSize) : PixelSize;
        using var surface = SKSurface.Create(lease.GrContext, true,
            new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Opaque))
            ?? throw new NotSupportedException("Cannot create the GPU benchmark surface.");
        var bounds = SKRect.Create(size.Width, size.Height);
        RenderShader(surface.Canvas, bounds, bounds, frameShader: shader);
        using var image = surface.Snapshot();
        lease.SkCanvas.Save();
        try
        {
            lease.SkCanvas.ClipRect(ToSkia(clip));
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * lease.CurrentOpacity)) };
            lease.SkCanvas.DrawImage(image, ToSkia(destination), paint);
        }
        finally { lease.SkCanvas.Restore(); }
    }

    private void ValidateGpu(GRContext context, SKShader? validationShader)
    {
        if (gpuValidated || validationShader is null)
            return;
        // 一次小图回读确认驱动实际接受完整程序，避免编译失败时黑屏并反复尝试。
        var info = new SKImageInfo(1, 1, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var surface = SKSurface.Create(context, false, info)
            ?? throw new NotSupportedException("Cannot create a GPU validation surface.");
        surface.Canvas.Clear(SKColors.Magenta);
        using var paint = new SKPaint { Shader = validationShader };
        surface.Canvas.DrawRect(0, 0, 1, 1, paint);
        using var pixel = new SKBitmap(info);
        if (!surface.ReadPixels(info, pixel.GetPixels(), pixel.RowBytes, 0, 0))
            throw new NotSupportedException("Cannot read the GPU validation result.");
        var color = pixel.GetPixel(0, 0);
        if (Math.Abs(color.Red - 51) > 2 || Math.Abs(color.Green - 204) > 2 || Math.Abs(color.Blue - 102) > 2)
            throw new NotSupportedException("The GPU driver rejected the video shader.");
        gpuValidated = true;
        validated();
    }

    private void ReportRendered(bool gpu, bool hardwareSurface = false)
    {
        if (Interlocked.Exchange(ref wasRendered, 1) == 0)
            rendered(gpu, hardwareSurface);
    }

    private static SKRect ToSkia(Rect rect) => new((float)rect.Left, (float)rect.Top, (float)rect.Right, (float)rect.Bottom);

    private void Release()
    {
        if (Interlocked.Decrement(ref references) != 0)
            return;
        if (Volatile.Read(ref wasRendered) == 0)
            dropped();
        fallback?.Dispose();
        fallbackPresenter?.Dispose();
        softwareShaders?.Dispose();
        hardwareParameters?.Dispose();
        source.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref ownerReleased, 1) == 0)
            Release();
    }

    private sealed class DrawOperation(SkiaVideoFrame frame, Rect destination, Rect clip) : ICustomDrawOperation
    {
        private SkiaVideoFrame? owner = frame;
        public Rect Bounds => clip;
        public bool HitTest(Point point) => false;
        public bool Equals(ICustomDrawOperation? other) => other is DrawOperation operation
            && ReferenceEquals(owner, operation.owner) && destination == operation.destination && clip == operation.clip;
        private readonly Rect destination = destination;
        private readonly Rect clip = clip;
        public void Render(ImmediateDrawingContext context) => owner?.Render(context, destination, clip);
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release();
    }
}
