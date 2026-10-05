using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.FFmpeg;

public sealed unsafe class Packet : SafeHandle
{
    public Packet() : base(IntPtr.Zero, true)
    {
        SetHandle((IntPtr)FF.av_packet_alloc());
        if (IsInvalid) throw new OutOfMemoryException();
    }
    public override bool IsInvalid => handle == IntPtr.Zero;
    private AVPacket* Raw => (AVPacket*)handle;
    public static implicit operator AVPacket*(Packet? value) => value is null ? null : (AVPacket*)value.handle;
    public int StreamIndex => Raw->stream_index;
    public int Flags => Raw->flags;
    public long Pts => Raw->pts;
    public long Position => Raw->pos;
    internal Utils.PacketEndSignature EndSignature => Raw->data == null || Raw->size < 32 ? default
        : Utils.PacketEndSignature.Create(new ReadOnlySpan<byte>(Raw->data, Raw->size));
    internal void SetPositionToken() => Raw->opaque = (void*)FramePacketPosition.Encode(Position);
    public void Unref() => FF.av_packet_unref(Raw);
    protected override bool ReleaseHandle()
    {
        var raw = Raw;
        FF.av_packet_free(&raw);
        handle = IntPtr.Zero;
        return true;
    }
}

public sealed unsafe class Frame : SafeHandle
{
    public Frame() : base(IntPtr.Zero, true)
    {
        SetHandle((IntPtr)FF.av_frame_alloc());
        if (IsInvalid) throw new OutOfMemoryException();
    }
    public override bool IsInvalid => handle == IntPtr.Zero;
    private AVFrame* Raw => (AVFrame*)handle;
    public static implicit operator AVFrame*(Frame frame) => (AVFrame*)frame.handle;
    public int Width { get => Raw->width; set => Raw->width = value; }
    public int Height { get => Raw->height; set => Raw->height = value; }
    public int Format { get => Raw->format; set => Raw->format = value; }
    public ref byte_ptr8 Data => ref Raw->data;
    public ref int8 Linesize => ref Raw->linesize;
    public long Pts => Raw->pts;
    public long BestEffortTimestamp => Raw->best_effort_timestamp;
    public int Flags => Raw->flags;
    public AVRational SampleAspectRatio => Raw->sample_aspect_ratio;
    public bool HasHardwareFramesContext => Raw->hw_frames_ctx != null;
    internal FramePacketPositionSource PacketPositionSource { get; set; }
    public long PacketPosition => FramePacketPosition.Resolve(Raw, PacketPositionSource);
    public void Unref()
    {
        FF.av_frame_unref(Raw);
        PacketPositionSource = FramePacketPositionSource.Opaque;
    }
    public static Frame CreateVideo(int width, int height, AVPixelFormat format)
    {
        var frame = new Frame { Width = width, Height = height, Format = (int)format };
        try { FFmpegException.Check(FF.av_frame_get_buffer(frame, 32)); return frame; }
        catch { frame.Dispose(); throw; }
    }
    protected override bool ReleaseHandle()
    {
        var raw = Raw;
        FF.av_frame_free(&raw);
        handle = IntPtr.Zero;
        return true;
    }
}

internal enum FramePacketPositionSource { Opaque, DecoderMetadata, Unknown }

internal static unsafe class FramePacketPosition
{
    // 0 表示未知位置。令牌只编码数值，不引用托管对象或包载荷，
    // 不需要为每个包分配 GCHandle 或 AVBufferRef。
    internal static nint Encode(long position) => position >= 0 && position < long.MaxValue
        ? (nint)(position + 1) : 0;
    internal static long Decode(nint token) => token > 0 ? (long)token - 1 : -1;
    internal static long Resolve(AVFrame* frame, FramePacketPositionSource source = FramePacketPositionSource.Opaque)
    {
        // 缺少扩展的异步解码器没有可靠来源，不能信任它附带的 opaque 或元数据。
        if (source == FramePacketPositionSource.Unknown) return -1;
        // 普通解码器通过 opaque 传递位置；无元数据时直接返回，省掉一次字典查询和 UTF-8 编组。
        if (frame->metadata == null) return source == FramePacketPositionSource.Opaque
            ? Decode((nint)frame->opaque) : -1;
        var entry = FF.av_dict_get(frame->metadata, "tscutter.packet_pos", null, 0);
        if (entry != null)
        {
            // 直接解析原生内存中的十进制数，不分配托管字符串。
            var value = entry->value;
            long position = 0;
            if (value == null || *value == 0) return -1;
            for (; *value != 0; value++)
            {
                var digit = *value - (byte)'0';
                if (digit is < 0 or > 9 || position > (long.MaxValue - digit) / 10) return -1;
                position = position * 10 + digit;
            }
            return position;
        }
        // 支持私有扩展的异步解码器也只能使用其来源元数据，缺失时保持未知。
        return source == FramePacketPositionSource.Opaque ? Decode((nint)frame->opaque) : -1;
    }
}

public static class RationalExtensions
{
    public static double ToDouble(this AVRational value) => FF.av_q2d(value);
}
