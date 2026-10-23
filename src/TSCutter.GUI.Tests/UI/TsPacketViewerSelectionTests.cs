using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Models.TransportStream.Analysis;
using TSCutter.GUI.Themes;
using TSCutter.GUI.Views.TransportStream.Analysis;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class TsPacketViewerSelectionTests
{
    [Fact]
    public async Task AbnormalPacketsFollowSelectionColorsWhenFocusMovesAndThemesChange()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            Avalonia.Application.Current!.Styles.Insert(1, new StyleInclude(new Uri("avares://TSCutterGUI/"))
            {
                Source = new Uri("avares://Classic.Avalonia.Theme.DataGrid/Classic.axaml")
            });
            var window = new TsPacketViewerWindow();
            window.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://TSCutterGUI/"))
            {
                Source = new Uri("avares://TSCutterGUI/Themes/StatusColors.axaml")
            });
            var grid = window.FindControl<DataGrid>("PacketGrid")!;
            var packets = new[] { TsCheckSeverity.Info, TsCheckSeverity.Error, TsCheckSeverity.Warning }
                .Select((severity, index) => new TsPacketViewerRow
                {
                    Info = default, PacketIndex = index, FileOffset = index * 188,
                    Data = [], StreamText = "Video", TimestampText = "", Severity = severity
                }).ToArray();
            grid.ItemsSource = packets;
            try
            {
                window.Show();
                var input = ((Control)window.Content!).GetVisualDescendants().OfType<TextBox>().First();
                foreach (var theme in new[]
                         {
                             ClassicTheme.Standard, CustomTheme.DarkStandard, CustomTheme.LunaBlue, CustomTheme.LunaDark,
                             CustomTheme.Aero, CustomTheme.AeroDark, CustomTheme.Windows8, CustomTheme.Windows8Dark,
                             CustomTheme.Windows10, CustomTheme.Windows10Dark, CustomTheme.Windows11, CustomTheme.Windows11Dark,
                             CustomTheme.MacOS, CustomTheme.MacOSDark
                         })
                {
                    window.RequestedThemeVariant = theme;
                    Flush(window);
                    foreach (var packet in packets.Skip(1))
                    {
                        grid.SelectedItem = packet;
                        grid.ScrollIntoView(packet, null);
                        Flush(window);
                        var row = grid.GetVisualDescendants().OfType<DataGridRow>()
                            .Single(control => ReferenceEquals(control.DataContext, packet));
                        var text = row.GetVisualDescendants().OfType<TextBlock>()
                            .Single(control => control.Text == packet.PacketText);
                        Assert.Contains(packet.HasError ? "errorRow" : "warningRow", row.Classes);
                        foreach (var focused in new[] { true, false, true })
                        {
                            Assert.True(focused ? row.Focus() : input.Focus(),
                                $"{theme}: focus={focused}, row enabled={row.IsEffectivelyEnabled}, focusable={row.Focusable}, visible={row.IsEffectivelyVisible}");
                            Flush(window);
                            Assert.Equal(focused, grid.IsKeyboardFocusWithin);
                            Assert.NotNull(row.Foreground);
                            Assert.Equal(row.Foreground.ToString(), text.Foreground?.ToString());
                            Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(
                                text.GetVisualAncestors().OfType<DataGridCell>().First().Background).Color);
                        }

                        grid.SelectedItem = packets[0];
                        Flush(window);
                        Assert.True(window.TryFindResource(packet.HasError
                                ? "AppStatusErrorForegroundBrush" : "AppStatusWarningForegroundBrush",
                            window.ActualThemeVariant, out var foreground));
                        Assert.Equal(foreground?.ToString(), text.Foreground?.ToString());
                        Assert.True(window.TryFindResource(packet.HasError
                                ? "AppStatusErrorBackgroundBrush" : "AppStatusWarningBackgroundBrush",
                            window.ActualThemeVariant, out var background));
                        Assert.Equal(background?.ToString(),
                            text.GetVisualAncestors().OfType<DataGridCell>().First().Background?.ToString());
                    }
                }
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    private static void Flush(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }
}
