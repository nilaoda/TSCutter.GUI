using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Classic.Avalonia.Theme;
using FFmpeg.AutoGen.Abstractions;
using Splat;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Tests.Editing;
using TSCutter.GUI.Themes;
using TSCutter.GUI.ViewModels.Media;
using TSCutter.GUI.Views.Media;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class MediaMuxWindowTests
{
    [RemuxExportFact(requireSubtitleEncoders: true)]
    public async Task ImportsEditsAndContainerChecksStayUsableAcrossThemes()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            locale.SwitchLanguage("zh-CN");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            Avalonia.Application.Current!.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://TSCutterGUI/"))
            {
                Source = new Uri("avares://TSCutterGUI/Themes/StatusColors.axaml")
            });
            Avalonia.Application.Current.Styles.Insert(1, new StyleInclude(new Uri("avares://TSCutterGUI/"))
            {
                Source = new Uri("avares://Classic.Avalonia.Theme.DataGrid/Classic.axaml")
            });
            var root = FFmpeg.NativeTestEnvironment.FixtureDirectory("remux");
            var vm = new MediaMuxWindowViewModel(null!);
            var window = new MediaMuxWindow { DataContext = vm };
            try
            {
                await vm.AddPathsAsync([Path.Combine(root, "multi.mp4"), Path.Combine(root, "events.srt"), Path.Combine(root, "multi.mp4")]);
                Assert.Equal(2, vm.Sources.Count);
                Assert.All(vm.Tracks, track => Assert.True(track.IsCompatible, track.Status));
                Assert.Equal("mp4", vm.Container);
                Assert.EndsWith(".mp4", vm.OutputPath);
                Assert.Contains(vm.Tracks, track => track.ConvertsSubtitle && track.Status.Contains("mov_text"));
                Assert.NotEmpty(vm.SubtitleConversionHint);
                Assert.True(vm.CanExport, vm.ValidationText);
                var validationUpdates = 0;
                void CountValidation(object? sender, PropertyChangedEventArgs args)
                {
                    if (args.PropertyName == nameof(vm.ValidationText))
                        validationUpdates++;
                }
                vm.PropertyChanged += CountValidation;
                vm.Container = "ts";
                vm.PropertyChanged -= CountValidation;
                Assert.InRange(validationUpdates, 1, 3);
                Assert.False(vm.CanExport);
                Assert.All(vm.Tracks.Where(track => track.IsSubtitle), track => Assert.False(track.IsCompatible));
                vm.Container = "mkv";
                var audio = vm.Tracks.Where(track => track.Stream.Type == AVMediaType.AVMEDIA_TYPE_AUDIO).ToArray();
                vm.SelectedTrack = audio[1];
                audio[1].IsDefault = true;
                Assert.False(audio[0].IsDefault);
                vm.MoveTrack(audio[1], vm.Tracks[0]);
                Assert.Same(audio[1], vm.Tracks[0]);
                audio[1].OffsetMilliseconds = -125;
                audio[1].Language = "x";
                Assert.False(vm.CanExport);
                audio[1].Language = "jpn";
                vm.Container = "mp4";
                Assert.True(vm.CanExport, vm.ValidationText);
                Assert.Contains(vm.Tracks, track => track.ConvertsSubtitle && track.Status.Contains("mov_text"));
                Assert.EndsWith(".mp4", vm.OutputPath);
                vm.Container = "mkv";
                Assert.True(vm.CanExport, vm.ValidationText);
                Assert.Contains(vm.Tracks, track => track.ConvertsSubtitle && track.Status.Contains("SRT"));
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var shortest = window.FindControl<CheckBox>("ShortestCheckBox")!;
                var shortestSubtitles = window.FindControl<CheckBox>("ShortestSubtitlesCheckBox")!;
                Assert.False(vm.FastStart);
                var fastStart = window.FindControl<CheckBox>("FastStartCheckBox")!;
                Assert.False(fastStart.IsEffectivelyEnabled);
                vm.Container = "mp4";
                Assert.True(fastStart.IsEffectivelyEnabled);
                fastStart.IsChecked = true;
                Assert.True(vm.FastStart);
                vm.Container = "mkv";
                Assert.False(fastStart.IsEffectivelyEnabled);
                Assert.False(vm.Shortest);
                Assert.False(vm.ShortestIncludesSubtitles);
                Assert.False(shortest.IsChecked);
                Assert.False(shortestSubtitles.IsEffectivelyEnabled);
                shortest.IsChecked = true;
                shortestSubtitles.IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                Assert.True(vm.Shortest);
                Assert.True(vm.ShortestIncludesSubtitles);
                shortest.IsChecked = false;
                Assert.False(shortestSubtitles.IsEffectivelyEnabled);
                var handle = window.FindControl<DataGrid>("TrackGrid")!.GetVisualDescendants()
                    .OfType<Border>().Single(border => ReferenceEquals(border.Tag, audio[1]));
                IPointer? pointer = null;
                handle.AddHandler(InputElement.PointerPressedEvent, (_, args) => pointer = args.Pointer, RoutingStrategies.Tunnel);
                var handlePoint = handle.TranslatePoint(handle.Bounds.Center, window)!.Value;
                window.MouseDown(handlePoint, MouseButton.Left);
                Assert.Same(handle, pointer!.Captured);
                window.MouseUp(handlePoint, MouseButton.Left);
                Assert.Null(pointer.Captured);
                foreach (var language in new[] { "en-US", "zh-CN", "zh-TW" })
                {
                    locale.SwitchLanguage(language);
                    foreach (var theme in new[] { ClassicTheme.Standard, CustomTheme.DarkStandard,
                                 CustomTheme.LunaBlue, CustomTheme.LunaOliveGreen, CustomTheme.LunaSilver, CustomTheme.LunaDark, CustomTheme.Aero, CustomTheme.AeroDark,
                                 CustomTheme.Windows8, CustomTheme.Windows8Dark, CustomTheme.Windows10, CustomTheme.Windows10Dark, CustomTheme.Windows11,
                                 CustomTheme.Windows11Dark, CustomTheme.MacOS, CustomTheme.MacOSDark })
                    foreach (var width in new[] { 1080d, 960d })
                    {
                        Avalonia.Application.Current.RequestedThemeVariant = theme;
                        window.Width = width;
                        window.Height = width == 960 ? 600 : 700;
                        await Task.Delay(20);
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                        var start = window.FindControl<Button>("StartMuxButton")!;
                        var position = start.TranslatePoint(default, window)!.Value;
                        Assert.True(start.IsEffectivelyEnabled);
                        Assert.InRange(position.X + start.Bounds.Width, 1, window.Bounds.Width);
                        Assert.InRange(position.Y + start.Bounds.Height, 1, window.Bounds.Height);
                        var grid = window.FindControl<DataGrid>("TrackGrid")!;
                        Assert.True(grid.Bounds.Height > 180);
                        Assert.NotEmpty(grid.GetVisualDescendants().OfType<DataGridRow>());
                        if (language == "zh-CN" && width == 960)
                        {
                            var originalBounds = grid.Bounds;
                            var status = vm.StatusText;
                            foreach (var (busy, exporting, progress, text) in new[]
                                     {
                                         (true, false, 0d, ""),
                                         (true, true, 0d, ""),
                                         (true, true, 99.9, "99.9% - 999.99 GiB - 9999.99 MiB/s"),
                                         (false, false, 100d, "999.99 GiB")
                                     })
                            {
                                vm.IsBusy = busy;
                                vm.IsExporting = exporting;
                                vm.Percent = progress;
                                vm.StatusText = busy ? "Muxing..." : status;
                                vm.ProgressText = text;
                                Dispatcher.UIThread.RunJobs();
                                window.UpdateLayout();
                                Assert.Equal(originalBounds, grid.Bounds);
                                Assert.Equal(position, start.TranslatePoint(default, window)!.Value);
                            }
                            vm.ProgressText = "";
                        }

                        Assert.Same(audio[1], vm.SelectedTrack);
                        Assert.Equal("jpn", audio[1].Language);
                        Assert.Equal(-125, audio[1].OffsetMilliseconds);
                    }
                }
                vm.MoveDownCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                Assert.Same(vm.SelectedTrack, window.FindControl<DataGrid>("TrackGrid")!.SelectedItem);
                vm.SelectedSource = vm.Sources[0];
                vm.RemoveSourceCommand.Execute(null);
                Assert.Single(vm.Sources);
                Assert.Single(vm.Tracks);
                Assert.False(vm.Tracks[0].IsDefault);
                vm.RemoveSourceCommand.Execute(null);
                Assert.Empty(vm.Sources);
                Assert.Empty(vm.OutputPath);
                vm.Container = "mp4";
                await vm.AddPathsAsync([Path.Combine(root, "long-audio.mp4")]);
                Assert.Equal(Path.Combine(root, "long-audio_mux.mp4"), vm.OutputPath);
                var customOutput = Path.Combine(root, "custom-output.mp4");
                vm.OutputPath = customOutput;
                vm.RemoveSourceCommand.Execute(null);
                Assert.Equal(customOutput, vm.OutputPath);
                await vm.AddPathsAsync([Path.Combine(root, "multi.mp4")]);
                Assert.Equal(customOutput, vm.OutputPath);
                return true;
            }
            finally
            {
                window.Close();
                vm.OnClosed();
            }
        }, CancellationToken.None);
    }
}
