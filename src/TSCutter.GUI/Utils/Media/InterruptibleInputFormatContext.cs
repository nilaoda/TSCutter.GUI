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
        double? dvdExportSeekAnchor = null) : base(context, isOwner: true)
    {
        interruptCallback = callback;
        this.dvdExportSeekAnchor = dvdExportSeekAnchor;
    }

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
        base.SeekFrame(timestamp, streamIndex, flags);
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
            var input = new InterruptibleInputFormatContext(context, callback, dvdExportSeekAnchor);
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
        GC.KeepAlive(interruptCallback);
        return released;
    }
}
