using System.Reflection;
using Avalonia.Headless;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using Splat;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Tests.Editing;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.ViewModels;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class StripWaveformTests
{
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
            var vm = new MainWindowViewModel(new DialogService(DispatchProxy.Create<IDialogManager, ExportDialogManager>()),
                new ConfigurationService(locale)) { VideoPath = source };
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

                // 所有音轨都不能解码时，整条波形不存在。
                var unsupported = new PreviewAudioTrackOption(vm.SelectedPreviewAudioTrack!.Selection, "", canDecode: false);
                typeof(MainWindowViewModel).GetMethod("SetPlaybackTrackChoices", flags)!.Invoke(vm, [new[] { unsupported }]);
                Assert.False(waveform.IsAvailable);
            }
            finally
            {
                vm.Close();
            }
            return true;
        }, CancellationToken.None);
    }
}
