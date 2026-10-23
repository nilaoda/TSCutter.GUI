using System;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;

namespace TSCutter.GUI.Utils.Media;

/// <summary>从 H.264/HEVC 包的首个 VCL NAL 读取随机访问类型，兼容 Annex B 和长度前缀格式。</summary>
internal static class KeyFrameTypeReader
{
    internal static unsafe string? Read(Packet packet, CodecParameters parameters)
    {
        AVPacket* raw = packet;
        AVCodecParameters* codec = parameters;
        if (raw->data == null || raw->size <= 0)
            return null;
        var extra = codec->extradata == null || codec->extradata_size <= 0
            ? ReadOnlySpan<byte>.Empty : new ReadOnlySpan<byte>(codec->extradata, codec->extradata_size);
        var lengthSize = 0;
        if (extra.Length > 0 && extra[0] == 1)
        {
            if (parameters.CodecId == AVCodecID.AV_CODEC_ID_H264 && extra.Length >= 5)
                lengthSize = (extra[4] & 3) + 1;
            else if (parameters.CodecId == AVCodecID.AV_CODEC_ID_HEVC && extra.Length >= 22)
                lengthSize = (extra[21] & 3) + 1;
        }
        return Read(new ReadOnlySpan<byte>(raw->data, raw->size), parameters.CodecId, lengthSize);
    }

    internal static string? Read(ReadOnlySpan<byte> packet, AVCodecID codec, int lengthSize = 0)
    {
        if (codec is not (AVCodecID.AV_CODEC_ID_H264 or AVCodecID.AV_CODEC_ID_HEVC)
            || lengthSize is < 0 or > 4)
            return null;
        var offset = 0;
        while (offset < packet.Length)
        {
            ReadOnlySpan<byte> nal;
            if (lengthSize > 0)
            {
                if (packet.Length - offset < lengthSize)
                    return null;
                uint length = 0;
                for (var index = 0; index < lengthSize; index++)
                    length = (length << 8) | packet[offset++];
                if (length == 0 || length > (uint)(packet.Length - offset))
                    return null;
                nal = packet.Slice(offset, (int)length);
                offset += (int)length;
            }
            else
            {
                var start = FindStartCode(packet, offset);
                if (start < 0)
                    return null;
                offset = start + 3;
                var end = FindStartCode(packet, offset);
                nal = packet.Slice(offset, (end < 0 ? packet.Length : end) - offset);
                offset = end < 0 ? packet.Length : end;
            }
            if (nal.IsEmpty || (nal[0] & 0x80) != 0)
                return null;
            if (codec == AVCodecID.AV_CODEC_ID_H264)
            {
                var type = nal[0] & 31;
                if (type is >= 1 and <= 5)
                    return type == 5 ? "IDR" : null;
            }
            else
            {
                if (nal.Length < 2 || (nal[1] & 7) == 0)
                    return null;
                var type = (nal[0] >> 1) & 63;
                if (type <= 31)
                    return type switch
                    {
                        >= 16 and <= 18 => "BLA",
                        19 or 20 => "IDR",
                        21 => "CRA",
                        22 or 23 => "IRAP",
                        _ => null
                    };
            }
        }
        return null;
    }

    // 四字节起始码同样包含 00 00 01，返回其最后三个字节的起点即可。
    private static int FindStartCode(ReadOnlySpan<byte> packet, int offset)
    {
        for (var index = offset; index <= packet.Length - 3; index++)
            if (packet[index] == 0 && packet[index + 1] == 0 && packet[index + 2] == 1)
                return index;
        return -1;
    }
}
