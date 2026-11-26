using System;
using System.Buffers.Binary;
using System.IO;
using System.Collections.Generic;
using FFmpeg.AutoGen.Abstractions;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Utils.Media;

// 只读取元数据，不解码图像；AVS2/AVS3 的扩展封装见 T/UWA 005.1-2022 第 8.1 节。
internal sealed class HdrMetadataProbe(AVCodecID codec)
{
    private const int MaximumSeiBytes = 64 * 1024;
    private int lengthSize;
    internal bool MasteringDisplay { get; set; }
    internal byte? VividVersion { get; private set; }
    internal byte? Hdr10PlusVersion { get; private set; }
    internal byte? AlternativeTransfer { get; private set; }

    internal static bool Supports(AVCodecID codec) => codec is AVCodecID.AV_CODEC_ID_HEVC
        or AVCodecID.AV_CODEC_ID_H264 or AVCodecID.AV_CODEC_ID_AVS2 or AVCodecID.AV_CODEC_ID_AVS3;

    internal void ReadConfiguration(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return;
        if (data[0] != 1 || codec is not (AVCodecID.AV_CODEC_ID_HEVC or AVCodecID.AV_CODEC_ID_H264))
        {
            ReadPacket(data);
            return;
        }
        if (codec == AVCodecID.AV_CODEC_ID_H264)
        {
            if (data.Length >= 5)
                lengthSize = (data[4] & 3) + 1;
            return;
        }
        if (data.Length < 23)
            return;
        lengthSize = (data[21] & 3) + 1;
        // hvcC 的数组还可包含 prefix/suffix SEI，不能只提取 VPS/SPS/PPS。
        var offset = 23;
        for (var array = 0; array < data[22]; array++)
        {
            if (data.Length - offset < 3)
                return;
            var count = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 1)..]);
            offset += 3;
            for (var index = 0; index < count; index++)
            {
                if (data.Length - offset < 2)
                    return;
                var size = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
                offset += 2;
                if (size > data.Length - offset)
                    return;
                ReadNal(data.Slice(offset, size));
                offset += size;
            }
        }
    }

    internal void ReadPacket(ReadOnlySpan<byte> data)
    {
        if (codec is AVCodecID.AV_CODEC_ID_AVS2 or AVCodecID.AV_CODEC_ID_AVS3)
        {
            ReadAvsPacket(data);
            return;
        }
        try
        {
            foreach (var range in VideoBitstream.EnumerateNals(data, lengthSize))
                ReadNal(data[range]);
        }
        catch (InvalidDataException)
        {
            // 损坏的包不影响常规媒体信息，也不清除已经确认的元数据。
        }
    }

    private void ReadAvsPacket(ReadOnlySpan<byte> data)
    {
        // HDR picture extension 的类型 5 携带 T.35；AVS 的末尾零字节可以属于元数据。
        while (!data.IsEmpty)
        {
            var start = data.IndexOf((ReadOnlySpan<byte>)[0, 0, 1, 0xb5, 0x55]);
            if (start < 0)
                return;
            data = data[(start + 5)..];
            var end = data.IndexOf((ReadOnlySpan<byte>)[0, 0, 1]);
            ReadT35(end < 0 ? data : data[..end]);
        }
    }

    private void ReadNal(ReadOnlySpan<byte> nal)
    {
        var hevc = codec == AVCodecID.AV_CODEC_ID_HEVC;
        var headerSize = hevc ? 2 : 1;
        if (nal.Length <= headerSize || (nal[0] & 0x80) != 0)
            return;
        var type = VideoBitstream.NalType(nal, hevc);
        if (hevc ? type is not (39 or 40) || (nal[1] & 7) == 0 : type != 6)
            return;
        var escaped = nal[headerSize..];
        if (escaped.Length > MaximumSeiBytes)
            return;
        var rbsp = new byte[escaped.Length];
        var count = 0;
        for (var index = 0; index < escaped.Length; index++)
        {
            if (index >= 2 && escaped[index] == 3 && escaped[index - 1] == 0 && escaped[index - 2] == 0)
            {
                if (index + 1 == escaped.Length || escaped[index + 1] > 3)
                    return;
                continue;
            }
            rbsp[count++] = escaped[index];
        }
        var messages = rbsp.AsSpan(0, count);
        var offset = 0;
        while (offset < messages.Length - 1)
        {
            var payloadType = ReadSeiValue(messages, ref offset);
            var size = ReadSeiValue(messages, ref offset);
            if (payloadType < 0 || size < 0 || size > messages.Length - offset)
                return;
            var payload = messages.Slice(offset, size);
            switch (payloadType)
            {
                case 4: ReadT35(payload); break;
                case 137 when size == 24: MasteringDisplay = true; break;
                case 147 when size == 1: AlternativeTransfer = payload[0]; break;
            }
            offset += size;
        }
    }

    private static int ReadSeiValue(ReadOnlySpan<byte> data, ref int offset)
    {
        var value = 0;
        while (offset < data.Length)
        {
            var current = data[offset++];
            value += current;
            if (current != 255)
                return value;
        }
        return -1;
    }

    private unsafe void ReadT35(ReadOnlySpan<byte> data)
    {
        // 完整注册标识 + system_start_code + 四个 12-bit 统计量 + 标志位。
        // 与 MediaInfo 一致，仅识别 system_start_code=1 的已知版本。
        if (data.Length >= 13 && data[..5].SequenceEqual((ReadOnlySpan<byte>)[0x26, 0, 4, 0, 5]) && data[5] == 1)
            VividVersion = 1;
        if (data.Length < 7 || !data[..6].SequenceEqual((ReadOnlySpan<byte>)[0xb5, 0, 0x3c, 0, 1, 4])
            || data[6] > 1 || Hdr10PlusVersion is not null)
            return;
        // FFmpeg 的解析器验证可变长度字段，避免把被截断的注册消息报告为 HDR10+。
        // 此结构的大小不属于公开 ABI，必须由运行库分配。
        var metadata = FF.av_dynamic_hdr_plus_alloc(null);
        if (metadata == null)
            throw new OutOfMemoryException();
        try
        {
            fixed (byte* payload = data[6..])
            {
                if (FF.av_dynamic_hdr_plus_from_t35(metadata, payload, (nuint)(data.Length - 6)) >= 0)
                    Hdr10PlusVersion = metadata->application_version;
            }
        }
        finally { FF.av_free(metadata); }
    }

    internal string Describe(AVColorTransferCharacteristic transfer, AVColorPrimaries primaries, int bitDepth,
        string? dolbyVision = null)
    {
        var formats = new List<string>();
        if (dolbyVision is not null)
            formats.Add(dolbyVision);
        transfer = EffectiveTransfer(transfer);
        if (MasteringDisplay)
            formats.Add(transfer == AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084
                && primaries == AVColorPrimaries.AVCOL_PRI_BT2020 && bitDepth >= 10
                ? "SMPTE ST 2086, HDR10 compatible" : "SMPTE ST 2086");
        if (Hdr10PlusVersion is { } plus)
            formats.Add($"SMPTE ST 2094 App 4, Version {plus}, HDR10+");
        if (VividVersion is { } vivid)
            formats.Add($"HDR Vivid, Version {vivid}");
        if (formats.Count == 0 && transfer == AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67)
            formats.Add("HLG");
        return string.Join(" / ", formats);
    }

    internal AVColorTransferCharacteristic EffectiveTransfer(AVColorTransferCharacteristic transfer)
        => AlternativeTransfer is 16 or 18 ? (AVColorTransferCharacteristic)AlternativeTransfer.Value : transfer;
}
