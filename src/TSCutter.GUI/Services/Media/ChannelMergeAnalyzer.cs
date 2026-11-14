using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.Extensions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Discs;
using TSCutter.GUI.Player.Audio;
using TSCutter.GUI.Services.Editing.ChannelMerge;
using TSCutter.GUI.Utils.Media;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Services.Media;

/// <summary>一条音轨一个声道的抽样结果。</summary>
/// <param name="Peak">采样绝对值的最大值（满刻度为 1）。</param>
/// <param name="Rms">均方根（满刻度为 1）。</param>
/// <param name="HighShare">150 Hz 以上的能量占全部能量的比例；只有低频的声道（LFE）很小。</param>
/// <param name="Crest">峰值与均方根之比；正弦测试音约为 1.41。</param>
internal readonly record struct ChannelMergeChannelLevel(double Peak, double Rms, double HighShare, double Crest)
{
    public bool Silent => Peak < AudioTrackLevel.SilenceThreshold;
    /// <summary>只有低频：150 Hz 以上的能量比全部能量低 20 dB 以上。</summary>
    public bool LowOnly => !Silent && HighShare < 0.01;
    /// <summary>像单一频率的测试音（峰均比接近正弦波的 1.41；节目声通常在 3 以上）。</summary>
    public bool Tone => !Silent && Crest < 2.0;
    public double? PeakDb => Peak > 0 ? 20 * Math.Log10(Peak) : null;
    public double? RmsDb => Rms > 0 ? 20 * Math.Log10(Rms) : null;
}

/// <summary>一条音轨的抽样结果。</summary>
/// <param name="StereoCorrelation">左右声道的相关系数；接近 1 时两路相同（单声道）。</param>
/// <param name="DuplicateOf">与它内容相同的另一条音轨（PID）；没有时为 null。</param>
internal sealed record ChannelMergeTrackLevel(int Pid, bool Decodable, ChannelMergeChannelLevel Left, ChannelMergeChannelLevel Right,
    double StereoCorrelation, int? DuplicateOf)
{
    public bool Silent => Left.Silent && Right.Silent;
    public bool Mono => !Silent && StereoCorrelation > 0.999;
    public bool Tone => (Left.Tone || Left.Silent) && (Right.Tone || Right.Silent) && !Silent;
    public double Rms => Math.Sqrt((Left.Rms * Left.Rms + Right.Rms * Right.Rms) / 2);
    public double HighShare => (Left.HighShare + Right.HighShare) / 2;
}

/// <summary>分析得出的结论。</summary>
internal enum ChannelMergeVerdict
{
    /// <summary>有 LFE 特征，能推出映射。</summary>
    Suggested,
    /// <summary>所选音轨中有静音、重复或单声道的，不像拆分的 5.1。</summary>
    NotSurround,
    /// <summary>全是测试音，无法判断。</summary>
    ToneOnly,
    /// <summary>没有只有低频的声道（LFE 可能整段静音），无法推出映射。</summary>
    Undetermined
}

/// <summary>分析结果与建议的映射（按槽位顺序的来源；没有建议时为 null）。</summary>
internal sealed record ChannelMergeAnalysis(IReadOnlyList<ChannelMergeTrackLevel> Tracks, ChannelMergeVerdict Verdict,
    IReadOnlyList<(int Pid, bool Swap)>? Suggestion);

/// <summary>
/// 多轨合并的声道分析：与静音检测一样在全片均匀抽取若干段解码各音轨，按时间戳对齐后统计每个声道的电平、
/// 150 Hz 以上能量的占比（LFE 只有低频）、峰均比（测试音），以及左右声道、音轨之间的相关性（单声道、重复的音轨）。
/// 结果只用于提示和建议映射，是否合并由用户决定。
/// </summary>
internal static class ChannelMergeAnalyzer
{
    private const int Rate = ChannelMergeTranscoder.OutputRate;
    private const int MaxFailures = 64;

    public static ChannelMergeAnalysis Analyze(string path, DiscInput? disc, long tsSyncOffset, IReadOnlyList<int> pids,
        IReadOnlyList<int> selected, IProgress<double>? progress, CancellationToken token)
    {
        using var input = disc is not null
            ? InterruptibleInputFormatContext.Open(disc, static () => false)
            : InterruptibleInputFormatContext.Open(path, static () => false, tsSyncOffset);
        input.LoadStreamInfo();
        var tracks = new List<Track>();
        try
        {
            foreach (var pid in pids.Distinct())
            {
                var stream = input.Streams.FirstOrDefault(item => item.Id == pid &&
                                                                  item.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_AUDIO);
                if (!stream.IsValid)
                    continue;
                tracks.Add(new Track(pid, stream, AudioPlaybackPipeline.TryOpenDecoder(stream)));
            }
            if (tracks.Any(track => track.Decoder is not null))
                Sample(input, tracks, AudioSampleWindows.ByteSeekSize(input, path, disc), progress, token);
            progress?.Report(1);
            var levels = tracks.Select(track => track.Result()).ToList();
            MarkDuplicates(tracks, levels);
            return Conclude(levels, selected);
        }
        finally
        {
            foreach (var track in tracks)
                track.Dispose();
        }
    }

    private static void Sample(FormatContext input, List<Track> tracks, long bytes, IProgress<double>? progress,
        CancellationToken token)
    {
        var video = input.Streams.FirstOrDefault(stream =>
            stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_VIDEO && !stream.IsAttachedPicture);
        var clock = video.IsValid ? video : tracks[0].Stream;
        var windows = new AudioSampleWindows(input, clock, bytes);
        var byIndex = tracks.Where(track => track.Decoder is not null).ToDictionary(track => track.Stream.Index);
        using var frame = new Frame();
        for (var window = 0; window < AudioSampleWindows.Count; window++)
        {
            token.ThrowIfCancellationRequested();
            windows.Seek(window);
            foreach (var track in byIndex.Values)
                track.BeginWindow(windows.From);
            foreach (var packet in input.ReadPackets())
            {
                token.ThrowIfCancellationRequested();
                var known = byIndex.TryGetValue(packet.StreamIndex, out var track);
                var timeBase = known ? track!.Stream.TimeBase : packet.StreamIndex == clock.Index ? clock.TimeBase : default;
                double? time = packet.Pts != FF.AV_NOPTS_VALUE && timeBase.den != 0
                    ? AudioSampleWindows.Seconds(packet.Pts, timeBase) : null;
                // 按字节定位时以读到的第一个所选音轨的包为参照（视频可能与音频错开）。
                if (windows.Observe(known ? time : null))
                {
                    foreach (var item in byIndex.Values)
                        item.SetStart(windows.From);
                }
                if (windows.IsDone(time))
                    break;
                if (!known || track!.Failures >= MaxFailures)
                    continue;
                try
                {
                    foreach (var decoded in track.Decoder!.DecodePacket(packet, frame, preserveRemainingOutput: true))
                        track.Accept(decoded);
                }
                catch (FFmpegException)
                {
                    track.Failures++;
                }
            }
            foreach (var track in byIndex.Values)
                track.EndWindow();
            progress?.Report((window + 1) / (double)AudioSampleWindows.Count);
        }
    }

    /// <summary>音轨之间的重复：左声道在抽样中逐样本高度相关（相关系数 0.99 以上）且电平相近。</summary>
    private static void MarkDuplicates(IReadOnlyList<Track> tracks, List<ChannelMergeTrackLevel> levels)
    {
        for (var i = 0; i < tracks.Count; i++)
        {
            if (levels[i].Silent)
                continue;
            for (var j = 0; j < i; j++)
            {
                if (levels[j].Silent || levels[j].DuplicateOf is not null)
                    continue;
                var correlation = Track.Correlation(tracks[i], tracks[j]);
                var ratio = levels[i].Rms / Math.Max(levels[j].Rms, 1e-12);
                if (correlation > 0.99 && ratio is > 0.8 and < 1.25)
                {
                    levels[i] = levels[i] with { DuplicateOf = tracks[j].Pid };
                    break;
                }
            }
        }
    }

    /// <summary>
    /// 结论与建议：所选三条音轨中有静音、重复或单声道的，不像拆分的 5.1；全是测试音时无法判断。
    /// 否则找一侧只有低频的音轨作中置与低音（LFE 在左时互换），其余两条中电平高、高频多的作前置，另一条作环绕。
    /// </summary>
    internal static ChannelMergeAnalysis Conclude(IReadOnlyList<ChannelMergeTrackLevel> levels, IReadOnlyList<int> selected)
    {
        var chosen = selected.Select(pid => levels.FirstOrDefault(level => level.Pid == pid)).ToArray();
        // 同一条音轨放进了多个槽位：推不出映射。
        if (chosen.Length != ChannelMergeMixer.SlotCount || selected.Distinct().Count() != chosen.Length ||
            chosen.Any(level => level is null || !level.Decodable))
            return new ChannelMergeAnalysis(levels, ChannelMergeVerdict.Undetermined, null);
        if (chosen.All(level => level!.Tone || level.Silent) && chosen.Any(level => level!.Tone))
            return new ChannelMergeAnalysis(levels, ChannelMergeVerdict.ToneOnly, null);
        if (chosen.Any(level => level!.Silent || level.Mono ||
                                level.DuplicateOf is { } other && selected.Contains(other)))
            return new ChannelMergeAnalysis(levels, ChannelMergeVerdict.NotSurround, null);
        var lfe = chosen.FirstOrDefault(level => level!.Left.LowOnly != level.Right.LowOnly);
        if (lfe is null)
            return new ChannelMergeAnalysis(levels, ChannelMergeVerdict.Undetermined, null);
        var rest = chosen.Where(level => !ReferenceEquals(level, lfe))
            .OrderByDescending(level => level!.Rms * Math.Sqrt(level.HighShare)).ToArray();
        // 中置在左、LFE 在右是常见的顺序；LFE 在左时互换。
        IReadOnlyList<(int, bool)> suggestion = [(rest[0]!.Pid, false), (lfe.Pid, lfe.Left.LowOnly), (rest[1]!.Pid, false)];
        return new ChannelMergeAnalysis(levels, ChannelMergeVerdict.Suggested, suggestion);
    }

    /// <summary>一条音轨的抽样：每段的左右声道按时间戳放进固定长度的缓冲，段结束时累计统计。</summary>
    private sealed class Track(int pid, MediaStream stream, CodecContext? decoder) : IDisposable
    {
        private static readonly int WindowSamples = (int)(AudioSampleWindows.WindowSeconds * Rate);
        private readonly StereoResampler resampler = new();
        private readonly float[] left = new float[WindowSamples];
        private readonly float[] right = new float[WindowSamples];
        private readonly bool[] filled = new bool[WindowSamples];
        // 各段保留的左声道（对齐到段内位置），用于音轨之间比较。
        private readonly List<(float[] Left, bool[] Filled)> windows = [];
        private double windowStart;
        // 上一帧之后的时间（秒），给没有时间戳的帧用。
        private double nextTime = double.NaN;
        private double peakLeft, peakRight;
        private double sumLeft, sumRight, sumCross, highLeft, highRight;
        private long count;
        private bool decoded;

        public int Pid { get; } = pid;
        public MediaStream Stream { get; } = stream;
        public CodecContext? Decoder { get; } = decoder;
        public int Failures { get; set; }

        public void SetStart(double start) => windowStart = start;

        public void BeginWindow(double start)
        {
            windowStart = start;
            nextTime = double.NaN;
            Array.Clear(left);
            Array.Clear(right);
            Array.Clear(filled);
            Decoder?.FlushBuffers();
        }

        public void Accept(Frame frame)
        {
            // 一个包解出多帧时后面的帧可能没有时间戳：按尽力时间戳，再没有就接着上一帧。
            var timestamp = frame.BestEffortTimestamp != FF.AV_NOPTS_VALUE ? frame.BestEffortTimestamp : frame.Pts;
            var time = timestamp != FF.AV_NOPTS_VALUE ? AudioSampleWindows.Seconds(timestamp, Stream.TimeBase) : nextTime;
            if (double.IsNaN(time))
                return;
            var (l, r) = resampler.Convert(frame);
            nextTime = time + l.Length / (double)Rate;
            if (double.IsNaN(windowStart))
                return;
            var offset = (int)Math.Round((time - windowStart) * Rate);
            for (var i = 0; i < l.Length; i++)
            {
                var index = offset + i;
                if (index < 0 || index >= WindowSamples)
                    continue;
                left[index] = l[i];
                right[index] = r[i];
                filled[index] = true;
            }
            decoded = true;
        }

        public void EndWindow()
        {
            var highL = new HighPass();
            var highR = new HighPass();
            for (var i = 0; i < WindowSamples; i++)
            {
                if (!filled[i])
                    continue;
                double l = left[i], r = right[i];
                peakLeft = Math.Max(peakLeft, Math.Abs(l));
                peakRight = Math.Max(peakRight, Math.Abs(r));
                sumLeft += l * l;
                sumRight += r * r;
                sumCross += l * r;
                var hl = highL.Process(l);
                var hr = highR.Process(r);
                highLeft += hl * hl;
                highRight += hr * hr;
                count++;
            }
            windows.Add(((float[])left.Clone(), (bool[])filled.Clone()));
        }

        public ChannelMergeTrackLevel Result()
        {
            if (Decoder is null || !decoded && Failures > 0)
                return new ChannelMergeTrackLevel(Pid, false, default, default, 0, null);
            var n = Math.Max(1, count);
            var rmsLeft = Math.Sqrt(sumLeft / n);
            var rmsRight = Math.Sqrt(sumRight / n);
            var correlation = sumLeft > 0 && sumRight > 0 ? sumCross / Math.Sqrt(sumLeft * sumRight) : 0;
            return new ChannelMergeTrackLevel(Pid, true,
                new ChannelMergeChannelLevel(peakLeft, rmsLeft, sumLeft > 0 ? highLeft / sumLeft : 0, rmsLeft > 0 ? peakLeft / rmsLeft : 0),
                new ChannelMergeChannelLevel(peakRight, rmsRight, sumRight > 0 ? highRight / sumRight : 0, rmsRight > 0 ? peakRight / rmsRight : 0),
                correlation, null);
        }

        /// <summary>两条音轨左声道在各段重叠部分的相关系数。</summary>
        public static double Correlation(Track a, Track b)
        {
            double cross = 0, sa = 0, sb = 0;
            for (var w = 0; w < Math.Min(a.windows.Count, b.windows.Count); w++)
            {
                var (la, fa) = a.windows[w];
                var (lb, fb) = b.windows[w];
                for (var i = 0; i < la.Length; i++)
                {
                    if (!fa[i] || !fb[i])
                        continue;
                    cross += la[i] * (double)lb[i];
                    sa += la[i] * (double)la[i];
                    sb += lb[i] * (double)lb[i];
                }
            }
            return sa > 0 && sb > 0 ? cross / Math.Sqrt(sa * sb) : 0;
        }

        public void Dispose()
        {
            Decoder?.Dispose();
            resampler.Dispose();
        }
    }

    /// <summary>150 Hz 四阶高通（两节二阶巴特沃斯）。</summary>
    private sealed class HighPass
    {
        private readonly Biquad first = new();
        private readonly Biquad second = new();

        public double Process(double value) => second.Process(first.Process(value));

        private sealed class Biquad
        {
            private static readonly double B0, B1, B2, A1, A2;
            private double x1, x2, y1, y2;

            static Biquad()
            {
                const double frequency = 150;
                const double q = 0.7071067811865476;
                var w0 = 2 * Math.PI * frequency / Rate;
                var alpha = Math.Sin(w0) / (2 * q);
                var cos = Math.Cos(w0);
                var a0 = 1 + alpha;
                B0 = (1 + cos) / 2 / a0;
                B1 = -(1 + cos) / a0;
                B2 = (1 + cos) / 2 / a0;
                A1 = -2 * cos / a0;
                A2 = (1 - alpha) / a0;
            }

            public double Process(double x)
            {
                var y = B0 * x + B1 * x1 + B2 * x2 - A1 * y1 - A2 * y2;
                x2 = x1;
                x1 = x;
                y2 = y1;
                y1 = y;
                return y;
            }
        }
    }
}
