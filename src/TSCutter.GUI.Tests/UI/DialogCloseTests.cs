using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Classic.Avalonia.Theme;
using Splat;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Themes;
using TSCutter.GUI.ViewModels.TransportStream.Processing;
using TSCutter.GUI.Views.Application;
using TSCutter.GUI.Views.Editing;
using TSCutter.GUI.Views.Preview;
using TSCutter.GUI.Views.TransportStream.Analysis;
using TSCutter.GUI.Views.TransportStream.Processing;
using TSCutter.GUI.Views.TransportStream.Repair;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class DialogCloseTests
{
    [Fact]
    public async Task BusyToolsFinishClosingAfterCancellationWithoutAnotherClick()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            var remux = new TsRemuxWindowViewModel(null!) { IsBusy = true };
            var merge = new TsBinaryMergeWindowViewModel(null!) { IsBusy = true };
            var extract = new TsEsExtractorWindowViewModel(null!) { IsBusy = true };
            (Window Window, Action Finish)[] cases =
            [
                (new TsRemuxWindow { DataContext = remux }, () => remux.IsBusy = false),
                (new TsBinaryMergeWindow { DataContext = merge }, () => merge.IsBusy = false),
                (new TsEsExtractorWindow { DataContext = extract }, () => extract.IsBusy = false)
            ];
            foreach (var (window, finish) in cases)
            {
                try
                {
                    window.Show();
                    WindowCommands.Close.Execute(window);
                    WindowCommands.Close.Execute(window);
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(window.IsVisible);
                    finish();
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(window.IsVisible);
                }
                finally
                {
                    finish();
                    window.Close();
                }
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ToolFootersFitMinimumWidthsAndEscapeClosesAcrossThemes()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            Func<Window>[] factories =
            [
                () => new MediainfoWindow(), () => new RawCutterWindow(),
                () => new TsCheckWindow(), () => new TsPacketViewerWindow(),
                () => new TsFilterWindow(), () => new TsServiceFilterWindow(),
                () => new TsEsExtractorWindow(), () => new TsRemuxWindow(),
                () => new TsBinaryMergeWindow(), () => new TsMultiSourceRepairWindow(),
                () => new TsTimelineRepairWindow(), () => new UpdatesInfoWindow()
            ];
            foreach (var create in factories)
            {
                var window = create();
                try
                {
                    window.Width = Math.Max(400, window.MinWidth);
                    window.Show();
                    foreach (var language in new[] { "en-US", "zh-CN" })
                    {
                        locale.SwitchLanguage(language);
                        foreach (var theme in new[]
                                 {
                                     ClassicTheme.Standard, CustomTheme.DarkStandard,
                                     CustomTheme.LunaBlue, CustomTheme.LunaDark,
                                     CustomTheme.Aero, CustomTheme.AeroDark,
                                     CustomTheme.Windows8, CustomTheme.Windows8Dark,
                                     CustomTheme.Windows10, CustomTheme.Windows10Dark,
                                     CustomTheme.Windows11, CustomTheme.Windows11Dark,
                                     CustomTheme.MacOS, CustomTheme.MacOSDark
                                 })
                        {
                            Avalonia.Application.Current!.RequestedThemeVariant = theme;
                            Dispatcher.UIThread.RunJobs();
                            window.UpdateLayout();
                            var close = Assert.Single(window.GetVisualDescendants().OfType<Button>(),
                                button => button.Command == WindowCommands.Close);
                            Assert.True(close.IsEffectivelyVisible && close.IsEffectivelyEnabled);
                            var position = close.TranslatePoint(default, window)!.Value;
                            Assert.True(position.X >= 0 && position.X + close.Bounds.Width <= window.Bounds.Width + 1,
                                $"{window.GetType().Name}: {theme.Key}, {language}, close={position.X}+{close.Bounds.Width}, window={window.Bounds.Width}");
                            Assert.True(position.Y >= 0 && position.Y + close.Bounds.Height <= window.Bounds.Height + 1);
                            foreach (var button in close.GetVisualParent()!.GetVisualDescendants().OfType<Button>())
                            {
                                var left = button.TranslatePoint(default, window)!.Value.X;
                                Assert.True(left >= 0 && left + button.Bounds.Width <= window.Bounds.Width + 1,
                                    $"{window.GetType().Name}: {theme.Key}, {language}, button={button.Content}, left={left}");
                            }
                        }
                    }
                    window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                    Assert.False(window.IsVisible);
                }
                finally
                {
                    window.Close();
                }
            }
        }, CancellationToken.None);
    }

}
