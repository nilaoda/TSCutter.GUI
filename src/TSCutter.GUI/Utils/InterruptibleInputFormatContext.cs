using System;
using Sdcb.FFmpeg.Common;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;

namespace TSCutter.GUI.Utils;

internal sealed unsafe class InterruptibleInputFormatContext : FormatContext
{
    private readonly AVIOInterruptCB_callback interruptCallback;

    private InterruptibleInputFormatContext(AVFormatContext* context, AVIOInterruptCB_callback callback)
        : base(context, isOwner: true)
    {
        interruptCallback = callback;
    }

    public static FormatContext Open(string path, Func<bool> shouldInterrupt)
    {
        AVFormatContext* context = ffmpeg.avformat_alloc_context();
        if (context == null)
            throw new OutOfMemoryException();

        AVDictionary* options = null;
        AVIOInterruptCB_callback callback = _ => shouldInterrupt() ? 1 : 0;
        try
        {
            // The protocol layer copies this callback when opening the file.
            // Setting only AVFormatContext.interrupt_callback after opening is too late.
            context->interrupt_callback = new AVIOInterruptCB { callback = callback };
            var result = ffmpeg.av_dict_set(&options, "scan_all_pmts", "1", 0);
            if (result < 0)
                throw FFmpegException.FromErrorCode(result, null);
            result = ffmpeg.avformat_open_input(&context, path, null, &options);
            if (result < 0)
                throw FFmpegException.FromErrorCode(result, null);

            var input = new InterruptibleInputFormatContext(context, callback);
            context = null;
            return input;
        }
        finally
        {
            ffmpeg.av_dict_free(&options);
            if (context != null)
                ffmpeg.avformat_close_input(&context);
            GC.KeepAlive(callback);
        }
    }

    protected override bool ReleaseHandle()
    {
        AVFormatContext* context = this;
        ffmpeg.avformat_close_input(&context);
        handle = IntPtr.Zero;
        GC.KeepAlive(interruptCallback);
        return true;
    }
}
