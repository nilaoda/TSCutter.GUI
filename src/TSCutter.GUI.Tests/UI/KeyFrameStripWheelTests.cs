using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using TSCutter.GUI.Controls;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

/// <summary>普通滚轮按事件步进，精细滚动累计到一格后步进。</summary>
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
    public async Task EachSpacedWheelEventMovesOneStep(double[] deltas, int expected)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var strip = new KeyFrameStripControl();
            var steps = new List<int>();
            strip.WheelStepRequested += (_, step) => steps.Add(step);
            ulong time = 1000;
            foreach (var delta in deltas)
            {
                Assert.True(strip.ApplyWheel(new Vector(0, delta), KeyModifiers.None, time));
                time += 120;
            }
            Assert.Equal(expected, steps.Sum());
            Assert.All(steps, step => Assert.Equal(1, Math.Abs(step)));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DiscreteWheelBurstsRespectTheMinimumInterval()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var strip = new KeyFrameStripControl();
            var steps = new List<int>();
            strip.WheelStepRequested += (_, step) => steps.Add(step);
            // 普通滚轮的密集事件仍受最小间隔限制。
            for (ulong time = 0; time < 100; time += 4)
                Assert.True(strip.ApplyWheel(new Vector(0, -0.02), KeyModifiers.None, 5000 + time));
            Assert.Equal(7, steps.Count);
            Assert.All(steps, step => Assert.Equal(1, step));
            // 增量为 0 的事件不处理。
            Assert.False(strip.ApplyWheel(default, KeyModifiers.None, 9000));
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(-0.02, 50, 1)]
    [InlineData(0.1, 10, -1)]
    public async Task PreciseScrollingAccumulatesSmallEventsBeforeMoving(double amount, int count, int expected)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var strip = new KeyFrameStripControl();
            var steps = new List<int>();
            strip.WheelStepRequested += (_, step) => steps.Add(step);
            for (var index = 0; index < count - 1; index++)
                Assert.True(strip.ApplyWheel(new Vector(0, amount), KeyModifiers.None, (ulong)(1000 + index * 4), precise: true));
            Assert.Empty(steps);
            strip.ApplyWheel(new Vector(0, amount), KeyModifiers.None, (ulong)(1000 + (count - 1) * 4), precise: true);
            Assert.Equal(new[] { expected }, steps);

            // 长滑可以继续逐格移动；单个大增量只移动一步，超出的余量清零。
            for (ulong time = 2000; time < 2020; time++)
                strip.ApplyWheel(new Vector(0, amount < 0 ? -0.25 : 0.25), KeyModifiers.None, time, precise: true);
            Assert.Equal(6, steps.Count);
            strip.ApplyWheel(new Vector(0, amount < 0 ? -4 : 4), KeyModifiers.None, 3000, precise: true);
            Assert.Equal(7, steps.Count);
            Assert.All(steps, step => Assert.Equal(expected, step));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PreciseRemaindersResetWhenDirectionModifiersOrWheelTypeChange()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var strip = new KeyFrameStripControl { WheelStep = 3, ShiftWheelStep = 12 };
            var steps = new List<int>();
            strip.WheelStepRequested += (_, step) => steps.Add(step);
            ulong time = 1000;
            void Scroll(double amount, KeyModifiers modifiers = KeyModifiers.None, bool precise = true) =>
                Assert.True(strip.ApplyWheel(new Vector(0, amount), modifiers, time += 20, precise));
            Scroll(-0.75);
            Scroll(0.25);
            Assert.Empty(steps);
            Scroll(0.75);
            Assert.Equal(new[] { -3 }, steps);
            Scroll(-0.75);
            Scroll(-0.25, KeyModifiers.Shift);
            Assert.Single(steps);
            Scroll(-0.75, KeyModifiers.Shift);
            Assert.Equal(new[] { -3, 12 }, steps);
            Scroll(-0.75);
            strip.ReverseWheel = true;
            Scroll(-0.25);
            Assert.Equal(2, steps.Count);
            Scroll(-0.75);
            Assert.Equal(new[] { -3, 12, -3 }, steps);
            Scroll(-0.75);
            Scroll(-0.2, precise: false);
            Scroll(-0.25);
            Assert.Equal(new[] { -3, 12, -3, -3 }, steps);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task StepSizeShiftAccelerationAndDirectionFollowTheSettings()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var strip = new KeyFrameStripControl { WheelStep = 3, ShiftWheelStep = 12 };
            var steps = new List<int>();
            strip.WheelStepRequested += (_, step) => steps.Add(step);
            strip.ApplyWheel(new Vector(0, -1), KeyModifiers.None, 1000);
            strip.ApplyWheel(new Vector(0, 1), KeyModifiers.None, 2000);
            strip.ApplyWheel(new Vector(0, -1), KeyModifiers.Shift, 3000);
            // macOS 按住 Shift 滚动时增量落在 X 上，仍按纵向滚动处理。
            strip.ApplyWheel(new Vector(-0.4, 0), KeyModifiers.Shift, 4000);
            // 不按 Shift 的横向滚动（触控板左右滑动、滚轮左右拨动）不处理。
            Assert.False(strip.ApplyWheel(new Vector(-1, 0), KeyModifiers.None, 5000));
            strip.ReverseWheel = true;
            strip.ApplyWheel(new Vector(0, -1), KeyModifiers.None, 6000);
            Assert.Equal(new[] { 3, -3, 12, 12, -3 }, steps);
        }, CancellationToken.None);
    }
}
