using Avalonia;
using Avalonia.Headless;
using Avalonia.Data;
using Avalonia.Media.Imaging;
using System.Reflection;
using Splat;
using TSCutter.GUI.Controls;
using Xunit;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Services.Preview;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.ViewModels.Editing;

namespace TSCutter.GUI.Tests.Preview;

public sealed class NativeSubtitleFactAttribute : TSCutter.GUI.Tests.FFmpeg.NativeMediaFactAttribute
{
    public NativeSubtitleFactAttribute(string? requiredVariable = null)
        : base(requiredVariable is null ? "subtitles" : null, requiredVariable) { }
}

[Collection("Avalonia UI")]
public sealed class SubtitlePreviewTests
{
    [Fact]
    public void OverlayUsesTheVideoTransformForZoomAndPan()
    {
        var rectangle = ImageViewer.GetSubtitlePartRect(new Rect(-100, 20, 640, 360), new PixelSize(320, 180),
            new Rect(20, 40, 32, 16));
        Assert.Equal(new Rect(-60, 100, 64, 32), rectangle);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void ActualSizeKeepsSubtitlePixelsAtOnePhysicalPixel(double dpiScale)
    {
        var canvas = new PixelSize(3840, 2160);
        var rectangle = ImageViewer.GetSubtitlePartRect(
            new Rect(0, 0, canvas.Width / dpiScale, canvas.Height / dpiScale), canvas,
            new Rect(100, 200, 480, 84));
        Assert.Equal(100, rectangle.X * dpiScale, 6);
        Assert.Equal(200, rectangle.Y * dpiScale, 6);
        Assert.Equal(480, rectangle.Width * dpiScale, 6);
        Assert.Equal(84, rectangle.Height * dpiScale, 6);
    }

    [Fact]
    public void DefaultTextStyleOnlyChangesTheGeneratedDefaultStyle()
    {
        const string header = "[Script Info]\r\nPlayResX: 1920\r\nPlayResY: 1080\r\n"
            + "[V4+ Styles]\r\nFormat: Name, Fontname, Fontsize, Bold, Outline, Shadow, Alignment, MarginL, MarginR, MarginV\r\n"
            + "Style: Default,Arial,20,-1,2,3,1,10,10,10\r\nStyle: Sign,Custom Font,24,-1,1,0,8,5,5,5\r\n"
            + "[Events]\r\nFormat: Layer, Text\r\nDialogue: 0,{\\fnArial}中英文 ABC\r\n";
        var actual = System.Text.Encoding.UTF8.GetString(SubtitlePreviewService.ApplyDefaultTextStyle(
            System.Text.Encoding.UTF8.GetBytes(header), "System CJK Font"));
        Assert.Equal(header.Replace("Style: Default,Arial,20,-1,2,3,1,10,10,10",
            "Style: Default,System CJK Font,42,0,0,0,2,96,96,59"), actual);
    }

    [Fact]
    public void TextBoundsIgnoreUnevenTransparentMarginsAndStridePadding()
    {
        var mask = new byte[12 * 10];
        for (var y = 0; y < 10; y++)
        {
            mask[y * 12 + 10] = mask[y * 12 + 11] = 255;
            for (var x = 2; x < 7; x++)
                if (y is >= 1 and < 4) mask[y * 12 + x] = 255;
        }
        mask[0] = 1;
        Assert.Equal(new PixelRect(2, 1, 5, 3), SubtitlePreviewService.GetVisibleTextBounds(mask, 10, 10, 12, 128));
        Assert.Null(SubtitlePreviewService.GetVisibleTextBounds(mask, 10, 10, 12, 0));
        Assert.Null(SubtitlePreviewService.GetVisibleTextBounds(new byte[12 * 10], 10, 10, 12, 255));
    }

    [Fact]
    public async Task DefaultTextBackdropEnclosesMultilineTextAndBlendsWithTheVideo()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            using var video = new WriteableBitmap(new PixelSize(320, 180), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            using (var buffer = video.Lock())
                System.Runtime.InteropServices.Marshal.Copy(Enumerable.Repeat((byte)255, buffer.RowBytes * 180).ToArray(),
                    0, buffer.Address, buffer.RowBytes * 180);
            var first = new WriteableBitmap(new PixelSize(60, 8), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            var second = new WriteableBitmap(new PixelSize(40, 8), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            foreach (var bitmap in new[] { first, second })
            {
                using var buffer = bitmap.Lock();
                System.Runtime.InteropServices.Marshal.Copy(new byte[buffer.RowBytes * 8], 0, buffer.Address, buffer.RowBytes * 8);
            }
            using var overlay = SubtitlePreviewService.CreateDefaultTextOverlay(new PixelSize(320, 180),
                [new(first, 10, 20), new(second, 20, 30)]);
            var backdrop = Assert.IsType<SubtitleBackdrop>(overlay.Backdrop);
            Assert.InRange(Math.Abs(backdrop.Bounds.Bottom - 180 * 0.945), 0, 0.5);
            Assert.All(overlay.Parts, part => Assert.True(backdrop.Bounds.Contains(
                new Rect(part.X, part.Y, part.Bitmap.PixelSize.Width, part.Bitmap.PixelSize.Height))));
            var text = new Rect(overlay.Parts[0].X, overlay.Parts[0].Y, 60, 8)
                .Union(new Rect(overlay.Parts[1].X, overlay.Parts[1].Y, 40, 8));
            Assert.Equal(text.Center, backdrop.Bounds.Center);
            var viewer = new ImageViewer { Image = video, SubtitleOverlay = overlay, AutoFit = true };
            viewer.Measure(new Size(320, 180));
            viewer.Arrange(new Rect(0, 0, 320, 180));
            using var rendered = new RenderTargetBitmap(new PixelSize(320, 180));
            rendered.Render(viewer);
            var pixels = ReadPixels(new(rendered, 0, 0));
            var center = ((int)backdrop.Bounds.Center.Y * 320 + (int)backdrop.Bounds.Center.X) * 4;
            Assert.InRange(pixels[center], (byte)106, (byte)108);
            Assert.Equal(255, pixels[center + 3]);
            Assert.Equal(255, pixels[(10 * 320 + 10) * 4]);
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SystemDefaultSubtitleFontSupportsMixedTextWhenAvailable()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var family = SubtitlePreviewService.GetSystemSubtitleFontFamily();
            if (family == "sans-serif") return;
            Assert.True(Avalonia.Media.FontManager.Current.TryGetGlyphTypeface(new Avalonia.Media.Typeface(family), out var font));
            Assert.True(font.TryGetGlyph('中', out _), family);
            Assert.True(font.TryGetGlyph('G', out _), family);
        }, CancellationToken.None);
    }

    [NativeSubtitleFact]
    public async Task TextSubtitlesRenderAtNativeResolutionAbove1080p()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            foreach (var name in new[] { "text.mp4", "animated.mkv" })
            {
                using var video = OpenVideo(name);
                using var subtitles = Load(video, name);
                using var original = subtitles.Render(2, new PixelSize(1920, 1080));
                Assert.NotNull(original);
                var originalHeight = GetVisibleHeight(original.Parts[0]);
                foreach (var scale in new[] { 2, 4 })
                {
                    var size = new PixelSize(1920 * scale, 1080 * scale);
                    using var overlay = subtitles.Render(2, size);
                    Assert.NotNull(overlay);
                    Assert.Equal(size, overlay.CanvasSize);
                    Assert.InRange(GetVisibleHeight(overlay.Parts[0]),
                        originalHeight * scale - 2 * scale, originalHeight * scale + 2 * scale);
                }
                using var restored = subtitles.Render(2, new PixelSize(1920, 1080));
                Assert.NotNull(restored);
                Assert.Equal(ReadPixels(original.Parts[0]), ReadPixels(restored.Parts[0]));
            }
        }, CancellationToken.None);
    }

    [NativeSubtitleFact]
    public async Task TextAndBitmapTracksRenderAtTheDisplayedTimeAndClearAfterTheirEnd()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            foreach (var name in new[] { "text.mp4", "bitmap.mkv", "bitmap.ts" })
            {
                using var video = OpenVideo(name);
                Assert.NotEmpty(video.PreviewSubtitleTracks);
                using var subtitles = Load(video, name);
                Assert.Null(subtitles.Render(0.5, new PixelSize(320, 180)));
                using var first = subtitles.Render(1.5, new PixelSize(320, 180));
                Assert.NotNull(first);
                Assert.NotEmpty(first.Parts);
                Assert.Equal(name == "text.mp4", first.Backdrop is not null);
                var bytes = ReadPixels(first.Parts[0]);
                Assert.True(Enumerable.Range(0, bytes.Length / 4).Any(i => bytes[i * 4 + 3] > 0), name);
                using var middle = subtitles.Render(name == "text.mp4" ? 4.0 : 2.5, new PixelSize(320, 180));
                Assert.NotNull(middle);
                Assert.Null(subtitles.Render(name == "text.mp4" ? 7.8 : 3.5, new PixelSize(320, 180)));
                using var backward = subtitles.Render(1.5, new PixelSize(320, 180));
                Assert.NotNull(backward);
                Assert.Equal(bytes, ReadPixels(backward.Parts[0]));
                Assert.Equal(first.Parts[0].X, backward.Parts[0].X);
                if (name == "text.mp4")
                {
                    Assert.Contains("English preview", video.PreviewSubtitleTracks[0].DisplayText);
                    Assert.Contains("简体中文", video.PreviewSubtitleTracks[1].DisplayText);
                    using var chinese = Load(video, name, 1);
                    using var overlay = chinese.Render(2, new PixelSize(320, 180));
                    Assert.NotNull(overlay);
                }
            }
        }, CancellationToken.None);
    }

    [NativeSubtitleFact]
    public async Task AssPreservesAnimationAndAttachedFontsAcrossSeekingAndResizing()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            using var video = OpenVideo("animated.mkv");
            using var subtitles = Load(video, "animated.mkv");
            using var first = subtitles.Render(1.5, new PixelSize(320, 180));
            using var later = subtitles.Render(4, new PixelSize(320, 180));
            Assert.NotNull(first);
            Assert.NotNull(later);
            Assert.Null(first.Backdrop);
            Assert.True(later.Parts[0].X > first.Parts[0].X);
            using var resized = subtitles.Render(4, new PixelSize(640, 360));
            Assert.NotNull(resized);
            Assert.Equal(new PixelSize(640, 360), resized.CanvasSize);
            using var backward = subtitles.Render(1.5, new PixelSize(320, 180));
            Assert.NotNull(backward);
            Assert.Equal(first.Parts[0].X, backward.Parts[0].X);
            Assert.Null(subtitles.Render(5.1, new PixelSize(320, 180)));
        }, CancellationToken.None);
    }

    [NativeSubtitleFact]
    public async Task TrackSwitchingPublishesOnlyTheLatestOverlayAndOffClearsIt()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var resolverScope = resolver.WithResolver();
            var locale = new LocalizationService();
            locale.SwitchLanguage("zh-CN");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            var vm = new MainWindowViewModel(null!, new ConfigurationService(locale)) { VideoPath = SamplePath("text.mp4") };
            try
            {
                var load = typeof(MainWindowViewModel).GetMethod("LoadVideoAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.True(await (Task<bool>)load.Invoke(vm, [OpenVideo("text.mp4")])!);
                Assert.True(vm.HasPreviewSubtitles);
                Assert.True(vm.CanSelectPreviewSubtitle);
                Assert.Equal(3, vm.PreviewSubtitleTracks.Count);
                await vm.SeekToTimeAndDrawFrameAsync(TimeSpan.FromSeconds(2));
                vm.SelectedPreviewSubtitleTrack = vm.PreviewSubtitleTracks[1];
                vm.SelectedPreviewSubtitleTrack = vm.PreviewSubtitleTracks[2];
                vm.SelectedPreviewSubtitleTrack = vm.PreviewSubtitleTracks[0];
                Assert.Null(vm.SubtitleOverlay);
                vm.SelectedPreviewSubtitleTrack = vm.PreviewSubtitleTracks[2];
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (vm.IsSubtitleLoading || vm.SubtitleOverlay is null)
                    await Task.Delay(10, timeout.Token);
                Assert.Same(vm.PreviewSubtitleTracks[2], vm.SelectedPreviewSubtitleTrack);
                var viewer = new ImageViewer { DataContext = vm, AutoFit = true };
                viewer.Bind(ImageViewer.ImageProperty, new Binding(nameof(vm.DecodedBitmap)));
                viewer.Bind(ImageViewer.SourcePixelSizeProperty, new Binding(nameof(vm.DecodedFrameSourceSize)));
                viewer.Bind(ImageViewer.SubtitleOverlayProperty, new Binding(nameof(vm.SubtitleOverlay)));
                viewer.Measure(new Size(640, 360));
                viewer.Arrange(new Rect(0, 0, 640, 360));
                using var rendered = new RenderTargetBitmap(new PixelSize(640, 360));
                rendered.Render(viewer);
                if (Environment.GetEnvironmentVariable("TSCUTTER_SUBTITLE_PREVIEW_IMAGE") is { Length: > 0 } image)
                    rendered.Save(image);
                vm.SelectedPreviewSubtitleTrack = vm.PreviewSubtitleTracks[0];
                Assert.Null(viewer.SubtitleOverlay);
                Assert.Null(vm.SubtitleOverlay);
            }
            finally { await vm.CloseVideoClickCommand.ExecuteAsync(null); }
            Assert.Empty(vm.PreviewSubtitleTracks);
            return true;
        }, CancellationToken.None);
    }

    [NativeSubtitleFact]
    public async Task SwitchingVideoTracksRebuildsExportSettingsAndPreservesQueuedSnapshots()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var resolverScope = resolver.WithResolver();
            var locale = new LocalizationService();
            locale.SwitchLanguage("zh-CN");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            var vm = new MainWindowViewModel(null!, new ConfigurationService(locale)) { VideoPath = SamplePath("multi-video.mp4") };
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var load = typeof(MainWindowViewModel).GetMethod("LoadVideoAsync", flags)!;
                Assert.True(await (Task<bool>)load.Invoke(vm, [OpenVideo("multi-video.mp4")])!);
                var settingsProperty = typeof(MainWindowViewModel).GetProperty("CurrentRemuxSettings", flags)!;
                var before = (RemuxExportSettings)settingsProperty.GetValue(vm)!;
                before.Container = "mkv";
                vm.ExportQueue.Add(new ExportQueueItem { RemuxSettings = before.Snapshot() });
                var choice = vm.PreviewVideoTracks[1];
                var change = typeof(MainWindowViewModel).GetMethod("SwitchPreviewTrackAsync", flags)!;
                await (Task)change.Invoke(vm, [choice])!;
                var after = (RemuxExportSettings)settingsProperty.GetValue(vm)!;
                Assert.Equal(choice.Selection.StreamIndex, after.ReferenceVideoIndex);
                Assert.Equal("mp4", after.Container);
                Assert.NotSame(before, after);
                Assert.Equal(before.ReferenceVideoIndex, vm.ExportQueue[0].RemuxSettings!.ReferenceVideoIndex);
                Assert.Equal("mkv", vm.ExportQueue[0].RemuxSettings!.Container);
            }
            finally { vm.Close(); }
            return true;
        }, CancellationToken.None);
    }

    [NativeSubtitleFact("TSCUTTER_SUBTITLE_REAL_SAMPLE")]
    public async Task RealMp4SubtitleTrackRendersAtAnInteriorTimestamp()
    {
        var path = Environment.GetEnvironmentVariable("TSCUTTER_SUBTITLE_REAL_SAMPLE")!;
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            using var video = new VideoInstance(path);
            video.InitVideo();
            var selection = Assert.Single(video.PreviewSubtitleTracks).Selection!;
            using var subtitles = SubtitlePreviewService.Load(path, selection, video.CodedVideoSize,
                video.TimelineOriginSeconds, video.GetVideoDurationInSeconds(), video.TsSyncOffset, CancellationToken.None);
            using var overlay = subtitles.Render(2.5, new PixelSize(1920, 1080));
            Assert.NotNull(overlay);
            Assert.NotEmpty(overlay.Parts);
        }, CancellationToken.None);
    }

    [Fact]
    public void CanceledSubtitleLoadStopsBeforeOpeningTheFile()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => SubtitlePreviewService.Load("nonexistent.mp4", new(0, 0, null),
            new PixelSize(320, 180), 0, 10, -1, cancellation.Token));
    }

    private static VideoInstance OpenVideo(string name)
    {
        var video = new VideoInstance(SamplePath(name));
        try { video.InitVideo(); return video; }
        catch { video.Dispose(); throw; }
    }

    private static SubtitlePreviewService Load(VideoInstance video, string name, int track = 0) =>
        SubtitlePreviewService.Load(SamplePath(name), video.PreviewSubtitleTracks[track].Selection!,
            video.CodedVideoSize, video.TimelineOriginSeconds, video.GetVideoDurationInSeconds(), video.TsSyncOffset, CancellationToken.None);

    private static string SamplePath(string name) => Path.Combine(
        TSCutter.GUI.Tests.FFmpeg.NativeTestEnvironment.FixtureDirectory("subtitles"), name);

    private static int GetVisibleHeight(SubtitleOverlayPart part)
    {
        var size = part.Bitmap.PixelSize;
        var pixels = ReadPixels(part);
        var mask = Enumerable.Range(0, pixels.Length / 4).Select(i => pixels[i * 4 + 3]).ToArray();
        return SubtitlePreviewService.GetVisibleTextBounds(mask, size.Width, size.Height, size.Width, 255)!.Value.Height;
    }

    private static unsafe byte[] ReadPixels(SubtitleOverlayPart part)
    {
        var size = part.Bitmap.PixelSize;
        var bytes = new byte[size.Width * size.Height * 4];
        fixed (byte* data = bytes)
            part.Bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), (nint)data, bytes.Length, size.Width * 4);
        return bytes;
    }
}
