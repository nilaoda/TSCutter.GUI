using System.Globalization;
using TSCutter.GUI.ViewModels.Editing;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

/// <summary>
/// 输出窗口的智能剪辑计划摘要：只要有重编码就不显示 100% 无需重新编码，有复制就不显示 0%；
/// 只重编码 1 帧（不足 0.1 秒）时时长不显示成 0.0 秒。
/// </summary>
public sealed class FrameAccuratePlanTextTests
{
    [Theory]
    // 4K25 约 19 分钟的素材只重编码 1 帧：复制占 99.996%，四舍五入会显示 100%。
    [InlineData(0.04, 1138.16, "99.9")]
    [InlineData(1.88, 30.0, "94.1")]
    [InlineData(0.0, 30.0, "100")]
    [InlineData(30.0, 0.0, "0")]
    [InlineData(0.0, 0.0, "0")]
    [InlineData(1.0, 1.0, "50")]
    // 浮点误差：52.3% 不应向下取整成 52.2%。
    [InlineData(47.7, 52.3, "52.3")]
    // 只复制了极短的一段：不足 0.1%，不显示成 0%。
    [InlineData(1138.16, 0.04, "<0.1")]
    public void CopiedPercentNeverRoundsToTheEnds(double encode, double copy, string expected) =>
        WithInvariantCulture(() => Assert.Equal(expected, OutputWindowViewModel.CopiedPercent(encode, copy)));

    [Theory]
    [InlineData(0.0, "0.0")]
    // 60 帧/秒的 1 帧与 240 帧/秒的 1 帧。
    [InlineData(1 / 60.0, "0.02")]
    [InlineData(1 / 240.0, "0.01")]
    [InlineData(0.1, "0.1")]
    [InlineData(12.34, "12.3")]
    public void SecondsShowSingleFrames(double seconds, string expected) =>
        WithInvariantCulture(() => Assert.Equal(expected, OutputWindowViewModel.PlanSeconds(seconds)));

    private static void WithInvariantCulture(Action action)
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            action();
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }
}
