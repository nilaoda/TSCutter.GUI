using Avalonia;
using SkiaSharp;
using TSCutter.GUI.Web;
using Xunit;

namespace TSCutter.GUI.Tests.Application;

public sealed class WebUiRenderingTests
{
    [Fact]
    public void IncrementalWindowsPreservePixelsAndRestoreAreasExposedByDialogs()
    {
        using var buffers = new WebUiFrameBuffers();
        var main = new WebUiRecordedWindow();
        var dialog = new WebUiRecordedWindow();
        Render(Layer(main, new PixelSize(24, 16), default, Picture(canvas => canvas.Clear(SKColors.Red))),
            Layer(dialog, new PixelSize(6, 4), new SKPoint(4, 4), Picture(canvas => canvas.Clear(SKColors.Blue))));
        Assert.Equal(SKColors.Red, Pixel(0, 0));
        Assert.Equal(SKColors.Blue, Pixel(5, 5));

        Render(Layer(main, new PixelSize(24, 16), default, Picture(canvas =>
            {
                canvas.ClipRect(new SKRect(0, 0, 4, 4));
                canvas.Clear(SKColors.Green);
            })),
            Layer(dialog, new PixelSize(6, 4), new SKPoint(12, 8)));
        Assert.Equal(SKColors.Green, Pixel(2, 2));
        Assert.Equal(SKColors.Red, Pixel(5, 5));
        Assert.Equal(SKColors.Blue, Pixel(13, 9));

        Render(Layer(main, new PixelSize(24, 16), default));
        Assert.Equal(SKColors.Red, Pixel(13, 9));
        Assert.Equal(SKColors.Green, Pixel(2, 2));
        using var completed = buffers.SnapshotIfChanged(true, out _);
        using var unchanged = new WebUiCapturedFrame([Layer(main, new PixelSize(24, 16), default)],
            new PixelSize(24, 16), new PixelSize(24, 16), 85, 1);
        Assert.False(buffers.Rasterize(unchanged));
        Assert.Null(buffers.SnapshotIfChanged(false, out var changed));
        Assert.False(changed);
        using var refresh = buffers.SnapshotIfChanged(true, out changed);
        using var refreshedPixels = SKBitmap.FromImage(refresh!);
        Assert.Equal(SKColors.Green, refreshedPixels.GetPixel(2, 2));
        Assert.False(changed);

        void Render(params WebUiWindowFrame[] windows)
        {
            using var frame = new WebUiCapturedFrame(windows, new PixelSize(24, 16), new PixelSize(24, 16), 85, 1);
            buffers.Rasterize(frame);
        }
        SKColor Pixel(int x, int y)
        {
            using var image = buffers.SnapshotIfChanged(true, out _);
            using var pixels = SKBitmap.FromImage(image!);
            return pixels.GetPixel(x, y);
        }
    }

    [Fact]
    public async Task OverflowAndAbandonedCoalescedUpdatesRequireAFullRefresh()
    {
        var window = new WebUiRecordedWindow();
        try
        {
            window.BeginRefresh();
            for (var i = 0; i < 6; ++i)
                window.Publish(Picture(canvas => canvas.Clear(SKColors.Blue)), new PixelSize(24, 16));
            Assert.True(window.NeedsRefresh);
            Assert.Empty(window.TakePictures());

            window.BeginRefresh();
            window.Publish(Picture(canvas => canvas.Clear(SKColors.Red)), new PixelSize(24, 16));
            Assert.False(window.NeedsRefresh);
            var pictures = window.TakePictures();
            Assert.Single(pictures);
            foreach (var picture in pictures)
                picture.Dispose();

            window.BeginRefresh();
            var full = new Rect(0, 0, 24, 16);
            window.Publish(RegionPicture(SKColors.Blue, full), new PixelSize(24, 16), [full]);
            await Task.Delay(300);
            window.Publish(RegionPicture(SKColors.Green, full), new PixelSize(24, 16), [full]);
            Assert.True(window.NeedsRefresh);
            Assert.Empty(window.TakePictures());
        }
        finally { window.Reset(); }
    }

    [Fact]
    public void CoveredUpdatesAreCoalescedWithoutDroppingOtherRegions()
    {
        var window = new WebUiRecordedWindow();
        var size = new PixelSize(24, 16);
        var full = new Rect(0, 0, 24, 16);
        var left = new Rect(0, 0, 4, 4);
        var right = new Rect(8, 0, 4, 4);
        try
        {
            window.BeginRefresh();
            window.Publish(RegionPicture(SKColors.Red, full), size, [full]);
            window.Publish(RegionPicture(SKColors.Green, left), size, [left]);
            var obsolete = RegionPicture(SKColors.Blue, right);
            window.Publish(obsolete, size, [right]);
            for (var index = 0; index < 8; ++index)
                window.Publish(RegionPicture(SKColors.Magenta, right), size, [right]);
            Assert.False(window.NeedsRefresh);
            Assert.Equal(IntPtr.Zero, obsolete.Handle);
            var pictures = window.TakePictures();
            Assert.Equal(3, pictures.Length);
            using var frame = new WebUiCapturedFrame([Layer(window, size, default, pictures)], size, size, 85, 1);
            using var buffers = new WebUiFrameBuffers();
            buffers.Rasterize(frame);
            using var image = buffers.SnapshotIfChanged(true, out _);
            using var pixels = SKBitmap.FromImage(image!);
            Assert.Equal(SKColors.Green, pixels.GetPixel(1, 1));
            Assert.Equal(SKColors.Magenta, pixels.GetPixel(9, 1));
            Assert.Equal(SKColors.Red, pixels.GetPixel(1, 8));
        }
        finally { window.Reset(); }
    }

    [Fact]
    public void DisjointReplacementRegionsDoNotEraseTheirGapsAndFullRepaintsCanReplaceUnknownUpdates()
    {
        var window = new WebUiRecordedWindow();
        var size = new PixelSize(24, 16);
        var full = new Rect(0, 0, 24, 16);
        Rect[] edges = [new Rect(0, 0, 4, 16), new Rect(20, 0, 4, 16)];
        try
        {
            window.BeginRefresh();
            var original = RegionPicture(SKColors.Red, full);
            window.Publish(original, size, [full]);
            window.Publish(RegionPicture(SKColors.Blue, edges), size, edges);
            var pictures = window.TakePictures();
            Assert.Equal(2, pictures.Length);
            using var frame = new WebUiCapturedFrame([Layer(window, size, default, pictures)], size, size, 85, 1);
            using var buffers = new WebUiFrameBuffers();
            buffers.Rasterize(frame);
            using var image = buffers.SnapshotIfChanged(true, out _);
            using var pixels = SKBitmap.FromImage(image!);
            Assert.Equal(SKColors.Blue, pixels.GetPixel(1, 1));
            Assert.Equal(SKColors.Red, pixels.GetPixel(10, 1));

            var unknown = RegionPicture(SKColors.Green, full);
            window.Publish(unknown, size);
            window.Publish(RegionPicture(SKColors.Blue, edges), size, edges);
            window.Publish(RegionPicture(SKColors.White, full), size, [full]);
            Assert.Equal(IntPtr.Zero, unknown.Handle);
            var replacement = window.TakePictures();
            Assert.Single(replacement);
            foreach (var picture in replacement)
                picture.Dispose();
        }
        finally { window.Reset(); }
    }

    [Fact]
    public void DeferredSnapshotPreservesPendingChangesAcrossEmptyCaptures()
    {
        var size = new PixelSize(24, 16);
        var window = new WebUiRecordedWindow();
        window.BeginRefresh();
        using var buffers = new WebUiFrameBuffers();
        Update(SKColors.Red, new Rect(0, 0, 24, 16));
        using var original = buffers.SnapshotIfChanged(false, out var changed);
        Assert.True(changed);
        Update(SKColors.Blue, new Rect(0, 0, 3, 3));
        Update(SKColors.Green, new Rect(20, 12, 4, 4));
        using var empty = new WebUiCapturedFrame([Layer(window, size, default)], size, size, 85, 1);
        Assert.False(buffers.Rasterize(empty));
        using var completed = buffers.SnapshotIfChanged(false, out changed);
        Assert.True(changed);
        using var pixels = SKBitmap.FromImage(completed!);
        Assert.Equal(SKColors.Blue, pixels.GetPixel(1, 1));
        Assert.Equal(SKColors.Green, pixels.GetPixel(22, 14));
        Assert.Equal(SKColors.Red, pixels.GetPixel(10, 8));
        using var oldPixels = SKBitmap.FromImage(original!);
        Assert.Equal(SKColors.Red, oldPixels.GetPixel(1, 1));
        Assert.Null(buffers.SnapshotIfChanged(false, out changed));
        Assert.False(changed);
        Update(SKColors.Green, new Rect(20, 12, 4, 4));
        Assert.Null(buffers.SnapshotIfChanged(false, out changed));
        Assert.False(changed);

        void Update(SKColor color, Rect region)
        {
            window.Publish(RegionPicture(color, region), size, [region]);
            var pictures = window.TakePictures(out var damage);
            Assert.Equal(region, damage);
            using var frame = new WebUiCapturedFrame([new WebUiWindowFrame(window, pictures,
                size, default, new SKPoint(1, 1), damage)], size, size, 85, 1);
            buffers.Rasterize(frame);
        }
    }

    [Fact]
    public void DamageCompositionMatchesFullWindowCompositionAcrossScalingAndLayoutChanges()
    {
        var size = new PixelSize(96, 64);
        var main = new WebUiRecordedWindow();
        var dialog = new WebUiRecordedWindow();
        using var buffers = new WebUiFrameBuffers();
        using var reference = new WebUiFrameBuffers();
        var popupSize = new PixelSize(25, 20);
        var popupOffset = new SKPoint(19.5f, 13.25f);
        var popupScale = new SKPoint(1.25f, 1.25f);
        Check(Main(SKColors.Red, new Rect(0, 0, 96, 64)), Popup(SKColors.Blue.WithAlpha(128), new Rect(0, 0, 20, 16)));
        Check(Main(SKColors.Green, new Rect(4, 4, 6, 6)), Popup());
        Check(Main(SKColors.White, new Rect(80, 50, 6, 6)), Popup());
        Check(Main(SKColors.Yellow, new Rect(24, 16, 5, 5)), Popup());
        Check(Main(), Popup(SKColors.Transparent, new Rect(2, 2, 3, 3)));
        Check(Main(SKColors.Magenta, new Rect(40, 28, 4, 4)) with
        {
            Damage = null
        }, Popup());
        popupOffset = new SKPoint(48.5f, 30.25f);
        Check(Main(), Popup());
        Check(Popup(), Main());
        Check(Main());
        // 输出尺寸变化也必须丢弃旧双缓冲状态。
        size = new PixelSize(80, 60);
        Check(Main(SKColors.White, new Rect(0, 0, 80, 60)));

        WebUiWindowFrame Main(SKColor? color = null, Rect region = default) =>
            Make(main, size, default, new SKPoint(1, 1), color, region);
        WebUiWindowFrame Popup(SKColor? color = null, Rect region = default) =>
            Make(dialog, popupSize, popupOffset, popupScale, color, region);
        WebUiWindowFrame Make(WebUiRecordedWindow window, PixelSize windowSize, SKPoint offset, SKPoint scale, SKColor? color, Rect region)
        {
            if (color is null)
                return new(window, [], windowSize, offset, scale);
            using var recorder = new SKPictureRecorder();
            var canvas = recorder.BeginRecording(new SKRect(0, 0, 96, 64));
            canvas.ClipRect(new SKRect((float)region.Left, (float)region.Top, (float)region.Right, (float)region.Bottom));
            canvas.Clear(color.Value);
            return new(window, [recorder.EndRecording()], windowSize, offset, scale, region);
        }
        void Check(params WebUiWindowFrame[] layers)
        {
            using var frame = new WebUiCapturedFrame(layers, size, size, 85, 1);
            // 对照路径逐次合成完整窗口，与优化路径共用指令但由 frame 统一释放。
            var full = new WebUiCapturedFrame(layers.Select(layer => layer with { Damage = null }).ToArray(), size, size, 85, 1);
            buffers.Rasterize(frame);
            reference.Rasterize(full);
            using var image = buffers.SnapshotIfChanged(true, out var changed);
            using var expected = reference.SnapshotIfChanged(true, out var referenceChanged);
            using var actualPixels = SKBitmap.FromImage(image!);
            using var expectedPixels = SKBitmap.FromImage(expected!);
            Assert.Equal(referenceChanged, changed);
            Assert.True(actualPixels.GetPixelSpan().SequenceEqual(expectedPixels.GetPixelSpan()));
        }
    }

    private static SKPicture RegionPicture(SKColor color, params Rect[] regions) => Picture(canvas =>
    {
        using var clip = new SKPath();
        foreach (var region in regions)
            clip.AddRect(new SKRect((float)region.Left, (float)region.Top, (float)region.Right, (float)region.Bottom));
        canvas.ClipPath(clip);
        canvas.Clear(color);
    });

    private static WebUiWindowFrame Layer(WebUiRecordedWindow window, PixelSize size, SKPoint offset, params SKPicture[] pictures) =>
        new(window, pictures, size, offset, new SKPoint(1, 1));

    private static SKPicture Picture(Action<SKCanvas> draw)
    {
        using var recorder = new SKPictureRecorder();
        draw(recorder.BeginRecording(new SKRect(0, 0, 24, 16)));
        return recorder.EndRecording();
    }
}
