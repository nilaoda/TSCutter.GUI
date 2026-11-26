using System;
using System.Collections.Generic;
using System.Globalization;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;

namespace TSCutter.GUI.Utils.Media;

public static partial class MediaInfoBuilder
{
    private static unsafe IReadOnlyDictionary<int, HdrMetadataProbe> ReadHdrMetadata(FormatContext input)
    {
        const int packetsPerStream = 128;
        var probes = new Dictionary<int, HdrMetadataProbe>();
        var remaining = new Dictionary<int, int>();
        foreach (var stream in input.Streams)
        {
            if (stream.IsAttachedPicture || stream.Codecpar is not { } parameters
                || parameters.CodecType != AVMediaType.AVMEDIA_TYPE_VIDEO || !HdrMetadataProbe.Supports(parameters.CodecId))
                continue;
            probes[stream.Index] = ReadHdrConfiguration(parameters);
            remaining[stream.Index] = packetsPerStream;
        }
        if (remaining.Count == 0)
            return probes;
        // 弹窗只抽样开头的编码包，避免扫描整部影片；不需要创建视频解码器。
        var timer = System.Diagnostics.Stopwatch.StartNew();
        long bytes = 0;
        var count = 0;
        try
        {
            foreach (var packet in input.ReadPackets())
            {
                AVPacket* raw = packet;
                bytes += Math.Max(0, raw->size);
                if (bytes > 32 * 1024 * 1024 || ++count > 8192 || timer.Elapsed.TotalSeconds > 2)
                    break;
                if (!remaining.TryGetValue(packet.StreamIndex, out var packets))
                    continue;
                if (raw->data != null && raw->size > 0)
                    probes[packet.StreamIndex].ReadPacket(new ReadOnlySpan<byte>(raw->data, raw->size));
                if (packets > 1)
                    remaining[packet.StreamIndex] = packets - 1;
                else
                    remaining.Remove(packet.StreamIndex);
                if (remaining.Count == 0)
                    break;
            }
        }
        catch (FFmpegException)
        {
            // 元数据抽样失败时保留容器信息和已经确认的格式。
        }
        return probes;
    }

    private static unsafe HdrMetadataProbe ReadHdrConfiguration(CodecParameters parameters)
    {
        var probe = new HdrMetadataProbe(parameters.CodecId);
        AVCodecParameters* raw = parameters;
        for (var index = 0; raw->coded_side_data != null && index < raw->nb_coded_side_data; index++)
        {
            var side = raw->coded_side_data[index];
            if (side.type != AVPacketSideDataType.AV_PKT_DATA_MASTERING_DISPLAY_METADATA
                || side.data == null || side.size < (nuint)sizeof(AVMasteringDisplayMetadata))
                continue;
            var mastering = (AVMasteringDisplayMetadata*)side.data;
            probe.MasteringDisplay = mastering->has_primaries != 0 && mastering->has_luminance != 0;
        }
        if (HdrMetadataProbe.Supports(parameters.CodecId) && raw->extradata != null && raw->extradata_size > 0)
            probe.ReadConfiguration(new ReadOnlySpan<byte>(raw->extradata, raw->extradata_size));
        return probe;
    }

    private static unsafe DolbyVisionConfiguration? ReadDolbyVisionConfiguration(CodecParameters parameters)
    {
        AVCodecParameters* raw = parameters;
        for (var index = 0; raw->coded_side_data != null && index < raw->nb_coded_side_data; index++)
        {
            var side = raw->coded_side_data[index];
            if (side.type != AVPacketSideDataType.AV_PKT_DATA_DOVI_CONF || side.data == null || side.size < 8)
                continue;
            // FFmpeg 9 的 AVDOVIDecoderConfigurationRecord 是独立字节字段，不是容器里的位打包数据。
            // 仅读取已知前缀；旧记录缺少压缩字段时不猜测其值。
            return ParseDolbyVisionConfiguration(new ReadOnlySpan<byte>(side.data, side.size >= 9 ? 9 : 8), parameters.CodecId);
        }
        return null;
    }

    internal static DolbyVisionConfiguration? ParseDolbyVisionConfiguration(ReadOnlySpan<byte> data, AVCodecID codec)
    {
        if (data.Length < 8 || data[2] == 0 || data[4] > 1 || data[5] > 1 || data[6] > 1) return null;
        var fields = new List<string>
        {
            "Dolby Vision", $"Version {data[0]}.{data[1]}", $"Profile {data[2]}"
        };
        var prefix = codec switch
        {
            AVCodecID.AV_CODEC_ID_HEVC => "dvhe",
            AVCodecID.AV_CODEC_ID_H264 => "dvav",
            AVCodecID.AV_CODEC_ID_AV1 => "dav1",
            _ => null
        };
        if (prefix is not null)
            fields.Add(string.Create(CultureInfo.InvariantCulture, $"{prefix}.{data[2]:00}.{data[3]:00}"));
        else
            fields.Add($"Level {data[3]}");
        var layers = new List<string>();
        if (data[6] != 0) layers.Add("BL");
        if (data[5] != 0) layers.Add("EL");
        if (data[4] != 0) layers.Add("RPU");
        if (layers.Count > 0) fields.Add(string.Join("+", layers));
        var compatibility = data[7] switch
        {
            0 => null, 1 => "HDR10 compatible", 2 => "SDR compatible", 4 => "HLG compatible",
            _ => $"base layer compatibility ID {data[7]}"
        };
        if (compatibility is not null) fields.Add(compatibility);
        if (data.Length >= 9)
            fields.Add(data[8] switch
            {
                0 => "no metadata compression", 1 => "limited metadata compression",
                3 => "extended metadata compression", _ => $"metadata compression ID {data[8]}"
            });
        return new DolbyVisionConfiguration(data[2], string.Join(", ", fields));
    }

    internal static string VideoCodecId(uint tag, AVCodecID codec)
    {
        Span<char> characters = stackalloc char[4];
        for (var index = 0; index < characters.Length; index++)
        {
            var value = (byte)(tag >> (index * 8));
            if (value is < 0x20 or > 0x7e) return ((int)codec).ToString(CultureInfo.InvariantCulture);
            characters[index] = (char)value;
        }
        return new string(characters);
    }

    internal sealed record DolbyVisionConfiguration(byte Profile, string Description);
}
