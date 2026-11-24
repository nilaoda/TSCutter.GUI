using System.Diagnostics;
using System.Text.Json;
using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Classic.Avalonia.Theme;
using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Web;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class WebUiDesktopTests
{
    [Fact]
    public async Task SceneNotificationsFollowWindowsAndModelsWithoutFeedingBackFromCapture()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            var model = new SceneModel();
            var window = new ClassicWindow { Width = 500, Height = 300, DataContext = model, Content = new TextBlock { Text = "Scene" } };
            using var observer = new WebUiSceneObserver([window]);
            try
            {
                window.Show();
                await Task.Delay(50);
                window.UpdateLayout();
                var version = observer.Version;
                model.Value = 1;
                Assert.True(observer.Version > version);
                window.DataContext = new SceneModel();
                version = observer.Version;
                model.Value = 2;
                Assert.Equal(version, observer.Version);
                using (observer.PauseLayoutNotifications())
                    window.UpdateLayout();
                Assert.Equal(version, observer.Version);
                window.UpdateLayout();
                Assert.True(observer.Version > version);
                version = observer.Version;
                var child = new ClassicWindow { Width = 200, Height = 100 };
                child.Show(window);
                Assert.True(observer.Version > version);
                version = observer.Version;
                child.Close();
                Assert.True(observer.Version > version);
                observer.Dispose();
                version = observer.Version;
                ((SceneModel)window.DataContext!).Value = 3;
                Assert.Equal(version, observer.Version);
                return true;
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task InputStateReleasesMissingKeysAndButtonsAndExpiresAbandonedHolds()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            var box = new TextBox { Margin = new Thickness(20), Height = 80, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            var window = new ClassicWindow { Content = box };
            using var lifetime = new WindowLifetime(window);
            using var desktop = new WebUiDesktop(lifetime, window);
            var released = new List<Key>();
            var pointerReleases = 0;
            window.AddHandler(InputElement.KeyUpEvent, (_, e) => released.Add(e.Key), RoutingStrategies.Tunnel, true);
            window.AddHandler(InputElement.PointerReleasedEvent, (_, _) => ++pointerReleases, RoutingStrategies.Tunnel, true);
            try
            {
                desktop.Initialize();
                window.Show();
                await Task.Delay(50);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                box.Focus();
                Send(new
                {
                    type = "keyDown",
                    key = "Control",
                    code = "ControlLeft",
                    control = true
                });
                Send(new
                {
                    type = "input-state",
                    keys = new[] { "ControlLeft" },
                    buttons = 0
                });
                desktop.ExpireInput(Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2);
                Assert.Empty(released);
                Send(new
                {
                    type = "input-state",
                    keys = new[] { 42 },
                    buttons = 0
                });
                Assert.Empty(released);
                Send(new
                {
                    type = "input-state",
                    keys = Array.Empty<string>(),
                    buttons = 0
                });
                Assert.Contains(Key.LeftCtrl, released);

                Send(new
                {
                    type = "keyDown",
                    key = "Shift",
                    code = "ShiftLeft"
                });
                desktop.ExpireInput(Stopwatch.GetTimestamp() + Stopwatch.Frequency * 4);
                Assert.Contains(Key.LeftShift, released);
                var point = box.TranslatePoint(new Point(10, 10), window)!.Value;
                Send(new
                {
                    type = "down",
                    x = point.X,
                    y = point.Y,
                    button = 0,
                    buttons = 1
                });
                await Task.Delay(1800);
                var version = desktop.SceneVersion;
                Send(new
                {
                    type = "input-state",
                    keys = Array.Empty<string>(),
                    buttons = 1
                });
                Assert.Equal(version, desktop.SceneVersion);
                await Task.Delay(1800);
                Assert.Equal(0, pointerReleases);
                Send(new
                {
                    type = "input-state",
                    keys = Array.Empty<string>(),
                    buttons = 0
                });
                Assert.True(pointerReleases > 0);
                return true;
            }
            finally { window.Close(); }

            void Send(object input)
            {
                using var json = JsonDocument.Parse(JsonSerializer.Serialize(input));
                desktop.HandleInput(json.RootElement);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RecordedFramesKeepTheirPixelsAfterSourcesChangeAndWindowsClose()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(WebUiRenderingTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var source = new WriteableBitmap(new PixelSize(16, 16), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
            var pixels = new byte[16 * 16 * 4];
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i + 2] = 255;
                pixels[i + 3] = 255;
            }
            using (var frame = source.Lock())
                Marshal.Copy(pixels, 0, frame.Address, pixels.Length);
            var image = new Image { Source = source, Width = 80, Height = 80 };
            var window = new ClassicWindow { Content = image };
            using var lifetime = new WindowLifetime(window);
            using var desktop = new WebUiDesktop(lifetime, window);
            var captures = new List<(WebUiCapturedFrame Frame, Point Center)>();
            try
            {
                desktop.Initialize();
                window.Show();
                foreach (var ratio in new[] { 1, 2 })
                {
                    using var input = JsonDocument.Parse(JsonSerializer.Serialize(new
                    {
                        type = "resize",
                        width = 800,
                        height = 600,
                        pixelRatio = ratio
                    }));
                    desktop.HandleInput(input.RootElement);
                    await Task.Delay(80);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    var center = image.TranslatePoint(new Point(40, 40), window)!.Value;
                    captures.Add((desktop.Capture(null), center));
                    var recorded = WebUiRenderBackend.GetWindow(window);
                    for (var repaint = 0; repaint < 6; ++repaint)
                    {
                        image.InvalidateVisual();
                        Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    }
                    Assert.False(recorded.NeedsRefresh);
                    var latest = recorded.TakePictures();
                    try
                    {
                        Assert.Single(latest);
                    }
                    finally
                    {
                        foreach (var picture in latest)
                            picture.Dispose();
                    }
                    if (ratio == 1)
                    {
                        window.Hide();
                        using var hidden = desktop.Capture(null);
                        Assert.False(hidden.Rendered);
                        Assert.True(WebUiRenderBackend.GetWindow(window).NeedsRefresh);
                        window.Show();
                    }
                }
                window.Close();
                using (var frame = source.Lock())
                    Marshal.Copy(new byte[pixels.Length], 0, frame.Address, pixels.Length);
                await Task.Run(() =>
                {
                    using var buffers = new WebUiFrameBuffers();
                    foreach (var (capture, center) in captures)
                    {
                        Assert.False(Dispatcher.UIThread.CheckAccess());
                        buffers.Rasterize(capture);
                        using var snapshot = buffers.SnapshotIfChanged(true, out _);
                        using var bitmap = SKBitmap.FromImage(snapshot!);
                        var scaleX = (double)capture.Size.Width / capture.Viewport.Width;
                        var scaleY = (double)capture.Size.Height / capture.Viewport.Height;
                        Assert.Equal(SKColors.Red, bitmap.GetPixel((int)(center.X * scaleX), (int)(center.Y * scaleY)));
                    }
                });
                return true;
            }
            finally
            {
                window.Close();
                foreach (var (capture, _) in captures)
                    capture.Dispose();
            }
        }, CancellationToken.None);
    }

    public static class WebUiRenderingTestAppBuilder
    {
        public static AppBuilder BuildAvaloniaApp() => ThemeTestAppBuilder.BuildAvaloniaApp().UseWebUiRendering();
    }

    [Fact]
    public async Task CursorFollowsInheritedAndDynamicStylesAndResetsAfterLeaving()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var child = new Border { Background = Avalonia.Media.Brushes.Transparent };
            var region = new Border { Cursor = new Cursor(StandardCursorType.SizeWestEast), Child = child };
            var window = new Window { Content = region };
            using var lifetime = new WindowLifetime(window);
            using var desktop = new WebUiDesktop(lifetime, window);
            var changes = new List<string>();
            desktop.CursorChanged += changes.Add;
            try
            {
                desktop.Initialize();
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Send(new
                {
                    type = "move",
                    x = 200,
                    y = 150,
                    buttons = 0
                });
                Assert.Equal("ew-resize", desktop.CursorStyle);
                Assert.Equal("ew-resize", changes.Last());
                region.Cursor = new Cursor(StandardCursorType.SizeAll);
                Assert.Equal("move", desktop.CursorStyle);
                var count = changes.Count;
                Send(new
                {
                    type = "move",
                    x = 210,
                    y = 150,
                    buttons = 0
                });
                Assert.Equal(count, changes.Count);
                child.Cursor = new Cursor(StandardCursorType.Ibeam);
                Assert.Equal("text", desktop.CursorStyle);
                Send(new
                {
                    type = "leave"
                });
                Assert.Equal("default", desktop.CursorStyle);
                Send(new
                {
                    type = "move",
                    x = 200,
                    y = 150,
                    buttons = 0
                });
                Assert.Equal("text", desktop.CursorStyle);
                child.Cursor = new Cursor(StandardCursorType.None);
                Assert.Equal("none", desktop.CursorStyle);
                desktop.ResetInput();
                Assert.Equal("default", desktop.CursorStyle);
                count = changes.Count;
                region.Cursor = new Cursor(StandardCursorType.Hand);
                Assert.Equal(count, changes.Count);
            }
            finally { window.Close(); }

            void Send(object input)
            {
                using var json = JsonDocument.Parse(JsonSerializer.Serialize(input));
                desktop.HandleInput(json.RootElement);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChildWindowCaptionShowsGrabAndGrabbingAndClearsOnClose()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var window = new ClassicWindow();
            var child = new ClassicWindow
            {
                Width = 400,
                Height = 250,
                Title = "Child",
                RequestedThemeVariant = TSCutter.GUI.Themes.CustomTheme.MacOS
            };
            child.Classes.Add("macos-native-titlebar");
            using var lifetime = new WindowLifetime(window, child);
            using var desktop = new WebUiDesktop(lifetime, window);
            try
            {
                desktop.Initialize();
                window.Show();
                child.Show(window);
                child.Position = new PixelPoint(100, 100);
                Dispatcher.UIThread.RunJobs();
                child.UpdateLayout();
                Send(new
                {
                    type = "move",
                    x = 300,
                    y = 114,
                    buttons = 0
                });
                Assert.Equal("grab", desktop.CursorStyle);
                Send(new
                {
                    type = "down",
                    x = 300,
                    y = 114,
                    button = 0,
                    buttons = 1
                });
                Assert.Equal("grabbing", desktop.CursorStyle);
                Send(new
                {
                    type = "move",
                    x = 320,
                    y = 134,
                    button = -1,
                    buttons = 1
                });
                Assert.Equal(new PixelPoint(120, 120), child.Position);
                Send(new
                {
                    type = "up",
                    x = 320,
                    y = 134,
                    button = 0,
                    buttons = 0
                });
                Assert.Equal("grab", desktop.CursorStyle);
                child.Close();
                Assert.Equal("default", desktop.CursorStyle);
            }
            finally { child.Close(); window.Close(); }

            void Send(object input)
            {
                using var json = JsonDocument.Parse(JsonSerializer.Serialize(input));
                desktop.HandleInput(json.RootElement);
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ResolutionKeepsWorkspaceAspectAndMatchesRetinaPixelsWithOptionalWidescreen()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var window = new Window();
            using var lifetime = new WindowLifetime(window);
            using var desktop = new WebUiDesktop(lifetime, window);
            try
            {
                desktop.Initialize();
                window.Show();
                Resize("auto");
                using (var frame = Capture())
                {
                    Assert.Equal(new PixelSize(670, 1450), frame.Viewport);
                    Assert.Equal(new PixelSize(1170, 2532), frame.Size);
                }
                Resize("2160");
                using (var frame = Capture())
                {
                    Assert.Equal(new PixelSize(670, 1450), frame.Viewport);
                    Assert.Equal(new PixelSize(1774, 3840), frame.Size);
                }
                Resize("2160", "16:9");
                using (var frame = Capture())
                {
                    Assert.Equal(new PixelSize(720, 405), frame.Viewport);
                    Assert.Equal(new PixelSize(3840, 2160), frame.Size);
                }
                Resize("auto", width: 1920, height: 1080, ratio: 2);
                using (var frame = Capture())
                {
                    Assert.Equal(new PixelSize(1920, 1080), frame.Viewport);
                    Assert.Equal(new PixelSize(3840, 2160), frame.Size);
                }
                Resize("2160", width: 866, height: 400);
                using (var frame = Capture())
                {
                    Assert.Equal(new PixelSize(866, 400), frame.Viewport);
                    Assert.Equal(new PixelSize(3840, 1774), frame.Size);
                }
            }
            finally { window.Close(); }

            void Resize(string resolution, string aspect = "auto", int width = 670, int height = 1450, double ratio = 1170d / 670)
            {
                using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
                {
                    type = "resize",
                    width,
                    height,
                    pixelRatio = ratio,
                    resolution,
                    aspectRatio = aspect
                }));
                desktop.HandleInput(json.RootElement);
            }
            WebUiCapturedFrame Capture()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                return desktop.Capture(WebUiMediaEncoder.VideoLimit);
            }
        }, CancellationToken.None);
    }

    private sealed class SceneModel : ObservableObject
    {
        private int value;
        public int Value
        {
            get => value; set => SetProperty(ref this.value, value);
        }
    }

    private sealed class WindowLifetime(params Window[] windows) : ClassicDesktopStyleApplicationLifetime, IClassicDesktopStyleApplicationLifetime
    {
        public new IReadOnlyList<Window> Windows { get; } = windows;
    }
}
