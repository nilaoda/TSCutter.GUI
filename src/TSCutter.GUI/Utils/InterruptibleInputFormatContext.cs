using TSCutter.GUI.FFmpeg;
using System;
using FFmpeg.AutoGen.Abstractions;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Utils;

internal sealed unsafe class InterruptibleInputFormatContext : FormatContext
{
    private readonly AVIOInterruptCB_callback interruptCallback;

    private InterruptibleInputFormatContext(AVFormatContext* context, AVIOInterruptCB_callback callback)
        : base(context, isOwner: true)
    {
        interruptCallback = callback;
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
                // 已确认 188 字节布局后跳过损坏前缀；保留原文件的绝对包位置。
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

    protected override bool ReleaseHandle()
    {
        // 由基类统一释放输入上下文及可复用包，原生关闭完成前保持回调存活。
        var released = base.ReleaseHandle();
        GC.KeepAlive(interruptCallback);
        return released;
    }
}
