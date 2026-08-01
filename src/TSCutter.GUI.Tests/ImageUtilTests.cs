using Avalonia;
using TSCutter.GUI.Utils;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class ImageUtilTests
{
    [Theory]
    [InlineData(7680, 4320, 0, 0, 160, 90)]
    [InlineData(1920, 1080, 0, 0, 160, 90)]
    [InlineData(1440, 1080, 20, 0, 120, 90)]
    [InlineData(3840, 1600, 0, 11.666667, 160, 66.666667)]
    [InlineData(1080, 1920, 54.6875, 0, 50.625, 90)]
    public void CalculateAspectFitRectPreservesSourceAspectRatio(
        double sourceWidth,
        double sourceHeight,
        double expectedX,
        double expectedY,
        double expectedWidth,
        double expectedHeight)
    {
        var result = ImageUtil.CalculateAspectFitRect(
            new Size(sourceWidth, sourceHeight),
            new Size(160, 90));

        Assert.Equal(expectedX, result.X, 5);
        Assert.Equal(expectedY, result.Y, 5);
        Assert.Equal(expectedWidth, result.Width, 5);
        Assert.Equal(expectedHeight, result.Height, 5);
    }

    [Fact]
    public void CalculateAspectFitRectRejectsInvalidSourceSize()
    {
        var result = ImageUtil.CalculateAspectFitRect(new Size(0, 1080), new Size(160, 90));

        Assert.Equal(default, result);
    }
}
