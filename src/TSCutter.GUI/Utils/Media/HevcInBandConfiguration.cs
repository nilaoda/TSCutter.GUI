using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Utils.Files;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Utils.Media;

/// <summary>
/// 参数集只在码流中的 HEVC（配置记录不含参数集）输出 MP4 时，从各片段起点的关键帧取出 VPS、SPS、PPS，
/// 生成含参数集的配置记录，使输出可以标记 hvc1／dvh1（苹果设备只播放这两种）；样本中的参数集原样保留。
/// 各片段同编号参数集的内容不同、或读不到参数集时不改动附加数据，输出保留 hev1／dvhe。
/// </summary>
internal static unsafe class HevcInBandConfiguration
{
    private const int MaximumPackets = 4096;

    /// <param name="starts">各片段的源文件、源流序号与起点（秒，源时间轴；为 null 时从文件开头读取）。</param>
    /// <returns>已生成配置记录并写入附加数据时返回 true。</returns>
    public static bool TryFill(AVCodecParameters* parameters, IEnumerable<(string Path, int StreamIndex, double? StartSeconds)> starts,
        CancellationToken token)
    {
        if (parameters->extradata_size < 23 || !RemuxCodecTag.ParameterSetsOnlyInBand(parameters))
            return false;
        // 同一源文件只打开一次；只读文件头，不分析流信息（打开 4K 文件时分析流信息要解码画面）。
        var inputs = new Dictionary<string, FormatContext>(MediaFileUtil.PathComparer);
        try
        {
            var sets = new List<byte[]>();
            foreach (var (path, streamIndex, start) in starts)
            {
                token.ThrowIfCancellationRequested();
                if (!inputs.TryGetValue(path, out var input))
                    inputs[path] = input = InterruptibleInputFormatContext.Open(path, () => token.IsCancellationRequested);
                var found = Read(input, streamIndex, start, token);
                if (found is null)
                    return false;
                foreach (var set in found)
                {
                    var type = VideoBitstream.NalType(set, true);
                    if (VideoBitstream.ParameterSetId(set, true) is not { } id)
                        return false;
                    var existing = sets.Find(item => VideoBitstream.NalType(item, true) == type &&
                                                     VideoBitstream.ParameterSetId(item, true) == id);
                    if (existing is null)
                        VideoBitstream.AddParameterSet(set, true, sets);
                    else if (!existing.AsSpan().SequenceEqual(set))
                        return false;
                }
            }
            if (sets.Count == 0)
                return false;
            HevcMp4PacketConverter.ReplaceConfiguration(parameters, VideoBitstream.ToAnnexB([], 0, true, sets));
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (FFmpegException)
        {
            token.ThrowIfCancellationRequested();
            return false;
        }
        finally
        {
            foreach (var input in inputs.Values)
                input.Dispose();
        }
    }

    /// <summary>读取起点关键帧中完整的 VPS、SPS、PPS；不能用其他关键帧的定义代替。</summary>
    private static List<byte[]>? Read(FormatContext input, int streamIndex, double? start, CancellationToken token)
    {
        AVFormatContext* raw = input;
        if (streamIndex < 0 || streamIndex >= raw->nb_streams)
            return null;
        var stream = raw->streams[streamIndex];
        var lengthSize = VideoBitstream.ReadParameterSets(
            new ReadOnlySpan<byte>(stream->codecpar->extradata, stream->codecpar->extradata_size), true, []);
        if (lengthSize == 0)
            return null;
        // 只读取这一路视频，其余轨道的数据直接跳过。
        for (var index = 0; index < raw->nb_streams; index++)
            raw->streams[index]->discard = index == streamIndex ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;
        var timeBase = stream->time_base;
        if (start is { } seconds)
            input.SeekFrame((long)Math.Round(seconds * timeBase.den / timeBase.num), streamIndex);
        var packets = 0;
        foreach (var packet in input.ReadPackets())
        {
            token.ThrowIfCancellationRequested();
            if (++packets > MaximumPackets)
                break;
            AVPacket* p = packet;
            if (p->stream_index != streamIndex)
                continue;
            if (start is { } target)
            {
                if ((p->flags & 1) == 0 || p->pts == FF.AV_NOPTS_VALUE)
                    continue;
                var pts = p->pts * (double)timeBase.num / timeBase.den;
                if (pts < target - 0.002)
                    continue;
                if (pts > target + 0.002)
                    return null;
            }
            else if ((p->flags & 1) == 0)
                return null;
            var data = new ReadOnlySpan<byte>(p->data, p->size);
            var sets = new List<byte[]>();
            foreach (var range in VideoBitstream.EnumerateNals(data, lengthSize))
                VideoBitstream.RecordParameterSet(data[range], true, sets);
            return sets.Select(set => VideoBitstream.NalType(set, true)).Distinct().Count() == 3 ? sets : null;
        }
        return null;
    }
}
