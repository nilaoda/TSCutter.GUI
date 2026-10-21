using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Themes;
using Xunit;
using TSCutter.GUI.Services.Application;

namespace TSCutter.GUI.Tests.UI;

[CollectionDefinition("Avalonia UI", DisableParallelization = true)]
public sealed class AvaloniaUiCollection;

[Collection("Avalonia UI")]
public sealed class ThemeSwitchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComboBoxesKeepControlItemsAndSelectionAfterThemeSwitch(bool keepOpen)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var app = Avalonia.Application.Current!;
            app.RequestedThemeVariant = ClassicTheme.Standard;
            var controlItems = new[] { new TextBlock { Text = "First" }, new TextBlock { Text = "Second" } };
            var dataItems = new[] { new Option("First"), new Option("Second") };
            var controls = new ComboBox { ItemsSource = controlItems, SelectedIndex = 1 };
            var data = new ComboBox
            {
                ItemsSource = dataItems, SelectedIndex = 1,
                DisplayMemberBinding = new Binding(nameof(Option.Label))
            };
            var window = new ClassicWindow
            {
                Width = 400, Height = 250,
                Content = new StackPanel { Children = { controls, data } }
            };
            try
            {
                window.Show();
                void Flush()
                {
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                }
                foreach (var theme in new[]
                         {
                             ClassicTheme.Standard, CustomTheme.Windows11, CustomTheme.Windows11Dark,
                             CustomTheme.LunaBlue, CustomTheme.Aero, CustomTheme.Windows10, ClassicTheme.Standard
                         })
                {
                    app.RequestedThemeVariant = theme;
                    Flush();
                    foreach (var combo in new[] { controls, data })
                    {
                        combo.IsDropDownOpen = true;
                        Flush();
                        Assert.Equal(1, combo.SelectedIndex);
                        Assert.Same(combo.Items[1], combo.SelectedItem);
                        Assert.NotNull(combo.ContainerFromIndex(1));
                        combo.IsDropDownOpen = keepOpen;
                        Flush();
                    }
                    Assert.Contains(data.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Second");
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LunaMenuBarKeepsReadableTextAcrossMenuStates()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var radio = new MenuItem { Header = "Windows XP", ToggleType = MenuItemToggleType.Radio, IsChecked = true };
            var disabled = new MenuItem { Header = "不可用", IsEnabled = false };
            var nested = new MenuItem { Header = "主题", ItemsSource = new[] { radio, disabled } };
            var top = new MenuItem { Header = "视图(V)", ItemsSource = new[] { nested } };
            var menu = new Menu { ItemsSource = new[] { top, new MenuItem { Header = "帮助(H)" } } };
            var layout = new DockPanel { Children = { menu, new TextBlock { Text = "内容" } } };
            DockPanel.SetDock(menu, Dock.Top);
            var window = new ClassicWindow { Width = 500, Height = 300, Content = layout };
            try
            {
                window.Show();
                void Flush()
                {
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                }
                IBrush? HeaderColor(MenuItem item) => item.GetVisualDescendants()
                    .OfType<Avalonia.Controls.Presenters.ContentPresenter>()
                    .Single(p => p.Name == "PART_HeaderPresenter").Foreground;
                void AssertBarColor()
                {
                    Assert.Equal(top.FindResource(Classic.CommonControls.SystemColors.MenuTextBrushKey)?.ToString(),
                        HeaderColor(top)?.ToString());
                }
                foreach (var theme in new[] { CustomTheme.LunaBlue, CustomTheme.LunaSilver, CustomTheme.LunaOliveGreen })
                {
                    Avalonia.Application.Current!.RequestedThemeVariant = theme;
                    window.MouseMove(new Point(350, 250));
                    Flush();
                    AssertBarColor();
                    var point = top.TranslatePoint(new Point(top.Bounds.Width / 2, top.Bounds.Height / 2), window)!.Value;
                    window.MouseMove(point);
                    Flush();
                    Assert.Contains(":selected", top.Classes);
                    Assert.False(top.IsSubMenuOpen);
                    AssertBarColor();
                    top.IsEnabled = false;
                    Flush();
                    Assert.Equal(top.FindResource(Classic.CommonControls.SystemColors.GrayTextBrushKey)?.ToString(),
                        HeaderColor(top)?.ToString());
                    top.IsEnabled = true;
                    Flush();
                    window.MouseDown(point, MouseButton.Left);
                    window.MouseUp(point, MouseButton.Left);
                    Flush();
                    Assert.True(top.IsSubMenuOpen);
                    AssertBarColor();
                    window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                    window.KeyReleaseQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
                    Flush();
                    Assert.Contains(":selected", nested.Classes);
                    Assert.Equal(nested.FindResource(Classic.CommonControls.SystemColors.HighlightTextBrushKey)?.ToString(),
                        HeaderColor(nested)?.ToString());
                    AssertBarColor();
                    nested.IsSubMenuOpen = true;
                    Flush();
                    ((Avalonia.Controls.IPseudoClasses)radio.Classes).Set(":selected", true);
                    Flush();
                    var glyph = radio.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single();
                    Assert.Equal(HeaderColor(radio)?.ToString(), glyph.Fill?.ToString());
                    Assert.Equal(disabled.FindResource(Classic.CommonControls.SystemColors.GrayTextBrushKey)?.ToString(),
                        HeaderColor(disabled)?.ToString());
                    nested.IsSubMenuOpen = false;
                    window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                    window.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                    Flush();
                    Assert.False(top.IsSubMenuOpen);
                    AssertBarColor();
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Windows11CaptionFillsTitleBarAndSnapTracksWindowCapabilities()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var window = new ClassicWindow { Width = 500, Height = 300 };
            try
            {
                window.Show();
                void Flush()
                {
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                }
                Button Caption(string name) => window.GetVisualDescendants().OfType<Button>()
                    .Single(button => button.Name == name);
                void AssertCloseBackgroundReachesWindowEdges()
                {
                    var background = Caption("PART_CloseButton").GetVisualDescendants().OfType<Border>()
                        .Single(border => border.Name == "Windows11CaptionButtonBorder");
                    var origin = background.TranslatePoint(default, window)!.Value;
                    Assert.Equal(0, origin.Y);
                    Assert.Equal(window.Bounds.Width, origin.X + background.Bounds.Width);
                    Assert.Equal(default, background.CornerRadius);
                    Assert.All(background.GetVisualAncestors().TakeWhile(visual => visual != window),
                        visual => Assert.False(visual.ClipToBounds));
                }
                void AssertHitTest(Win32Properties.Win32HitTestValue expected)
                {
                    Flush();
                    var maximize = Caption("PART_RestoreButton");
                    Assert.Equal(expected, Win32Properties.GetNonClientHitTestResult(maximize));
                    // Windows 命中的是模板内的视觉元素，标记必须传递到按钮背景和图标。
                    Assert.All(maximize.GetVisualDescendants(), visual =>
                        Assert.Equal(expected, Win32Properties.GetNonClientHitTestResult(visual)));
                    Assert.Equal(Win32Properties.Win32HitTestValue.Client,
                        Win32Properties.GetNonClientHitTestResult(Caption("PART_CloseButton")));
                }
                foreach (var theme in new[] { CustomTheme.Windows11, CustomTheme.Windows11Dark })
                {
                    Avalonia.Application.Current!.RequestedThemeVariant = theme;
                    Flush();
                    Assert.Equal(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000),
                        window.Classes.Contains("windows11-snap"));
                    var close = Caption("PART_CloseButton");
                    var title = window.GetVisualDescendants().OfType<Border>()
                        .Single(border => border.Name == "PART_GradientBar");
                    Assert.Equal(46, close.Bounds.Width);
                    Assert.Equal(title.Bounds.Height, close.Bounds.Height);
                    Assert.True(close.Bounds.Height > 0);
                    AssertCloseBackgroundReachesWindowEdges();
                    Assert.Equal(title.TranslatePoint(default, window)!.Value.Y,
                        close.TranslatePoint(default, window)!.Value.Y);

                    // 无头平台模拟符合版本条件的 Windows，验证样式标记及其撤销。
                    window.Classes.Add("windows11-snap");
                    AssertHitTest(Win32Properties.Win32HitTestValue.MaxButton);
                    window.CanMaximize = false;
                    AssertHitTest(Win32Properties.Win32HitTestValue.Client);
                    window.CanMaximize = true;
                    window.CanResize = false;
                    AssertHitTest(Win32Properties.Win32HitTestValue.Client);
                    window.CanResize = true;
                    AssertHitTest(Win32Properties.Win32HitTestValue.MaxButton);
                    window.WindowState = WindowState.FullScreen;
                    AssertHitTest(Win32Properties.Win32HitTestValue.Client);
                    window.WindowState = WindowState.Normal;
                    AssertHitTest(Win32Properties.Win32HitTestValue.MaxButton);
                    foreach (var state in new[] { WindowState.Maximized, WindowState.Normal })
                    {
                        var maximize = Caption("PART_RestoreButton");
                        var point = maximize.TranslatePoint(
                            new Point(maximize.Bounds.Width / 2, maximize.Bounds.Height / 2), window)!.Value;
                        window.MouseMove(point);
                        window.MouseDown(point, MouseButton.Left);
                        window.MouseUp(point, MouseButton.Left);
                        Flush();
                        Assert.Equal(state, window.WindowState);
                        AssertCloseBackgroundReachesWindowEdges();
                        AssertHitTest(Win32Properties.Win32HitTestValue.MaxButton);
                    }
                    window.Classes.Remove("windows11-snap");
                    AssertHitTest(Win32Properties.Win32HitTestValue.Client);
                }
                window.Classes.Add("windows11-snap");
                Avalonia.Application.Current!.RequestedThemeVariant = CustomTheme.Windows10;
                Flush();
                Assert.DoesNotContain("windows11-snap", window.Classes);
                AssertHitTest(Win32Properties.Win32HitTestValue.Client);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("windows-native-corners")]
    [InlineData("macos-native-corners")]
    public async Task NativeWindowCornersReplaceManagedClipAndRestoreWhenDisabled(string nativeClass)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var window = new ClassicWindow { Width = 500, Height = 300 };
            try
            {
                window.Show();
                void Flush()
                {
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                }
                foreach (var theme in new[]
                         { CustomTheme.Windows11, CustomTheme.Windows11Dark, CustomTheme.Aero, CustomTheme.AeroDark })
                {
                    Avalonia.Application.Current!.RequestedThemeVariant = theme;
                    Flush();
                    var surface = Assert.IsType<Panel>(window.GetVisualChildren().Single());
                    // 无头窗口保留应用裁剪；原生调用成功后才会启用对应平台的样式类。
                    Assert.DoesNotContain(nativeClass, window.Classes);
                    Assert.NotNull(surface.Clip);
                    var frame = window.GetVisualDescendants().OfType<ClassicBorderDecorator>()
                        .Single(control => control.Name == "Bd");
                    Assert.Null(frame.Clip);
                    window.Classes.Add(nativeClass);
                    Flush();
                    Assert.Null(surface.Clip);
                    Assert.Equal(default, DesktopWindowChrome.GetCornerRadius(surface));
                    window.Classes.Remove(nativeClass);
                    Flush();
                    Assert.NotNull(surface.Clip);
                }
                window.WindowState = WindowState.Maximized;
                window.Classes.Add(nativeClass);
                Flush();
                Assert.Null(window.GetVisualChildren().Single().Clip);
                window.WindowState = WindowState.Normal;
                window.Classes.Add(nativeClass);
                Flush();
                Assert.Null(window.GetVisualChildren().Single().Clip);
                Avalonia.Application.Current!.RequestedThemeVariant = CustomTheme.Windows10;
                Flush();
                Assert.DoesNotContain(nativeClass, window.Classes);
                Assert.Null(window.GetVisualChildren().Single().Clip);
                Avalonia.Application.Current.RequestedThemeVariant = ClassicTheme.Standard;
                Flush();
                Assert.DoesNotContain(nativeClass, window.Classes);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Windows11RestoreGlyphTracksThemeStateAndCaptionForeground()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var window = new ClassicWindow { Width = 500, Height = 300 };
            void Flush()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
            try
            {
                window.Show();
                foreach (var theme in new[]
                         { CustomTheme.Windows11, CustomTheme.Windows11Dark, CustomTheme.Windows10, CustomTheme.Windows11 })
                {
                    Avalonia.Application.Current!.RequestedThemeVariant = theme;
                    window.WindowState = WindowState.Maximized;
                    Flush();
                    var button = window.GetVisualDescendants().OfType<Button>()
                        .Single(control => control.Name == "PART_RestoreButton");
                    var glyph = button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
                    Assert.Equal(new Size(10, 10), glyph.Bounds.Size);
                    if (CustomTheme.IsWindows11(theme))
                    {
                        Assert.Equal(1, glyph.StrokeThickness);
                        Assert.Equal(button.Foreground, glyph.Stroke);
                        Assert.Null(glyph.Fill);
                        // 鼠标操作仍从最大化还原，并恢复单框图标。
                        var point = button.TranslatePoint(new Point(23, 16), window)!.Value;
                        window.MouseMove(point);
                        window.MouseDown(point, MouseButton.Left);
                        window.MouseUp(point, MouseButton.Left);
                        Flush();
                        Assert.Equal(WindowState.Normal, window.WindowState);
                        glyph = button.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
                    }
                    Assert.Null(glyph.Stroke);
                    Assert.Equal(button.Foreground, glyph.Fill);
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Windows10DisabledMaximizeRemainsVisibleWithoutEnablingActions(bool dark)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            Avalonia.Application.Current!.RequestedThemeVariant = dark ? CustomTheme.Windows10Dark : CustomTheme.Windows10;
            var window = new ClassicWindow { Width = 400, Height = 200, CanResize = false };
            void Flush()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
            static double Luminance(Color color)
            {
                static double Linear(byte channel)
                {
                    var value = channel / 255d;
                    return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
                }
                return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
            }
            try
            {
                window.Show();
                Flush();
                var maximize = window.GetVisualDescendants().OfType<Button>()
                    .Single(control => control.Name == "PART_RestoreButton");
                var minimize = window.GetVisualDescendants().OfType<Button>()
                    .Single(control => control.Name == "PART_MinimizeButton");
                var caption = window.GetVisualDescendants().OfType<Border>()
                    .Single(control => control.Name == "PART_GradientBar");
                foreach (var active in new[] { true, false })
                {
                    // Headless does not transfer activation between windows automatically.
                    typeof(WindowBase).GetProperty(nameof(WindowBase.IsActive))!.SetValue(window, active);
                    Flush();
                    Assert.Equal(active, window.IsActive);
                    Assert.False(maximize.IsEffectivelyEnabled);
                    var foreground = Assert.IsAssignableFrom<ISolidColorBrush>(maximize.Foreground).Color;
                    var background = Assert.IsAssignableFrom<ISolidColorBrush>(caption.Background).Color;
                    var alpha = foreground.A / 255d * maximize.Opacity;
                    var visible = Color.FromRgb(
                        (byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
                        (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
                        (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
                    var first = Luminance(visible) + 0.05;
                    var second = Luminance(background) + 0.05;
                    Assert.True(Math.Max(first, second) / Math.Min(first, second) >= 2,
                        "Disabled maximize should remain distinguishable on the caption background.");
                    if (!dark)
                    {
                        Assert.True(minimize.IsEffectivelyEnabled);
                        var enabled = Assert.IsAssignableFrom<ISolidColorBrush>(minimize.Foreground).Color;
                        var enabledLuminance = Luminance(enabled) + 0.05;
                        Assert.True(Math.Max(first, enabledLuminance) / Math.Min(first, enabledLuminance) >= 1.8,
                            "Disabled maximize should look different from enabled caption buttons in both activation states.");
                    }
                    var point = maximize.TranslatePoint(new Point(23, 16), window)!.Value;
                    window.MouseMove(point);
                    Flush();
                    Assert.Equal(foreground, Assert.IsAssignableFrom<ISolidColorBrush>(maximize.Foreground).Color);
                    Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(maximize.Background).Color.A);
                    window.MouseDown(point, MouseButton.Left);
                    window.MouseUp(point, MouseButton.Left);
                    Flush();
                    Assert.Equal(WindowState.Normal, window.WindowState);
                }
                window.CanResize = true;
                Flush();
                Assert.True(maximize.IsEffectivelyEnabled);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Windows11BackdropBecomesOpaqueAcrossEntireInactiveWindow(bool dark)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            Avalonia.Application.Current!.RequestedThemeVariant = dark ? CustomTheme.Windows11Dark : CustomTheme.Windows11;
            var window = new ClassicWindow { Width = 400, Height = 240 };
            void Flush()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
            try
            {
                window.Show();
                Flush();
                var changed = (Action<WindowTransparencyLevel>)window.PlatformImpl!.GetType()
                    .GetProperty("TransparencyLevelChanged")!.GetValue(window.PlatformImpl)!;
                var root = Assert.IsType<Panel>(window.GetVisualChildren().Single());
                var tint = root.GetVisualChildren().OfType<Border>()
                    .Single(control => !control.IsHitTestVisible && control.Name != "PART_TransparencyFallback");
                var caption = window.GetVisualDescendants().OfType<Border>()
                    .Single(control => control.Name == "PART_GradientBar");
                foreach (var level in new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur,
                             WindowTransparencyLevel.Blur })
                {
                    changed(level);
                    foreach (var active in new[] { true, false, true })
                    {
                        // Headless does not transfer activation between windows automatically.
                        typeof(WindowBase).GetProperty(nameof(WindowBase.IsActive))!.SetValue(window, active);
                        Flush();
                        Assert.Contains("windows11-backdrop", window.Classes);
                        var color = Assert.IsAssignableFrom<ISolidColorBrush>(tint.Background).Color;
                        Assert.Equal(active, color.A < 255);
                        Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(caption.Background).Color.A);
                    }
                }
                changed(WindowTransparencyLevel.None);
                Flush();
                Assert.DoesNotContain("windows11-backdrop", window.Classes);
                Assert.Equal(255, Assert.IsAssignableFrom<ISolidColorBrush>(tint.Background).Color.A);
                Avalonia.Application.Current.RequestedThemeVariant = ClassicTheme.Standard;
                Flush();
                Assert.DoesNotContain("windows11-backdrop", window.Classes);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyWindowsFrameIsOpaqueAndCaptionStaysModernThroughWindowStateChanges(bool dark)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            Avalonia.Application.Current!.RequestedThemeVariant = dark ? CustomTheme.Windows11Dark : CustomTheme.Windows11;
            var window = new ClassicWindow { Width = 500, Height = 300 };
            void Flush()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
            try
            {
                window.Show();
                Flush();
                var originalDecorations = window.SystemDecorations;
                var originalTransparencyHints = window.TransparencyLevelHint.ToArray();
                window.Classes.Add("windows-legacy-frame");
                Flush();
                var title = window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.TemplatedControl>()
                    .Single(control => control.GetType().Name == "AutoAttachTitleBar");
                var template = title.Template;
                var frame = window.GetVisualDescendants().OfType<ClassicBorderDecorator>()
                    .Single(control => control.Name == "Bd");
                foreach (var state in new[]
                         { WindowState.Normal, WindowState.Maximized, WindowState.Normal, WindowState.FullScreen, WindowState.Normal })
                {
                    window.WindowState = state;
                    Flush();
                    Assert.Equal(SystemDecorations.BorderOnly, window.SystemDecorations);
                    Assert.Equal(originalTransparencyHints, window.TransparencyLevelHint);
                    Assert.True(window.CanResize);
                    Assert.Same(template, title.Template);
                    Assert.Single(title.GetVisualDescendants().OfType<TextBlock>(), control => control.Name == "Windows10Title");
                    Assert.DoesNotContain(title.GetVisualDescendants().OfType<TextBlock>(), control => control.Name == "AeroTitle");
                    Assert.Equal(state == WindowState.Normal ? new Thickness(1) : default,
                        DesktopWindowChrome.GetFrameThickness(frame));
                    foreach (var active in new[] { true, false })
                    {
                        typeof(WindowBase).GetProperty(nameof(WindowBase.IsActive))!.SetValue(window, active);
                        Flush();
                        var palette = DesktopWindowChrome.GetFramePalette(frame)!;
                        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(palette.Outer);
                        Assert.Equal(255, brush.Color.A);
                        if (state == WindowState.Normal)
                        {
                            // Verify the rendered outline, not just its palette: the content layer used to cover it.
                            using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(500, 300));
                            bitmap.Render(window);
                            var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
                            try
                            {
                                Color Pixel(PixelPoint point)
                                {
                                    bitmap.CopyPixels(new PixelRect(point, new PixelSize(1, 1)), buffer, 4, 4);
                                    var rgba = bitmap.Format == Avalonia.Platform.PixelFormat.Rgba8888;
                                    return Color.FromArgb(System.Runtime.InteropServices.Marshal.ReadByte(buffer, 3),
                                        System.Runtime.InteropServices.Marshal.ReadByte(buffer, rgba ? 0 : 2),
                                        System.Runtime.InteropServices.Marshal.ReadByte(buffer, 1),
                                        System.Runtime.InteropServices.Marshal.ReadByte(buffer, rgba ? 2 : 0));
                                }
                                foreach (var point in new[] { new PixelPoint(0, 150), new PixelPoint(499, 150),
                                             new PixelPoint(250, 0), new PixelPoint(250, 299) })
                                {
                                    Assert.Equal(brush.Color, Pixel(point));
                                }
                                // Hover must cover the top/right outline, including in an inactive window.
                                window.MouseMove(new Point(477, 16));
                                Flush();
                                bitmap.Render(window);
                                Assert.True(window.TryFindResource("Windows10CloseHoverBrush", window.ActualThemeVariant, out var hoverResource));
                                var hover = Assert.IsAssignableFrom<ISolidColorBrush>(hoverResource).Color;
                                Assert.Equal(hover, Pixel(new PixelPoint(477, 0)));
                                Assert.Equal(hover, Pixel(new PixelPoint(499, 16)));
                                Assert.Equal(hover, Pixel(new PixelPoint(499, 0)));
                                window.MouseMove(new Point(250, 150));
                                Flush();
                            }
                            finally
                            {
                                System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
                            }
                        }
                    }
                }
                window.Classes.Remove("windows-legacy-frame");
                Flush();
                Assert.Equal(originalDecorations, window.SystemDecorations);
                Assert.Equal(originalTransparencyHints, window.TransparencyLevelHint);
                Assert.True(Assert.IsAssignableFrom<ISolidColorBrush>(DesktopWindowChrome.GetFramePalette(frame)!.Outer).Color.A < 255);
                window.Classes.Add("windows-legacy-frame");
                Avalonia.Application.Current.RequestedThemeVariant = ClassicTheme.Standard;
                Flush();
                Assert.DoesNotContain("windows-legacy-frame", window.Classes);
                Assert.Equal(originalDecorations, window.SystemDecorations);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LegacyRoundedThemesUseSquareCornersAndRestoreWhenDisabled(bool aero, bool dark)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var window = new ClassicWindow { Width = 500, Height = 300 };
            void Flush()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
            try
            {
                window.Show();
                Avalonia.Application.Current!.RequestedThemeVariant = aero
                    ? dark ? CustomTheme.AeroDark : CustomTheme.Aero
                    : dark ? CustomTheme.Windows11Dark : CustomTheme.Windows11;
                Flush();
                var surface = Assert.IsType<Panel>(window.GetVisualChildren().Single());
                var frame = window.GetVisualDescendants().OfType<ClassicBorderDecorator>().Single(control => control.Name == "Bd");
                var corners = DesktopWindowChrome.GetCornerRadius(surface);
                Assert.NotEqual(default, corners);
                var transparencyHints = window.TransparencyLevelHint.ToArray();
                window.Classes.Add("windows-legacy-frame");
                foreach (var state in new[] { WindowState.Normal, WindowState.Maximized, WindowState.Normal,
                             WindowState.FullScreen, WindowState.Normal })
                {
                    window.WindowState = state;
                    Flush();
                    Assert.Equal(default, DesktopWindowChrome.GetCornerRadius(surface));
                    Assert.Equal(default, DesktopWindowChrome.GetCornerRadius(frame));
                    Assert.Null(surface.Clip);
                    Assert.Equal(transparencyHints, window.TransparencyLevelHint);
                }
                // Render all four corners at common display scales: no transparent round cutouts remain.
                foreach (var scale in new[] { 1d, 1.25d, 2d })
                {
                    var size = PixelSize.FromSize(window.Bounds.Size, scale);
                    using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
                    bitmap.Render(window);
                    var buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
                    try
                    {
                        foreach (var point in new[] { new PixelPoint(0, 0), new PixelPoint(size.Width - 1, 0),
                                     new PixelPoint(0, size.Height - 1), new PixelPoint(size.Width - 1, size.Height - 1) })
                        {
                            bitmap.CopyPixels(new PixelRect(point, new PixelSize(1, 1)), buffer, 4, 4);
                            Assert.Equal(255, System.Runtime.InteropServices.Marshal.ReadByte(buffer, 3));
                        }
                    }
                    finally
                    {
                        System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
                    }
                }
                window.Classes.Remove("windows-legacy-frame");
                Flush();
                Assert.Equal(corners, DesktopWindowChrome.GetCornerRadius(surface));
                Assert.Equal(corners, DesktopWindowChrome.GetCornerRadius(frame));
                Assert.NotNull(surface.Clip);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task CaptionTooltipsFollowLanguageAndStateAndYieldToNativeSnap()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var window = new ClassicWindow { Width = 500, Height = 300 };
            var languages = new TSCutter.GUI.Services.Application.LocalizationService();
            void Flush()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
            Button Caption(string name) => window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);
            void Label(string part, string text)
            {
                Assert.Equal(text, ToolTip.GetTip(Caption(part)));
            }
            try
            {
                window.Show();
                foreach (var theme in new[]
                         {
                             ClassicTheme.Standard, CustomTheme.DarkStandard, CustomTheme.LunaBlue, CustomTheme.LunaSilver,
                             CustomTheme.LunaOliveGreen, CustomTheme.LunaDark, CustomTheme.Aero, CustomTheme.AeroDark,
                             CustomTheme.Windows8, CustomTheme.Windows8Dark, CustomTheme.Windows10, CustomTheme.Windows10Dark,
                             CustomTheme.Windows11, CustomTheme.Windows11Dark
                         })
                {
                    Avalonia.Application.Current!.RequestedThemeVariant = theme;
                    Flush();
                    foreach (var (language, minimize, maximize, restore, close) in new[]
                             {
                                 ("en-US", "Minimize", "Maximize", "Restore", "Close"),
                                 ("zh-CN", "最小化", "最大化", "还原", "关闭"),
                                 ("zh-TW", "最小化", "最大化", "還原", "關閉")
                             })
                    {
                        languages.SwitchLanguage(language);
                        window.WindowState = WindowState.Normal;
                        Flush();
                        Label("PART_MinimizeButton", minimize);
                        Label("PART_RestoreButton", maximize);
                        Label("PART_CloseButton", close);
                        window.WindowState = WindowState.Maximized;
                        Flush();
                        Label("PART_RestoreButton", restore);
                        window.WindowState = WindowState.Minimized;
                        Flush();
                        Label("PART_MinimizeButton", restore);
                    }
                }
                window.WindowState = WindowState.Normal;
                window.Classes.Add("windows11-snap");
                Flush();
                Assert.Null(ToolTip.GetTip(Caption("PART_RestoreButton")));
                Label("PART_MinimizeButton", "最小化");
                Label("PART_CloseButton", "關閉");
                window.WindowState = WindowState.Maximized;
                Flush();
                Assert.Null(ToolTip.GetTip(Caption("PART_RestoreButton")));
                window.CanMaximize = false;
                Flush();
                Label("PART_RestoreButton", "還原");
                window.CanMaximize = true;
                window.CanResize = false;
                Flush();
                Label("PART_RestoreButton", "還原");
                window.CanResize = true;
                Flush();
                Assert.Null(ToolTip.GetTip(Caption("PART_RestoreButton")));
                window.Classes.Remove("windows11-snap");
                Flush();
                Label("PART_RestoreButton", "還原");
                Avalonia.Application.Current!.RequestedThemeVariant = CustomTheme.Windows10;
                Flush();
                Label("PART_RestoreButton", "還原");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    public sealed record Option(string Label);
}

public static class ThemeTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<ThemeTestApplication>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia();
}

public sealed class ThemeTestApplication : Avalonia.Application
{
    public override void Initialize()
    {
        Styles.Add(new ClassicTheme { FontAliasing = false });
        Styles.Add(new StyleInclude(new Uri("avares://TSCutterGUI/"))
        {
            Source = new Uri("avares://TSCutterGUI/Themes/CustomTheme.axaml")
        });
    }
}
