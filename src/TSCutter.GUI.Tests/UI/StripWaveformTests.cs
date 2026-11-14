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
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Services.Preview;
using TSCutter.GUI.Tests.Editing;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.Preview;
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

    [Fact]
    public void SpanAcrossATimestampJumpIsCappedWhileRegularSpansStayUnchanged()
    {
        static KeyFrameStripItem Frame(double seconds) =>
            new((long)(seconds * 90000), TimeSpan.FromSeconds(seconds), null, false);
        var placeholder = new KeyFrameStripItem(long.MinValue, default, null, false);
        var limit = StripWaveformViewModel.MaximumSpanSeconds;

        // 正常间隔照旧：到下一张为止，最后一张沿用前一段的长度；跨过跳变的一格只取开头一段。
        var frames = new[] { placeholder, Frame(1), Frame(2), Frame(20002), Frame(20003), placeholder };
        Assert.Null(StripWaveformViewModel.GetSpan(frames, 0));
        Assert.Equal((1d, 2d), StripWaveformViewModel.GetSpan(frames, 1));
        Assert.Equal((2d, 2 + limit), StripWaveformViewModel.GetSpan(frames, 2));
        Assert.Equal((20002d, 20003d), StripWaveformViewModel.GetSpan(frames, 3));
        Assert.Equal((20003d, 20004d), StripWaveformViewModel.GetSpan(frames, 4));
        Assert.Null(StripWaveformViewModel.GetSpan(frames, 5));
        Assert.Equal(4, StripWaveformViewModel.GetSpans(frames).Count);

        // 跳变后的最后一张按前一段外推时同样受限，不会把读取范围再翻一倍。
        Assert.Equal((20001d, 20001 + limit), StripWaveformViewModel.GetSpan([Frame(1), Frame(20001)], 1));
    }

    [NativeMediaFact("frameaccurate")]
    public async Task WaveformSkipsTheGapOfAForwardClockJumpTheStripSpans()
    {
        // 12 秒的合成 TS（440 Hz 正弦波，振幅 1/8，约每秒一个关键帧）。从第 6 秒的关键帧起，PCR、PTS、DTS
        // 一起向前跳 20000 秒，不设置不连续标志：时间轴约 20012 秒，打开时导航条有一格跨过跳变。
        const double jumpSeconds = 20000;
        using var sample = ClockJumpSample.Create(jumpSeconds);
        var source = sample.Path;
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            var config = new MemoryConfiguration();
            config.CurrentConfig.ClipCutMode = ClipCutMode.KeyFrame;
            var vm = new MainWindowViewModel(
                new DialogService(DispatchProxy.Create<IDialogManager, ExportDialogManager>()), config) { VideoPath = source };
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                Assert.True(await (Task<bool>)typeof(MainWindowViewModel).GetMethod("LoadVideoAsync", flags)!
                    .Invoke(vm, [null])!);
                Assert.InRange(vm.DurationMax, jumpSeconds, jumpSeconds + 20);
                await vm.KeyFrameStrip.IdleTask;
                var items = vm.KeyFrameStrip.Items.Where(item => !item.IsPlaceholder).ToArray();
                Assert.Contains(items, item => item.Timestamp.TotalSeconds < 6);
                Assert.Contains(items, item => item.Timestamp.TotalSeconds > jumpSeconds);

                var waveform = vm.KeyFrameStripWaveform;
                var spans = StripWaveformViewModel.GetSpans(vm.KeyFrameStrip.Items);
                var probes = spans.Select(span => span.Start + 0.1).ToArray();
                for (var attempt = 0; attempt < 100 && !probes.All(second => waveform.TryGetPeak(second, second + 0.1, out _)); attempt++)
                {
                    await waveform.IdleTask;
                    await Task.Delay(50);
                    Dispatcher.UIThread.RunJobs();
                }
                await waveform.IdleTask;
                Dispatcher.UIThread.RunJobs();

                // 跳变前后的每张画面下方都有波形，峰值与正弦波振幅一致。
                foreach (var second in probes)
                {
                    Assert.True(waveform.TryGetPeak(second, second + 0.1, out var peak), $"{second}");
                    Assert.InRange(peak, 0.08, 0.1);
                }
                // 跳变中间的空白不读取：跨过跳变的一格只读开头一段，而不是逐秒读完两万秒。
                Assert.All(spans, span => Assert.InRange(span.End - span.Start, 0, StripWaveformViewModel.MaximumSpanSeconds));
                Assert.False(waveform.TryGetPeak(jumpSeconds / 2, jumpSeconds / 2 + 0.1, out _));
                var cached = ((Dictionary<int, float[]>)typeof(StripWaveformViewModel)
                    .GetField("cache", flags)!.GetValue(waveform)!).Keys;
                Assert.All(cached, second => Assert.True(
                    second <= 6 + StripWaveformViewModel.MaximumSpanSeconds || second >= jumpSeconds, $"{second}"));
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
