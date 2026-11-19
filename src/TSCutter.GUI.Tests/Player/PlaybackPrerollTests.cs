using System.Security.Cryptography;
using System.Threading.Channels;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Player;
using TSCutter.GUI.Player.Video;
using TSCutter.GUI.Tests.FFmpeg;
using Xunit;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Tests.Player;

/// <summary>
/// 预滚跳过目标之前的非参考帧，也不处理（回读、去隔行）远早于目标的画面：
/// 从中间起播得到的每一帧，与从片头连续播放到同一时间戳的画面逐字节一致。
/// 覆盖 H.264 B 帧、HEVC 开放 GOP、MPEG-2 与隔行去隔行。
/// </summary>
public sealed class PlaybackPrerollTests
{
    [NativeMediaTheory("frameaccurate")]
    [InlineData("h264.ts", new[] { 2.3, 5.6 }, false)]
    [InlineData("hevc-cra.ts", new[] { 1.9, 4.95 }, false)]
    [InlineData("open-gop.ts", new[] { 3.0 }, false)]
    [InlineData("mpeg2.ts", new[] { 3.1, 6.5 }, false)]
    [InlineData("interlaced.ts", new[] { 1.5, 3.3 }, true)]
    public async Task StartingMidStreamMatchesContinuousPlayback(string name, double[] starts, bool deinterlace)
    {
        var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), name);
        using var video = new VideoInstance(source);
        await video.InitVideoAsync();
        var options = new PlaybackOptions
        {
            FilePath = source,
            TimelineOriginSeconds = video.TimelineOriginSeconds,
            EstimatedKeyFrameIntervalSeconds = video.EstimatedKeyFrameIntervalSeconds,
            EnableGpuPresentation = false,
        };
        var continuous = await DecodeAsync(options with { EndSeconds = starts.Max() + 1 }, deinterlace, int.MaxValue);
        foreach (var start in starts)
        {
            var resumed = await DecodeAsync(options with { StartSeconds = start, EndSeconds = start + 1 }, deinterlace, 12);
            Assert.NotEmpty(resumed);
            Assert.True(resumed[0].Seconds >= start - 0.001, $"{resumed[0].Seconds} < {start}");
            foreach (var frame in resumed)
            {
                var expected = continuous.Find(item => item.Pts == frame.Pts && item.Seconds == frame.Seconds);
                Assert.True(expected != default, $"{name} {start}: frame {frame.Pts} missing from continuous playback");
                Assert.Equal(expected.Hash, frame.Hash);
            }
            // 跳入后的第一帧就是连续播放中不早于起点的第一帧，没有漏帧。
            Assert.Equal(continuous.First(item => item.Seconds >= start - 0.001).Seconds, resumed[0].Seconds);
        }
    }

    private static async Task<List<(long Pts, double Seconds, string Hash)>> DecodeAsync(PlaybackOptions options,
        bool deinterlace, int limit)
    {
        using var queue = new VideoFrameQueue(4);
        await using var engine = new VideoPlaybackEngine(options, queue, deinterlace);
        var run = engine.RunAsync();
        var frames = new List<(long, double, string)>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            while (frames.Count < limit)
            {
                using var item = await queue.DequeueAsync(timeout.Token);
                frames.Add((item.Pts, Math.Round(item.TimelineSeconds, 6), Hash(item.Frame)));
            }
        }
        catch (ChannelClosedException) { }
        engine.Cancel();
        try { await run; }
        catch (OperationCanceledException) { }
        return frames;
    }

    private static unsafe string Hash(Frame frame)
    {
        AVFrame* raw = frame;
        var descriptor = FF.av_pix_fmt_desc_get((AVPixelFormat)raw->format);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (uint plane = 0; plane < 4 && raw->data[plane] != null; plane++)
        {
            var height = plane is 1 or 2 ? (raw->height + (1 << descriptor->log2_chroma_h) - 1) >> descriptor->log2_chroma_h : raw->height;
            var width = plane is 1 or 2 ? (raw->width + (1 << descriptor->log2_chroma_w) - 1) >> descriptor->log2_chroma_w : raw->width;
            var bytes = width * (descriptor->comp[0].depth > 8 ? 2 : 1) * (descriptor->comp[1].plane == descriptor->comp[2].plane && plane == 1 ? 2 : 1);
            for (var row = 0; row < height; row++)
                hash.AppendData(new ReadOnlySpan<byte>(raw->data[plane] + (long)row * raw->linesize[plane], bytes));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
