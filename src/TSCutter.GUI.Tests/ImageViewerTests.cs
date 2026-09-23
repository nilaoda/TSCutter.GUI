using Avalonia;
using TSCutter.GUI.Controls;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class ImageViewerTests
{
    [Fact]
    public void SourceSizeKeepsLowResolutionPreviewAtFullDisplayGeometry()
    {
        var result = ImageViewer.GetContentPixelSize(
            new PixelSize(1280, 720),
            new PixelSize(7680, 4320));

        Assert.Equal(new PixelSize(7680, 4320), result);
    }

    [Fact]
    public void MissingSourceSizeFallsBackToBitmapSize()
    {
        var result = ImageViewer.GetContentPixelSize(
            new PixelSize(1280, 720),
            default);

        Assert.Equal(new PixelSize(1280, 720), result);
    }

    [Fact]
    public void VisibleDisplayRegionMapsToPreviewBitmapCoordinates()
    {
        var result = ImageViewer.MapVisibleRectToBitmap(
            new Rect(100, 50, 400, 225),
            new Rect(0, 0, 800, 450),
            new PixelSize(1280, 720));

        Assert.Equal(new Rect(160, 80, 640, 360), result);
    }

    [Fact]
    public void DecodeTargetSizeUsesPhysicalViewerPixels()
    {
        var result = ImageViewer.CalculateDecodeTargetSize(new Size(960, 540), 1.0);

        Assert.Equal(new PixelSize(960, 540), result);
    }

    [Fact]
    public void FitTransformCentersImageWithoutWaitingForLayoutCallback()
    {
        var result = ImageViewer.CalculateFitTransform(
            new Size(1000, 700),
            new PixelSize(4000, 6000),
            1.0);

        Assert.NotNull(result);
        Assert.Equal(700d / 6000d, result.Value.Zoom, 6);
        Assert.Equal((1000d - 4000d * result.Value.Zoom) / 2, result.Value.OffsetX, 6);
        Assert.Equal(0, result.Value.OffsetY, 6);
    }

    [Fact]
    public void FitTransformAccountsForDisplayScaling()
    {
        var result = ImageViewer.CalculateFitTransform(
            new Size(1000, 700),
            new PixelSize(4000, 6000),
            2.0);

        Assert.NotNull(result);
        Assert.Equal(2 * 700d / 6000d, result.Value.Zoom, 6);
        Assert.Equal((1000d - 4000d * result.Value.Zoom / 2) / 2, result.Value.OffsetX, 6);
        Assert.Equal(0, result.Value.OffsetY, 6);
    }

    [Theory]
    [InlineData(1.0, 1.1, 0.01, 4.0, 1.1)]
    [InlineData(3.9, 1.1, 0.01, 4.0, 4.0)]
    [InlineData(0.011, 0.9, 0.01, 4.0, 0.01)]
    public void WheelZoomClampsToConfiguredRange(
        double zoom,
        double delta,
        double minimum,
        double maximum,
        double expected)
    {
        var result = ImageViewer.CalculateNextZoom(zoom, delta, minimum, maximum);

        Assert.Equal(expected, result, 6);
    }
}
