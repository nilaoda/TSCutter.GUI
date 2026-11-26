using System;
using System.Collections.Generic;
using System.IO;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Utils.Media;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Services.Editing;

// 原始注册消息通过 opaque_ref 跟随解码帧及其引用，保持 B 帧重排序后的对应关系。
internal static unsafe class HevcDynamicHdrMetadata
{
    private const int MaximumBytes = 64 * 1024;
    private const int HeaderBytes = 5;
    private static ReadOnlySpan<byte> Signature => [0x54, 0x53, 0x48, 0x44];
    internal sealed record Metadata(byte Flags, byte[] Sei);

    internal static Metadata? Read(ReadOnlySpan<byte> packet, int lengthSize)
    {
        var messages = new List<byte>();
        byte flags = 0;
        Span<byte> prefix = stackalloc byte[7];
        foreach (var range in VideoBitstream.EnumerateNals(packet, lengthSize))
        {
            var nal = packet[range];
            if (nal.Length < 2 || VideoBitstream.NalType(nal, true) is not (39 or 40))
                continue;
            if ((nal[0] & 0x80) != 0 || (nal[1] & 7) == 0)
                throw new InvalidDataException("Invalid HEVC SEI.");
            if (nal.Length < 3 || nal[^1] != 0x80)
                throw new InvalidDataException("Invalid HEVC SEI trailing bits.");
            // 无关 SEI 可以很大，逐字节跳过，不为整条 NAL 分配解包缓冲。
            var reader = new RbspReader(nal[2..^1]);
            while (!reader.End)
            {
                var type = reader.ReadValue();
                var size = reader.ReadValue();
                if (type != 4)
                {
                    reader.Skip(size);
                    continue;
                }
                var header = prefix[..Math.Min(size, prefix.Length)];
                for (var index = 0; index < header.Length; index++)
                    header[index] = reader.ReadByte();
                var kind = header.Length >= 7 && header[..6].SequenceEqual((ReadOnlySpan<byte>)[0xb5, 0, 0x3c, 0, 1, 4])
                    ? 1 : header.Length >= 6 && header[..5].SequenceEqual((ReadOnlySpan<byte>)[0x26, 0, 4, 0, 5]) ? 2 : 0;
                if (kind == 0)
                {
                    reader.Skip(size - header.Length);
                    continue;
                }
                if (size > MaximumBytes - messages.Count)
                    throw new InvalidDataException("HEVC dynamic HDR metadata exceeds the limit.");
                flags |= (byte)kind;
                WriteValue(messages, 4);
                WriteValue(messages, size);
                foreach (var value in header)
                    messages.Add(value);
                for (var index = header.Length; index < size; index++)
                    messages.Add(reader.ReadByte());
                if (messages.Count > MaximumBytes)
                    throw new InvalidDataException("HEVC dynamic HDR metadata exceeds the limit.");
            }
        }
        if (flags == 0)
            return null;
        messages.Add(0x80);
        // 只重发动态 HDR 消息；图像时序、恢复点等 SEI 属于原编码序列。
        var sei = new List<byte> { 0, 0, 1, 0x4e, 1 };
        var zeros = 0;
        foreach (var value in messages)
        {
            if (zeros == 2 && value <= 3)
            {
                sei.Add(3);
                zeros = 0;
            }
            sei.Add(value);
            zeros = value == 0 ? zeros + 1 : 0;
        }
        if (sei.Count > MaximumBytes)
            throw new InvalidDataException("HEVC dynamic HDR metadata exceeds the limit.");
        return new Metadata(flags, sei.ToArray());
    }

    internal static AVPacket* Attach(AVPacket* source, Packet destination, int lengthSize)
    {
        destination.Unref();
        if (source == null)
            return null;
        Metadata? metadata;
        try { metadata = Read(new ReadOnlySpan<byte>(source->data, source->size), lengthSize); }
        catch (InvalidDataException)
        {
            throw new FrameAccurateExportException(FrameAccurateExportError.Unsupported, "HEVC SEI", FrameAccurateSupport.DynamicHdr);
        }
        if (metadata is null)
            return source;
        AVPacket* copy = destination;
        FFmpegException.Check(FF.av_packet_ref(copy, source));
        FF.av_buffer_unref(&copy->opaque_ref);
        copy->opaque_ref = FF.av_buffer_alloc((nuint)(HeaderBytes + metadata.Sei.Length));
        if (copy->opaque_ref == null)
            throw new OutOfMemoryException();
        var target = new Span<byte>(copy->opaque_ref->data, HeaderBytes + metadata.Sei.Length);
        Signature.CopyTo(target);
        target[4] = metadata.Flags;
        metadata.Sei.CopyTo(target[HeaderBytes..]);
        return copy;
    }

    internal static ReadOnlySpan<byte> ReadFrame(AVFrame* frame, AVCodecID codec)
    {
        var buffer = frame->opaque_ref;
        var stored = buffer != null && buffer->data != null && buffer->size > HeaderBytes
            && buffer->size <= MaximumBytes + HeaderBytes ? new ReadOnlySpan<byte>(buffer->data, (int)buffer->size) : [];
        var flags = stored.Length > HeaderBytes && stored[..4].SequenceEqual(Signature) && stored[4] is >= 1 and <= 3
            ? stored[4] : 0;
        var plus = FF.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_PLUS) != null;
        var vivid = FF.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_VIVID) != null;
        if (codec != AVCodecID.AV_CODEC_ID_HEVC && (flags != 0 || plus || vivid)
            || plus && (flags & 1) == 0 || vivid && (flags & 2) == 0)
            throw new FrameAccurateExportException(FrameAccurateExportError.Unsupported, "", FrameAccurateSupport.DynamicHdr);
        return flags == 0 ? [] : stored[HeaderBytes..];
    }

    internal static void Insert(AVPacket* packet, ReadOnlySpan<byte> sei)
    {
        var data = new ReadOnlySpan<byte>(packet->data, packet->size);
        var insertion = -1;
        foreach (var range in VideoBitstream.EnumerateNals(data, 0))
        {
            if (VideoBitstream.NalType(data[range], true) > 31)
                continue;
            insertion = range.Start.Value - 3;
            break;
        }
        if (insertion < 0)
            throw new FrameAccurateExportException(FrameAccurateExportError.EncodeFailed, "HEVC picture missing");
        var oldSize = packet->size;
        FFmpegException.Check(FF.av_grow_packet(packet, sei.Length));
        var target = new Span<byte>(packet->data, packet->size);
        target.Slice(insertion, oldSize - insertion).CopyTo(target[(insertion + sei.Length)..]);
        sei.CopyTo(target[insertion..]);
    }

    private ref struct RbspReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> bytes = data;
        private int position;
        private int zeros;
        internal readonly bool End => position == bytes.Length;

        internal byte ReadByte()
        {
            if (position >= bytes.Length)
                throw new InvalidDataException("Truncated HEVC SEI.");
            var value = bytes[position++];
            if (zeros == 2 && value == 3)
            {
                if (position >= bytes.Length || bytes[position] > 3)
                    throw new InvalidDataException("Invalid HEVC SEI escape.");
                zeros = 0;
                value = bytes[position++];
            }
            zeros = value == 0 ? Math.Min(zeros + 1, 2) : 0;
            return value;
        }

        internal int ReadValue()
        {
            var value = 0;
            while (true)
            {
                var current = ReadByte();
                if (value > int.MaxValue - current)
                    throw new InvalidDataException("HEVC SEI header exceeds the limit.");
                value += current;
                if (current != 255)
                    return value;
            }
        }

        internal void Skip(int count)
        {
            for (var index = 0; index < count; index++)
                ReadByte();
        }
    }

    private static void WriteValue(List<byte> data, int value)
    {
        while (value >= 255)
        {
            data.Add(255);
            value -= 255;
        }
        data.Add((byte)value);
    }
}
