using System.Reflection;
using Avalonia.Headless;
using HanumanInstitute.MvvmDialogs;
using Splat;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Media;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.Player;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.ViewModels;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

/// <summary>主窗口的多轨合并：入口、试听音轨、导出计划与工程保存。</summary>
[Collection("Avalonia UI")]
public sealed class ChannelMergeProjectTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [NativeMediaFact("remux")]
    public async Task MergeSettingAddsPreviewTrackPlanAndProjectEntry()
    {
        var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("remux"), "channel-merge.ts");
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var localization = new LocalizationService();
            localization.SwitchLanguage("zh-CN");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(localization);
            var vm = new MainWindowViewModel(new HanumanInstitute.MvvmDialogs.Avalonia.DialogService(
                DispatchProxy.Create<IDialogManager, MainWindowPlaybackTests.UnusedDialogs>()), new RemoveModeWorkflowTests.MemoryConfiguration())
            {
                VideoPath = source
            };
            Assert.True(await (Task<bool>)typeof(MainWindowViewModel).GetMethod("LoadVideoAsync", Private)!.Invoke(vm, [null])!);

            // 新打开的文件：显示入口，默认不合并，没有试听项。
            Assert.True(vm.HasChannelMergeCandidates);
            Assert.False(vm.IsChannelMergeConfigured);
            Assert.DoesNotContain(vm.PreviewAudioTracks, track => track.ChannelMerge is not null);
            Assert.Null(Plan(vm));

            var settings = new ChannelMergeSettings
            {
                ProgramId = 1, Merge = true,
                Sources = [new ChannelMergeSource { Pid = 0x101 }, new ChannelMergeSource { Pid = 0x102, Swap = true },
                    new ChannelMergeSource { Pid = 0x103 }],
                Outputs = [new AudioOutput { Codec = AudioOutputCodec.Ac3, BitrateKbps = 448 }]
            };
            Restore(vm, [settings]);
            Assert.True(vm.IsChannelMergeConfigured);
            var preview = Assert.Single(vm.PreviewAudioTracks, track => track.ChannelMerge is not null);
            Assert.Equal([(0x101, false), (0x102, true), (0x103, false)], preview.ChannelMerge!.Select(item => (item.Pid, item.Swap)));
            Assert.Equal(0x101, preview.Selection.StreamId);
            Assert.Contains("0x0101 + 0x0102 + 0x0103", preview.DisplayText);
            Assert.Contains("AC3", vm.ChannelMergeButtonTip);
            Assert.True(Plan(vm)!.HasMerges);

            // 工程保存与恢复。
            var project = (CutterProject)typeof(MainWindowViewModel).GetMethod("CaptureProject", Private)!.Invoke(vm, [false])!;
            CutterProjectService.PopulateSources(project);
            var restored = CutterProjectService.Deserialize(CutterProjectService.Serialize(project));
            Assert.Equal(settings.Signature, Assert.Single(restored!.ChannelMerge!).Signature);

            // 改回不合并：试听项去掉，导出计划为空。
            Restore(vm, [new ChannelMergeSettings { ProgramId = 1, Sources = settings.Sources, Outputs = settings.Outputs }]);
            Assert.False(vm.IsChannelMergeConfigured);
            Assert.DoesNotContain(vm.PreviewAudioTracks, track => track.ChannelMerge is not null);
            Assert.Null(Plan(vm));
            return true;
        }, CancellationToken.None);
    }

    private static void Restore(MainWindowViewModel vm, List<ChannelMergeSettings> settings) =>
        typeof(MainWindowViewModel).GetMethod("RestoreChannelMergeSettings", Private)!.Invoke(vm, [settings]);

    private static AudioRebuildPlan? Plan(MainWindowViewModel vm) =>
        (AudioRebuildPlan?)typeof(MainWindowViewModel).GetProperty("CurrentAudioRebuildPlan", Private)!.GetValue(vm);
}
