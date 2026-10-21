using Avalonia;
using TSCutter.GUI.Controls;
using Xunit;

namespace TSCutter.GUI.Tests.Preview;

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

    [Fact]
    public void NavigatorIsHiddenWhenFullImageFitsOrViewerIsTooSmall()
    {
        Assert.Null(ImageViewer.CalculateNavigatorLayout(new Size(800, 600), new Rect(0, 75, 800, 450)));
        Assert.Null(ImageViewer.CalculateNavigatorLayout(new Size(80, 60), new Rect(-10, -10, 160, 120)));
    }

    [Fact]
    public void NavigatorMapsVisibleAreaAndPreservesDisplayAspectRatio()
    {
        var layout = ImageViewer.CalculateNavigatorLayout(new Size(800, 600), new Rect(-400, -150, 1600, 900))!.Value;

        Assert.Equal(120, layout.Image.Width);
        Assert.Equal(67.5, layout.Image.Height);
        Assert.Equal(new Rect(layout.Image.X + 30, layout.Image.Y + 11.25, 60, 45), layout.Viewport);
        Assert.Equal(12, 800 - layout.Bounds.Right);
        Assert.Equal(12, 600 - layout.Bounds.Bottom);
    }

    [Fact]
    public void NavigatorRemainsAvailableToRecoverAnOffscreenImage()
    {
        var layout = ImageViewer.CalculateNavigatorLayout(new Size(800, 600), new Rect(900, 700, 1600, 900));

        Assert.NotNull(layout);
        Assert.Equal(default, layout.Value.Viewport);
    }

    [Theory]
    [InlineData(0.5, 0.5, -400, -150)]
    [InlineData(-1, -1, 0, 0)]
    [InlineData(2, 2, -800, -300)]
    public void NavigatorNavigationCentersTargetAndClampsAtImageEdges(double x, double y, double offsetX, double offsetY)
    {
        var offset = ImageViewer.CalculateNavigatorOffset(new Point(x, y), new Size(800, 600), new Size(1600, 900));

        Assert.Equal(new Point(offsetX, offsetY), offset);
    }

    [Fact]
    public void NavigatorKeepsTheUncroppedAxisCentered()
    {
        var offset = ImageViewer.CalculateNavigatorOffset(new Point(0, 1), new Size(800, 600), new Size(1600, 450));

        Assert.Equal(new Point(0, 75), offset);
    }
}
