using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Headless;
using HanumanInstitute.MvvmDialogs;
using Splat;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Themes;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.Views;
using TSCutter.GUI.Views.Application;
using Xunit;

namespace TSCutter.GUI.Tests.Player;

/// <summary>
/// 主窗口播放接线的场景化验证：加载文件 → 播放推进画面 → 停止保留画面。
/// 覆盖 TogglePlaybackCommand 的可用性与播放状态属性。
/// </summary>
[Collection("Avalonia UI")]
public sealed class MainWindowPlaybackTests
{
    private static MainWindowViewModel CreateViewModel()
    {
        var localization = new LocalizationService();
        localization.SwitchLanguage("en-US");
        Locator.CurrentMutable.RegisterConstant<ILocalizationService>(localization);
        return new MainWindowViewModel(
            new HanumanInstitute.MvvmDialogs.Avalonia.DialogService(
                System.Reflection.DispatchProxy.Create<IDialogManager, UnusedDialogs>()),
            new MemoryConfiguration());
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(30);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    public class UnusedDialogs : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) => null;
    }

    private sealed class MemoryConfiguration : IConfigurationService
    {
        public AppConfig CurrentConfig { get; } = new() { PreferHardwareDecoding = false };
        public int Saves { get; private set; }
        public void Load() { }
        public void Save() => Saves++;
        public void ApplyTheme(string theme) { }
        public void ApplyLanguage(string language) { }
    }

    [Fact]
    public async Task TrackPickersStayStableThroughoutTimelineScrubbing()
    {
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch(() =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var vm = CreateViewModel();
            vm.VideoPath = "unused.mp4";
            var video = new VideoInstance(vm.VideoPath);
            typeof(VideoInstance).GetProperty(nameof(video.Inited))!.SetValue(video, true);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(MainWindowViewModel).GetField("_videoInstance", flags)!.SetValue(vm, video);
            var tracks = new[]
            {
                new PreviewAudioTrackOption(new(1, 101, null), "Audio 1"),
                new PreviewAudioTrackOption(new(2, 102, null), "Unsupported", false),
                new PreviewAudioTrackOption(new(3, 103, null), "Audio 2")
            };
            typeof(MainWindowViewModel).GetMethod("SetPlaybackTrackChoices", flags)!.Invoke(vm, [tracks]);
            vm.SelectedPreviewAudioTrack = tracks[2];
            var window = new MainWindow { DataContext = vm };
            try
            {
                window.Show();
                var picker = window.FindControl<ComboBox>("PreviewAudioPicker")!;
                foreach (var theme in new[] { CustomTheme.Windows8,
                             CustomTheme.Windows11, CustomTheme.MacOS })
                {
                    window.RequestedThemeVariant = theme;
                    Dispatcher.UIThread.RunJobs();
                    var items = picker.ItemsSource;
                    Assert.True(picker.IsEffectivelyEnabled);
                    Assert.True(vm.CanSwitchPreviewTrack);
                    vm.BeginTimelineScrub();
                    for (var frame = 0; frame < 3; frame++)
                    {
                        vm.DecodingOpCount++;
                        Assert.True(picker.IsEffectivelyEnabled);
                        Assert.False(vm.CanSwitchPreviewTrack);
                        vm.DecodingOpCount--;
                        Assert.True(picker.IsEffectivelyEnabled);
                        Assert.False(vm.CanSwitchPreviewTrack);
                        Assert.Same(items, picker.ItemsSource);
                        Assert.Same(tracks[2], picker.SelectedValue);
                    }
                    vm.EndTimelineScrub();
                    Assert.True(vm.CanSwitchPreviewTrack);
                    Assert.False(picker.Items.OfType<ComboBoxItem>().ElementAt(1).IsEnabled);
                }
                // 单条音轨也保留入口；全部不支持时仍显示具体轨道和解码限制。
                foreach (var choice in new[] { tracks[1], tracks[0] })
                {
                    typeof(MainWindowViewModel).GetMethod("SetPlaybackTrackChoices", flags)!.Invoke(vm,
                        [new[] { choice }]);
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(vm.ShowPreviewTrackSelector);
                    Assert.True(picker.IsEffectivelyVisible);
                    Assert.Same(choice, picker.SelectedValue);
                    Assert.Equal(choice.CanDecode, Assert.Single(picker.Items.OfType<ComboBoxItem>()).IsEnabled);
                }
                vm.Close();
                Assert.False(picker.IsEffectivelyEnabled);
            }
            finally
            {
                window.Close();
                vm.Close();
            }
        }, System.Threading.CancellationToken.None);
    }

    [Fact]
    public async Task PlaybackPreparationDelaysProgressAndCanBeCanceledAcrossThemes()
    {
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var vm = CreateViewModel();
            vm.VideoPath = "unused.mp4";
            using var video = new VideoInstance(vm.VideoPath);
            typeof(VideoInstance).GetProperty(nameof(video.Inited))!.SetValue(video, true);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(MainWindowViewModel).GetField("_videoInstance", flags)!.SetValue(vm, video);
            var window = new MainWindow { DataContext = vm };
            window.Show();
            try
            {
                foreach (var theme in new[] { CustomTheme.Windows8, CustomTheme.Windows11, CustomTheme.MacOSDark })
                {
                    window.RequestedThemeVariant = theme;
                    Dispatcher.UIThread.RunJobs();
                    var preview = window.FindControl<Grid>("PreviewImageHost")!;
                    var bounds = preview.Bounds;
                    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    typeof(MainWindowViewModel).GetField("playbackStopTask", flags)!.SetValue(vm, ready.Task);
                    var starting = vm.TogglePlaybackCommand.ExecuteAsync(null);
                    try
                    {
                        Dispatcher.UIThread.RunJobs();
                        Assert.True(vm.IsPlaybackStarting);
                        Assert.False(vm.ShowPlaybackLoading);
                        Assert.False(window.FindControl<Border>("PlaybackLoadingPanel")!.IsVisible);
                        await WaitForAsync(() => vm.ShowPlaybackLoading);
                        Assert.True(window.FindControl<Border>("PlaybackLoadingPanel")!.IsEffectivelyVisible);
                        Assert.Equal(bounds, preview.Bounds);
                        Assert.NotEqual("Play (Space)", vm.PlaybackButtonToolTip);
                        Assert.True(vm.CanTogglePlayback);
                        window.ToggleVideoFullScreenCommand.Execute(null);
                        Dispatcher.UIThread.RunJobs();
                        Assert.True(window.FindControl<Border>("FullScreenPlaybackLoadingPanel")!.IsEffectivelyVisible);
                        await vm.TogglePlaybackCommand.ExecuteAsync(null);
                        Dispatcher.UIThread.RunJobs();
                        Assert.False(vm.IsPlaybackStarting);
                        Assert.False(window.FindControl<Border>("FullScreenPlaybackLoadingPanel")!.IsVisible);
                    }
                    finally
                    {
                        ready.TrySetResult();
                        await starting;
                        window.ToggleVideoFullScreenCommand.Execute(null);
                    }
                    Assert.False(vm.IsPlaybackActive);
                }
                // 立即结束准备时提示始终隐藏，旧计时器也不能在稍后重新显示。
                var fastReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                typeof(MainWindowViewModel).GetField("playbackStopTask", flags)!.SetValue(vm, fastReady.Task);
                var fastStarting = vm.TogglePlaybackCommand.ExecuteAsync(null);
                try
                {
                    Assert.False(vm.ShowPlaybackLoading);
                    await vm.TogglePlaybackCommand.ExecuteAsync(null);
                }
                finally { fastReady.TrySetResult(); await fastStarting; }
                await Task.Delay(600);
                Dispatcher.UIThread.RunJobs();
                Assert.False(vm.ShowPlaybackLoading);
                Assert.False(window.FindControl<Border>("PlaybackLoadingPanel")!.IsVisible);
                return true;
            }
            finally { window.Close(); vm.Close(); }
        }, System.Threading.CancellationToken.None);
    }

    [NativeMediaFact("keyframes")]
    public async Task TogglePlaybackPlaysAdvancesAndStopsKeepingTheFrame()
    {
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(new LocalizationService());
            var vm = CreateViewModel();
            vm.VideoPath = Path.Combine(NativeTestEnvironment.FixtureDirectory("keyframes"), "fixed.mp4");
            var load = typeof(MainWindowViewModel).GetMethod("LoadVideoAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            try
            {
                Assert.True(await (Task<bool>)load.Invoke(vm, new object?[] { null })!);
                Assert.True(vm.IsVideoInitialized);
                Assert.True(vm.TogglePlaybackCommand.CanExecute(null));
                Assert.False(vm.IsPlaybackActive);

                // 模拟按钮绑定：CanExecute 依赖 CanExecuteChanged 事件重查，
                // 加载完成后必须收到通知，否则按钮停留在禁用态。
                vm.TogglePlaybackCommand.Execute(null);
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!vm.IsPlaybackPlaying && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(30);
                    Dispatcher.UIThread.RunJobs();
                }
                Assert.True(vm.IsPlaybackPlaying);
                Assert.True(vm.IsPlaybackActive);

                double timeWhilePlaying = -1;
                deadline = DateTime.UtcNow.AddSeconds(10);
                while (timeWhilePlaying < 0.3 && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50);
                    Dispatcher.UIThread.RunJobs();
                    timeWhilePlaying = vm.CurrentTime;
                }
                Assert.True(timeWhilePlaying >= 0.3, $"播放时间未推进：{timeWhilePlaying}");
                Assert.NotNull(vm.DecodedBitmap);
                await WaitForAsync(() => vm.PlaybackFpsText != "— FPS");
                Assert.NotEqual("— FPS", vm.PlaybackFpsText);

                // 再次触发 = 停止播放；画面与显示时间保留。
                var pausedTime = vm.CurrentTime;
                var pausedBitmap = vm.DecodedBitmap;
                while (!vm.TogglePlaybackCommand.CanExecute(null))
                {
                    await Task.Delay(30);
                    Dispatcher.UIThread.RunJobs();
                }
                vm.TogglePlaybackCommand.Execute(null);
                Assert.False(vm.IsPlaybackActive);
                Assert.Equal("— FPS", vm.PlaybackFpsText);
                Assert.Equal(pausedTime, vm.CurrentTime, 6);
                Assert.Same(pausedBitmap, vm.DecodedBitmap);
                Assert.True(vm.TogglePlaybackCommand.CanExecute(null));
                return true;
            }
            finally
            {
                var close = typeof(MainWindowViewModel).GetMethod("ClearVars",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                close?.Invoke(vm, null);
            }
        }, System.Threading.CancellationToken.None);
    }

    [NativeMediaFact("keyframes")]
    public async Task TimelineScrubResumesPlaybackOnlyWhenPlaying()
    {
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(new LocalizationService());
            var vm = CreateViewModel();
            vm.VideoPath = Path.Combine(NativeTestEnvironment.FixtureDirectory("keyframes"), "fixed.mp4");
            var load = typeof(MainWindowViewModel).GetMethod("LoadVideoAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            try
            {
                Assert.True(await (Task<bool>)load.Invoke(vm, new object?[] { null })!);

                // 真实导航操作后，绑定按钮也应重新变为可用；不能只测试空的 Begin/End。
                var button = new Avalonia.Controls.Button { Command = vm.TogglePlaybackCommand };
                await vm.SeekToTimeAndDrawFrameAsync(TimeSpan.FromSeconds(2.5));
                Dispatcher.UIThread.RunJobs();
                Assert.True(button.IsEffectivelyEnabled);
                Assert.True(vm.TogglePlaybackCommand.CanExecute(null));

                // 未在播放时拖动：结束后保持暂停，且仍可再次开始播放。
                vm.BeginTimelineScrub();
                Assert.False(vm.IsPlaybackActive);
                vm.EndTimelineScrub();
                await Task.Delay(150);
                Dispatcher.UIThread.RunJobs();
                Assert.False(vm.IsPlaybackActive);
                Assert.True(vm.TogglePlaybackCommand.CanExecute(null), "拖动后播放按钮应可用");
                Assert.True(button.IsEffectivelyEnabled);
                vm.TogglePlaybackCommand.Execute(null);
                await WaitForAsync(() => vm.IsPlaybackPlaying);
                Assert.True(vm.IsPlaybackPlaying, "拖动后应能重新开始播放");
                vm.TogglePlaybackCommand.Execute(null);
                await WaitForAsync(() => !vm.IsPlaybackActive);

                // 播放中拖动：结束后从新位置恢复播放。
                vm.TogglePlaybackCommand.Execute(null);
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (!vm.IsPlaybackPlaying && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(30);
                    Dispatcher.UIThread.RunJobs();
                }
                Assert.True(vm.IsPlaybackPlaying);

                vm.BeginTimelineScrub();
                Assert.False(vm.IsPlaybackActive);
                await vm.CompleteScrubPreviewAsync();
                await vm.SeekToTimeAndDrawFrameAsync(TimeSpan.FromSeconds(3.5));
                vm.EndTimelineScrub();
                deadline = DateTime.UtcNow.AddSeconds(8);
                while (!vm.IsPlaybackPlaying && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(30);
                    Dispatcher.UIThread.RunJobs();
                }
                Assert.True(vm.IsPlaybackPlaying);
                return true;
            }
            finally
            {
                typeof(MainWindowViewModel).GetMethod("StopPlaybackSession",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
                var close = typeof(MainWindowViewModel).GetMethod("ClearVars",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                close?.Invoke(vm, null);
            }
        }, System.Threading.CancellationToken.None);
    }

    [NativeMediaFact("keyframes")]
    public async Task ClosingWhileNavigationWaitsForPauseDiscardsTheOldSeek()
    {
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var vm = CreateViewModel();
            vm.VideoPath = Path.Combine(NativeTestEnvironment.FixtureDirectory("keyframes"), "fixed.mp4");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var load = typeof(MainWindowViewModel).GetMethod("LoadVideoAsync", flags)!;
            var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task navigation = Task.CompletedTask;
            try
            {
                Assert.True(await (Task<bool>)load.Invoke(vm, [null])!);
                // 固定暂停尚未完成的时刻，无需依赖声卡停止速度或线程调度。
                typeof(MainWindowViewModel).GetField("playbackPauseTask", flags)!.SetValue(vm, paused.Task);
                navigation = vm.SeekToTimeAsync(TimeSpan.FromSeconds(2));
                Assert.False(navigation.IsCompleted);
                Assert.True(vm.IsDecoding);
                vm.Close();
                var disposal = (Task)typeof(MainWindowViewModel).GetField("previewDisposalTask", flags)!.GetValue(vm)!;
                Assert.False(disposal.IsCompleted);
                paused.TrySetResult();
                await navigation.WaitAsync(TimeSpan.FromSeconds(5));
                await disposal.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(vm.IsDecoding);
                Assert.False(vm.IsVideoInitialized);
                return true;
            }
            finally
            {
                paused.TrySetResult();
                try { await navigation; }
                finally { vm.Close(); }
            }
        }, System.Threading.CancellationToken.None);
    }

    [NativeMediaFact("remux")]
    public async Task MarkAfterPlaybackSnapsToACompatibleKeyframe()
    {
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(new LocalizationService());
            var vm = CreateViewModel();
            var path = Path.Combine(NativeTestEnvironment.FixtureDirectory("remux"), "hdr.mp4");
            vm.VideoPath = path;
            var load = typeof(MainWindowViewModel).GetMethod("LoadVideoAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            try
            {
                Assert.True(await (Task<bool>)load.Invoke(vm, new object?[] { null })!);
                vm.TogglePlaybackCommand.Execute(null);
                var deadline = DateTime.UtcNow.AddSeconds(8);
                while (vm.CurrentTime < 0.4 && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(30);
                    Dispatcher.UIThread.RunJobs();
                }
                Assert.True(vm.CurrentTime >= 0.4);
                vm.TogglePlaybackCommand.Execute(null);
                await WaitForAsync(() => !vm.IsPlaybackActive);

                var pausedTime = vm.CurrentTime;
                Assert.True(vm.HasHdrPreview);
                vm.ToggleSdrToneMappingCommand.Execute(null);
                await (Task)typeof(MainWindowViewModel).GetField("toneMappingRefreshTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
                Assert.Equal(pausedTime, vm.CurrentTime, 6);
                vm.ToggleSdrToneMappingCommand.Execute(null);
                await (Task)typeof(MainWindowViewModel).GetField("toneMappingRefreshTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
                Assert.Equal(pausedTime, vm.CurrentTime, 6);

                // 播放画面标记（I 键路径）：起点吸附到合法关键帧并显示对齐后的画面。
                await vm.MarkClipStartShortcutCommand.ExecuteAsync(null);
                Assert.NotEmpty(vm.Clips);
                var clip = vm.Clips[0];

                using var reference = TSCutter.GUI.FFmpeg.FormatContext.OpenInputUrl(path);
                reference.LoadStreamInfo();
                var videoIndex = reference.GetVideoStream().Index;
                var keyframes = reference.ReadPackets().Where(packet => packet.StreamIndex == videoIndex && (packet.Flags & 1) != 0)
                    .Select(packet => packet.Pts).ToArray();
                Assert.Contains(clip.StartPts, keyframes);
                Assert.Equal(clip.StartTime, vm.CurrentTime, 3);
                return true;
            }
            finally
            {
                typeof(MainWindowViewModel).GetMethod("StopPlaybackSession",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
                var close = typeof(MainWindowViewModel).GetMethod("ClearVars",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                close?.Invoke(vm, null);
            }
        }, System.Threading.CancellationToken.None);
    }
}
