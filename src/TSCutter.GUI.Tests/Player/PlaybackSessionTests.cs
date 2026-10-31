using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Player;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.UI;
using Xunit;

namespace TSCutter.GUI.Tests.Player;

/// <summary>
/// 播放会话的生命周期验证：起播呈现、区间结束、中途停止。
/// 呈现转换依赖 Avalonia 位图，测试在 headless 会话内运行；
/// UI 投递注入为同步执行，使断言直接观察呈现结果。
/// </summary>
[Collection("Avalonia UI")]
public sealed class PlaybackSessionTests
{
    private static async Task<VideoInstance> OpenSampleAsync(string name)
    {
        var instance = new VideoInstance(
            Path.Combine(NativeTestEnvironment.FixtureDirectory("keyframes"), name));
        await instance.InitVideoAsync();
        return instance;
    }

    private static PlaybackOptions BuildOptions(VideoInstance instance, string path,
        double startSeconds = 0, double? endSeconds = null) => new()
    {
        FilePath = path,
        StartSeconds = startSeconds,
        EndSeconds = endSeconds,
        TimelineOriginSeconds = instance.TimelineOriginSeconds,
        EstimatedKeyFrameIntervalSeconds = instance.EstimatedKeyFrameIntervalSeconds,
    };

    private static void DisposeResult(DecodeResult result)
    {
        result.GpuFrame?.Dispose();
        result.BitmapLease?.Dispose();
        if (result.BitmapLease is null)
            result.Bitmap?.Dispose();
    }

    [NativeMediaFact("keyframes")]
    public async Task SessionPresentsFramesInOrderAndReachesEnd()
    {
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            using var instance = await OpenSampleAsync("fixed.mp4");
            var path = Path.Combine(NativeTestEnvironment.FixtureDirectory("keyframes"), "fixed.mp4");
            var presented = new System.Collections.Concurrent.ConcurrentQueue<double>();
            var lastPts = long.MinValue;
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var session = await PlaybackSession.StartAsync(BuildOptions(instance, path, endSeconds: 3),
                uiPost: action => action(), initialize: owner =>
                {
                    owner.FramePresented += result =>
                    {
                        presented.Enqueue(result.FrameTimestamp.TotalSeconds);
                        lastPts = result.FramePts;
                        DisposeResult(result);
                    };
                    owner.Ended += () => ended.TrySetResult();
                });
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(PlaybackState.Ended, session.State);
            var frames = presented.ToArray();
            Assert.True(frames.Length >= 2);
            Assert.Equal(0, frames[0], 6);
            Assert.Equal(3, frames[^1], 6);
            for (var index = 1; index < frames.Length; index++)
                Assert.True(frames[index] > frames[index - 1]);
            // 未缓存的播放帧也能重新解码截图，回收临时引擎不能让成功结果变成取消。
            var capture = await instance.CapturePlaybackFrameAsync(lastPts).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(capture);
            try
            {
                Assert.Equal(lastPts, capture.FramePts);
                Assert.NotNull(capture.Bitmap);
            }
            finally { DisposeResult(capture); }
            return true;
        }, System.Threading.CancellationToken.None);
    }

    [NativeMediaFact("keyframes")]
    public async Task StartupCancellationEmptyRangesAndPendingUiFramesStopCleanly()
    {
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            using var instance = await OpenSampleAsync("fixed.mp4");
            var path = Path.Combine(NativeTestEnvironment.FixtureDirectory("keyframes"), "fixed.mp4");
            using var cancellation = new System.Threading.CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlaybackSession.StartAsync(
                BuildOptions(instance, path), action => action(), cancellation.Token, _ => cancellation.Cancel()));

            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var empty = await PlaybackSession.StartAsync(BuildOptions(instance, path, 2.51, 2.51),
                action => action(), initialize: owner => owner.Ended += () => ended.TrySetResult()))
                await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var pending = new System.Collections.Concurrent.ConcurrentQueue<Action>();
            var frames = 0;
            var session = await PlaybackSession.StartAsync(BuildOptions(instance, path), pending.Enqueue,
                initialize: owner => owner.FramePresented += frame => { frames++; DisposeResult(frame); });
            await Task.Delay(250);
            await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            while (pending.TryDequeue(out var deliver))
                deliver();
            Assert.Equal(0, frames);
            await session.DisposeAsync();
            await session.DisposeAsync();
            return true;
        }, System.Threading.CancellationToken.None);
    }

    [NativeMediaFact("keyframes")]
    public async Task PauseResumeAndRepeatedSeeksKeepTheSessionAndDiscardOldFrames()
    {
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            using var instance = await OpenSampleAsync("fixed.mp4");
            var path = Path.Combine(NativeTestEnvironment.FixtureDirectory("keyframes"), "fixed.mp4");
            var frames = new System.Collections.Concurrent.ConcurrentQueue<double>();
            var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var session = await PlaybackSession.StartAsync(BuildOptions(instance, path),
                action => action(), initialize: owner =>
                {
                    owner.FramePresented += result =>
                    {
                        frames.Enqueue(result.FrameTimestamp.TotalSeconds);
                        DisposeResult(result);
                    };
                    owner.Ended += () => ended.TrySetResult();
                }, audioOutputFactory: () => null);
            await Task.Delay(200);
            await session.PauseAsync();
            var pausedTime = session.Clock.CurrentSeconds;
            var count = frames.Count;
            await Task.Delay(150);
            Assert.Equal(PlaybackState.Paused, session.State);
            Assert.Equal(pausedTime, session.Clock.CurrentSeconds, 6);
            Assert.Equal(count, frames.Count);
            await session.ResumeAsync();
            await Task.Delay(200);
            Assert.Equal(PlaybackState.Playing, session.State);
            Assert.True(frames.Count > count);

            foreach (var range in new[] { (4.0, 4.5), (1.0, 1.25) })
            {
                await session.PauseAsync();
                frames.Clear();
                ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await session.SeekAsync(range.Item1, range.Item2);
                await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(PlaybackState.Ended, session.State);
                Assert.NotEmpty(frames);
                Assert.Equal(range.Item1, frames.First(), 6);
                Assert.Equal(range.Item2, frames.Last(), 6);
                Assert.All(frames, seconds => Assert.InRange(seconds, range.Item1, range.Item2));
            }
            await session.SeekAsync(2, 4);
            await session.PauseAsync();
            await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var countAtStop = frames.Count;
            await Task.Delay(100);
            Assert.Equal(countAtStop, frames.Count);
            Assert.Equal(PlaybackState.Idle, session.State);
            return true;
        }, System.Threading.CancellationToken.None);
    }

    [NativeMediaFact("keyframes")]
    public async Task SeekingDropsQueuedUiCallbacksAndCancellationReleasesTheSession()
    {
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            using var instance = await OpenSampleAsync("fixed.mp4");
            var path = Path.Combine(NativeTestEnvironment.FixtureDirectory("keyframes"), "fixed.mp4");
            var pending = new System.Collections.Concurrent.ConcurrentQueue<Action>();
            var frames = new System.Collections.Concurrent.ConcurrentQueue<double>();
            await using var session = await PlaybackSession.StartAsync(BuildOptions(instance, path), pending.Enqueue,
                initialize: owner => owner.FramePresented += frame =>
                {
                    frames.Enqueue(frame.FrameTimestamp.TotalSeconds);
                    DisposeResult(frame);
                }, audioOutputFactory: () => null);
            await Task.Delay(100);
            await session.SeekAsync(4, 4.25);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (session.State != PlaybackState.Ended && DateTime.UtcNow < deadline)
            {
                while (pending.TryDequeue(out var callback))
                    callback();
                await Task.Delay(10);
            }
            Assert.Equal(PlaybackState.Ended, session.State);
            Assert.NotEmpty(frames);
            Assert.All(frames, seconds => Assert.InRange(seconds, 4, 4.25));
            var seeking = session.SeekAsync(2, 3);
            session.Cancel();
            try { await seeking; }
            catch (OperationCanceledException) { }
            await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            while (pending.TryDequeue(out var callback))
                callback();
            return true;
        }, System.Threading.CancellationToken.None);
    }

    /// <summary>
    /// 带音轨文件的真实设备会话（默认关闭；TSCUTTER_ENABLE_AUDIO_DEVICE_TESTS=1 启用）。
    /// 音量固定为 0：全程数字静音；验证音频链路建立、音频主时钟推进与结束语义。
    /// </summary>
    [AudioDeviceFact]
    public async Task AudioBackedSessionReachesEndWithSilentVolume()
    {
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        var endReached = false;
        var maxTime = 0.0;
        await headless.Dispatch<bool>(async () =>
        {
            using var instance = new VideoInstance(
                Path.Combine(NativeTestEnvironment.FixtureDirectory("remux"), "long-audio.mp4"));
            await instance.InitVideoAsync();
            var path = Path.Combine(NativeTestEnvironment.FixtureDirectory("remux"), "long-audio.mp4");
            var session = await PlaybackSession.StartAsync(new PlaybackOptions
            {
                FilePath = path,
                TimelineOriginSeconds = instance.TimelineOriginSeconds,
                EstimatedKeyFrameIntervalSeconds = instance.EstimatedKeyFrameIntervalSeconds,
                Volume = 0.0, // 数字静音：验证时钟与结束语义，不发出声音。
            }, uiPost: action => action(), initialize: owner => owner.Ended += () => endReached = true);
            try
            {
                Assert.Equal(PlaybackAudioStatus.Normal, session.AudioStatus);
                var deadline = DateTime.UtcNow.AddSeconds(25);
                while (!endReached && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50);
                    maxTime = Math.Max(maxTime, session.Clock.CurrentSeconds);
                }
                Assert.True(endReached, $"音频时钟未推进到结束（最大 {maxTime:F2}s）");
                Assert.True(maxTime > 5.0, $"播放时间过短：{maxTime:F2}s");
                return true;
            }
            finally
            {
                await session.DisposeAsync();
            }
        }, System.Threading.CancellationToken.None);
        Assert.True(endReached);
    }

    /// <summary>
    /// 中段起播（等价于跳转后播放）：预滚需跨过整个 GOP，音频预滚与
    /// 视频队列背压不能互相卡死。音量 0，全程数字静音。
    /// </summary>
    [AudioDeviceFact]
    public async Task AudioBackedSessionStartsFromMiddleWithoutDeadlock()
    {
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        var endReached = false;
        await headless.Dispatch<bool>(async () =>
        {
            using var instance = new VideoInstance(
                Path.Combine(NativeTestEnvironment.FixtureDirectory("remux"), "long-audio.mp4"));
            await instance.InitVideoAsync();
            var path = Path.Combine(NativeTestEnvironment.FixtureDirectory("remux"), "long-audio.mp4");
            var devicesCreated = 0;
            var session = await PlaybackSession.StartAsync(new PlaybackOptions
            {
                FilePath = path,
                TimelineOriginSeconds = instance.TimelineOriginSeconds,
                EstimatedKeyFrameIntervalSeconds = instance.EstimatedKeyFrameIntervalSeconds,
                Volume = 0.0,
            }, uiPost: action => action(), initialize: owner => owner.Ended += () => endReached = true,
                audioOutputFactory: () =>
                {
                    devicesCreated++;
                    return TSCutter.GUI.Player.Audio.AudioOutputDevice.TryCreate(48_000, 2, 12_000);
                });
            try
            {
                Assert.Equal(PlaybackAudioStatus.Normal, session.AudioStatus);
                await Task.Delay(300);
                await session.PauseAsync();
                var frozen = session.Clock.CurrentSeconds;
                await Task.Delay(100);
                Assert.Equal(frozen, session.Clock.CurrentSeconds, 6);
                await session.ResumeAsync();
                await Task.Delay(100);
                await session.SeekAsync(5, 7);
                Assert.Equal(1, devicesCreated);
                Assert.Equal(PlaybackAudioStatus.Normal, session.AudioStatus);
                Assert.InRange(session.Clock.CurrentSeconds, 5, 5.25);
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (!endReached && DateTime.UtcNow < deadline)
                    await Task.Delay(50);
                Assert.True(endReached, "中段起播未在超时内到达区间终点（可能死锁）");
                return true;
            }
            finally
            {
                await session.DisposeAsync();
            }
        }, System.Threading.CancellationToken.None);
        Assert.True(endReached);
    }
}
