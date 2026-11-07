using Avalonia.Headless;
using TSCutter.GUI.Controls;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

/// <summary>
/// 导航条的滚轮换算与图片预览的滚轮缩放一致：每个滚轮事件按方向移动一格，与增量大小无关；
/// 距上一次移动不足 16 毫秒的事件（触控板、平滑滚动的连续事件流）忽略。
/// </summary>
[Collection("Avalonia UI")]
public sealed class KeyFrameStripWheelTests
{
    [Theory]
    // macOS 普通滚轮：每格增量不足 1，且随滚动加速变化（Avalonia 换算为 scrollingDeltaY / 5）。
    [InlineData(new[] { -0.2, -0.4, -0.2, -1.2 }, 4)]
    // Windows：每格固定为 1。
    [InlineData(new[] { -1.0, -1.0 }, 2)]
    // 向上滚后退。
    [InlineData(new[] { 0.2, 0.6 }, -2)]
    public void EachSpacedWheelEventMovesOneStep(double[] deltas, int expected)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        session.Dispatch(() =>
        {
            var strip = new KeyFrameStripControl();
            var steps = new List<int>();
            strip.WheelStepRequested += (_, step) => steps.Add(step);
            ulong time = 1000;
            foreach (var delta in deltas)
            {
                Assert.True(strip.ApplyWheel(delta, time));
                time += 120;
            }
            Assert.Equal(expected, steps.Sum());
            Assert.All(steps, step => Assert.Equal(1, Math.Abs(step)));
        }, CancellationToken.None);
    }

    [Fact]
    public void ContinuousStreamsAreThrottled()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        session.Dispatch(() =>
        {
            var strip = new KeyFrameStripControl();
            var steps = new List<int>();
            strip.WheelStepRequested += (_, step) => steps.Add(step);
            // 触控板滑动：每 4 毫秒一个很小的增量，持续 100 毫秒；每 16 毫秒内最多移动一格。
            for (ulong time = 0; time < 100; time += 4)
                Assert.True(strip.ApplyWheel(-0.02, 5000 + time));
            Assert.InRange(steps.Count, 6, 7);
            Assert.All(steps, step => Assert.Equal(1, step));
            // 增量为 0 的事件不处理。
            Assert.False(strip.ApplyWheel(0, 9000));
        }, CancellationToken.None);
    }
}
