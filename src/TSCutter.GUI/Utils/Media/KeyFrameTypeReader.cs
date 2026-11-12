using System;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;

namespace TSCutter.GUI.Utils.Media;

/// <summary>
/// 从 H.264/HEVC 包的首个 VCL NAL 读取随机访问类型，兼容 Annex B 和长度前缀格式；
/// MPEG-2 只识别帧内刷新的入口（见 <see cref="RecoveryPoint"/>）。
/// </summary>
internal static class KeyFrameTypeReader
{
    /// <summary>
    /// 恢复点。H.264：解复用器按恢复点 SEI 标为关键帧，但首个条带是 P/B（非 IDR、非 I）。
    /// MPEG-2：图像前重复了序列头，但图像是 P 帧；解复用器只把 I 帧标为关键帧，需由读取层补标（见
    /// <see cref="InterruptibleInputFormatContext.Mpeg2RefreshPointStreamIndex"/>）。
    /// 逐行刷新（周期帧内刷新）的码流只有这种“关键帧”，单独从这里解码要经过若干秒画面才完整。
    /// </summary>
    internal const string RecoveryPoint = "RP";

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
        if (codec == AVCodecID.AV_CODEC_ID_MPEG2VIDEO)
            return IsMpeg2RefreshPoint(packet) ? RecoveryPoint : null;
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
                    return type == 5 ? "IDR" : IsPredictedSlice(nal) ? RecoveryPoint : null;
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

    /// <summary>
    /// MPEG-2 帧内刷新入口：包内首个图像之前有序列头，而图像编码类型是 P。常规码流的序列头只出现在 I 帧前；
    /// 部分广播回传编码器全程没有 I 帧，只在部分 P 帧前重复序列头和 GOP 头作为解码入口。
    /// 只检查首个图像头之前的起始码，遇到条带即停止，不扫描图像数据。
    /// </summary>
    private static bool IsMpeg2RefreshPoint(ReadOnlySpan<byte> packet)
    {
        var sequenceHeader = false;
        for (var start = FindStartCode(packet, 0); start >= 0 && start + 3 < packet.Length;
             start = FindStartCode(packet, start + 4))
        {
            var code = packet[start + 3];
            if (code == 0xB3)
                sequenceHeader = true;
            else if (code == 0x00)
                // picture_header：10 位 temporal_reference 之后是 3 位 picture_coding_type，2 为 P。
                return sequenceHeader && start + 5 < packet.Length && ((packet[start + 5] >> 3) & 7) == 2;
            else if (code <= 0xAF)
                return false;
        }
        return false;
    }

    /// <summary>读取 slice_header 的 first_mb_in_slice 与 slice_type，判断是否为 P/B/SP 条带。</summary>
    private static bool IsPredictedSlice(ReadOnlySpan<byte> nal)
    {
        // 两个指数哥伦布码最多占 66 位；去除防竞争字节后读取，无法解析时不改变原有判断。
        Span<byte> header = stackalloc byte[12];
        var length = 0;
        var zeros = 0;
        for (var index = 1; index < nal.Length && length < header.Length; index++)
        {
            var value = nal[index];
            if (zeros >= 2 && value == 3)
            {
                zeros = 0;
                continue;
            }
            zeros = value == 0 ? zeros + 1 : 0;
            header[length++] = value;
        }
        var bit = 0;
        if (ReadUnsignedExpGolomb(header[..length], ref bit) is null
            || ReadUnsignedExpGolomb(header[..length], ref bit) is not { } sliceType)
            return false;
        return (sliceType % 5) is 0 or 1 or 3;
    }

    private static uint? ReadUnsignedExpGolomb(ReadOnlySpan<byte> data, ref int bit)
    {
        var leadingZeros = 0;
        while (true)
        {
            if (bit >= data.Length * 8 || leadingZeros > 31)
                return null;
            if (((data[bit >> 3] >> (7 - (bit & 7))) & 1) != 0)
                break;
            leadingZeros++;
            bit++;
        }
        bit++;
        if (bit + leadingZeros > data.Length * 8)
            return null;
        ulong suffix = 0;
        for (var index = 0; index < leadingZeros; index++, bit++)
            suffix = (suffix << 1) | (uint)((data[bit >> 3] >> (7 - (bit & 7))) & 1);
        return (uint)((1UL << leadingZeros) - 1 + suffix);
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
