using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using TSCutter.GUI.Extensions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Rendering.Gpu;
using TSCutter.GUI.Utils.Media;

namespace TSCutter.GUI.Player.Diagnostics;

/// <summary>借用预览所在的 GPU 上下文，分批执行离屏测速，不按显示刷新率计时。</summary>
public sealed class PlaybackGpuBenchmarkControl : Control
{
    private BatchRequest? pending;

    internal async Task<PlaybackBenchmarkResult?> MeasureAsync(GpuBenchmarkRequest request,
        IProgress<PlaybackBenchmarkProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!SkiaVideoFramePresenter.IsRendererAvailable)
            return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var count = 0;
        var seconds = 0.0;
        var batchSize = 4;
        var lastReport = 0.0;
        PixelSize size = default;
        var transfer = GpuBenchmarkTransfer.HardwareSurface;
        try
        {
            while (seconds < request.Seconds)
            {
                timeout.Token.ThrowIfCancellationRequested();
                // 每批单独持有帧引用，取消后即使绘制尚未结束，也不依赖服务的样本生命周期。
                using var batch = new BatchRequest(request, batchSize, timeout.Token);
                try
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (!IsEffectivelyVisible || Bounds.Width <= 0 || Bounds.Height <= 0)
                        {
                            batch.CancelBeforeRender();
                            return;
                        }
                        pending = batch;
                        InvalidateVisual();
                    });
                    var result = await batch.Completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
                    if (result is null)
                        return null;
                    count += result.Count;
                    seconds += result.Seconds;
                    size = result.Size;
                    transfer = (GpuBenchmarkTransfer)Math.Max((int)transfer, (int)result.Transfer);
                    // 控制每次占用渲染线程的时间，批次间允许窗口重绘和处理取消操作。
                    batchSize = Math.Clamp((int)(0.012 * result.Count / result.Seconds), 1, 64);
                    if (seconds - lastReport >= 0.25 || seconds >= request.Seconds)
                    {
                        progress?.Report(new(PlaybackBenchmarkStage.GpuProcessing, Math.Min(1, seconds / request.Seconds),
                            FramesPerSecond: count / seconds));
                        lastReport = seconds;
                    }
                }
                finally
                {
                    batch.CancelBeforeRender();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (ReferenceEquals(pending, batch))
                        {
                            pending = null;
                            InvalidateVisual();
                        }
                    });
                }
            }
            return new(PlaybackBenchmarkStage.GpuProcessing, count / seconds, request.FrameRate, count, seconds,
                request.HardwareDecoding, OutputSize: size, GpuPresentation: true,
                HardwareSurfacePresentation: transfer == GpuBenchmarkTransfer.HardwareSurface, GpuTransfer: transfer);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            Console.WriteLine("GPU benchmark unavailable: " + exception.Message);
            return null;
        }
    }

    public override void Render(DrawingContext context)
    {
        if (pending is { } batch)
            context.Custom(new BatchOperation(batch, new Rect(Bounds.Size)));
    }

    private sealed record BatchResult(int Count, double Seconds, PixelSize Size, GpuBenchmarkTransfer Transfer);

    private sealed class BatchRequest : IDisposable
    {
        private readonly GpuBenchmarkRequest request;
        private readonly Frame[] samples;
        private readonly int count;
        private readonly CancellationToken token;
        private int state;
        internal TaskCompletionSource<BatchResult?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal BatchRequest(GpuBenchmarkRequest request, int count, CancellationToken token)
        {
            this.request = request;
            this.count = count;
            this.token = token;
            samples = new Frame[request.Frames.Count];
            try
            {
                for (var index = 0; index < samples.Length; index++)
                    samples[index] = request.Frames[index].CloneReference();
            }
            catch
            {
                foreach (var sample in samples)
                    sample?.Dispose();
                throw;
            }
        }

        internal void Run(ImmediateDrawingContext drawing)
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0)
                return;
            try
            {
                token.ThrowIfCancellationRequested();
                var feature = drawing.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) as ISkiaSharpApiLeaseFeature;
                using var lease = feature?.Lease();
                if (lease?.GrContext is not { IsAbandoned: false } context)
                {
                    Completion.TrySetResult(null);
                    return;
                }
                MacOSVideoTexture.Probe(context);
                var size = ImageUtil.CalculateDecodeSize(new(samples[0].Width, samples[0].Height), request.OutputSize);
                using var surface = SKSurface.Create(context, true,
                    new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Opaque))
                    ?? throw new NotSupportedException("Cannot create the GPU benchmark surface.");
                using var presenter = new SkiaVideoFramePresenter();
                var transfer = GpuBenchmarkTransfer.HardwareSurface;
                // 预热表格、着色器和目标纹理；先排空旧命令，避免把先前的界面绘制算入本批。
                Draw(samples[0]);
                context.Flush();
                context.Submit(true);
                transfer = GpuBenchmarkTransfer.HardwareSurface;
                var started = Stopwatch.GetTimestamp();
                try
                {
                    for (var index = 0; index < count; index++)
                    {
                        token.ThrowIfCancellationRequested();
                        Draw(samples[index % samples.Length]);
                    }
                }
                finally
                {
                    // 同步等待实际 GPU 工作完成，不能只测 CPU 的提交速度。
                    surface.Flush();
                    context.Flush();
                    context.Submit(true);
                }
                var elapsed = Math.Max(Stopwatch.GetElapsedTime(started).TotalSeconds, 0.000001);
                Completion.TrySetResult(new(count, elapsed, size, transfer));

                void Draw(Frame source)
                {
                    using var software = source.HasHardwareFramesContext &&
                        (request.ForceSoftwareFrames || !MacOSVideoTexture.CanImport(source))
                            ? source.TransferToSoftwareFrame() : null;
                    var input = software ?? source;
                    if (!presenter.TryCreateFrame(input, request.ToneMapping, size, false, static (_, _) => { },
                            static () => { }, out var frame))
                        throw new NotSupportedException("The GPU cannot process this pixel format.");
                    using (frame)
                    {
                        try { frame!.RenderBenchmark(context, surface); }
                        catch (Exception exception) when (input.HasHardwareFramesContext && exception is not OutOfMemoryException)
                        {
                            MacOSVideoTexture.Disable(exception.Message);
                            using var fallback = input.TransferToSoftwareFrame();
                            Draw(fallback);
                            transfer = GpuBenchmarkTransfer.ReadbackAndUpload;
                            return;
                        }
                    }
                    var mode = input.HasHardwareFramesContext ? GpuBenchmarkTransfer.HardwareSurface
                        : software is not null ? GpuBenchmarkTransfer.ReadbackAndUpload : GpuBenchmarkTransfer.Upload;
                    transfer = (GpuBenchmarkTransfer)Math.Max((int)transfer, (int)mode);
                }
            }
            catch (OperationCanceledException) { Completion.TrySetCanceled(token); }
            catch (Exception exception) { Completion.TrySetException(exception); }
            finally
            {
                foreach (var sample in samples)
                    sample.Dispose();
                Volatile.Write(ref state, 2);
            }
        }

        internal void CancelBeforeRender()
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) != 0)
            {
                // 已开始的绘制仍由渲染线程回收帧；提前结束等待，避免取消后的异常无人观察。
                if (token.IsCancellationRequested)
                    Completion.TrySetCanceled(token);
                return;
            }
            foreach (var sample in samples)
                sample.Dispose();
            Completion.TrySetResult(null);
        }

        public void Dispose() => CancelBeforeRender();
    }

    private sealed class BatchOperation(BatchRequest request, Rect bounds) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point point) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Render(ImmediateDrawingContext context) => request.Run(context);
        public void Dispose() => request.CancelBeforeRender();
    }
}
