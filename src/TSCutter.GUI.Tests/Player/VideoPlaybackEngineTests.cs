using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Player;
using TSCutter.GUI.Player.Video;
using TSCutter.GUI.Tests.FFmpeg;
using Xunit;

namespace TSCutter.GUI.Tests.Player;

/// <summary>
/// 连续视频解码引擎的场景化验证：全量解码、预滚边界、区间终点。
/// keyframes 素材为恒定帧率 H.264（含 B 帧）；json 记录的是关键帧时间戳，
/// 引擎输出的是全部画面，预期从全量解码结果推导，不硬编码帧数。
/// 引擎不依赖 Avalonia 对象，直接运行。
/// </summary>
public sealed class VideoPlaybackEngineTests
{
    private static string SamplePath(string name) =>
        Path.Combine(NativeTestEnvironment.FixtureDirectory("keyframes"), name);

    private static async Task<VideoInstance> OpenSampleAsync(string name)
    {
        var instance = new VideoInstance(SamplePath(name));
        await instance.InitVideoAsync();
        return instance;
    }

    private static async Task<double[]> ReadKeyframeTimestampsAsync(string name) =>
        JsonSerializer.Deserialize<double[]>(await File.ReadAllTextAsync(SamplePath(name) + ".json"))!;

    private static PlaybackOptions BuildOptions(VideoInstance instance, string path,
        double startSeconds = 0, double? endSeconds = null) => new()
    {
        FilePath = path,
        StartSeconds = startSeconds,
        EndSeconds = endSeconds,
        TimelineOriginSeconds = instance.TimelineOriginSeconds,
        EstimatedKeyFrameIntervalSeconds = instance.EstimatedKeyFrameIntervalSeconds,
    };

    private static async Task<List<double>> RunEngineToEofAsync(VideoInstance instance, string path,
        double startSeconds = 0, double? endSeconds = null, bool preferHardware = false)
    {
        using var queue = new VideoFrameQueue(4);
        var options = BuildOptions(instance, path, startSeconds, endSeconds) with
        {
            PreferHardwareDecoding = preferHardware,
        };
        await using var engine = new VideoPlaybackEngine(options, queue);
        var runTask = engine.RunAsync();
        var collected = new List<double>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                using var frame = await queue.DequeueAsync(timeout.Token);
                collected.Add(frame.TimelineSeconds);
            }
        }
        catch (ChannelClosedException)
        {
            // 流结束：正常退出路径。
        }
        await runTask;
        Assert.NotEmpty(collected);
        for (var index = 1; index < collected.Count; index++)
            Assert.True(collected[index] > collected[index - 1],
                "解码输出必须按显示顺序（PTS 单调递增）排列。");
        return collected;
    }

    [NativeMediaFact("keyframes")]
    public async Task CatchUpResumesAtAFutureKeyframeAndKeepsTimestampsIncreasing()
    {
        using var video = await OpenSampleAsync("fixed.mp4");
        using var queue = new VideoFrameQueue(2);
        await using var engine = new VideoPlaybackEngine(BuildOptions(video, SamplePath("fixed.mp4")), queue);
        await engine.OpenAsync();
        var run = Task.Run(engine.RunAsync);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var last = -1.0;
        try
        {
            using (var first = await queue.DequeueAsync(timeout.Token))
                last = first.TimelineSeconds;
            engine.RequestCatchUp(6);
            engine.RequestCatchUp(10); // 同一轮合并，不能不断把目标推远。
            for (var index = 0; index < 12; index++)
            {
                using var frame = await queue.DequeueAsync(timeout.Token);
                Assert.True(frame.TimelineSeconds > last);
                last = frame.TimelineSeconds;
                if (last >= 6)
                    break;
            }
            Assert.InRange(last, 6, 7);
            Assert.Equal(1, engine.CatchUpCount);
        }
        finally
        {
            engine.Cancel();
            await run;
        }
    }

    [NativeMediaFact("keyframes")]
    public async Task DeinterlacedPlaybackKeepsSourceAnchorsAfterSeekAndAtTheEndBoundary()
    {
        using var instance = await OpenSampleAsync("interlaced.mp4");
        using var queue = new VideoFrameQueue(4);
        var options = BuildOptions(instance, SamplePath("interlaced.mp4"), startSeconds: 2, endSeconds: 3);
        await using var engine = new VideoPlaybackEngine(options, queue, enableDeinterlacing: true);
        var run = Task.Run(engine.RunAsync);
        var frames = new List<(double Time, long Pts)>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            while (true)
            {
                using var frame = await queue.DequeueAsync(timeout.Token);
                frames.Add((frame.TimelineSeconds, frame.Pts));
                Assert.Equal(0, frame.Frame.Flags & NativeMethods.AV_FRAME_FLAG_INTERLACED);
            }
        }
        catch (ChannelClosedException)
        {
            await run;
        }
        finally
        {
            engine.Cancel();
            await run;
        }
        Assert.Equal(50, engine.SourceFrameRate);
        Assert.Equal(52, frames.Count); // 2.00 至 3.00 共 26 幅原始帧，包含终帧的两个场。
        for (var index = 0; index < frames.Count; index++)
        {
            Assert.Equal(2 + index * 0.02, frames[index].Time, 5);
            Assert.Equal(frames[index / 2 * 2].Pts, frames[index].Pts);
        }
    }

    [NativeMediaFact("keyframes")]
    public async Task EngineDecodesEveryFrameInDisplayOrderUntilEof()
    {
        using var instance = await OpenSampleAsync("fixed.mp4");
        var keyframes = await ReadKeyframeTimestampsAsync("fixed.mp4");
        var timelines = await RunEngineToEofAsync(instance, SamplePath("fixed.mp4"));

        Assert.Equal(0.0, timelines[0], 3);
        // 帧间隔由输出推导；总数与最后一帧时间自洽（恒定帧率素材）。
        var interval = timelines[1] - timelines[0];
        Assert.True(interval > 0);
        Assert.Equal(timelines.Count, (long)Math.Round(timelines.Last() / interval) + 1);
        // 全部画面中包含素材 json 记录的每个关键帧时刻。
        foreach (var keyframe in keyframes)
            Assert.Contains(timelines, seconds => Math.Abs(seconds - keyframe) < 0.002);
    }

    [NativeMediaFact("keyframes")]
    public async Task PrerollSkipsFramesBeforeTheTargetAndPresentsFromIt()
    {
        foreach (var name in new[] { "fixed.mp4", "fixed.ts", "hevc-hlg.ts" })
        {
            using var instance = await OpenSampleAsync(name);
            var path = SamplePath(name);
            var reference = await RunEngineToEofAsync(instance, path);
            const double startSeconds = 2.5;
            var frames = await RunEngineToEofAsync(instance, path, startSeconds);

            // 索引定位和 TS GOP 内定位均须保留目标之后的全部画面。
            var expectedIndex = reference.FindIndex(seconds => seconds >= startSeconds);
            Assert.True(expectedIndex >= 0);
            Assert.Equal(reference[expectedIndex], frames[0], 6);
            Assert.Equal(reference.Count - expectedIndex, frames.Count);
        }
    }

    [NativeMediaFact("keyframes")]
    public async Task EndBoundaryStopsPlaybackAfterTheIncludedEndFrame()
    {
        using var instance = await OpenSampleAsync("fixed.mp4");
        var path = SamplePath("fixed.mp4");
        var reference = await RunEngineToEofAsync(instance, path);
        const double endSeconds = 2.5;
        var frames = await RunEngineToEofAsync(instance, path, 0, endSeconds);

        var expected = reference.Where(seconds => seconds <= endSeconds).ToList();
        Assert.Equal(expected.Count, frames.Count);
        Assert.Equal(expected.Last(), frames.Last(), 6);
    }

    [NativeMediaFact("keyframes")]
    public async Task HardwareDecodePreferenceFallsBackAndStillProducesAllFrames()
    {
        using var instance = await OpenSampleAsync("fixed.mp4");
        var reference = await RunEngineToEofAsync(instance, SamplePath("fixed.mp4"),
            preferHardware: false);
        var frames = await RunEngineToEofAsync(instance, SamplePath("fixed.mp4"),
            preferHardware: true);
        // 无论是否走硬件（或回退软件），输出都必须一致。
        Assert.Equal(reference.Count, frames.Count);
        for (var index = 0; index < frames.Count; index++)
            Assert.Equal(reference[index], frames[index], 6);
    }

    [NativeMediaFact("keyframes")]
    public async Task EngineCancelStopsTheLoopPromptly()
    {
        using var instance = await OpenSampleAsync("variable.mp4");
        using var queue = new VideoFrameQueue(2);
        await using var engine = new VideoPlaybackEngine(
            BuildOptions(instance, SamplePath("variable.mp4"), 0), queue);
        var runTask = engine.RunAsync();
        engine.Cancel();
        var completed = await Task.WhenAny(runTask, Task.Delay(5000));
        Assert.Equal(runTask, completed);
        queue.Dispose();
    }
}
