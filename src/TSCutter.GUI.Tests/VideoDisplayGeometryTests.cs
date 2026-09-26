using Avalonia;
using TSCutter.GUI.Models;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class VideoDisplayGeometryTests
{
    [Theory]
    [InlineData(1, 1, false)]
    [InlineData(2, 2, false)]
    [InlineData(0, 1, false)]
    [InlineData(1, 0, false)]
    [InlineData(64, 45, true)]
    [InlineData(16, 15, true)]
    public void SampleAspectRatioCorrectionOnlyAppliesToNonSquarePixels(
        int numerator,
        int denominator,
        bool expected)
    {
        Assert.Equal(
            expected,
            VideoInstance.RequiresSampleAspectRatioCorrection(numerator, denominator));
    }

    [Theory]
    [InlineData(720, 576, 64, 45, 1024, 576)]
    [InlineData(720, 576, 16, 15, 768, 576)]
    [InlineData(1920, 1080, 1, 1, 1920, 1080)]
    [InlineData(720, 576, 0, 1, 720, 576)]
    [InlineData(720, 576, 1, 0, 720, 576)]
    [InlineData(576, 720, 1, 1, 576, 720)]
    public void DisplayPixelSizeAppliesSampleAspectRatio(
        int codedWidth,
        int codedHeight,
        int sarNumerator,
        int sarDenominator,
        int expectedWidth,
        int expectedHeight)
    {
        var result = VideoInstance.CalculateDisplayPixelSize(
            codedWidth,
            codedHeight,
            sarNumerator,
            sarDenominator);

        Assert.Equal(new PixelSize(expectedWidth, expectedHeight), result);
    }
}
