using TSCutter.GUI.Services.Media;
using TSCutter.GUI.Tests.FFmpeg;
using Xunit;

namespace TSCutter.GUI.Tests.Media;

/// <summary>
/// 静音音轨检测：五条音轨分别为响亮（约 -18 dBFS）、完全静音、约 -66 dBFS 的底噪、-40 dBFS 的小声音轨、
/// 后半段才开始有声音。抽样后只有静音与底噪判为静音；非音频轨与不存在的序号不出现在结果中。
/// </summary>
public sealed class SilentTrackDetectorTests
{
    [NativeMediaFact("remux")]
    public void DetectsSilentAndNearSilentTracks()
    {
        var path = Path.Combine(NativeTestEnvironment.FixtureDirectory("remux"), "silent-tracks.ts");
        var reported = new List<double>();
        var levels = SilentTrackDetector.Analyze(path, null, -1, [0, 1, 2, 3, 4, 5, 99],
            new SynchronousProgress(reported.Add), CancellationToken.None);
        Assert.Equal([1, 2, 3, 4, 5], levels.Select(level => level.StreamIndex));
        Assert.All(levels, level => Assert.True(level.Decodable));
        Assert.Equal([false, true, true, false, false], levels.Select(level => level.Silent));
        Assert.InRange(levels[0].PeakDb!.Value, -20, -16);
        Assert.InRange(levels[2].PeakDb!.Value, -80, -60);
        Assert.InRange(levels[3].PeakDb!.Value, -42, -37);
        Assert.Equal(1, reported[^1]);
        Assert.Equal(reported.Order(), reported);
    }

    [NativeMediaFact("remux")]
    public void StopsWhenCancelled()
    {
        var path = Path.Combine(NativeTestEnvironment.FixtureDirectory("remux"), "silent-tracks.ts");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            SilentTrackDetector.Analyze(path, null, -1, [1, 2], null, cancellation.Token));
    }

    /// <summary>
    /// 可选的杜比 E 素材目录（每个 TS 含一条以 302M 承载的杜比 E 轨）：按杜比 E 解码，列出各声道组的峰值，
    /// 不把杜比 E 数据当作 PCM（那样峰值接近满刻度），也不因 302M 解码器不支持而无法检测。
    /// </summary>
    [NativeRuntimeFact("TSCUTTER_DOLBY_E_SAMPLES")]
    public void DecodesDolbyEChannelGroups()
    {
        var files = Directory.GetFiles(Environment.GetEnvironmentVariable("TSCUTTER_DOLBY_E_SAMPLES")!, "*.ts");
        Assert.NotEmpty(files);
        foreach (var path in files)
        {
            int carrier;
            using (var input = TSCutter.GUI.FFmpeg.FormatContext.OpenInputUrl(path))
            {
                input.LoadStreamInfo();
                carrier = input.Streams.First(TSCutter.GUI.Utils.Media.DolbyE.DolbyEDecoder.IsCarrier).Index;
            }
            var level = Assert.Single(SilentTrackDetector.Analyze(path, null, -1, [carrier], null, CancellationToken.None));
            Assert.True(level.Decodable);
            Assert.NotNull(level.DolbyE);
            Assert.NotEmpty(level.DolbyE);
            Assert.Equal(level.DolbyE.Max(program => program.Peak), level.Peak);
            Assert.Equal(level.DolbyE.All(program => program.Silent), level.Silent);
        }
    }

    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
