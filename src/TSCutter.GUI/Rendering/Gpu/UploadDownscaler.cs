using System;
using Avalonia;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Utils.Media;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Rendering.Gpu;

/// <summary>
/// 超过 4K 的软件帧先在呈现线程上缩到 4K 以内再交给 GPU。8K 10 位一帧约 100 MB，上传在渲染线程上要 17–20 ms，
/// 足以拖慢整个界面；缩到 4K 后上传量只有四分之一，放大查看时仍保留足够细节。
/// 缩放在出帧的关键路径上，单线程 8K→4K 约 11 ms，会让每帧错过一次刷新，所以按多线程缩放；
/// 多线程上下文不可用时退回单线程。只缩放尺寸，保持原像素格式和色彩信息，转色与映射仍在着色器中完成。
/// 缩放失败时返回 null，调用方照原样上传。
/// </summary>
internal sealed unsafe class UploadDownscaler : IDisposable
{
    internal const int MaxWidth = 3840, MaxHeight = 2160;
    private const int Bilinear = 2;
    private SwsContext* threaded;
    private (int Width, int Height, int Format, int OutWidth, int OutHeight) threadedKey;
    private bool threadedFailed;
    private SwsContext* single;
    private bool failed;

    /// <summary>缩放线程数：与解码并行，留出一半核心给解码器。</summary>
    internal static int ThreadCount => Math.Clamp(Environment.ProcessorCount / 4, 1, 4);

    internal static bool Applies(Frame frame) =>
        !frame.HasHardwareFramesContext && (frame.Width > MaxWidth || frame.Height > MaxHeight);

    internal Frame? TryScale(Frame source)
    {
        if (failed || !Applies(source))
            return null;
        var size = ImageUtil.CalculateDecodeSize(new PixelSize(source.Width, source.Height), new PixelSize(MaxWidth, MaxHeight));
        // 色度二次采样的格式要求偶数尺寸。
        var width = Math.Max(2, size.Width & ~1);
        var height = Math.Max(2, size.Height & ~1);
        Frame? scaled = null;
        try
        {
            scaled = Frame.CreateVideo(width, height, (AVPixelFormat)source.Format);
            // 保留色彩描述、采样宽高比和 HDR 边数据，着色器按原帧的元数据映射。
            FFmpegException.Check(FF.av_frame_copy_props(scaled, source));
            AVFrame* output = scaled;
            // 裁剪值按原尺寸计算，缩放后的画面已是完整显示区域。
            output->crop_left = output->crop_right = output->crop_top = output->crop_bottom = 0;
            if (!TryScaleThreaded(source, scaled))
                ScaleSingleThreaded(source, scaled);
            return scaled;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            scaled?.Dispose();
            failed = true;
            Console.WriteLine("Large frame downscale unavailable; uploading full size: " + exception.Message);
            return null;
        }
    }

    private bool TryScaleThreaded(Frame source, Frame destination)
    {
        if (threadedFailed)
            return false;
        AVFrame* input = source;
        AVFrame* output = destination;
        var key = (input->width, input->height, input->format, output->width, output->height);
        try
        {
            if (threaded == null || threadedKey != key)
            {
                FreeThreaded();
                threaded = FF.sws_alloc_context();
                if (threaded == null)
                    throw new NotSupportedException("swscale cannot allocate a context.");
                Set("srcw", input->width);
                Set("srch", input->height);
                Set("src_format", input->format);
                Set("dstw", output->width);
                Set("dsth", output->height);
                Set("dst_format", output->format);
                Set("sws_flags", Bilinear);
                Set("threads", ThreadCount);
                FFmpegException.Check(FF.sws_init_context(threaded, null, null));
                threadedKey = key;
            }
            FFmpegException.Check(FF.sws_scale_frame(threaded, output, input));
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            FreeThreaded();
            threadedFailed = true;
            Console.WriteLine("Threaded downscale unavailable; using one thread: " + exception.Message);
            return false;
        }

        void Set(string name, long value) => FFmpegException.Check(FF.av_opt_set_int(threaded, name, value, 0));
    }

    private void ScaleSingleThreaded(Frame source, Frame destination)
    {
        AVFrame* input = source;
        AVFrame* output = destination;
        single = FF.sws_getCachedContext(single, input->width, input->height, (AVPixelFormat)input->format,
            output->width, output->height, (AVPixelFormat)output->format, Bilinear, null, null, null);
        if (single == null)
            throw new NotSupportedException($"swscale cannot resize {(AVPixelFormat)input->format}.");
        var lines = FF.sws_scale(single, (byte**)&input->data, (int*)&input->linesize, 0, input->height,
            (byte**)&output->data, (int*)&output->linesize);
        if (lines <= 0)
            throw new InvalidOperationException("swscale produced no output.");
    }

    private void FreeThreaded()
    {
        FF.sws_freeContext(threaded);
        threaded = null;
    }

    public void Dispose()
    {
        FreeThreaded();
        FF.sws_freeContext(single);
        single = null;
    }
}
