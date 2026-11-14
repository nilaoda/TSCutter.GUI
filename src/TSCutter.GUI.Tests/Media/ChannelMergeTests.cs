using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Media;
using TSCutter.GUI.Player.Audio;
using TSCutter.GUI.Services.Editing.AudioRebuild;
using TSCutter.GUI.Services.Media;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Utils.Media;
using Xunit;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Tests.Media;

public sealed class ChannelMergeTests
{
    // 可选真实素材：三条立体声承载 5.1 的 TS（完整路径），参见 README。
    private const string SampleVariable = "TSCUTTER_CHANNEL_MERGE_SAMPLE";
    // 合成素材中每个声道的正弦频率（L R C LFE Ls Rs）。
    private static readonly int[] Frequencies = [440, 550, 660, 50, 770, 880];

    [Fact]
    public void SplitterCutsMpegFramesAcrossPesAndGivesEachPesPtsToItsFirstFrame()
    {
        // MPEG-1 Layer II、256 kbps、48 kHz：每帧 768 字节。
        var frames = Enumerable.Range(0, 3).Select(index =>
        {
            var frame = new byte[768];
            frame[0] = 0xFF;
            frame[1] = 0xFD;
            frame[2] = 0xC4;
            frame[3] = (byte)index;
            return frame;
        }).ToArray();
        var stream = frames.SelectMany(frame => frame).ToArray();
        var splitter = HeaderFrameSplitter.TryCreate(AVCodecID.AV_CODEC_ID_MP2)!;
        var taken = new List<(byte Index, long Pts)>();
        // 第一个 PES：第 0 帧与第 1 帧的前半；第二个 PES：第 1 帧的后半与第 2 帧。
        splitter.Append(stream.AsSpan(0, 768 + 300), 1000);
        while (splitter.TryTake(out var frame, out var pts))
            taken.Add((frame[3], pts));
        splitter.Append(stream.AsSpan(768 + 300), 3160);
        while (splitter.TryTake(out var frame, out var pts))
            taken.Add((frame[3], pts));
        Assert.Equal(new (int, long)[] { (0, 1000), (1, FF.AV_NOPTS_VALUE), (2, 3160) }, taken.Select(item => ((int)item.Index, item.Pts)));
    }

    [Fact]
    public void ConclusionFindsLfeAndOrdersFrontBeforeSurround()
    {
        var levels = new[]
        {
            Track(0x101, Channel(0.07), Channel(0.07)),
            Track(0x102, Channel(0.01), Channel(0.0003, lowOnly: true)),
            Track(0x103, Channel(0.02, high: 0.2), Channel(0.02, high: 0.2))
        };
        // 当前映射顺序打乱：建议仍按特征排好，并且 LFE 在右不需要互换。
        var analysis = ChannelMergeAnalyzer.Conclude(levels, [0x103, 0x101, 0x102]);
        Assert.Equal(ChannelMergeVerdict.Suggested, analysis.Verdict);
        Assert.Equal(new (int, bool)[] { (0x101, false), (0x102, false), (0x103, false) }, analysis.Suggestion!);

        // LFE 在左：建议互换。
        levels[1] = Track(0x102, Channel(0.0003, lowOnly: true), Channel(0.01));
        Assert.Equal((0x102, true), ChannelMergeAnalyzer.Conclude(levels, [0x101, 0x102, 0x103]).Suggestion![1]);

        // 同一条音轨放进了两个槽位：推不出映射，不报错。
        var repeated = ChannelMergeAnalyzer.Conclude(levels, [0x102, 0x102, 0x103]);
        Assert.Equal(ChannelMergeVerdict.Undetermined, repeated.Verdict);
        Assert.Null(repeated.Suggestion);
    }

    [Fact]
    public void ConclusionRejectsSilentDuplicateMonoAndToneTracks()
    {
        var silent = Track(0x24, default, default);
        var mono = Track(0x22, Channel(0.05), Channel(0.05)) with { StereoCorrelation = 1 };
        var duplicate = Track(0x23, Channel(0.05), Channel(0.05)) with { StereoCorrelation = 1, DuplicateOf = 0x22 };
        Assert.Equal(ChannelMergeVerdict.NotSurround,
            ChannelMergeAnalyzer.Conclude([mono, duplicate, silent], [0x22, 0x23, 0x24]).Verdict);

        var tones = new[] { 0x22, 0x23, 0x25 }.Select(pid => Track(pid, Channel(0.05, crest: 1.41), Channel(0.05, crest: 1.41))).ToArray();
        Assert.Equal(ChannelMergeVerdict.ToneOnly, ChannelMergeAnalyzer.Conclude(tones, [0x22, 0x23, 0x25]).Verdict);

        // 没有只有低频的声道：无法推出映射。
        var plain = new[] { 1, 2, 3 }.Select(pid => Track(pid, Channel(0.05), Channel(0.04))).ToArray();
        Assert.Equal(ChannelMergeVerdict.Undetermined, ChannelMergeAnalyzer.Conclude(plain, [1, 2, 3]).Verdict);
    }

    [NativeMediaFact(group: "remux")]
    public void AnalyzerSuggestsTheGeneratedMapping()
    {
        var path = Fixture();
        var analysis = ChannelMergeAnalyzer.Analyze(path, null, 0, [0x101, 0x102, 0x103], [0x103, 0x102, 0x101], null,
            CancellationToken.None);
        Assert.Equal(ChannelMergeVerdict.Suggested, analysis.Verdict);
        Assert.Equal(new (int, bool)[] { (0x101, false), (0x102, false), (0x103, false) }, analysis.Suggestion!);
        var lfe = analysis.Tracks.Single(track => track.Pid == 0x102);
        Assert.True(lfe.Right.LowOnly);
        Assert.False(lfe.Left.LowOnly);
    }

    [NativeMediaTheory(group: "remux")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void MergedTrackCarriesEachSourceChannel(bool keepOriginal, bool swapFront)
    {
        var source = Fixture();
        var directory = Path.Combine(Path.GetTempPath(), $"tscutter-merge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var output = Path.Combine(directory, "out.ts");
            var plan = new AudioRebuildPlan
            {
                Merges =
                [
                    new ChannelMergeSettings
                    {
                        ProgramId = 1, Merge = true, KeepOriginal = keepOriginal,
                        Sources =
                        [
                            new ChannelMergeSource { Pid = 0x101, Swap = swapFront },
                            new ChannelMergeSource { Pid = 0x102 },
                            new ChannelMergeSource { Pid = 0x103 }
                        ],
                        Outputs =
                        [
                            new AudioOutput { Codec = AudioOutputCodec.Ac3, BitrateKbps = 448, Language = "eng" },
                            new AudioOutput { Codec = AudioOutputCodec.Aac, Layout = AudioOutputLayout.Stereo, BitrateKbps = 192 },
                            new AudioOutput { Codec = AudioOutputCodec.Flac }
                        ]
                    }
                ]
            };
            var setup = Rebuild(plan, source, output);
            Assert.Null(setup.Error);
            Assert.True(setup.ChannelsMerged);
            Assert.False(setup.DolbyETranscoded);
            var sideFiles = setup.Commit();
            Assert.Equal(Path.Combine(directory, "out.51.flac"), Assert.Single(sideFiles));

            using var reopened = InterruptibleInputFormatContext.Open(output, static () => false);
            reopened.LoadStreamInfo();
            var audio = reopened.Streams.Where(stream => stream.Codecpar!.CodecType == AVMediaType.AVMEDIA_TYPE_AUDIO).ToArray();
            // 新音轨在前置来源的位置之前；替换时沿用前置来源的 PID，来源都去掉。
            Assert.Equal(keepOriginal ? new[] { 0x104, 0x105, 0x101, 0x102, 0x103 } : new[] { 0x101, 0x104 }, audio.Select(stream => stream.Id));
            var merged = audio[0];
            Assert.Equal(AVCodecID.AV_CODEC_ID_AC3, merged.Codecpar!.CodecId);
            Assert.Equal(6, merged.Codecpar.ChLayout.nb_channels);
            Assert.Equal(AVCodecID.AV_CODEC_ID_AAC, audio[1].Codecpar!.CodecId);

            int[] expected = swapFront ? [550, 440, 660, 50, 770, 880] : Frequencies;
            var channels = DecodeChannels(reopened, merged, 8, 1);
            for (var channel = 0; channel < 6; channel++)
                Assert.Equal(expected[channel], DominantFrequency(channels[channel], Frequencies));
            // 环绕来源晚 80 ms 开始：合并结果开头的这一段环绕声道是静音。
            var start = DecodeChannels(reopened, merged, 0, 0.06);
            Assert.True(Rms(start[4]) < 0.001, $"surround rms {Rms(start[4])}");
            Assert.True(Rms(start[0]) > 0.05, $"front rms {Rms(start[0])}");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [NativeMediaFact(sampleVariable: SampleVariable)]
    public void RealSampleIsAnalyzedAndMerged()
    {
        var source = Environment.GetEnvironmentVariable(SampleVariable)!;
        ChannelMergeProgramInfo program;
        using (var probe = InterruptibleInputFormatContext.Open(source, static () => false))
        {
            probe.LoadStreamInfo();
            program = Assert.Single(ChannelMergeProbe.Probe(probe));
        }
        var pids = program.Tracks.Select(track => track.Pid).ToArray();
        var analysis = ChannelMergeAnalyzer.Analyze(source, null, 0, pids, pids.Take(3).ToArray(), null, CancellationToken.None);
        Assert.Equal(ChannelMergeVerdict.Suggested, analysis.Verdict);
        var directory = Path.Combine(Path.GetTempPath(), $"tscutter-merge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var output = Path.Combine(directory, "out.ts");
            var plan = new AudioRebuildPlan
            {
                Merges =
                [
                    new ChannelMergeSettings
                    {
                        ProgramId = program.ProgramId, Merge = true,
                        Sources = analysis.Suggestion!.Select(item => new ChannelMergeSource { Pid = item.Pid, Swap = item.Swap }).ToList(),
                        Outputs = [new AudioOutput { Codec = AudioOutputCodec.Ac3, BitrateKbps = 448 }]
                    }
                ]
            };
            var setup = Rebuild(plan, source, output);
            Assert.Null(setup.Error);
            Assert.True(setup.ChannelsMerged);
            using var reopened = InterruptibleInputFormatContext.Open(output, static () => false);
            reopened.LoadStreamInfo();
            var merged = reopened.Streams.Single(stream => stream.Id == analysis.Suggestion![0].Pid);
            Assert.Equal(6, merged.Codecpar!.ChLayout.nb_channels);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Fixture() => Path.Combine(NativeTestEnvironment.FixtureDirectory("remux"), "channel-merge.ts");

    private static AudioRebuildSetup Rebuild(AudioRebuildPlan plan, string source, string output)
    {
        var setup = new AudioRebuildSetup(plan, source, output, null);
        TsOutputFile.Current = setup;
        try
        {
            // 像导出服务一样分块写入：块大小不定，包会跨越分块。
            using var stream = TsOutputFile.Create(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1,
                FileOptions.SequentialScan);
            var data = File.ReadAllBytes(source);
            var random = new Random(11);
            for (var offset = 0; offset < data.Length;)
            {
                var count = Math.Min(data.Length - offset, random.Next(1, 200_000));
                stream.Write(data, offset, count);
                offset += count;
            }
        }
        finally
        {
            TsOutputFile.Current = null;
        }
        return setup;
    }

    /// <summary>从合并音轨的起点之后 <paramref name="from"/> 秒起解码 <paramref name="seconds"/> 秒，按声道返回样本。</summary>
    private static unsafe float[][] DecodeChannels(FormatContext input, MediaStream stream, double from, double seconds)
    {
        input.SeekFrame(0, stream.Index);
        using var decoder = AudioPlaybackPipeline.TryOpenDecoder(stream)!;
        using var frame = new Frame();
        var channels = Enumerable.Range(0, 6).Select(_ => new List<float>()).ToArray();
        long first = FF.AV_NOPTS_VALUE;
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex != stream.Index)
                continue;
            foreach (var decoded in decoder.DecodePacket(packet, frame))
            {
                AVFrame* raw = decoded;
                if (first == FF.AV_NOPTS_VALUE)
                    first = raw->pts;
                var time = (raw->pts - first) * (double)stream.TimeBase.num / stream.TimeBase.den;
                if (time + raw->nb_samples / 48000.0 <= from)
                    continue;
                for (var c = 0; c < 6; c++)
                    channels[c].AddRange(new ReadOnlySpan<float>(raw->extended_data[c], raw->nb_samples));
                if (channels[0].Count >= seconds * 48000)
                    return channels.Select(list => list.Take((int)(seconds * 48000)).ToArray()).ToArray();
            }
        }
        return channels.Select(list => list.ToArray()).ToArray();
    }

    /// <summary>在候选频率中找能量最大的一个（Goertzel）。</summary>
    private static int DominantFrequency(float[] samples, int[] candidates) => candidates.MaxBy(frequency =>
    {
        double s1 = 0, s2 = 0;
        var coefficient = 2 * Math.Cos(2 * Math.PI * frequency / 48000);
        foreach (var sample in samples)
        {
            var s0 = sample + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        return s1 * s1 + s2 * s2 - coefficient * s1 * s2;
    });

    private static double Rms(float[] samples) => samples.Length == 0 ? 0 : Math.Sqrt(samples.Average(sample => (double)sample * sample));

    private static ChannelMergeChannelLevel Channel(double rms, bool lowOnly = false, double high = 0.5, double crest = 4) =>
        new(rms * crest, rms, lowOnly ? 0.001 : high, crest);

    private static ChannelMergeTrackLevel Track(int pid, ChannelMergeChannelLevel left, ChannelMergeChannelLevel right) =>
        new(pid, true, left, right, 0.3, null);
}
