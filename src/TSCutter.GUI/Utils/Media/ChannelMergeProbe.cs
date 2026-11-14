using System;
using System.Collections.Generic;
using System.Linq;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;

namespace TSCutter.GUI.Utils.Media;

/// <summary>可以参加多轨合并的一条立体声音轨。</summary>
/// <param name="Text">显示文字：PID、语言、编码。</param>
internal sealed record ChannelMergeTrackInfo(int Pid, int StreamIndex, string Text, string Language, int SampleRate);

/// <summary>一个节目中可以合并的音轨（至少三条采样率相同的立体声）。</summary>
internal sealed record ChannelMergeProgramInfo(int ProgramId, string Name, IReadOnlyList<ChannelMergeTrackInfo> Tracks);

/// <summary>
/// 多轨合并的候选：TS 节目中有三条以上声道数为 2、采样率相同、可以解码的普通音轨（MPEG 音频、AAC、AC3、E-AC3）。
/// 只看节目表与流信息，不解码、不扫描。码流中没有说明多轨承载 5.1 的信息，是否合并、怎样映射由用户决定。
/// </summary>
internal static class ChannelMergeProbe
{
    public const int MinimumTracks = 3;

    private static readonly HashSet<AVCodecID> Supported =
    [
        AVCodecID.AV_CODEC_ID_MP1, AVCodecID.AV_CODEC_ID_MP2, AVCodecID.AV_CODEC_ID_MP3, AVCodecID.AV_CODEC_ID_AAC,
        AVCodecID.AV_CODEC_ID_AAC_LATM, AVCodecID.AV_CODEC_ID_AC3, AVCodecID.AV_CODEC_ID_EAC3
    ];

    public static IReadOnlyList<ChannelMergeProgramInfo> Probe(FormatContext context)
    {
        var result = new List<ChannelMergeProgramInfo>();
        foreach (var program in context.Programs)
        {
            var tracks = context.Streams
                .Where(stream => program.ContainsStream(stream.Index) && stream.Codecpar is { } parameters &&
                                 parameters.CodecType == AVMediaType.AVMEDIA_TYPE_AUDIO && Supported.Contains(parameters.CodecId) &&
                                 parameters.ChLayout.nb_channels == 2 && parameters.SampleRate > 0 && stream.Id is > 0 and < 0x1FFF)
                .Select(stream =>
                {
                    stream.Metadata.TryGetValue("language", out var language);
                    var codec = stream.Codecpar!.CodecName.ToUpperInvariant();
                    var text = string.Join(" · ", new[] { $"PID 0x{stream.Id:X4}", language, codec }
                        .Where(part => !string.IsNullOrWhiteSpace(part)));
                    return new ChannelMergeTrackInfo(stream.Id, stream.Index, text, language ?? "", stream.Codecpar.SampleRate);
                })
                .GroupBy(track => track.Pid).Select(group => group.First())
                .ToArray();
            // 采样率不同的音轨不能逐样本对齐：取数量最多的一组。
            var group = tracks.GroupBy(track => track.SampleRate).OrderByDescending(item => item.Count()).FirstOrDefault();
            if (group is null || group.Count() < MinimumTracks)
                continue;
            program.Metadata.TryGetValue("service_name", out var name);
            result.Add(new ChannelMergeProgramInfo(program.Id, name ?? "", group.OrderBy(track => track.Pid).ToArray()));
        }
        return result;
    }
}
