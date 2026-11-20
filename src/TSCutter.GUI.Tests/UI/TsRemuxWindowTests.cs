using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Classic.Avalonia.Theme;
using Splat;
using TSCutter.GUI.Models.TransportStream.Analysis;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Themes;
using TSCutter.GUI.Utils.TransportStream.Packets;
using TSCutter.GUI.ViewModels.TransportStream.Processing;
using TSCutter.GUI.Views.TransportStream.Processing;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class TsRemuxWindowTests
{
    [Fact]
    public async Task TrackSelectionKeepsTheGridHeightAndEnablesTransferEditingOnlyForKeptHevc()
    {
        var folder = Directory.CreateTempSubdirectory("ts-remux-ui-");
        try
        {
            await VerifyTrackSelectionAsync(Path.Combine(folder.FullName, "tracks.ts"));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static async Task VerifyTrackSelectionAsync(string path)
    {
        var pat = TsPsiSectionBuilder.BuildPat(1, 0, [new(1, 0x100)]);
        var pmt = TsPsiSectionBuilder.BuildPmt(1, 0, 0x101, [],
        [
            new(0x101, new TsStreamDefinition { StreamType = TsStreamTypes.Hevc }),
            new(0x102, new TsStreamDefinition { StreamType = TsStreamTypes.Aac })
        ]);
        var tables = Enumerable.Range(0, 4).SelectMany(_ => new[] { (0, pat), (0x100, pmt) }).ToArray();
        await File.WriteAllBytesAsync(path,
            TsProgramTablePacketizer.Create(tables, TsPacketLayout.FromPacketOffset(188, 0), 0, new int[8192]));

        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            locale.SwitchLanguage("en-US");
            Avalonia.Application.Current!.Styles.Insert(1, new StyleInclude(new Uri("avares://TSCutterGUI/"))
            {
                Source = new Uri("avares://Classic.Avalonia.Theme.DataGrid/Classic.axaml")
            });
            var window = new TsRemuxWindow();
            window.Width = window.MinWidth;
            try
            {
                window.Show();
                Flush(window);
                var viewModel = new TsRemuxWindowViewModel(null!) { FilePath = path };
                window.DataContext = viewModel;
                await viewModel.ProbeCommand.ExecuteAsync(null);
                var service = Assert.Single(viewModel.Services);
                var video = service.Tracks.Single(track => track.CanEditTransferCharacteristic);
                var audio = service.Tracks.Single(track => !track.CanEditTransferCharacteristic);
                var grid = window.GetVisualDescendants().OfType<DataGrid>()
                    .Single(control => ReferenceEquals(control.ItemsSource, service.Tracks));
                var transfer = window.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => ReferenceEquals(control.ItemsSource, viewModel.TransferOptions));
                var serviceType = window.GetVisualDescendants().OfType<ComboBox>()
                    .Single(control => ReferenceEquals(control.ItemsSource, viewModel.ServiceTypeOptions));

                foreach (var theme in new[] { ClassicTheme.Standard, CustomTheme.MacOS })
                {
                    window.RequestedThemeVariant = theme;
                    viewModel.SelectedTrack = video;
                    Flush(window);
                    Assert.True(transfer.IsEffectivelyVisible);
                    Assert.True(transfer.IsEffectivelyEnabled);
                    Assert.Same(serviceType.Parent, transfer.Parent);
                    Assert.Equal(serviceType.Bounds.Top, transfer.Bounds.Top, precision: 0);
                    Assert.True(serviceType.Bounds.Right < transfer.Bounds.Left);
                    var rightEdge = transfer.Bounds.Right;
                    var gridHeight = grid.Bounds.Height;
                    Assert.True(gridHeight > 0);

                    video.IsSelected = false;
                    Flush(window);
                    Assert.True(transfer.IsEffectivelyVisible);
                    Assert.False(transfer.IsEffectivelyEnabled);
                    video.IsSelected = true;

                    viewModel.SelectedTrack = audio;
                    Flush(window);
                    Assert.False(transfer.IsEffectivelyVisible);
                    Assert.Equal(rightEdge, serviceType.Bounds.Right, precision: 0);
                    Assert.Equal(gridHeight, grid.Bounds.Height);
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static void Flush(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
