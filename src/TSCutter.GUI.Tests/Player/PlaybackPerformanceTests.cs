using TSCutter.GUI.Player;
using Xunit;

namespace TSCutter.GUI.Tests.Player;

public sealed class PlaybackPerformanceTests
{
    [Fact]
    public void FluencyUsesTheSlowerRateRelativeToTheSource()
    {
        Assert.Equal(PlaybackFluency.Smooth, new PlaybackPerformance(60, 60, 60).Fluency);
        var ahead = new PlaybackPerformance(70, 45, 50);
        Assert.Equal(45, ahead.PlaybackFps);
        Assert.Equal(PlaybackFluency.Limited, ahead.Fluency);
        Assert.Equal(50, new PlaybackPerformance(70, 51, 50).PlaybackFps);
        Assert.Equal(PlaybackFluency.Smooth, new PlaybackPerformance(60, 59.99, 60).Fluency);
        Assert.Equal(PlaybackFluency.Smooth, new PlaybackPerformance(59.99, 60, 60).Fluency);
        var rounded = new PlaybackPerformance(49.99, 49.98, 50);
        Assert.Equal(50, rounded.DecodeFps);
        Assert.Equal(PlaybackFluency.Smooth, rounded.Fluency);
        Assert.Equal(PlaybackFluency.Limited, new PlaybackPerformance(49.94, 50, 50).Fluency);
        Assert.Equal(PlaybackFluency.Limited, new PlaybackPerformance(50, 49.94, 50).Fluency);
        Assert.Equal(PlaybackFluency.Limited, new PlaybackPerformance(42, 60, 60).Fluency);
        Assert.Equal(PlaybackFluency.Slow, new PlaybackPerformance(60, 41, 60).Fluency);
        Assert.Equal(PlaybackFluency.Smooth, new PlaybackPerformance(24, 24, 24).Fluency);
        Assert.Equal(PlaybackFluency.Unknown, new PlaybackPerformance(24, 24, 0).Fluency);
        Assert.Equal(PlaybackFluency.Smooth, new PlaybackPerformance(49.7, 49.7, 50, 0.5).Fluency);
        Assert.Equal(PlaybackFluency.Limited, new PlaybackPerformance(49.7, 49.7, 50, 0.5, 1).Fluency);
        // 短窗口计数抖动不应让流畅的 25fps 画面反复显示 24.9。
        Assert.Equal(25, new PlaybackPerformance(25, 24.9, 25, 0.5).PlaybackFps);
        Assert.Equal(24.9, new PlaybackPerformance(25, 24.9, 25, 0.5, 1).PlaybackFps);
        Assert.Equal(24, new PlaybackPerformance(25, 24, 25, 0.5).PlaybackFps);
    }

    [Fact]
    public void RollingRatesExcludePrerollAndDetectStallsAndReset()
    {
        var counter = new PlaybackPerformanceCounter();
        counter.Reset(0, 100, 0); // 已解码的预滚不参与播放统计。
        Assert.Null(counter.Measure(0.5, 115, 15, 30));
        var normal = counter.Measure(1, 130, 30, 30)!.Value;
        Assert.Equal(30, normal.DecodeFps);
        Assert.Equal(30, normal.PresentedFps);
        Assert.Equal(PlaybackFluency.Smooth, normal.Fluency);
        counter.Measure(2, 160, 60, 30);
        Assert.Equal(PlaybackFluency.Slow, counter.Measure(5, 160, 60, 30)!.Value.Fluency);
        // 停顿后恢复，旧窗口逐步淘汰。
        counter.Measure(6, 190, 90, 30);
        Assert.Equal(PlaybackFluency.Smooth, counter.Measure(7, 220, 120, 30)!.Value.Fluency);
        counter.Reset(10, 400, 0);
        Assert.Null(counter.Measure(10.5, 415, 15, 30));
        Assert.Equal(30, counter.Measure(11, 430, 30, 30)!.Value.DecodeFps);
        counter.Reset(0, 0, 0);
        Assert.Equal(PlaybackFluency.Limited, counter.Measure(2, 100, 99, 50, 1)!.Value.Fluency);
        counter.Measure(3, 150, 149, 50, 1);
        Assert.Equal(PlaybackFluency.Smooth, counter.Measure(4, 200, 199, 50, 1)!.Value.Fluency);
    }
}
