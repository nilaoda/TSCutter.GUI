using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text.Json;
using System.Threading.Channels;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Web;

internal sealed record WebUiVideoStream(int Id, string Codec, bool Feedback = false)
{
    internal bool IsH264 => Codec == "h264";
    internal int FrameRate => IsH264 ? 60 : 30;
}

internal sealed record WebUiVideoFallback(int StreamId, string Reason);
internal sealed record WebUiVideoTransport(WebUiVideoStream? Stream, bool JpegEnabled, bool ManualRecovery);

internal sealed unsafe class WebUiVideoSocket(bool initialJpeg = true) : IDisposable
{
    private const int KeyPacket = 1;
    private const int IoBufferSize = 32 * 1024;
    private static readonly AVRational FrameTime = new() { num = 1, den = 90_000 };
    private readonly object streamSync = new();
    private WebUiVideoTransport transport = new(null, initialJpeg, false);
    private WebUiVideoFallback? notifyFallback;
    private GCHandle sink;
    private AVFormatContext* muxer;
    private AVIOContext* io;
    private AVStream* track;
    private ArrayBufferWriter<byte>? output;
    private int encoderGeneration = -1;
    private long nextTimestamp;
    private long lastPacketTime;

    // 视频帧有引用关系。队列满时暂停取新画面，避免丢掉已经编码的中间帧。
    internal Channel<ReadOnlyMemory<byte>> Frames
    {
        get;
    } =
        Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(2)
        {
            SingleReader = true,
            SingleWriter = true
        });

    internal WebUiStreamFeedback Feedback { get; } = new();
    internal WebUiVideoTransport Transport => Volatile.Read(ref transport);
    internal WebUiVideoStream? Stream => Transport.Stream;
    internal WebUiVideoFallback? TakeFallback() => Interlocked.Exchange(ref notifyFallback, null);

    internal void Start(int id, string codec, bool feedback = false, bool manualRecovery = false)
    {
        var requested = codec == "jpeg" ? null : new WebUiVideoStream(id, codec, feedback);
        var supported = false;
        try
        {
            supported = requested is not null && WebUiMediaEncoder.IsSupported(requested);
        }
        catch (Exception error) when (WebUiMediaEncoder.IsMediaError(error))
        {
            Console.Error.WriteLine($"Video WebSocket initialization failed: {error.Message}");
        }

        lock (streamSync)
        {
            Volatile.Write(ref notifyFallback, requested is not null && !supported
                ? new WebUiVideoFallback(id, "encoder-unavailable") : null);
            Feedback.Start(supported ? requested : null);
            // 新网页由用户决定是否改用 JPEG；旧网页继续沿用原协议的自动回退行为。
            Volatile.Write(ref transport, new WebUiVideoTransport(supported ? requested : null,
                codec == "jpeg" || !manualRecovery && !supported, manualRecovery));
        }
    }

    internal void Stop(int id, bool notify = false, string reason = "encoder-error")
    {
        lock (streamSync)
        {
            if (transport.Stream?.Id != id)
                return;
            if (notify)
                Volatile.Write(ref notifyFallback, new WebUiVideoFallback(id, reason));
            Volatile.Write(ref transport, new WebUiVideoTransport(null, !transport.ManualRecovery, transport.ManualRecovery));
            Feedback.Start(null);
        }
    }

    internal void ReportPlayback(JsonElement report)
    {
        if (Stream is not { Feedback: true } active ||
            !report.TryGetProperty("sequence", out var sequence) || sequence.ValueKind != JsonValueKind.Number || !sequence.TryGetUInt32(out var acknowledged) ||
            !report.TryGetProperty("pending", out var pending) || pending.ValueKind != JsonValueKind.Number || !pending.TryGetInt32(out var count))
            return;
        var visible = !report.TryGetProperty("visible", out var visibility) || visibility.ValueKind != JsonValueKind.False;
        Feedback.Report(active, new WebUiPlaybackStatus(acknowledged, Number("presentMs"), Number("bufferedMs"),
            count, Number("queuedMs"), Number("decodeMs"), visible), Stopwatch.GetTimestamp());

        double Number(string name) => !report.TryGetProperty(name, out var value) ? 0 :
            value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : double.NaN;
    }

    internal bool IsCurrent(ReadOnlyMemory<byte> frame) =>
        Stream?.Id == BinaryPrimitives.ReadInt32LittleEndian(frame.Span[1..]);

    internal void Package(AVCodecContext* codec, AVPacket* packet, int generation, WebUiVideoStream active)
    {
        var initialize = muxer is null || encoderGeneration != generation;
        if (initialize && (packet->flags & KeyPacket) == 0)
            return;
        if (initialize)
            Reset();

        output = new ArrayBufferWriter<byte>(packet->size + 4096);
        // 切换编码时仍可能有旧分片在传输，浏览器按流编号忽略旧分片。
        var headerSize = active.Feedback ? 9 : 5;
        var header = output.GetSpan(headerSize);
        header[0] = initialize ? (byte)'I' : (packet->flags & KeyPacket) != 0 ? (byte)'K' : (byte)'V';
        BinaryPrimitives.WriteInt32LittleEndian(header[1..], active.Id);
        if (active.Feedback)
            header.Slice(5, 4).Clear();
        output.Advance(headerSize);
        if (initialize)
            Open(codec, packet, generation);

        // FFmpeg 写入一个独立的 MP4 分片，浏览器可以立即追加这一帧。
        var sample = *packet;
        sample.stream_index = track->index;
        var now = Stopwatch.GetTimestamp();
        var seconds = Stopwatch.GetElapsedTime(lastPacketTime, now).TotalSeconds;
        // 按实际产帧节奏推进媒体时钟，跳过静止期间的空档，避免恢复时重播旧画面。
        if (lastPacketTime == 0 || seconds > 0.25)
            seconds = Feedback.CaptureInterval(TimeSpan.FromSeconds(1.0 / active.FrameRate)).TotalSeconds;
        sample.pts = sample.dts = nextTimestamp;
        sample.duration = Math.Max(1, (long)Math.Round(seconds * track->time_base.den / track->time_base.num));
        nextTimestamp += sample.duration;
        lastPacketTime = now;
        FFmpegException.Check(FF.av_write_frame(muxer, &sample));
        FFmpegException.Check(FF.av_write_frame(muxer, null));
        FF.avio_flush(io);
        FFmpegException.Check(io->error);
        // 封装完成后才登记真实字节数；缓冲扩容可能更换数组，不能复用之前取得的头部 Span。
        var packaged = output.WrittenMemory;
        if (active.Feedback)
        {
            if (!MemoryMarshal.TryGetArray(packaged, out var array))
                throw new InvalidOperationException("Video packet storage is unavailable.");
            BinaryPrimitives.WriteUInt32LittleEndian(array.AsSpan()[5..],
                Feedback.FrameQueued(active, Stopwatch.GetTimestamp(), packaged.Length));
        }
        // 生产者在编码前确认过容量，另一个线程只会读取队列。
        if (!Frames.Writer.TryWrite(packaged))
            throw new InvalidOperationException("Video socket frame queue is full.");
        output = null;
    }

    private void Open(AVCodecContext* codec, AVPacket* firstFrame, int generation)
    {
        AVFormatContext* context = null;
        FFmpegException.Check(FF.avformat_alloc_output_context2(&context, null, "mp4", "desktop.mp4"));
        muxer = context;
        sink = GCHandle.Alloc(this);
        var buffer = (byte*)FF.av_malloc(IoBufferSize);
        if (buffer is null)
            throw new OutOfMemoryException();
        io = FF.avio_alloc_context(buffer, IoBufferSize, 1, GCHandle.ToIntPtr(sink), 0,
            (nint)(delegate* unmanaged[Cdecl]<nint, byte*, int, int>)&Write, 0);
        if (io is null)
        {
            FF.av_free(buffer);
            throw new OutOfMemoryException();
        }
        muxer->pb = io;
        track = FF.avformat_new_stream(muxer, null);
        if (track is null)
            throw new OutOfMemoryException();
        track->time_base = FrameTime;
        track->avg_frame_rate = codec->framerate;
        FFmpegException.Check(FF.avcodec_parameters_from_context(track->codecpar, codec));
        if (track->codecpar->extradata_size == 0)
        {
            // libaom 在关键帧中写序列头，由 MP4 封装器从首帧提取 av1C。
            var data = (byte*)FF.av_mallocz((ulong)firstFrame->size + 64);
            if (data is null)
                throw new OutOfMemoryException();
            new ReadOnlySpan<byte>(firstFrame->data, firstFrame->size).CopyTo(new Span<byte>(data, firstFrame->size));
            track->codecpar->extradata = data;
            track->codecpar->extradata_size = firstFrame->size;
        }
        AVDictionary* options = null;
        try
        {
            FFmpegException.Check(FF.av_dict_set(&options, "movflags", "empty_moov+default_base_moof+frag_custom+skip_trailer", 0));
            FFmpegException.Check(FF.avformat_write_header(muxer, &options));
        }
        finally { FF.av_dict_free(&options); }
        encoderGeneration = generation;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Write(nint opaque, byte* bytes, int length)
    {
        try
        {
            var writer = ((WebUiVideoSocket)GCHandle.FromIntPtr(opaque).Target!).output!;
            new ReadOnlySpan<byte>(bytes, length).CopyTo(writer.GetSpan(length));
            writer.Advance(length);
            return length;
        }
        catch (Exception)
        {
            // 错误必须通过返回值传回 FFmpeg，不能穿过原生回调边界。
            return -5;
        }
    }

    internal void Reset()
    {
        if (muxer is not null)
        {
            muxer->pb = null;
            FF.avformat_free_context(muxer);
            muxer = null;
        }
        if (io is not null)
        {
            var owned = io;
            FF.av_free(owned->buffer);
            owned->buffer = null;
            FF.avio_context_free(&owned);
            io = null;
        }
        if (sink.IsAllocated)
            sink.Free();
        track = null;
        output = null;
        encoderGeneration = -1;
        nextTimestamp = 0;
        lastPacketTime = 0;
    }

    public void Dispose() => Reset();
}
