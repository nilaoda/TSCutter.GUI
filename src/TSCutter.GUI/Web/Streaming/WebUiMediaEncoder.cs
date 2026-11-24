using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Avalonia;
using FFmpeg.AutoGen.Abstractions;
using SkiaSharp;
using TSCutter.GUI.FFmpeg;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Web;

internal sealed unsafe class WebUiMediaEncoder : IDisposable
{
    private static readonly Histogram<double> ConversionDuration = WebUiSession.FrameMeter.CreateHistogram<double>("webui.convert.duration", "ms");
    private static readonly Histogram<double> EncodeDuration = WebUiSession.FrameMeter.CreateHistogram<double>("webui.encode_mux.duration", "ms");
    private readonly Frame frame = new();
    private readonly Packet packet = new();
    private AVCodecContext* context;
    private SwsContext* scaler;
    private SwsContext* parallelScaler;
    private readonly Frame sourceFrame = new();
    private bool parallelScalerUnavailable;
    private PixelSize videoSize;
    private int videoBitrate;
    private bool lowerRateLimits;
    private WebUiVideoStream? videoStream;
    private long pts;
    private int videoGeneration;
    private static readonly PixelSize FullHdSize = new(1920, 1080);
    internal static readonly PixelSize VideoLimit = new(3840, 2160);

    private static string EncoderName(WebUiVideoStream stream) => stream.IsH264 ? "libx264" : "libaom-av1";

    internal static bool IsSupported(WebUiVideoStream stream) => FF.avcodec_find_encoder_by_name(EncoderName(stream)) is not null;

    internal static bool IsMediaError(Exception error) => error is DllNotFoundException or EntryPointNotFoundException
        or BadImageFormatException or InvalidOperationException or FFmpegException;

    private void OpenVideo(PixelSize size, int bitrate, WebUiVideoStream stream)
    {
        ReleaseContext();
        var codec = FF.avcodec_find_encoder_by_name(EncoderName(stream));
        if (codec is null)
            throw new InvalidOperationException($"{stream.Codec} encoder unavailable.");
        context = FF.avcodec_alloc_context3(codec);
        if (context is null)
            throw new OutOfMemoryException();
        context->width = size.Width;
        context->height = size.Height;
        context->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
        context->time_base = new AVRational { num = 1, den = stream.FrameRate };
        context->framerate = new AVRational { num = stream.FrameRate, den = 1 };
        context->gop_size = stream.FrameRate;
        context->max_b_frames = 0;
        context->bit_rate = bitrate;
        context->rc_max_rate = bitrate * 7 / 6;
        context->rc_buffer_size = bitrate / 4;
        var above1080p = size.Width * (long)size.Height > FullHdSize.Width * FullHdSize.Height;
        context->thread_count = Math.Min(above1080p ? 12 : 8, Environment.ProcessorCount);
        if (stream.IsH264)
        {
            // 限制过度量化导致的大片灰色宏块；码率不足交由发送预算和降帧处理。
            context->qmax = 40;
            context->flags |= FF.AV_CODEC_FLAG_GLOBAL_HEADER;
            SetVideoOption("preset", "ultrafast");
            SetVideoOption("tune", "zerolatency");
            SetVideoOption("profile", "baseline");
            SetVideoOption("level", "5.2");
        }
        else
        {
            SetVideoOption("usage", "realtime");
            SetVideoOption("cpu-used", "8");
            SetVideoOption("lag-in-frames", "0");
            SetVideoOption("row-mt", "1");
            if (size.Width >= 960)
                SetVideoOption("tiles", above1080p ? "2x2" : "2x1");
            SetVideoOption("aom-params", "tune-content=screen");
        }
        FFmpegException.Check(FF.avcodec_open2(context, codec, null));
        frame.Unref();
        AVFrame* raw = frame;
        raw->format = (int)context->pix_fmt;
        raw->width = size.Width;
        raw->height = size.Height;
        FFmpegException.Check(FF.av_frame_get_buffer(raw, 32));
        videoSize = size;
        videoBitrate = bitrate;
        lowerRateLimits = false;
        videoStream = stream;
        pts = 0;
        ++videoGeneration;
    }

    private void SetVideoOption(string name, string value) =>
        FFmpegException.Check(FF.av_opt_set(context->priv_data, name, value, 0));

    private void UpdateH264Bitrate(int bitrate)
    {
        if (lowerRateLimits)
        {
            context->rc_max_rate = videoBitrate * 7 / 6;
            context->rc_buffer_size = videoBitrate / 4;
            lowerRateLimits = false;
        }
        if (bitrate == videoBitrate)
            return;
        // FFmpeg 先重配 VBV 再重配平均码率。降码率时分两帧更新，避免旧平均码率超过新峰值。
        if (bitrate < videoBitrate)
            lowerRateLimits = true;
        else
        {
            context->rc_max_rate = bitrate * 7 / 6;
            context->rc_buffer_size = bitrate / 4;
        }
        context->bit_rate = bitrate;
        videoBitrate = bitrate;
    }

    internal void Video(SKImage image, int bitrate, WebUiVideoStream stream, WebUiVideoSocket socket)
    {
        using var pixels = image.PeekPixels();
        // 竖屏使用旋转后的同等像素上限，避免编码时再次按横屏高度压低清晰度。
        var limit = pixels.Height > pixels.Width ? new PixelSize(VideoLimit.Height, VideoLimit.Width) : VideoLimit;
        var scale = Math.Min(1, Math.Min((double)limit.Width / pixels.Width, (double)limit.Height / pixels.Height));
        var size = new PixelSize(Math.Max(2, (int)(pixels.Width * scale) & ~1),
            Math.Max(2, (int)(pixels.Height * scale) & ~1));
        if (size != videoSize || videoStream != stream || !stream.IsH264 && bitrate != videoBitrate)
            OpenVideo(size, bitrate, stream);
        else if (stream.IsH264)
            UpdateH264Bitrate(bitrate);

        AVFrame* raw = frame;
        FFmpegException.Check(FF.av_frame_make_writable(raw));
        var conversionStart = ConversionDuration.Enabled ? Stopwatch.GetTimestamp() : 0;
        ConvertPixels(pixels, raw);
        if (conversionStart != 0)
            ConversionDuration.Record(Stopwatch.GetElapsedTime(conversionStart).TotalMilliseconds);
        var encodeStart = EncodeDuration.Enabled ? Stopwatch.GetTimestamp() : 0;
        raw->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
        raw->pts = pts++;
        FFmpegException.Check(FF.avcodec_send_frame(context, raw));
        AVPacket* encoded = packet;
        while (true)
        {
            var result = FF.avcodec_receive_packet(context, encoded);
            if (result == FF.AVERROR_EAGAIN || result == FF.AVERROR_EOF)
                break;
            FFmpegException.Check(result);
            try
            {
                socket.Package(context, encoded, videoGeneration, stream);
            }
            finally { FF.av_packet_unref(encoded); }
        }
        if (encodeStart != 0)
            EncodeDuration.Record(Stopwatch.GetElapsedTime(encodeStart).TotalMilliseconds);
    }

    private void ConvertPixels(SKPixmap pixels, AVFrame* destination)
    {
        AVFrame* source = sourceFrame;
        source->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
        source->width = pixels.Width;
        source->height = pixels.Height;
        source->linesize[0] = pixels.RowBytes;
        // 像素由当前 Skia 快照持有，仅在本次同步转换期间借用。
        source->data[0] = (byte*)pixels.GetPixels();
        try
        {
            if (pixels.Width * (long)pixels.Height > 1920 * 1080 && !parallelScalerUnavailable)
            {
                if (parallelScaler is null)
                {
                    parallelScaler = FF.sws_alloc_context();
                    var threads = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
                    parallelScalerUnavailable = parallelScaler is null ||
                        FF.av_opt_set_int(parallelScaler, "threads", threads, 0) < 0 ||
                        FF.av_opt_set_int(parallelScaler, "sws_flags", 2, 0) < 0;
                }
                if (!parallelScalerUnavailable && FF.sws_scale_frame(parallelScaler, destination, source) >= 0)
                    return;
                parallelScalerUnavailable = true;
            }

            scaler = FF.sws_getCachedContext(scaler, pixels.Width, pixels.Height, AVPixelFormat.AV_PIX_FMT_BGRA,
                destination->width, destination->height, (AVPixelFormat)destination->format, 2, null, null, null);
            if (scaler is null)
                throw new InvalidOperationException("Could not create video pixel converter.");
            FFmpegException.Check(FF.sws_scale(scaler, (byte**)&source->data, (int*)&source->linesize, 0,
                pixels.Height, (byte**)&destination->data, (int*)&destination->linesize));
        }
        finally { source->data[0] = null; }
    }

    private void ReleaseContext()
    {
        var owned = context;
        if (owned is not null)
            FF.avcodec_free_context(&owned);
        context = null;
    }

    public void Dispose()
    {
        ReleaseContext();
        if (scaler is not null)
            FF.sws_freeContext(scaler);
        FF.sws_freeContext(parallelScaler);
        sourceFrame.Dispose();
        frame.Dispose();
        packet.Dispose();
    }
}
