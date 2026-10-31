using System;
using System.Collections.Generic;
using System.Globalization;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Player.Video;

/// <summary>连续播放按场去隔行；滤镜保留前后参考帧，跳转时整体重建。</summary>
internal sealed unsafe class VideoDeinterlacer : IDisposable
{
    private AVFilterGraph* graph;
    private AVFilterContext* source;
    private AVFilterContext* sink;
    private readonly AVRational timeBase;
    private readonly int width, height, format;
    private readonly Queue<long> pendingSourcePts = new();
    private long currentSourcePts;
    private FF.FilterTimeBase outputTimeBase;
    private FramePacketPositionSource positionSource;
    private Frame? inputFrame;

    public VideoDeinterlacer(Frame frame, AVRational timeBase)
    {
        this.timeBase = timeBase;
        (width, height, format) = (frame.Width, frame.Height, frame.Format);
        graph = FF.avfilter_graph_alloc();
        if (graph == null)
            throw new OutOfMemoryException();
        try
        {
            var aspect = frame.SampleAspectRatio;
            if (aspect.num <= 0 || aspect.den <= 0)
                aspect = new AVRational { num = 1, den = 1 };
            var args = string.Create(CultureInfo.InvariantCulture,
                $"video_size={width}x{height}:pix_fmt={format}:time_base={timeBase.num}/{timeBase.den}:pixel_aspect={aspect.num}/{aspect.den}");
            source = CreateFilter("buffer", args);
            var filter = CreateFilter("bwdif", "mode=send_field:parity=auto:deint=interlaced");
            sink = CreateFilter("buffersink", null);
            FFmpegException.Check(FF.avfilter_link(source, 0, filter, 0));
            FFmpegException.Check(FF.avfilter_link(filter, 0, sink, 0));
            FFmpegException.Check(FF.avfilter_graph_config(graph, null));
            outputTimeBase = FF.av_buffersink_get_time_base(sink);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private AVFilterContext* CreateFilter(string name, string? args)
    {
        var filter = FF.avfilter_get_by_name(name);
        if (filter == null)
            throw new InvalidOperationException($"FFmpeg filter unavailable: {name}");
        AVFilterContext* context = null;
        FFmpegException.Check(FF.avfilter_graph_create_filter(&context, filter, name, args, null, graph));
        return context;
    }

    public bool Matches(Frame frame) => frame.Width == width && frame.Height == height && frame.Format == format;

    public void Push(Frame frame)
    {
        var pts = frame.BestEffortTimestamp != FF.AV_NOPTS_VALUE ? frame.BestEffortTimestamp : frame.Pts;
        // 使用解码后的显示时间，不修改解码器交回的共享帧。
        var input = inputFrame ??= new Frame();
        try
        {
            FFmpegException.Check(FF.av_frame_ref(input, frame));
            AVFrame* raw = input;
            raw->pts = pts;
            FFmpegException.Check(FF.av_buffersrc_add_frame_flags(source, input, FF.AV_BUFFERSRC_FLAG_KEEP_REF));
        }
        finally
        {
            input.Unref();
        }
        pendingSourcePts.Enqueue(pts);
        positionSource = frame.PacketPositionSource;
    }

    public void Complete() => FFmpegException.Check(FF.av_buffersrc_add_frame_flags(source, null, 0));

    public bool TryReceive(Frame destination, out long presentationPts, out long sourcePts)
    {
        destination.Unref();
        var result = FF.av_buffersink_get_frame(sink, destination);
        presentationPts = sourcePts = 0;
        if (result == FF.AVERROR_EAGAIN || result == FF.AVERROR_EOF)
            return false;
        FFmpegException.Check(result);
        var numerator = (Int128)destination.Pts * outputTimeBase.Numerator * timeBase.den;
        var denominator = (Int128)outputTimeBase.Denominator * timeBase.num;
        presentationPts = checked((long)((numerator + (numerator >= 0 ? denominator / 2 : -denominator / 2)) / denominator));
        destination.PacketPositionSource = positionSource;
        while (pendingSourcePts.TryPeek(out var next) && next <= presentationPts)
            currentSourcePts = pendingSourcePts.Dequeue();
        sourcePts = currentSourcePts;
        // 两场使用各自的呈现时间，但标记/剪辑仍引用同一幅原始帧。
        return true;
    }

    public void Dispose()
    {
        var owned = graph;
        graph = null;
        source = sink = null;
        FF.avfilter_graph_free(&owned);
        inputFrame?.Dispose();
        inputFrame = null;
    }
}
