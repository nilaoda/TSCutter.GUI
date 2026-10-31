using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Classic.Avalonia.Theme;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using Splat;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Player.Diagnostics;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Themes;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.ViewModels.Player;
using TSCutter.GUI.Views;
using TSCutter.GUI.Views.Player;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class PlaybackBenchmarkWindowTests
{
    [NativeMediaFact("keyframes")]
    public async Task EntriesUseDifferentSourcesAndTheWindowRunsAcrossThemes()
    {
        using var ui = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await ui.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            locale.SwitchLanguage("zh-CN");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            Avalonia.Application.Current!.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://TSCutterGUI/"))
            { Source = new Uri("avares://TSCutterGUI/Themes/StatusColors.axaml") });
            Avalonia.Application.Current.Styles.Insert(1, new StyleInclude(new Uri("avares://TSCutterGUI/"))
            { Source = new Uri("avares://Classic.Avalonia.Theme.DataGrid/Classic.axaml") });
            Avalonia.Application.Current.DataTemplates.Add(new ViewLocator());
            var conclusions = new[]
            {
                new PlaybackBenchmarkResult(PlaybackBenchmarkStage.Decode, 100, 50, 100, 1, false),
                new PlaybackBenchmarkResult(PlaybackBenchmarkStage.Conversion, 10, 50, 10, 1, false),
                new PlaybackBenchmarkResult(PlaybackBenchmarkStage.Playback, 50, 50, 250, 5, false, GpuPresentation: true)
            };
            Assert.Equal(LocalizationManager.Instance.String_PlaybackBenchmark_Smooth, PlaybackBenchmarkWindowViewModel.DescribeConclusion(conclusions));
            var playbackRow = new PlaybackBenchmarkRow(PlaybackBenchmarkStage.Playback) { Result = conclusions[2] };
            Assert.EndsWith(" · GPU", playbackRow.StageText);
            playbackRow.Result = conclusions[2] with { HardwareSurfacePresentation = true };
            Assert.EndsWith(LocalizationManager.Instance.String_PlaybackBenchmark_GpuDirectPresentation, playbackRow.StageText);
            playbackRow.Result = conclusions[2] with { GpuPresentation = false };
            Assert.EndsWith(" · CPU", playbackRow.StageText);
            conclusions[2] = conclusions[2] with { BusyDrops = 1 };
            Assert.Equal(LocalizationManager.Instance.String_PlaybackBenchmark_PipelineLow, PlaybackBenchmarkWindowViewModel.DescribeConclusion(conclusions));
            var dialogs = new DialogService(DispatchProxy.Create<IDialogManager, UnusedDialogs>());
            var main = new MainWindowViewModel(dialogs, new MemoryConfiguration());
            var path = Path.Combine(NativeTestEnvironment.FixtureDirectory("keyframes"), "fixed.mp4");
            main.VideoPath = path;
            Assert.True(await (Task<bool>)typeof(MainWindowViewModel).GetMethod("LoadVideoAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(main, new object?[] { null })!);
            var mainWindow = new MainWindow { DataContext = main };
            var vm = new PlaybackBenchmarkWindowViewModel(dialogs);
            var window = new PlaybackBenchmarkWindow { DataContext = vm };
            try
            {
                mainWindow.Show();
                var unsupported = new PreviewAudioTrackOption(new(1, 1, null), "AV3A", false);
                var firstAudio = new PreviewAudioTrackOption(new(2, 2, null), "AAC");
                var secondAudio = new PreviewAudioTrackOption(new(3, 3, null), "AC3");
                typeof(MainWindowViewModel).GetMethod("SetPlaybackTrackChoices", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(main, [new[] { unsupported, firstAudio, secondAudio }]);
                Assert.Same(firstAudio, main.SelectedPreviewAudioTrack);
                main.SelectedPreviewAudioTrack = unsupported;
                Assert.Same(firstAudio, main.SelectedPreviewAudioTrack);
                var audioPicker = mainWindow.FindControl<ComboBox>("PreviewAudioPicker")!;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(3, audioPicker.Items.Count);
                Assert.False(((ComboBoxItem)audioPicker.Items[0]!).IsEnabled);
                audioPicker.SelectedValue = secondAudio;
                Assert.Same(secondAudio, main.SelectedPreviewAudioTrack);
                var tools = mainWindow.FindControl<Classic.CommonControls.ToolBarButton>("CurrentFileToolsButton")!;
                tools.Flyout!.ShowAt(tools);
                Dispatcher.UIThread.RunJobs();
                Assert.True(tools.IsEffectivelyEnabled);
                Assert.False(main.CanUseCurrentFileTools); // MP4 仍不启用 TS 专用工具。
                Assert.True(main.CanUsePlaybackBenchmark);
                Assert.Null(mainWindow.FindControl<MenuItem>("OpenPlaybackBenchmarkItem")!.CommandParameter);
                Assert.Equal(path, mainWindow.FindControl<MenuItem>("CurrentPlaybackBenchmarkItem")!.CommandParameter);
                tools.Flyout.Hide();

                window.Show();
                await vm.InitializeAsync(path, null, null, 2, new PixelSize(96, 54), false, true);
                Assert.Equal(2, vm.StartSeconds);
                Assert.True(vm.CanStart);
                Assert.False(vm.HasHdrTrack);
                Assert.False(vm.ToneMapping);
                Assert.False(window.FindControl<CheckBox>("ToneMappingCheckBox")!.IsEffectivelyEnabled);
                var sdrTrack = vm.SelectedTrack!;
                var hdrTrack = sdrTrack with { DynamicRange = VideoDynamicRange.Hlg };
                vm.Tracks = [sdrTrack, hdrTrack];
                vm.SelectedTrack = hdrTrack;
                Assert.True(window.FindControl<CheckBox>("ToneMappingCheckBox")!.IsEffectivelyEnabled);
                Assert.True(vm.ToneMapping);
                vm.SelectedTrack = sdrTrack;
                Assert.False(vm.ToneMapping);
                vm.SelectedTrack = hdrTrack;
                Assert.True(vm.ToneMapping);
                vm.SelectedTrack = sdrTrack;
                vm.Tracks = [sdrTrack];
                Assert.Equal(3, vm.Rows.Count);
                foreach (var theme in new[] { CustomTheme.MacOS, CustomTheme.MacOSDark, CustomTheme.Windows8,
                             CustomTheme.Windows10, CustomTheme.Windows11, ClassicTheme.Standard })
                {
                    Avalonia.Application.Current.RequestedThemeVariant = theme;
                    window.RequestedThemeVariant = theme;
                    window.Width = 700;
                    window.Height = 540;
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    mainWindow.RequestedThemeVariant = theme;
                    mainWindow.UpdateLayout();
                    Assert.Contains(audioPicker.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == secondAudio.DisplayText);
                    Assert.True(window.FindControl<Button>("StartBenchmarkButton")!.IsEffectivelyEnabled);
                    Assert.True(window.FindControl<DataGrid>("ResultGrid")!.Bounds.Height >= 120);
                    var rows = window.FindControl<DataGrid>("ResultGrid")!.GetVisualDescendants().OfType<DataGridRow>().ToArray();
                    Assert.Equal(3, rows.Length);
                    Assert.All(rows, row => Assert.True(row.Bounds.Height >= 32));
                    Assert.All(vm.Rows, row => Assert.Contains(window.FindControl<DataGrid>("ResultGrid")!.GetVisualDescendants()
                        .OfType<TextBlock>(), text => text.Text == row.StageText));
                    var startButton = window.FindControl<Button>("StartBenchmarkButton")!;
                    Assert.True(startButton.TranslatePoint(new Point(0, startButton.Bounds.Height), window)!.Value.Y <= window.ClientSize.Height);
                    var progress = window.FindControl<ProgressBar>("BenchmarkProgress")!;
                    var progressBottom = progress.TranslatePoint(new Point(0, progress.Bounds.Height), window)!.Value.Y;
                    var buttonsTop = startButton.TranslatePoint(default, window)!.Value.Y;
                    Assert.True(progress.Bounds.Height > 0);
                    Assert.True(progressBottom <= buttonsTop, $"Progress bar overlaps buttons in {theme}.");
                }
                var test = vm.StartCommand.ExecuteAsync(null);
                var deadline = DateTime.UtcNow.AddSeconds(20);
                var sawLiveFps = false;
                while (!test.IsCompleted && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(25);
                    Dispatcher.UIThread.RunJobs();
                    sawLiveFps |= vm.Rows.Any(row => row.Result is null && row.LiveFramesPerSecond > 0);
                }
                Assert.True(sawLiveFps);
                Assert.True(test.IsCompleted);
                await test;
                Assert.False(vm.HasError, vm.StatusText);
                Assert.All(vm.Rows, row => Assert.NotNull(row.Result));
                Assert.NotNull(vm.PreviewBitmap);
                var running = vm.StartCommand.ExecuteAsync(null);
                vm.CancelCommand.Execute(null);
                await running;
                Assert.False(vm.IsBusy);
                Assert.False(vm.HasError);
            }
            finally
            {
                vm.OnClosed();
                window.Close();
                main.Close();
                mainWindow.Close();
            }
            return true;
        }, CancellationToken.None);
    }

    public class UnusedDialogs : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }
    private sealed class MemoryConfiguration : IConfigurationService
    {
        public AppConfig CurrentConfig { get; } = new() { PreferHardwareDecoding = false, ShowKeyFrameStrip = false };
        public void Load() { }
        public void Save() { }
        public void ApplyTheme(string theme) { }
        public void ApplyLanguage(string language) { }
    }
}
