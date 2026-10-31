using TSCutter.GUI.Player;
using Xunit;

namespace TSCutter.GUI.Tests.Player;

public sealed class PlaybackPerformanceTests
{
    [Fact]
    public void FluencyUsesPresentationAndDropsRatherThanBufferedDecodeThroughput()
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
        Assert.Equal(PlaybackFluency.Smooth, new PlaybackPerformance(49.94, 50, 50).Fluency);
        Assert.Equal(PlaybackFluency.Limited, new PlaybackPerformance(50, 49.94, 50).Fluency);
        Assert.Equal(PlaybackFluency.Smooth, new PlaybackPerformance(42, 60, 60).Fluency);
        Assert.Equal(PlaybackFluency.Slow, new PlaybackPerformance(60, 41, 60).Fluency);
        Assert.Equal(PlaybackFluency.Smooth, new PlaybackPerformance(24, 24, 24).Fluency);
        Assert.Equal(PlaybackFluency.Unknown, new PlaybackPerformance(24, 24, 0).Fluency);
        foreach (var rate in new[] { 24000.0 / 1001, 30000.0 / 1001, 60000.0 / 1001 })
        {
            var fractional = new PlaybackPerformance(rate, rate, rate);
            Assert.Equal(rate, fractional.SourceFps);
            Assert.Equal(rate, fractional.PlaybackFps);
            Assert.Equal(PlaybackFluency.Smooth, fractional.Fluency);
            Assert.Equal(PlaybackFluency.Limited, new PlaybackPerformance(rate, rate, rate, droppedFrames: 1).Fluency);
        }
        Assert.Equal(PlaybackFluency.Smooth, new PlaybackPerformance(49.7, 49.7, 50, 0.5).Fluency);
        Assert.Equal(PlaybackFluency.Limited, new PlaybackPerformance(49.7, 49.7, 50, 0.5, 1).Fluency);
        // 短窗口计数抖动不应让流畅的 25fps 画面反复显示 24.9。
        Assert.Equal(25, new PlaybackPerformance(25, 24.9, 25, 0.5).PlaybackFps);
        Assert.Equal(24.9, new PlaybackPerformance(25, 24.9, 25, 0.5, 1).PlaybackFps);
        Assert.Equal(24, new PlaybackPerformance(25, 24, 25, 0.5).PlaybackFps);
        // 先把容限加入原始速率再取整，避免浮点边界导致显示 50.0 却变黄。
        Assert.Equal(PlaybackFluency.Smooth, new PlaybackPerformance(49.49, 49.49, 50, 0.499).Fluency);
        Assert.Equal(PlaybackFluency.Limited, new PlaybackPerformance(60, 50, 50, 0.499, 1).Fluency);
    }

    [Fact]
    public void RenderTimesRemovePollingPhaseJitterButDoNotHideStallsOrDrops()
    {
        var counter = new PlaybackPerformanceCounter();
        counter.Reset(0.012, 100, 20, presentedAt: 0);
        // UI 轮询相位变化，解码端按场成对填充队列；实际每秒呈现 50 帧。
        var first = counter.Measure(1.017, 149, 70, 50, presentedAt: 1)!.Value;
        Assert.Equal(50, first.PresentedFps);
        Assert.Equal(PlaybackFluency.Smooth, first.Fluency);
        var second = counter.Measure(2.006, 199, 120, 50, presentedAt: 2)!.Value;
        Assert.Equal(50, second.PlaybackFps);
        Assert.Equal(PlaybackFluency.Smooth, second.Fluency);
        // 即使最后一次呈现时刻不再变化，停顿仍计入统计分母。
        Assert.Equal(PlaybackFluency.Slow, counter.Measure(3.006, 199, 120, 50, presentedAt: 2)!.Value.Fluency);
        counter.Reset(10, 500, 400, presentedAt: 10);
        Assert.Equal(PlaybackFluency.Limited, counter.Measure(12.005, 600, 499, 50, 1, 12)!.Value.Fluency);
        counter.Reset(20, 700, 600, presentedAt: 20);
        Assert.Equal(PlaybackFluency.Slow, counter.Measure(22, 720, 620, 50, presentedAt: 22)!.Value.Fluency);
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
