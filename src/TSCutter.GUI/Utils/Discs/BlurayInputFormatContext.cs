using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Discs;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Utils.Discs;

/// <summary>
/// 直接按播放列表读取蓝光，并在包离开读取层时统一时间轴。
/// 定位使用 libbluray 的 CLPI 入点表，不按平均码率估算文件位置，也不受
/// FFmpeg bluray 协议过滤三分钟以下播放列表的限制。
/// </summary>
internal sealed unsafe class BlurayInputFormatContext : FormatContext
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ReadCallback(nint opaque, byte* buffer, int size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long SeekCallback(nint opaque, long offset, int whence);

    private readonly Reader reader;
    private readonly ReadCallback read;
    private readonly SeekCallback seek;
    private readonly AVIOInterruptCB_callback interrupt;
    private AVIOContext* io;
    private readonly DiscInput disc;
    private readonly Dictionary<int, DiscSegment> packetSegments = [];
    private DiscSegment? seekSegment;

    private BlurayInputFormatContext(AVFormatContext* context, AVIOContext* io, Reader reader,
        ReadCallback read, SeekCallback seek, AVIOInterruptCB_callback interrupt, DiscInput disc) : base(context, true)
    {
        this.io = io;
        this.reader = reader;
        this.read = read;
        this.seek = seek;
        this.interrupt = interrupt;
        this.disc = disc;
        seekSegment = disc.Segments.Count > 0 ? disc.Segments[0] : null;
    }

    public static BlurayInputFormatContext Open(DiscInput disc, Func<bool> shouldInterrupt)
    {
        var reader = new Reader(disc, shouldInterrupt);
        ReadCallback read = reader.Read;
        SeekCallback seek = reader.Seek;
        AVIOInterruptCB_callback interrupt = _ => shouldInterrupt() ? 1 : 0;
        AVFormatContext* context = null;
        AVIOContext* io = null;
        byte* buffer = null;
        AVDictionary* options = null;
        try
        {
            context = FF.avformat_alloc_context();
            buffer = (byte*)FF.av_mallocz(192 * 256);
            if (context == null || buffer == null)
                throw new OutOfMemoryException();
            io = FF.avio_alloc_context(buffer, 192 * 256, 0, 0,
                Marshal.GetFunctionPointerForDelegate(read), 0, Marshal.GetFunctionPointerForDelegate(seek));
            if (io == null)
                throw new OutOfMemoryException();
            buffer = null; // AVIO 可替换其缓冲区，释放时以 io->buffer 为准。
            io->seekable = 1;
            context->pb = io;
            context->flags |= 0x80; // AVFMT_FLAG_CUSTOM_IO，生命周期由本类管理。
            context->interrupt_callback = new AVIOInterruptCB { callback = interrupt };
            var format = FF.av_find_input_format("mpegts");
            if (format == null)
                throw new DiscOpenException("The runtime does not support MPEG-TS input.");
            FFmpegException.Check(FF.av_opt_set_int(context, "skip_estimate_duration_from_pts", 1, 0));
            FFmpegException.Check(FF.av_dict_set(&options, "scan_all_pmts", "1", 0));
            FFmpegException.Check(FF.avformat_open_input(&context, disc.SourcePath, format, &options));
            return new BlurayInputFormatContext(context, io, reader, read, seek, interrupt, disc);
        }
        catch
        {
            if (context != null)
                FF.avformat_close_input(&context);
            FreeIo(ref io);
            FF.av_free(buffer);
            reader.Dispose();
            throw;
        }
        finally
        {
            FF.av_dict_free(&options);
            GC.KeepAlive(read);
            GC.KeepAlive(seek);
            GC.KeepAlive(interrupt);
        }
    }

    public override void SeekFrame(long timestamp, int streamIndex, AVSEEK_FLAG flags = AVSEEK_FLAG.Backward)
    {
        if ((flags & AVSEEK_FLAG.Byte) != 0)
        {
            base.SeekFrame(timestamp, streamIndex, flags);
            packetSegments.Clear();
            seekSegment = reader.Timeline.FindByByte(timestamp);
            return;
        }
        AVFormatContext* context = this;
        var timeBase = streamIndex < 0 ? new AVRational { num = 1, den = 1_000_000 }
            : context->streams[streamIndex]->time_base;
        var ticks = timestamp * timeBase.num * 90_000.0 / timeBase.den;
        var target = (ulong)Math.Clamp(ticks, 0, Math.Max(0, disc.Duration.TotalSeconds * 90_000 - 1));
        var position = FF.bd_seek_time(reader.Navigator.Handle, target);
        if (position < 0)
            throw new DiscOpenException("The selected playlist could not be positioned.");
        base.SeekFrame(position, streamIndex, AVSEEK_FLAG.Byte);
        packetSegments.Clear();
        seekSegment = reader.Timeline.FindByByte(position);
    }

    public override int ReadPacket(Packet packet)
    {
        var result = base.ReadPacket(packet);
        if (result < 0)
            return result;
        AVPacket* raw = packet;
        AVFormatContext* context = this;
        var segment = reader.Timeline.FindByByte(raw->pos);
        if (segment is not null)
            packetSegments[raw->stream_index] = segment;
        else if (raw->pos < 0)
            // MPEG-TS 的同一 PES 可拆成多个音频包，后续包的 pos 实际会是 -1。
            // 复用本流已知的片段，不能采用其他流的最新片段：解复用缓冲可能跨流交错。
            segment = packetSegments.GetValueOrDefault(raw->stream_index) ?? seekSegment;
        if (segment is null)
            // 无法确定来源时停止读取，不能猜测 offset 或静默跳包，避免定位错误、音画不同步。
            throw new InvalidDataException($"Cannot identify the playlist segment for stream {raw->stream_index}, position {raw->pos}.");
        var timeBase = context->streams[raw->stream_index]->time_base;
        var offset = (long)Math.Round((segment.RelativeStart90k - segment.InPts90k)
            * (double)timeBase.den / (timeBase.num * 90_000.0));
        if (raw->pts != FF.AV_NOPTS_VALUE)
            raw->pts += offset;
        if (raw->dts != FF.AV_NOPTS_VALUE)
            raw->dts += offset;
        return result;
    }

    protected override bool ReleaseHandle()
    {
        try
        {
            return base.ReleaseHandle();
        }
        finally
        {
            FreeIo(ref io);
            reader.Dispose();
            GC.KeepAlive(read);
            GC.KeepAlive(seek);
            GC.KeepAlive(interrupt);
        }
    }

    private static void FreeIo(ref AVIOContext* io)
    {
        if (io == null)
            return;
        FF.av_free(io->buffer);
        fixed (AVIOContext** pointer = &io)
            FF.avio_context_free(pointer);
    }

    private sealed class Reader : IDisposable
    {
        private readonly BlurayDisc source;
        private readonly Func<bool> shouldInterrupt;
        public BlurayDisc Navigator { get; }
        public DiscTimeline Timeline { get; }

        public Reader(DiscInput disc, Func<bool> shouldInterrupt)
        {
            this.shouldInterrupt = shouldInterrupt;
            Timeline = disc.CreateTimeline();
            source = BlurayDisc.Open(disc.SourcePath);
            try
            {
                Navigator = BlurayDisc.Open(disc.SourcePath);
                FF.bd_get_titles(source.Handle, FF.BlurayTitlesAll, 0);
                FF.bd_get_titles(Navigator.Handle, FF.BlurayTitlesAll, 0);
                if (FF.bd_select_playlist(source.Handle, (uint)disc.TitleNumber) == 0 ||
                    FF.bd_select_playlist(Navigator.Handle, (uint)disc.TitleNumber) == 0 ||
                    FF.bd_select_angle(source.Handle, (uint)disc.Angle) == 0 ||
                    FF.bd_select_angle(Navigator.Handle, (uint)disc.Angle) == 0)
                    throw new DiscOpenException("The selected playlist or angle could not be opened.");
            }
            catch
            {
                source.Dispose();
                Navigator?.Dispose();
                throw;
            }
        }

        public int Read(nint opaque, byte* buffer, int size)
        {
            try
            {
                if (shouldInterrupt())
                    return FF.AVERROR_EXIT;
                var count = FF.bd_read(source.Handle, buffer, size);
                return count == 0 ? FF.AVERROR_EOF : count;
            }
            catch { return -5; } // 回调不能让托管异常越过原生边界。
        }

        public long Seek(nint opaque, long offset, int whence)
        {
            try
            {
                if (shouldInterrupt())
                    return FF.AVERROR_EXIT;
                if ((whence & 0x10000) != 0) // AVSEEK_SIZE
                    return (long)FF.bd_get_title_size(source.Handle);
                var position = (whence & ~0x20000) switch // AVSEEK_FORCE
                {
                    0 => offset,
                    1 => (long)FF.bd_tell(source.Handle) + offset,
                    2 => (long)FF.bd_get_title_size(source.Handle) + offset,
                    _ => -1
                };
                return position < 0 ? -22 : FF.bd_seek(source.Handle, (ulong)position);
            }
            catch { return -5; }
        }

        public void Dispose()
        {
            source.Dispose();
            Navigator.Dispose();
        }
    }
}
