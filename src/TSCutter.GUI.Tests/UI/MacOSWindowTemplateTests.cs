using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Themes;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class MacOSWindowTemplateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeTemplateKeepsFirstRowClickableAndRestoresOtherThemes(bool dark)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var top = new MenuItem { Header = "File", ItemsSource = new[] { new MenuItem { Header = "Open" } } };
            var menu = new Menu { ItemsSource = new[] { top } };
            var button = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
            var clicks = 0;
            button.Click += (_, _) => clicks++;
            var tabs = new TabControl { ItemsSource = new[] { new TabItem { Header = "Clips" } } };
            var section = new ClassicBorderDecorator { Height = 80, Child = tabs };
            section.Classes.Add("app-section-frame");
            section.Classes.Add("app-sidebar-frame");
            var content = new StackPanel { Children = { menu, button, section } };
            var window = new ClassicWindow { Width = 500, Height = 300, Content = content };
            void Flush()
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
            void Click(Control control)
            {
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Flush();
            }
            try
            {
                window.Show();
                foreach (var nativeTemplate in new[] { false, true })
                {
                    Avalonia.Application.Current!.RequestedThemeVariant = dark ? CustomTheme.MacOSDark : CustomTheme.MacOS;
                    // Headless has no NSWindow. Exercise both the fallback and the native client template.
                    window.Classes.Set("macos-native-titlebar", nativeTemplate);
                    Flush();
                    Assert.Contains("fluent", window.Classes);
                    Assert.Equal(new CornerRadius(4), button.GetVisualDescendants().OfType<Border>()
                        .Single(control => control.Name == "DesktopButtonBorder").CornerRadius);
                    Assert.Equal(!nativeTemplate, window.GetVisualDescendants().OfType<CaptionButtons>().Any());
                    if (nativeTemplate)
                    {
                        Assert.Equal(28, content.TranslatePoint(default, window)!.Value.Y);
                        Assert.Equal(new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur },
                            window.TransparencyLevelHint);
                        var surface = window.GetVisualDescendants().OfType<Border>()
                            .Single(control => control.Name == "MacOSSurface");
                        Assert.Equal(dark ? "#ff1e1e1e" : "#fff5f5f7", surface.Background?.ToString());
                        window.Classes.Add("macos-backdrop");
                        Flush();
                        Assert.Equal(dark ? "#661c1c1e" : "#66f2f2f7", surface.Background?.ToString());
                        Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(section.Background).Color.A);
                        Assert.Equal(0, Assert.IsAssignableFrom<ISolidColorBrush>(tabs.Background).Color.A);
                        window.Classes.Remove("macos-backdrop");
                        Flush();
                    }
                    Click(top);
                    Assert.True(top.IsSubMenuOpen);
                    top.IsSubMenuOpen = false;
                    Flush();
                    Click(button);
                    Assert.Equal(nativeTemplate ? 2 : 1, clicks);
                    // Switching to another family removes all native template styling.
                    Avalonia.Application.Current.RequestedThemeVariant = CustomTheme.LunaBlue;
                    Flush();
                    Assert.DoesNotContain("macos-native-titlebar", window.Classes);
                    Assert.DoesNotContain("fluent", window.Classes);
                    Assert.Contains(window.GetVisualDescendants(), control => control is CaptionButtons);
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
    public async Task TimelineDefinesTranslucentTracksAndRestoresSolidFallback(bool dark)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var timeline = new TSCutter.GUI.Controls.TimelineControl();
            var frame = new ClassicBorderDecorator { Child = timeline };
            frame.Classes.Add("app-section-frame");
            var window = new ClassicWindow { Width = 500, Height = 300, Content = frame };
            window.Resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new System.Uri("avares://TSCutterGUI/"))
            { Source = new System.Uri("avares://TSCutterGUI/Themes/StatusColors.axaml") });
            byte Alpha(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color.A;
            try
            {
                window.Show();
                foreach (var variant in new[] { dark ? CustomTheme.MacOSDark : CustomTheme.MacOS,
                             dark ? CustomTheme.Windows11Dark : CustomTheme.Windows11 })
                {
                    window.RequestedThemeVariant = variant;
                    Dispatcher.UIThread.RunJobs();
                    var fallback = (timeline.BackgroundBrush, timeline.TrackBrush, timeline.EmptyTrackBrush,
                        timeline.BorderDarkBrush, timeline.BorderLightBrush);
                    Assert.Equal(255, Alpha(timeline.BackgroundBrush));
                    Assert.Equal(255, Alpha(timeline.TrackBrush));
                    Assert.Equal(255, Alpha(timeline.EmptyTrackBrush));
                    var materialClass = CustomTheme.IsMacOS(variant) ? "macos-backdrop" : "windows11-backdrop";
                    window.Classes.Add(materialClass);
                    Assert.Equal(0, Alpha(frame.Background));
                    Assert.Equal(0, Alpha(timeline.BackgroundBrush));
                    Assert.InRange(Alpha(timeline.TrackBrush), 1, 32);
                    Assert.InRange(Alpha(timeline.EmptyTrackBrush), 1, 32);
                    Assert.True(Alpha(timeline.BorderDarkBrush) > Alpha(fallback.BorderDarkBrush));
                    Assert.Equal(timeline.BorderDarkBrush, timeline.BorderLightBrush);
                    window.Classes.Remove(materialClass);
                    Assert.Equal(fallback, (timeline.BackgroundBrush, timeline.TrackBrush, timeline.EmptyTrackBrush,
                        timeline.BorderDarkBrush, timeline.BorderLightBrush));
                }
                window.RequestedThemeVariant = CustomTheme.LunaBlue;
                Assert.Equal(255, Alpha(timeline.BackgroundBrush));
                Assert.Equal(255, Alpha(timeline.TrackBrush));
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task NativeTemplateAcceptsDropsOverEmptyAndDisabledContent()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            Avalonia.Application.Current!.RequestedThemeVariant = CustomTheme.MacOS;
            var window = new ClassicWindow
            {
                Width = 400, Height = 250,
                Content = new Button { Content = "Disabled preview", IsEnabled = false, Width = 100, Height = 60 }
            };
            window.Classes.Add("macos-native-titlebar");
            DragDrop.SetAllowDrop(window, true);
            var drops = 0;
            window.AddHandler(DragDrop.DropEvent, (_, _) => drops++);
            using var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText("external drop"));
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                foreach (var point in new[] { new Point(20, 20), new Point(200, 125) })
                {
                    window.DragDrop(point, RawDragEventType.DragEnter, data, DragDropEffects.Copy);
                    window.DragDrop(point, RawDragEventType.Drop, data, DragDropEffects.Copy);
                }
                Assert.Equal(2, drops);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task NativeDialogHasFinalChromeAndSizeWhenFirstShown()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            Avalonia.Application.Current!.RequestedThemeVariant = CustomTheme.MacOS;
            var owner = new ClassicWindow { Width = 800, Height = 600 };
            var dialog = new TSCutter.GUI.Views.Editing.RemuxExportOptionsView
            {
                DataContext = new TSCutter.GUI.ViewModels.Editing.RemuxExportOptionsViewModel()
            };
            dialog.Classes.Add("macos-native-titlebar");
            try
            {
                owner.Show();
                dialog.Show(owner);
                Assert.Equal(SystemDecorations.Full, dialog.SystemDecorations);
                Assert.True(dialog.ExtendClientAreaToDecorationsHint);
                Assert.DoesNotContain(dialog.GetVisualDescendants(), control => control is CaptionButtons);
                var size = dialog.ClientSize;
                var position = dialog.Position;
                Dispatcher.UIThread.RunJobs();
                dialog.UpdateLayout();
                Assert.Equal(size, dialog.ClientSize);
                Assert.Equal(position, dialog.Position);
            }
            finally { dialog.Close(); owner.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task LeavingNativeThemeRestoresEachFamilyToItsFreshWindowAppearance()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var button = new Button { Content = "Action", Width = 100, Height = 40 };
            var window = new ClassicWindow { Width = 500, Height = 300, Content = button };
            var clicks = 0;
            button.Click += (_, _) => clicks++;
            void Flush(ClassicWindow current)
            {
                Dispatcher.UIThread.RunJobs();
                current.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }
            object Appearance(ClassicWindow current) => (
                current.SystemDecorations, current.ExtendClientAreaToDecorationsHint,
                current.ExtendClientAreaChromeHints, current.ExtendClientAreaTitleBarHeightHint,
                string.Join(",", current.TransparencyLevelHint), current.Classes.Contains("fluent"),
                current.GetVisualDescendants().OfType<CaptionButtons>().Any(),
                DesktopWindowChrome.GetCornerRadius(current.GetVisualChildren().OfType<Panel>().Single()),
                current.GetVisualDescendants().Any(control => control.Name == "MacOSSurface"));
            try
            {
                window.Show();
                foreach (var target in new[]
                         {
                             ClassicTheme.Standard, CustomTheme.DarkStandard, CustomTheme.LunaBlue, CustomTheme.LunaDark,
                             CustomTheme.Aero, CustomTheme.AeroDark, CustomTheme.Windows8, CustomTheme.Windows8Dark,
                             CustomTheme.Windows10, CustomTheme.Windows10Dark, CustomTheme.Windows11, CustomTheme.Windows11Dark
                         })
                {
                    var reference = new ClassicWindow { Width = 500, Height = 300, RequestedThemeVariant = target };
                    try
                    {
                        reference.Show();
                        Flush(reference);
                        var expected = Appearance(reference);
                        foreach (var native in new[] { CustomTheme.MacOS, CustomTheme.MacOSDark })
                        {
                            window.RequestedThemeVariant = native;
                            Flush(window);
                            window.RequestedThemeVariant = target;
                            Flush(window);
                            Assert.Equal(expected, Appearance(window));
                            Assert.DoesNotContain("macos-native-titlebar", window.Classes);
                            Assert.DoesNotContain("macos-backdrop", window.Classes);
                            var point = button.TranslatePoint(new Point(50, 20), window)!.Value;
                            window.MouseMove(point);
                            window.MouseDown(point, MouseButton.Left);
                            window.MouseUp(point, MouseButton.Left);
                        }
                    }
                    finally { reference.Close(); }
                }
                Assert.Equal(24, clicks);
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

}
