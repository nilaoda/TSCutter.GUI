using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Media;
using TSCutter.GUI.Utils.Media;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Services.Media;

/// <summary>按独立轨道的实际时间轴确定最短输出，不能用整个源文件的时长替代轨道时长。</summary>
internal static unsafe class MediaMuxShortest
{
    public static double FindKnownEnd(FormatContext input, IReadOnlyList<MediaMuxTrack> tracks,
        double origin, bool includeSubtitles)
    {
        var end = double.PositiveInfinity;
        foreach (var track in tracks)
        {
            if (Participates(track, includeSubtitles) && TryGetEnd(input.Streams[track.Stream.Index], origin, out var known))
                end = Math.Min(end, known - origin + (double)(track.OffsetMilliseconds / 1000m));
        }
        return end;
    }

    public static double FindEnd(FormatContext input, IReadOnlyList<MediaMuxTrack> tracks,
        double origin, bool includeSubtitles, double upperBound, CancellationToken token)
    {
        var participants = tracks.Where(track => Participates(track, includeSubtitles)).ToArray();
        if (participants.Length == 0)
            return double.PositiveInfinity;
        var ends = new Dictionary<int, double>();
        foreach (var track in participants)
        {
            if (TryGetEnd(input.Streams[track.Stream.Index], origin, out var end))
            {
                ends.Add(track.Stream.Index, end);
                upperBound = Math.Min(upperBound, end - origin + (double)(track.OffsetMilliseconds / 1000m));
            }
        }
        var missing = participants.Where(track => !ends.ContainsKey(track.Stream.Index)).ToArray();
        if (missing.Length > 0)
        {
            // 字幕等轨道可能没有时长。仅在 shortest 开启且缺少时长时读取数据包，避免缓存稀疏字幕间的音视频。
            using var scan = InterruptibleInputFormatContext.Open(participants[0].Source.Path, () => token.IsCancellationRequested);
            scan.LoadStreamInfo();
            AVFormatContext* raw = scan;
            var offsets = missing.ToDictionary(track => track.Stream.Index, track => (double)(track.OffsetMilliseconds / 1000m) - origin);
            var indices = offsets.Keys.ToHashSet();
            for (var index = 0; index < raw->nb_streams; index++)
            {
                if (!indices.Contains(index))
                    raw->streams[index]->discard = AVDiscard.AVDISCARD_ALL;
            }
            using var packet = new Packet();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                packet.Unref();
                var code = scan.ReadPacket(packet);
                if (code == FF.AVERROR_EOF)
                    break;
                FFmpegException.Check(code);
                AVPacket* data = packet;
                if (!indices.Contains(data->stream_index))
                    continue;
                var timestamp = data->pts != FF.AV_NOPTS_VALUE ? data->pts : data->dts;
                if (timestamp == FF.AV_NOPTS_VALUE)
                    throw new MediaMuxException(MediaMuxError.MissingTimestamp);
                var timeBase = raw->streams[data->stream_index]->time_base;
                var end = (timestamp + (double)Math.Max(0, data->duration)) * timeBase.num / timeBase.den;
                ends[data->stream_index] = Math.Max(ends.GetValueOrDefault(data->stream_index, double.NegativeInfinity), end);
                // 该轨道已有数据延伸到已知截止点，就不可能再缩短输出，无需继续寻找它的真实 EOF。
                if (end + offsets[data->stream_index] >= upperBound)
                {
                    indices.Remove(data->stream_index);
                    raw->streams[data->stream_index]->discard = AVDiscard.AVDISCARD_ALL;
                    if (indices.Count == 0)
                        break;
                }
            }
        }
        return participants.Min(track => ends.GetValueOrDefault(track.Stream.Index, 0) - origin +
            (double)(track.OffsetMilliseconds / 1000m));
    }

    private static bool Participates(MediaMuxTrack track, bool includeSubtitles) => track.Stream.IsTimed &&
        (track.Stream.Type is AVMediaType.AVMEDIA_TYPE_VIDEO or AVMediaType.AVMEDIA_TYPE_AUDIO ||
         includeSubtitles && track.Stream.Type == AVMediaType.AVMEDIA_TYPE_SUBTITLE);

    private static bool TryGetEnd(MediaStream stream, double origin, out double end)
    {
        if (stream.Duration > 0)
        {
            var start = stream.StartTime == FF.AV_NOPTS_VALUE ? origin :
                stream.StartTime * (double)stream.TimeBase.num / stream.TimeBase.den;
            end = start + stream.Duration * (double)stream.TimeBase.num / stream.TimeBase.den;
            return true;
        }
        // Matroska 的 DURATION 已包含起点，不能再加 start_time。
        end = 0;
        return stream.Metadata.TryGetValue("DURATION", out var duration) && TryParseEnd(duration, out end);
    }

    private static bool TryParseEnd(string text, out double end)
    {
        // FFmpeg 写入九位小数，超出 TimeSpan 的七位小数精度，按时、分、秒分别解析。
        var parts = text.Split(':');
        end = 0;
        if (parts.Length != 3 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || minutes >= 60 ||
            !double.TryParse(parts[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds is < 0 or >= 60)
            return false;
        end = hours * 3600d + minutes * 60 + seconds;
        return end > 0;
    }
}
