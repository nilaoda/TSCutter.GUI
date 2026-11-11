using System.Reflection;
using Avalonia.Headless;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using Splat;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.Views;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

/// <summary>
/// 滚轮按秒定位：智能剪辑模式落在目标时间的那一帧；关键帧剪辑模式与点击时间轴相同落在附近的关键帧，
/// 关键帧间隔比步长长时仍至少移动一个关键帧。
/// </summary>
[Collection("Avalonia UI")]
public sealed class WheelNavigationTests
{
    [NativeMediaFact("keyframes")]
    public Task SecondsMoveToTheExactFrameInSmartCutMode() => WithVideo("fixed.mp4", ClipCutMode.FrameAccurate,
        new InputPreferences { WheelStepUnit = WheelStepUnit.Second }, async vm =>
        {
            Assert.True(vm.UsesFrameAccurateCut);
            await vm.ScrollKeyFrameStripAsync(2);
            Assert.Equal(2.0, vm.CurrentTime, 3);
            await vm.ScrollKeyFrameStripAsync(-1);
            Assert.Equal(1.0, vm.CurrentTime, 3);
            // 片头之前停在首帧。
            await vm.ScrollKeyFrameStripAsync(-5);
            Assert.Equal(0.0, vm.CurrentTime, 3);
        });

    [NativeMediaFact("keyframes")]
    public Task SecondsMoveAtLeastOneKeyFrameWhenTheGopIsLonger() => WithVideo("variable.mp4", ClipCutMode.KeyFrame,
        new InputPreferences { WheelStepUnit = WheelStepUnit.Second }, async vm =>
        {
            // 关键帧位于 0、1、2、3、8… 秒：从 3 秒按 1 秒前进，附近没有关键帧，仍须移到下一个关键帧。
            await vm.ScrollKeyFrameStripAsync(3);
            Assert.Equal(3.0, vm.CurrentTime, 3);
            await vm.ScrollKeyFrameStripAsync(1);
            Assert.True(vm.CurrentTime > 3.0, $"stayed at {vm.CurrentTime}");
            var forward = vm.CurrentTime;
            await vm.ScrollKeyFrameStripAsync(-1);
            Assert.True(vm.CurrentTime < forward, $"stayed at {vm.CurrentTime}");
        });

    private static async Task WithVideo(string name, ClipCutMode mode, InputPreferences input,
        Func<MainWindowViewModel, Task> test)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            locale.SwitchLanguage("en-US");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            var config = new MemoryConfiguration();
            config.CurrentConfig.ClipCutMode = mode;
            config.CurrentConfig.Input = input;
            var vm = new MainWindowViewModel(new DialogService(DispatchProxy.Create<IDialogManager, NoDialogs>()), config)
            {
                VideoPath = Path.Combine(NativeTestEnvironment.FixtureDirectory("keyframes"), name)
            };
            var window = new MainWindow { DataContext = vm };
            try
            {
                var load = typeof(MainWindowViewModel).GetMethod("LoadVideoAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.True(await (Task<bool>)load.Invoke(vm, new object?[] { null })!);
                window.Show();
                await vm.KeyFrameStrip.IdleTask;
                Assert.Equal(0.0, vm.CurrentTime, 3);
                await test(vm);
                await vm.KeyFrameStrip.IdleTask;
            }
            finally
            {
                window.Close();
            }
            return true;
        }, CancellationToken.None);
    }

    private sealed class MemoryConfiguration : IConfigurationService
    {
        public AppConfig CurrentConfig { get; } = new() { PreferHardwareDecoding = false };
        public void Load() { }
        public void Save() { }
        public void ApplyTheme(string theme) { }
        public void ApplyLanguage(string language) { }
    }

    public class NoDialogs : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask : null;
    }
}
