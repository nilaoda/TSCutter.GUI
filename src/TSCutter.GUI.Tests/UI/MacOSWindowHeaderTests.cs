using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Themes;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class MacOSWindowHeaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeaderHandlesDragWithoutTakingClicksFromContentOrTrafficLights(bool dark)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var window = new ClassicWindow
            {
                Width = 400, Height = 250, Title = "Window title",
                RequestedThemeVariant = dark ? CustomTheme.MacOSDark : CustomTheme.MacOS,
                Content = new Border { Background = Avalonia.Media.Brushes.Transparent }
            };
            var handled = false;
            window.AddHandler(InputElement.PointerPressedEvent, (_, e) => handled = e.Handled, handledEventsToo: true);
            void Press(Point point, MouseButton button = MouseButton.Left)
            {
                handled = false;
                window.MouseDown(point, button);
                window.MouseUp(point, button);
            }
            try
            {
                window.Classes.Add("macos-native-titlebar");
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Press(new Point(200, 14));
                Assert.True(handled);
                Press(new Point(40, 14));
                Assert.False(handled);
                Press(new Point(200, 80));
                Assert.False(handled);
                Press(new Point(200, 14), MouseButton.Right);
                Assert.False(handled);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DoubleClickSupportsZoomMinimizeAndNoneWithWindowRestrictions()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var window = new ClassicWindow();
            try
            {
                window.Show();
                MacOSWindowHeader.ApplyDoubleClick(window, MacOSWindowHeader.DoubleClickAction.Zoom);
                Assert.Equal(WindowState.Maximized, window.WindowState);
                MacOSWindowHeader.ApplyDoubleClick(window, MacOSWindowHeader.DoubleClickAction.Zoom);
                Assert.Equal(WindowState.Normal, window.WindowState);
                MacOSWindowHeader.ApplyDoubleClick(window, MacOSWindowHeader.DoubleClickAction.None);
                Assert.Equal(WindowState.Normal, window.WindowState);
                window.CanResize = false;
                MacOSWindowHeader.ApplyDoubleClick(window, MacOSWindowHeader.DoubleClickAction.Zoom);
                Assert.Equal(WindowState.Normal, window.WindowState);
                window.CanResize = true;
                window.CanMaximize = false;
                MacOSWindowHeader.ApplyDoubleClick(window, MacOSWindowHeader.DoubleClickAction.Zoom);
                Assert.Equal(WindowState.Normal, window.WindowState);
                window.CanMinimize = false;
                MacOSWindowHeader.ApplyDoubleClick(window, MacOSWindowHeader.DoubleClickAction.Minimize);
                Assert.Equal(WindowState.Normal, window.WindowState);
                window.CanMinimize = true;
                MacOSWindowHeader.ApplyDoubleClick(window, MacOSWindowHeader.DoubleClickAction.Minimize);
                Assert.Equal(WindowState.Minimized, window.WindowState);
                window.WindowState = WindowState.FullScreen;
                MacOSWindowHeader.ApplyDoubleClick(window, MacOSWindowHeader.DoubleClickAction.Minimize);
                Assert.Equal(WindowState.FullScreen, window.WindowState);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }
}
