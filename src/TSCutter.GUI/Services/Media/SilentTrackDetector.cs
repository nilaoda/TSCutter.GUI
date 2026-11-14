using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.Extensions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Discs;
using TSCutter.GUI.Player.Audio;
using TSCutter.GUI.Utils.Media;
using TSCutter.GUI.Utils.Media.DolbyE;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Services.Media;

/// <summary>
/// 一条音轨的抽样结果。Peak 为各抽样段中采样绝对值的最大值（满刻度为 1）；Decodable 为假时运行库打不开该音轨的解码器，
/// 无法判断；抽样段中一个音频画面都没有解出时 Peak 为 0（音轨没有数据也算静音）。
/// 承载杜比 E 的 302M 音轨按杜比 E 解码，DolbyE 列出各声道组的峰值，全部声道组静音才算静音
/// （单个声道组不能单独删去）；302M 中没有杜比 E 时按普通 PCM 判断。
/// </summary>
internal readonly record struct AudioTrackLevel(int StreamIndex, bool Decodable, double Peak,
    IReadOnlyList<DolbyEProgramLevel>? DolbyE = null)
{
    /// <summary>静音门限：-60 dBFS。广播中占位的空音轨通常低于 -90 dBFS，带底噪的也在 -65 dBFS 以下。</summary>
    public const double SilenceThreshold = 0.001;

    public bool Silent => Decodable && Peak < SilenceThreshold;

    /// <summary>峰值的 dBFS；没有声音时为 null。</summary>
    public double? PeakDb => Db(Peak);

    internal static double? Db(double peak) => peak > 0 ? 20 * Math.Log10(peak) : null;
}

/// <summary>杜比 E 一个声道组的峰值：Number 从 1 开始，Layout 为“5.1”“2.0”等。</summary>
internal readonly record struct DolbyEProgramLevel(int Number, string Layout, double Peak)
{
    public bool Silent => Peak < AudioTrackLevel.SilenceThreshold;
    public double? PeakDb => AudioTrackLevel.Db(Peak);
}

/// <summary>
/// 静音音轨检测：在全片均匀抽取若干段，每段只读很短的一段，同时解码各音轨并记录峰值。只在用户手动检测时运行，
/// 读取量与片长无关（约为抽样段数 × 每段时长）。音轨只在片中某段才有声音时，抽样可能全部落在无声处，结果仅供参考。
/// </summary>
internal static class SilentTrackDetector
{
    public const int Windows = 12;
    public const double WindowSeconds = 1.5;
    // 复用时音频与同一时刻的视频在文件中的位置可能错开约一秒，每段多读一些。
    private const double MarginSeconds = 1.5;
    private const int MaxFailures = 64;

    /// <summary>检测 streamIndices 中的各音轨；progress 报告已完成的比例（0–1）。</summary>
    public static IReadOnlyList<AudioTrackLevel> Analyze(string path, DiscInput? disc, long tsSyncOffset,
        IReadOnlyCollection<int> streamIndices, IProgress<double>? progress, CancellationToken token)
    {
        using var input = disc is not null
            ? InterruptibleInputFormatContext.Open(disc, static () => false)
            : InterruptibleInputFormatContext.Open(path, static () => false, tsSyncOffset);
        input.LoadStreamInfo();
        var tracks = new List<Track>();
        try
        {
            foreach (var index in streamIndices.Distinct())
            {
                if (index < 0 || index >= input.Streams.Count)
                    continue;
                var stream = input.Streams[index];
                if (stream.Codecpar?.CodecType != AVMediaType.AVMEDIA_TYPE_AUDIO)
                    continue;
                // 302M 可能承载杜比 E：同时按杜比 E 解码，解出杜比 E 时以它为准。
                tracks.Add(new Track(stream, AudioPlaybackPipeline.TryOpenDecoder(stream), DolbyEDecoder.TryCreate(stream)));
            }
            var decodable = tracks.Where(track => track.Decoder is not null || track.DolbyE is not null).ToList();
            if (decodable.Count > 0)
                Sample(input, decodable, progress, token);
            progress?.Report(1);
            return tracks.Select(Result).ToList();
        }
        finally
        {
            foreach (var track in tracks)
                track.Dispose();
        }
    }

    private static AudioTrackLevel Result(Track track)
    {
        if (track.CarriesDolbyE)
        {
            // 读到了杜比 E 帧头却没有解出画面（突发损坏等）：按 PCM 读到的是杜比 E 数据，不能当作声音的峰值。
            if (track.Programs.Count == 0)
                return new AudioTrackLevel(track.Stream.Index, false, 0);
            var programs = track.Programs.OrderBy(pair => pair.Key)
                .Select(pair => new DolbyEProgramLevel(pair.Key, pair.Value.Layout, pair.Value.Peak)).ToList();
            return new AudioTrackLevel(track.Stream.Index, true, programs.Max(program => program.Peak), programs);
        }
        // 解码一直失败、什么都没解出的音轨（运行库不支持其中的编码方式）无法判断。
        return new AudioTrackLevel(track.Stream.Index,
            track.Decoder is not null && (track.Decoded || track.Failures == 0), track.Peak);
    }

    private static void Sample(FormatContext input, List<Track> tracks, IProgress<double>? progress,
        CancellationToken token)
    {
        var video = input.Streams.FirstOrDefault(stream =>
            stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_VIDEO && !stream.IsAttachedPicture);
        // 按视频流定位（与预览相同）；没有视频时按第一条音轨。
        var clock = video.IsValid ? video : tracks[0].Stream;
        var (start, duration) = Range(input, clock);
        var byIndex = tracks.ToDictionary(track => track.Stream.Index);
        using var frame = new Frame();
        for (var window = 0; window < Windows; window++)
        {
            token.ThrowIfCancellationRequested();
            var from = start + duration * (window + 0.5) / Windows - WindowSeconds / 2;
            var to = from + WindowSeconds;
            input.SeekFrame(Ticks(Math.Max(start, from - MarginSeconds), clock.TimeBase), clock.Index);
            foreach (var track in tracks)
            {
                track.Decoder?.FlushBuffers();
                track.DolbyE?.Flush();
            }
            foreach (var packet in input.ReadPackets())
            {
                token.ThrowIfCancellationRequested();
                var known = byIndex.TryGetValue(packet.StreamIndex, out var track);
                if (packet.Pts != FF.AV_NOPTS_VALUE)
                {
                    var timeBase = known ? track!.Stream.TimeBase : packet.StreamIndex == clock.Index ? clock.TimeBase : default;
                    if (timeBase.den != 0 && Seconds(packet.Pts, timeBase) > to + MarginSeconds)
                        break;
                    if (known && Seconds(packet.Pts, timeBase) < from)
                        continue;
                }
                if (!known)
                    continue;
                // 杜比 E：按帧头的声道组配置分别记录各声道组的峰值（损坏的突发由解码器跳过）。
                track!.DolbyE?.Decode(packet, (decoded, header) => track.Accumulate(decoded, header));
                // 已认出杜比 E 的音轨不再按 PCM 解码（解出的是杜比 E 数据）。
                if (track.Decoder is null || track.CarriesDolbyE || track.Failures >= MaxFailures)
                    continue;
                try
                {
                    foreach (var decoded in track.Decoder.DecodePacket(packet, frame, preserveRemainingOutput: true))
                    {
                        track.Peak = Math.Max(track.Peak, Peak(decoded));
                        track.Decoded = true;
                    }
                }
                catch (FFmpegException)
                {
                    // 定位可能落在音频 PES 中间，残包无法解码；解码器不支持的包同样跳过。失败太多次的音轨不再解码。
                    track.Failures++;
                }
            }
            progress?.Report((window + 1) / (double)Windows);
        }
    }

    /// <summary>可抽样的时间范围（秒）：流的起点与时长，缺失时取文件的。</summary>
    private static unsafe (double Start, double Duration) Range(FormatContext input, MediaStream clock)
    {
        AVFormatContext* raw = input;
        var start = clock.StartTime != FF.AV_NOPTS_VALUE ? Seconds(clock.StartTime, clock.TimeBase)
            : raw->start_time != FF.AV_NOPTS_VALUE ? raw->start_time / 1_000_000.0 : 0;
        var duration = clock.Duration > 0 ? Seconds(clock.Duration, clock.TimeBase)
            : raw->duration > 0 ? raw->duration / 1_000_000.0 : WindowSeconds * Windows;
        return (start, Math.Max(duration, WindowSeconds));
    }

    private static double Seconds(long value, AVRational timeBase) => value * (double)timeBase.num / timeBase.den;

    private static long Ticks(double seconds, AVRational timeBase) =>
        (long)Math.Floor(seconds * timeBase.den / timeBase.num);

    /// <summary>一个解码画面中各声道采样绝对值的最大值（满刻度为 1）。</summary>
    private static unsafe double Peak(Frame decoded)
    {
        AVFrame* frame = decoded;
        var channels = frame->ch_layout.nb_channels;
        var samples = frame->nb_samples;
        if (channels <= 0 || samples <= 0)
            return 0;
        var format = (AVSampleFormat)frame->format;
        var planar = FF.av_sample_fmt_is_planar(format) != 0;
        var planes = planar ? channels : 1;
        var count = planar ? samples : samples * channels;
        double peak = 0;
        for (var plane = 0; plane < planes; plane++)
        {
            var data = frame->extended_data[plane];
            if (data == null)
                continue;
            peak = Math.Max(peak, format switch
            {
                AVSampleFormat.AV_SAMPLE_FMT_U8 or AVSampleFormat.AV_SAMPLE_FMT_U8P => PeakU8(data, count),
                AVSampleFormat.AV_SAMPLE_FMT_S16 or AVSampleFormat.AV_SAMPLE_FMT_S16P => PeakS16((short*)data, count),
                AVSampleFormat.AV_SAMPLE_FMT_S32 or AVSampleFormat.AV_SAMPLE_FMT_S32P => PeakS32((int*)data, count),
                AVSampleFormat.AV_SAMPLE_FMT_FLT or AVSampleFormat.AV_SAMPLE_FMT_FLTP => PeakFloat((float*)data, count),
                AVSampleFormat.AV_SAMPLE_FMT_DBL or AVSampleFormat.AV_SAMPLE_FMT_DBLP => PeakDouble((double*)data, count),
                AVSampleFormat.AV_SAMPLE_FMT_S64 or AVSampleFormat.AV_SAMPLE_FMT_S64P => PeakS64((long*)data, count),
                _ => 0
            });
        }
        return Math.Min(peak, 1);
    }

    private static unsafe double PeakU8(byte* data, int count)
    {
        var peak = 0;
        for (var i = 0; i < count; i++)
            peak = Math.Max(peak, Math.Abs(data[i] - 128));
        return peak / 128.0;
    }

    private static unsafe double PeakS16(short* data, int count)
    {
        var peak = 0;
        for (var i = 0; i < count; i++)
            peak = Math.Max(peak, Math.Abs((int)data[i]));
        return peak / 32768.0;
    }

    private static unsafe double PeakS32(int* data, int count)
    {
        long peak = 0;
        for (var i = 0; i < count; i++)
            peak = Math.Max(peak, Math.Abs((long)data[i]));
        return peak / 2147483648.0;
    }

    private static unsafe double PeakS64(long* data, int count)
    {
        double peak = 0;
        for (var i = 0; i < count; i++)
            peak = Math.Max(peak, Math.Abs((double)data[i]));
        return peak / 9223372036854775808.0;
    }

    private static unsafe double PeakFloat(float* data, int count)
    {
        float peak = 0;
        for (var i = 0; i < count; i++)
            peak = Math.Max(peak, Math.Abs(data[i]));
        return float.IsFinite(peak) ? peak : 0;
    }

    private static unsafe double PeakDouble(double* data, int count)
    {
        double peak = 0;
        for (var i = 0; i < count; i++)
            peak = Math.Max(peak, Math.Abs(data[i]));
        return double.IsFinite(peak) ? peak : 0;
    }

    private sealed class Track(MediaStream stream, CodecContext? decoder, DolbyEDecoder? dolbyE) : IDisposable
    {
        public MediaStream Stream { get; } = stream;
        public CodecContext? Decoder { get; } = decoder;
        public DolbyEDecoder? DolbyE { get; } = dolbyE;
        public double Peak { get; set; }
        public bool Decoded { get; set; }
        public int Failures { get; set; }
        /// <summary>302M 中读到了杜比 E 帧头：此后按杜比 E 判断。</summary>
        public bool CarriesDolbyE => DolbyE is { HeaderCount: > 0 };
        /// <summary>杜比 E 各声道组（按编号）的布局与峰值；没有解出杜比 E 时为空。</summary>
        public Dictionary<int, (string Layout, double Peak)> Programs { get; } = [];

        /// <summary>杜比 E 解码器输出整帧（fltp，所有声道），按声道组配置取各组声道的峰值。</summary>
        public unsafe void Accumulate(Frame decoded, DolbyEFrameHeader header)
        {
            AVFrame* raw = decoded;
            if ((AVSampleFormat)raw->format != AVSampleFormat.AV_SAMPLE_FMT_FLTP || raw->nb_samples <= 0)
                return;
            foreach (var program in DolbyEConfiguration.GetPrograms(header.ProgramConfig))
            {
                double peak = 0;
                for (var channel = program.FirstChannel; channel <= program.LastChannel; channel++)
                {
                    var index = DolbyEConfiguration.DecoderIndex(header.ProgramConfig, channel);
                    if (index < raw->ch_layout.nb_channels && raw->extended_data[index] != null)
                        peak = Math.Max(peak, PeakFloat((float*)raw->extended_data[index], raw->nb_samples));
                }
                var previous = Programs.TryGetValue(program.Number, out var known) ? known.Peak : 0;
                Programs[program.Number] = (program.LayoutName, Math.Max(previous, Math.Min(peak, 1)));
            }
        }

        public void Dispose()
        {
            Decoder?.Dispose();
            DolbyE?.Dispose();
        }
    }
}
