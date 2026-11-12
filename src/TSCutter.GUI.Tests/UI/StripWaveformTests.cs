using System.Reflection;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Threading;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using Splat;
using TSCutter.GUI.Controls;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Services.Preview;
using TSCutter.GUI.Tests.Editing;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Utils.Media;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.ViewModels.Application;
using TSCutter.GUI.ViewModels.Preview;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class StripWaveformTests
{
    [Fact]
    public void ReadAudioGapsUseTheBaselineWhileUnloadedRangesStayEmpty()
    {
        var waveform = new StripWaveformViewModel();
        var peaks = Enumerable.Repeat(float.NaN, AudioWaveformReader.BucketsPerSecond).ToArray();
        peaks[279] = peaks[302] = 0.5f;
        typeof(StripWaveformViewModel).GetMethod("Store", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(waveform, [0, peaks]);
        Assert.True(waveform.TryGetPeak(0.280, 0.302, out var gap));
        Assert.Equal(0, gap);
        Assert.True(waveform.TryGetPeak(0.279, 0.303, out var sound));
        Assert.Equal(0.5f, sound);
        Assert.False(waveform.TryGetPeak(1.280, 1.302, out _));
        Assert.False(waveform.TryGetPeak(0.990, 1.010, out _));
    }

    [NativeMediaTheory("remux")]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(39)]
    public async Task FullVisibleWaveformLoadsRegardlessOfTheCurrentPosition(double center)
    {
        var path = Path.Combine(NativeTestEnvironment.FixtureDirectory("remux"), "waveform.mp4");
        Assert.True(File.Exists(path), "缺少 waveform.mp4；请重新生成 remux 合成素材。");
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var input = FormatContext.OpenInputUrl(path);
            input.LoadStreamInfo();
            var video = input.GetVideoStream();
            var selection = PreviewStreamUtil.GetSelection(input, video, null);
            var audio = Assert.Single(PreviewStreamUtil.CreateAudioOptions(input, selection));
            var waveform = new StripWaveformViewModel();
            waveform.SetSource(new AudioWaveformSource(path, null, -1, selection, audio.Selection, 0));
            try
            {
                waveform.Request(0, 20, 0);
                await waveform.IdleTask;
                Dispatcher.UIThread.RunJobs();
                // 回到已缓存的窗口时，另一侧尚未完成的读取必须取消。
                waveform.Request(20, 40, 30);
                var cancellation = (CancellationTokenSource)typeof(StripWaveformViewModel)
                    .GetField("active", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(waveform)!;
                waveform.Request(0, 20, 0);
                Assert.True(cancellation.IsCancellationRequested);
                await waveform.IdleTask;
                Dispatcher.UIThread.RunJobs();

                waveform.Request(0, 40, center);
                await waveform.IdleTask;
                Dispatcher.UIThread.RunJobs();
                foreach (var second in new[] { 0.1, 15.1, 30.1, 39.1 })
                {
                    Assert.True(waveform.TryGetPeak(second, second + 0.1, out var peak), $"{center}: {second}");
                    Assert.InRange(peak, 0.05, 0.2);
                }
                Assert.False(waveform.TryGetPeak(40, 40.1, out _));
            }
            finally
            {
                waveform.SetSource(null);
                await waveform.IdleTask;
                Dispatcher.UIThread.RunJobs();
            }
            return true;
        }, CancellationToken.None);
    }

    [NativeMediaFact("frameaccurate")]
    public async Task WaveformFollowsTheStripAcrossPtsWrapAndDisappearsWithoutDecodableAudio()
    {
        // 8 秒的 440 Hz 正弦波（振幅 1/8），33 位 PTS 约在第 3.7 秒回绕。
        var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "wrap.ts");
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            locale.SwitchLanguage("zh-CN");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            var config = new MemoryConfiguration();
            var vm = new MainWindowViewModel(new DialogService(DispatchProxy.Create<IDialogManager, ExportDialogManager>()),
                config) { VideoPath = source };
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                Assert.True(await (Task<bool>)typeof(MainWindowViewModel).GetMethod("LoadVideoAsync", flags)!
                    .Invoke(vm, [null])!);
                await vm.SeekToTimeAndDrawFrameAsync(TimeSpan.FromSeconds(6));
                await vm.KeyFrameStrip.IdleTask;
                var waveform = vm.KeyFrameStripWaveform;
                Assert.True(waveform.IsAvailable);
                // 导航条显示的各关键帧之后半秒处，范围跨过回绕点。
                var seconds = vm.KeyFrameStrip.Items.Where(item => !item.IsPlaceholder)
                    .Select(item => item.Timestamp.TotalSeconds + 0.5).ToArray();
                Assert.Contains(seconds, second => second < 3.7);
                Assert.Contains(seconds, second => second > 3.7);
                for (var attempt = 0; attempt < 100 && !seconds.All(second => waveform.TryGetPeak(second, second + 0.1, out _)); attempt++)
                {
                    await waveform.IdleTask;
                    await Task.Delay(50);
                }
                // 导航条范围内的波形跨过 PTS 回绕仍然连续，峰值与正弦波振幅一致
                // （单声道转为立体声时声像居中，峰值约为 1/8 × 0.707）。
                foreach (var second in seconds)
                {
                    Assert.True(waveform.TryGetPeak(second, second + 0.1, out var peak));
                    Assert.InRange(peak, 0.08, 0.1);
                }

                // 设置应用后停止读取并收起波形条；视图菜单可重新打开，保持同一偏好。
                var strip = new KeyFrameStripControl { Waveform = waveform };
                using var binding = strip.Bind(KeyFrameStripControl.ShowWaveformProperty,
                    new Binding(nameof(StripWaveformViewModel.IsAvailable)) { Source = waveform });
                var height = strip.Height;
                var settings = new SettingsWindowViewModel(config, locale) { ShowAudioWaveform = false };
                Assert.True(vm.ShowAudioWaveform);
                settings.ApplyCommand.Execute(null);
                await waveform.IdleTask;
                Assert.False(vm.ShowAudioWaveform);
                Assert.False(waveform.IsAvailable);
                Assert.Equal(height - KeyFrameStripControl.WaveformHeight, strip.Height);
                Assert.False(waveform.TryGetPeak(seconds[0], seconds[0] + 0.1, out _));
                vm.ShowAudioWaveform = true;
                await waveform.IdleTask;
                Dispatcher.UIThread.RunJobs();
                Assert.True(new SettingsWindowViewModel(config, locale).ShowAudioWaveform);
                Assert.True(waveform.IsAvailable);
                Assert.Equal(height, strip.Height);
                Assert.True(waveform.TryGetPeak(seconds[0], seconds[0] + 0.1, out _));

                // 所有音轨都不能解码时，整条波形不存在。
                var unsupported = new PreviewAudioTrackOption(vm.SelectedPreviewAudioTrack!.Selection, "", canDecode: false);
                typeof(MainWindowViewModel).GetMethod("SetPlaybackTrackChoices", flags)!.Invoke(vm, [new[] { unsupported }]);
                Assert.False(waveform.IsAvailable);
            }
            finally
            {
                vm.Close();
                await vm.KeyFrameStrip.IdleTask;
                await vm.KeyFrameStripWaveform.IdleTask;
                Dispatcher.UIThread.RunJobs();
            }
            return true;
        }, CancellationToken.None);
    }

    private sealed class MemoryConfiguration : IConfigurationService
    {
        public AppConfig CurrentConfig { get; } = new();
        public void Load() { }
        public void Save() { }
        public void ApplyTheme(string theme) { }
        public void ApplyLanguage(string language) { }
    }
}
