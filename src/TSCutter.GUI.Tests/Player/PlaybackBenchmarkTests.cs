using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using TSCutter.GUI.Player;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Player.Diagnostics;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.UI;
using Xunit;

namespace TSCutter.GUI.Tests.Player;

[Collection("Avalonia UI")]
public sealed class PlaybackBenchmarkTests
{
    [Fact]
    public void SparseOutputIsNotCountedAsFastDecodingAndRatingsRespectHeadroom()
    {
        PlaybackBenchmarkService.ValidateTimeline(101, 2, 50);
        Assert.Throws<PlaybackBenchmarkException>(() => PlaybackBenchmarkService.ValidateTimeline(8, 9, 50));
        Assert.Throws<PlaybackBenchmarkException>(() => PlaybackBenchmarkService.ValidateTimeline(8, 0, 50));
        Assert.Equal(PlaybackBenchmarkRating.Insufficient, Result(30).Rating);
        Assert.Equal(PlaybackBenchmarkRating.Marginal, Result(55).Rating);
        Assert.Equal(PlaybackBenchmarkRating.Good, Result(100).Rating);
        Assert.Equal(PlaybackBenchmarkRating.Marginal, (Result(100) with { Recoveries = 1 }).Rating);
        static PlaybackBenchmarkResult Result(double fps) => new(PlaybackBenchmarkStage.Decode, fps, 50, 100, 2, false);
    }

    [NativeMediaFact("keyframes")]
    public async Task ShortGeneratedVideoRunsAllStagesAndCancellationStopsWork()
    {
        using var ui = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await ui.Dispatch<bool>(async () =>
        {
            var path = Path.Combine(NativeTestEnvironment.FixtureDirectory("keyframes"), "fixed.mp4");
            var source = await PlaybackBenchmarkService.DescribeAsync(path, null, CancellationToken.None);
            Assert.Equal(VideoDynamicRange.Standard, source.Tracks[0].DynamicRange);
            var hdrSource = await PlaybackBenchmarkService.DescribeAsync(Path.Combine(
                NativeTestEnvironment.FixtureDirectory("keyframes"), "hevc-hlg.ts"), null, CancellationToken.None);
            Assert.Equal(VideoDynamicRange.Hlg, hdrSource.Tracks[0].DynamicRange);
            var request = new PlaybackBenchmarkRequest(source, source.Tracks[0], 0, new PixelSize(96, 54), false, true);
            var completed = new List<PlaybackBenchmarkStage>();
            var progress = new ImmediateProgress(value =>
            {
                if (value.Completed is not null)
                    completed.Add(value.Stage);
            });
            var result = await PlaybackBenchmarkService.RunAsync(request, progress, null, CancellationToken.None,
                new(0.05, 0.03, 0.4, 60, 5), action => action());
            Assert.Equal(3, result.Count);
            Assert.Equal(new[] { PlaybackBenchmarkStage.Decode, PlaybackBenchmarkStage.Conversion, PlaybackBenchmarkStage.Playback }, completed);
            Assert.All(result, value => { Assert.True(value.FramesPerSecond > 0); Assert.True(value.SourceFrameRate > 0); });
            Assert.Equal(new PixelSize(96, 54), result[1].OutputSize);
            using var cancel = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlaybackBenchmarkService.RunAsync(request,
                new ImmediateProgress(_ => cancel.Cancel()), null, cancel.Token, new(0.05, 0.03, 0.4), action => action()));
            return true;
        }, CancellationToken.None);
    }

    private sealed class ImmediateProgress(Action<PlaybackBenchmarkProgress> receive) : IProgress<PlaybackBenchmarkProgress>
    {
        public void Report(PlaybackBenchmarkProgress value) => receive(value);
    }
}
