using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Common;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using static Sdcb.FFmpeg.Raw.ffmpeg;

namespace TSCutter.GUI.Extensions;

public static unsafe class FFmpegLibExtension
{
    static int ThrowIfError(this int errorCode, string? message = null)
    {
        if (errorCode < 0)
        {
            throw FFmpegException.FromErrorCode(errorCode, message);
        }
        return errorCode;
    }

    static long ThrowIfError(this long errorCode, string? message = null)
    {
        if (errorCode < 0)
        {
            throw FFmpegException.FromErrorCode((int)errorCode, message);
        }
        return errorCode;
    }
    
    /// <summary>
    /// <see cref="avformat_seek_file"/>
    /// </summary>
    public static void SeekFile(this FormatContext formatContext, long timestamp, long minTimestamp, long maxTimestamp, int streamIndex = -1, AVSEEK_FLAG flags = AVSEEK_FLAG.Backward)
        => avformat_seek_file(formatContext, streamIndex,  minTimestamp, timestamp, maxTimestamp, (int)flags).ThrowIfError();

    /// <summary>
    /// <see cref="avcodec_flush_buffers"/>
    /// </summary>
    public static void FlushBuffers(this CodecContext c) => avcodec_flush_buffers(c);
    
    /// <summary>
    /// <see cref="avio_size"/>
    /// </summary>
    public static long GetFileSize(this FormatContext formatContext) => avio_size(formatContext.Pb!);

}