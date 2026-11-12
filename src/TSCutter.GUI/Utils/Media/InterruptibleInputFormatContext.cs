using TSCutter.GUI.FFmpeg;
using System;
using FFmpeg.AutoGen.Abstractions;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;
using TSCutter.GUI.Models.Discs;

namespace TSCutter.GUI.Utils.Media;

internal sealed unsafe class InterruptibleInputFormatContext : FormatContext
{
    private readonly AVIOInterruptCB_callback interruptCallback;
    private readonly double? dvdExportSeekAnchor;

    private InterruptibleInputFormatContext(AVFormatContext* context, AVIOInterruptCB_callback callback,
        double? dvdExportSeekAnchor = null, Discs.DvdTimeline? dvdTimeline = null) : base(context, isOwner: true)
    {
        interruptCallback = callback;
        this.dvdExportSeekAnchor = dvdExportSeekAnchor;
        DvdTimeline = dvdTimeline;
    }

    /// <summary>DVD 标题的统一时间轴：各次定位后的时间戳按已读到的关键帧校正；其他来源为 null。</summary>
    public Discs.DvdTimeline? DvdTimeline { get; }

    /// <summary>
    /// DVD-Video 的视频只允许 MPEG-2 主档次主级别（MP@ML），dvdvideo 解复用器不填档次与级别；按规范补上，
    /// 智能剪辑的编码检查与重编码参数（码率与 VBV 上限）据此确定。
    /// </summary>
    protected override void CompleteStreamInfo()
    {
        if (DvdTimeline is null)
            return;
        AVFormatContext* context = this;
        for (var i = 0; i < context->nb_streams; i++)
        {
            var parameters = context->streams[i]->codecpar;
            if (parameters->codec_id != AVCodecID.AV_CODEC_ID_MPEG2VIDEO)
                continue;
            if (parameters->profile < 0)
                parameters->profile = 4;
            if (parameters->level < 0)
                parameters->level = 8;
        }
    }

    /// <summary>
    /// MPEG-2 帧内刷新码流的视频流索引，-1 表示不处理。这种码流没有 I 帧，解复用器一个关键包都不标；
    /// 设置后把带序列头的 P 帧（<see cref="KeyFrameTypeReader.RecoveryPoint"/>）补标为关键包，
    /// 定位、导航、播放和字节剪辑沿用关键帧流程，入口画面仍需预滚才完整。由检测到这种码流的调用方设置。
    /// </summary>
    public int Mpeg2RefreshPointStreamIndex { get; set; } = -1;

    public override int ReadPacket(Packet packet)
    {
        var result = DvdTimeline is { } timeline ? timeline.Read(this, packet) : base.ReadPacket(packet);
        if (result >= 0 && Mpeg2RefreshPointStreamIndex >= 0 && packet.StreamIndex == Mpeg2RefreshPointStreamIndex)
            MarkMpeg2RefreshPoint(packet);
        return result;
    }

    private static void MarkMpeg2RefreshPoint(Packet packet)
    {
        AVPacket* raw = packet;
        if ((raw->flags & AV_PKT_FLAG_KEY) != 0 || raw->data == null || raw->size <= 0)
            return;
        if (KeyFrameTypeReader.Read(new ReadOnlySpan<byte>(raw->data, raw->size), AVCodecID.AV_CODEC_ID_MPEG2VIDEO)
            == KeyFrameTypeReader.RecoveryPoint)
            raw->flags |= AV_PKT_FLAG_KEY;
    }

    private const int AV_PKT_FLAG_KEY = 0x0001;

    public override void SeekFrame(long timestamp, int streamIndex, AVSEEK_FLAG flags = AVSEEK_FLAG.Backward)
    {
        if (dvdExportSeekAnchor is { } anchor)
        {
            // dvdvideo 会以请求的 seek 时间重建 PTS。导出各阶段必须从同一位置读取，
            // 才能在准备、GOP 检查和复制时得到相同的关键帧与时间戳。
            AVFormatContext* context = this;
            var timeBase = streamIndex < 0 ? new AVRational { num = 1, den = FF.AV_TIME_BASE }
                : context->streams[streamIndex]->time_base;
            timestamp = (long)Math.Round(anchor * timeBase.den / timeBase.num);
        }
        else if (DvdTimeline is not null && (flags & AVSEEK_FLAG.Backward) != 0)
        {
            // DVD 只能定位到 VOBU（约半秒），统一时间轴与读取层的时间通常也相差一个 VOBU 以内：多退 1 秒，
            // 让落点一般不晚于目标，调用方照常从落点向后读到目标。个别光盘的时间定位偏差可达数十秒，
            // 预览按读到的画面显示；导出不依赖这里的定位精度（见智能剪辑导出的 DVD 锚点）。
            AVFormatContext* context = this;
            var timeBase = streamIndex < 0 ? new AVRational { num = 1, den = FF.AV_TIME_BASE }
                : context->streams[streamIndex]->time_base;
            timestamp = Math.Max(0, timestamp - timeBase.den / Math.Max(1, timeBase.num));
        }
        base.SeekFrame(timestamp, streamIndex, flags);
        DvdTimeline?.Reset();
    }

    public static FormatContext Open(string path, Func<bool> shouldInterrupt, long tsSyncOffset = -1)
    {
        AVFormatContext* context = FF.avformat_alloc_context();
        if (context == null)
            throw new OutOfMemoryException();

        AVDictionary* options = null;
        AVIOInterruptCB_callback callback = _ => shouldInterrupt() ? 1 : 0;
        try
        {
            // The protocol layer copies this callback when opening the file.
            // Setting only AVFormatContext.interrupt_callback after opening is too late.
            context->interrupt_callback = new AVIOInterruptCB { callback = callback };
            var result = FF.av_dict_set(&options, "scan_all_pmts", "1", 0);
            if (result < 0)
                throw FFmpegException.FromErrorCode(result, null);
            AVInputFormat* inputFormat = null;
            if (tsSyncOffset >= 0)
            {
                // 已确认 TS 布局后跳过损坏前缀；保留原文件的绝对包位置。
                inputFormat = FF.av_find_input_format("mpegts");
                if (inputFormat == null)
                    throw new NotSupportedException("The runtime does not support MPEG-TS input.");
                FFmpegException.Check(FF.av_opt_set_int(context, "skip_initial_bytes", tsSyncOffset, 0));
            }
            result = FF.avformat_open_input(&context, path, inputFormat, &options);
            if (result < 0)
                throw FFmpegException.FromErrorCode(result, null);

            var input = new InterruptibleInputFormatContext(context, callback);
            context = null;
            return input;
        }
        finally
        {
            FF.av_dict_free(&options);
            if (context != null)
                FF.avformat_close_input(&context);
            GC.KeepAlive(callback);
        }
    }

    // 光盘标题统一从镜像或目录按需读取：蓝光使用 libbluray 的播放列表与定位接口，
    // DVD 经 dvdvideo 解复用器按标题号读取；无需挂载或解压镜像。
    public static FormatContext Open(DiscInput disc, Func<bool> shouldInterrupt, double? dvdExportSeekAnchor = null)
    {
        if (disc.Kind == DiscKind.BluRay)
            return Discs.BlurayInputFormatContext.Open(disc, shouldInterrupt);

        AVFormatContext* context = FF.avformat_alloc_context();
        if (context == null)
            throw new OutOfMemoryException();

        AVDictionary* options = null;
        AVIOInterruptCB_callback callback = _ => shouldInterrupt() ? 1 : 0;
        try
        {
            // The protocol layer copies this callback when opening the file.
            // Setting only AVFormatContext.interrupt_callback after opening is too late.
            context->interrupt_callback = new AVIOInterruptCB { callback = callback };
            AVInputFormat* inputFormat = null;
            if (disc.InputFormatName is { } formatName)
            {
                inputFormat = FF.av_find_input_format(formatName);
                if (inputFormat == null)
                    throw new DiscOpenException(
                        "The FFmpeg runtime does not provide the dvdvideo demuxer.");
            }
            foreach (var (key, value) in disc.OpenOptions)
            {
                var result = FF.av_dict_set(&options, key, value, 0);
                if (result < 0)
                    throw FFmpegException.FromErrorCode(result, null);
            }
            var code = FF.avformat_open_input(&context, disc.OpenUrl, inputFormat, &options);
            if (code < 0)
                throw DiscOpenException.FromErrorCode(code);
            // 预览各处共用同一标题的关键帧记录，时间戳一致；导出（带锚点）各用一份，结果不受预览读取影响。
            var keyFrames = dvdExportSeekAnchor is null ? Discs.DvdKeyFrameIndex.For(disc) : new Discs.DvdKeyFrameIndex();
            var input = new InterruptibleInputFormatContext(context, callback, dvdExportSeekAnchor, new Discs.DvdTimeline(keyFrames));
            context = null;
            return input;
        }
        finally
        {
            FF.av_dict_free(&options);
            if (context != null)
                FF.avformat_close_input(&context);
            GC.KeepAlive(callback);
        }
    }

    protected override bool ReleaseHandle()
    {
        // 由基类统一释放输入上下文及可复用包，原生关闭完成前保持回调存活。
        var released = base.ReleaseHandle();
        DvdTimeline?.Dispose();
        GC.KeepAlive(interruptCallback);
        return released;
    }
}
