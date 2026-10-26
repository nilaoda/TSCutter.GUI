using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Classic.Avalonia.Theme;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using Splat;
using TSCutter.GUI.Controls;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Rendering;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Themes;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.ViewModels.Application;
using TSCutter.GUI.Views;
using TSCutter.GUI.Views.Application;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

[Collection("Avalonia UI")]
public sealed class PreviewOptionsTests
{
    [Fact]
    public async Task StatusOverridesStayTemporaryAndSettingsOnlyChangeDefaults()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            locale.SwitchLanguage("zh-CN");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            var config = new MemoryConfiguration();
            var dialogs = new DialogService(DispatchProxy.Create<IDialogManager, UnusedDialogs>());
            var vm = new MainWindowViewModel(dialogs, config);
            try
            {
                Assert.True(vm.EnableSdrToneMapping);
                vm.EnableSdrToneMapping = false;
                Assert.True(config.CurrentConfig.EnableSdrToneMapping);
                Assert.Equal(0, config.SaveCount);
                Assert.False(vm.ToggleHardwareDecodingCommand.CanExecute(null));
                var settings = new SettingsWindowViewModel(config, locale) { EnableSdrToneMapping = false };
                settings.ApplyCommand.Execute(null);
                Assert.False(config.CurrentConfig.EnableSdrToneMapping);
                Assert.Equal(1, config.SaveCount);
                vm.EnableSdrToneMapping = true;
                Assert.False(config.CurrentConfig.EnableSdrToneMapping);
                typeof(MainWindowViewModel).GetMethod("ResetPreviewDefaults", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(vm, null);
                Assert.False(vm.EnableSdrToneMapping);
                Assert.Equal(1, config.SaveCount);

                var window = new MainWindow { DataContext = vm };
                var settingsWindow = new SettingsWindow { DataContext = settings };
                try
                {
                    window.Show();
                    settingsWindow.Show();
                    var mappingControl = window.FindControl<Button>("ToneMappingToggle")!;
                    Assert.False(Assert.IsAssignableFrom<Control>(mappingControl.Parent).IsVisible);
                    PublishFrame(vm, VideoDynamicRange.Hdr);
                    foreach (var theme in new[] { ClassicTheme.Standard, CustomTheme.Windows10, CustomTheme.Windows10Dark,
                                 CustomTheme.Windows11, CustomTheme.Windows11Dark, CustomTheme.MacOS, CustomTheme.MacOSDark })
                    {
                        Avalonia.Application.Current!.RequestedThemeVariant = theme;
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                        settingsWindow.UpdateLayout();
                        var hardware = window.FindControl<Button>("HardwareDecodingToggle")!;
                        var mapping = window.FindControl<Button>("ToneMappingToggle")!;
                        Assert.False(hardware.IsEffectivelyEnabled);
                        Assert.False(mapping.IsEffectivelyEnabled);
                        Assert.Equal(LocalizationManager.Instance.String_Status_SdrToneMappingOff, mapping.Content);
                        Assert.True(mapping.Bounds.Width > 0);
                        Assert.Equal(hardware.Bounds.Height, mapping.Bounds.Height);
                        foreach (var button in new[] { hardware, mapping })
                        {
                            var command = button.Command;
                            button.Command = null;
                            button.IsEnabled = true;
                            Dispatcher.UIThread.RunJobs();
                            var enabled = RenderButton(button);
                            button.IsEnabled = false;
                            Dispatcher.UIThread.RunJobs();
                            Assert.Equal(enabled, RenderButton(button));
                            Assert.Empty(button.GetVisualDescendants().OfType<Border>());
                            button.Command = command;
                            button.ClearValue(InputElement.IsEnabledProperty);
                        }
                        foreach (var content in new[] { LocalizationManager.Instance.String_Settings_ShowKeyFrameStrip,
                                     LocalizationManager.Instance.String_Settings_EnableSdrToneMapping })
                        {
                            var setting = settingsWindow.GetVisualDescendants().OfType<CheckBox>()
                                .Single(box => box.Content?.ToString() == content);
                            Assert.True(setting.IsEffectivelyVisible);
                            var bottom = setting.TranslatePoint(new Point(0, setting.Bounds.Height), settingsWindow)!.Value.Y;
                            Assert.InRange(bottom, 1, settingsWindow.Bounds.Height - 40);
                        }
                    }
                }
                finally { settingsWindow.Close(); window.Close(); }
            }
            finally { vm.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PreviewVisibilitySettingsShareMenuStateAndRespectApplyAndCancel()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            locale.SwitchLanguage("zh-CN");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            var config = new MemoryConfiguration();
            var vm = new MainWindowViewModel(
                new DialogService(DispatchProxy.Create<IDialogManager, UnusedDialogs>()), config);
            var window = new MainWindow { DataContext = vm };
            try
            {
                window.Show();
                var menu = window.FindControl<Menu>("AppMenu")!;
                var view = menu.Items.OfType<MenuItem>()
                    .Single(item => item.Header?.ToString() == LocalizationManager.Instance.String_Menu_View);
                var navigator = view.Items.OfType<MenuItem>()
                    .Single(item => item.Header?.ToString() == LocalizationManager.Instance.String_Menu_ImageNavigator);
                var strip = view.Items.OfType<MenuItem>()
                    .Single(item => item.Header?.ToString() == LocalizationManager.Instance.String_Menu_KeyFrameStrip);
                var viewer = window.FindControl<ImageViewer>("ImageViewer")!;
                Assert.True(navigator.IsChecked);
                Assert.True(strip.IsChecked);
                Assert.True(viewer.ShowNavigator);
                var settings = new SettingsWindowViewModel(config, locale);
                Assert.True(settings.ShowImageNavigator);
                Assert.True(settings.ShowKeyFrameStrip);
                settings.ShowImageNavigator = false;
                settings.ShowKeyFrameStrip = false;
                settings.CancelCommand.Execute(null);
                Assert.True(vm.ShowImageNavigator);
                Assert.True(vm.ShowKeyFrameStrip);
                Assert.Equal(0, config.SaveCount);

                settings.ApplyCommand.Execute(null);
                Assert.False(navigator.IsChecked);
                Assert.False(strip.IsChecked);
                Assert.False(viewer.ShowNavigator);
                Assert.False(vm.ShowKeyFrameStrip);
                Assert.Equal(1, config.SaveCount);
                // 原生菜单的勾选操作也必须回写绑定，并且每次只保存一次。
                using var projection = new MacOSMenuBar.MenuProjection(menu);
                var nativeView = Assert.IsType<NativeMenuItem>(projection.Menu.Items[menu.Items.IndexOf(view)]);
                foreach (var item in nativeView.Menu!.Items.OfType<NativeMenuItem>()
                             .Where(item => item.ToggleType == NativeMenuItemToggleType.CheckBox))
                    item.Command!.Execute(null);
                Assert.True(vm.ShowImageNavigator);
                Assert.True(vm.ShowKeyFrameStrip);
                Assert.True(viewer.ShowNavigator);
                Assert.Equal(3, config.SaveCount);
                settings = new SettingsWindowViewModel(config, locale);
                Assert.True(settings.ShowImageNavigator);
                Assert.True(settings.ShowKeyFrameStrip);

                // 关闭文件不应解除配置监听，随后打开文件仍然使用同一窗口。
                vm.Close();
                settings.ShowImageNavigator = false;
                settings.ShowKeyFrameStrip = false;
                settings.OkCommand.Execute(null);
                Assert.False(navigator.IsChecked);
                Assert.False(strip.IsChecked);
                Assert.False(viewer.ShowNavigator);
                Assert.Equal(4, config.SaveCount);
            }
            finally { window.Close(); vm.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task HardwareSwitchRequiresCurrentStreamAvailabilityAndRefreshesAfterFallback()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(new LocalizationService());
            var vm = new MainWindowViewModel(
                new DialogService(DispatchProxy.Create<IDialogManager, UnusedDialogs>()), new MemoryConfiguration());
            var video = new VideoInstance("unused.mp4");
            typeof(VideoInstance).GetProperty(nameof(VideoInstance.Inited))!.SetValue(video, true);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(MainWindowViewModel).GetField("_videoInstance", flags)!.SetValue(vm, video);
            var window = new MainWindow { DataContext = vm };
            try
            {
                window.Show();
                var button = window.FindControl<Button>("HardwareDecodingToggle")!;
                void Refresh() => typeof(MainWindowViewModel).GetMethod("UpdateDecodeMode", flags)!.Invoke(vm, null);
                Refresh();
                Assert.False(vm.ToggleHardwareDecodingCommand.CanExecute(null));
                Assert.False(button.IsEffectivelyEnabled);
                // 主动使用软解不会清除设备与当前编码的可用状态。
                typeof(VideoInstance).GetProperty(nameof(VideoInstance.IsHardwareDecodingAvailable))!.SetValue(video, true);
                Refresh();
                Assert.False(vm.IsHardwareDecoding);
                Assert.Equal(AppConfig.IsHardwareDecodingSupported, vm.ToggleHardwareDecodingCommand.CanExecute(null));
                Assert.Equal(AppConfig.IsHardwareDecodingSupported, button.IsEffectivelyEnabled);
                // 回退后即使 S/W 文案未变化，也须重新通知按钮不可点击。
                video.MarkHardwareDecodingUnavailable();
                Refresh();
                Assert.False(vm.ToggleHardwareDecodingCommand.CanExecute(null));
                Assert.False(button.IsEffectivelyEnabled);
            }
            finally { window.Close(); vm.Close(); }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseReturnsThePreviewLeaseAndWaitsForPendingDecodeBeforeDisposing(bool failDecode)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            var vm = new MainWindowViewModel(
                new DialogService(DispatchProxy.Create<IDialogManager, UnusedDialogs>()), new MemoryConfiguration());
            var video = new VideoInstance("unused.mp4");
            // 不加载原生库，只模拟一个已初始化且仍有操作在途的解码器。
            typeof(VideoInstance).GetProperty(nameof(VideoInstance.Inited))!.SetValue(video, true);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(MainWindowViewModel).GetField("_videoInstance", flags)!.SetValue(vm, video);
            using var bitmap = new WriteableBitmap(new PixelSize(4, 4), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
            var lease = new CountingLease(bitmap);
            typeof(MainWindowViewModel).GetMethod("SetDecodedBitmap", flags)!.Invoke(vm, [bitmap, lease]);
            var pending = new TaskCompletionSource();
            var operation = (Task)typeof(MainWindowViewModel).GetMethod("RunDecodeOperationAsync", flags)!
                .Invoke(vm, [(Func<Task>)(() => pending.Task)])!;
            typeof(MainWindowViewModel).GetField("toneMappingRefreshTask", flags)!.SetValue(vm, operation);
            typeof(MainWindowViewModel).GetField("toneMappingRefreshRunning", flags)!.SetValue(vm, true);
            var navigation = vm.SeekToTimeAndDrawFrameAsync(TimeSpan.FromSeconds(2));
            try
            {
                Assert.False(navigation.IsCompleted);
                vm.Close();
                Assert.Null(vm.DecodedBitmap);
                Assert.Equal(1, lease.ReturnCount);
                Assert.True(video.Inited);
            }
            finally
            {
                if (failDecode) pending.TrySetException(new InvalidOperationException("Decode failed"));
                else pending.TrySetResult();
            }
            if (failDecode) await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
            else await operation;
            await navigation;
            await (Task)typeof(MainWindowViewModel).GetField("previewDisposalTask", flags)!.GetValue(vm)!;
            Assert.False(video.Inited);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ToneMappingIsAvailableOnlyForDisplayedHdrFrames()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(new LocalizationService());
            var vm = new MainWindowViewModel(
                new DialogService(DispatchProxy.Create<IDialogManager, UnusedDialogs>()), new MemoryConfiguration());
            var video = new VideoInstance("unused.mp4");
            typeof(VideoInstance).GetProperty(nameof(VideoInstance.Inited))!.SetValue(video, true);
            typeof(MainWindowViewModel).GetField("_videoInstance", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(vm, video);
            var window = new MainWindow { DataContext = vm };
            try
            {
                window.Show();
                var mapping = window.FindControl<Button>("ToneMappingToggle")!;
                var mappingItem = Assert.IsAssignableFrom<Control>(mapping.Parent);
                Assert.False(mappingItem.IsVisible);
                Assert.False(vm.ToggleSdrToneMappingCommand.CanExecute(null));
                foreach (var range in new[] { VideoDynamicRange.Hdr, VideoDynamicRange.Hlg,
                             VideoDynamicRange.DolbyVision, VideoDynamicRange.Standard })
                {
                    PublishFrame(vm, range);
                    Dispatcher.UIThread.RunJobs();
                    var isHdr = range != VideoDynamicRange.Standard;
                    Assert.Equal(isHdr, mappingItem.IsVisible);
                    Assert.Equal(isHdr, vm.ToggleSdrToneMappingCommand.CanExecute(null));
                    Assert.Equal(isHdr, mapping.IsEffectivelyEnabled);
                }

                PublishFrame(vm, VideoDynamicRange.Hdr);
                vm.DecodingOpCount++;
                Assert.True(mappingItem.IsVisible);
                Assert.False(vm.ToggleSdrToneMappingCommand.CanExecute(null));
                // 关闭映射仍保留入口，解码忙碌时也不隐藏。
                vm.EnableSdrToneMapping = false;
                Assert.True(mappingItem.IsVisible);
                vm.Close();
                Assert.False(mappingItem.IsVisible);
                Assert.False(vm.ToggleSdrToneMappingCommand.CanExecute(null));
                vm.DecodingOpCount--;
            }
            finally { window.Close(); vm.Close(); }
        }, CancellationToken.None);
    }

    [TSCutter.GUI.Tests.FFmpeg.NativeRuntimeFact("TSCUTTER_DOVI_TEST_SAMPLE")]
    public async Task RealHdrPreviewKeepsItsFrameAcrossMappingDecoderSwitchesAndClose()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(new LocalizationService());
            var vm = new MainWindowViewModel(
                new DialogService(DispatchProxy.Create<IDialogManager, UnusedDialogs>()), new MemoryConfiguration())
                { VideoPath = Environment.GetEnvironmentVariable("TSCUTTER_DOVI_TEST_SAMPLE")! };
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                var load = typeof(MainWindowViewModel).GetMethod("LoadVideoAsync", flags)!;
                Assert.True(await (Task<bool>)load.Invoke(vm, new object?[] { null })!);
                Assert.True(vm.HasHdrPreview);
                // 跳过片头黑场，比较可见画面在映射切换前后的结果。
                await vm.SeekToTimeAndDrawFrameAsync(TimeSpan.FromSeconds(Math.Min(10, vm.DurationMax / 2)));
                long CurrentPts() => (long)typeof(MainWindowViewModel).GetField("_displayedFramePts", flags)!.GetValue(vm)!;
                var pts = CurrentPts();
                var original = Snapshot(vm.DecodedBitmap!);
                vm.ToggleSdrToneMappingCommand.Execute(null);
                await (Task)typeof(MainWindowViewModel).GetField("toneMappingRefreshTask", flags)!.GetValue(vm)!;
                Assert.Equal(pts, CurrentPts());
                Assert.False(original.SequenceEqual(Snapshot(vm.DecodedBitmap!)));
                vm.ToggleSdrToneMappingCommand.Execute(null);
                await (Task)typeof(MainWindowViewModel).GetField("toneMappingRefreshTask", flags)!.GetValue(vm)!;
                Assert.Equal(pts, CurrentPts());
                Assert.Equal(original, Snapshot(vm.DecodedBitmap!));
                if (vm.ToggleHardwareDecodingCommand.CanExecute(null))
                {
                    await vm.ToggleHardwareDecodingCommand.ExecuteAsync(null);
                    Assert.True(vm.ToggleHardwareDecodingCommand.CanExecute(null));
                    await vm.ToggleHardwareDecodingCommand.ExecuteAsync(null);
                }
                Assert.Equal(pts, CurrentPts());
                Assert.True(vm.IsVideoInitialized);
                // 映射与拖动可同时提交，最终定位必须完成，旧映射结果不能把画面拉回。
                var targetTime = Math.Min(20, vm.DurationMax * 0.75);
                vm.ToggleSdrToneMappingCommand.Execute(null);
                vm.BeginScrubPreview();
                vm.RequestScrubPreview(targetTime, new PixelSize(320, 180));
                await vm.CompleteScrubPreviewAsync();
                await vm.SeekToTimeAndDrawFrameAsync(TimeSpan.FromSeconds(targetTime));
                await (Task)typeof(MainWindowViewModel).GetField("toneMappingRefreshTask", flags)!.GetValue(vm)!;
                Assert.InRange(vm.CurrentTime, targetTime - 3, targetTime + 3);
                vm.ToggleSdrToneMappingCommand.Execute(null);
                vm.Close();
                await (Task)typeof(MainWindowViewModel).GetField("previewDisposalTask", flags)!.GetValue(vm)!;
                Assert.Null(vm.DecodedBitmap);
                Assert.False(vm.IsVideoInitialized);
            }
            finally { vm.Close(); }
            return true;
        }, CancellationToken.None);

        static byte[] Snapshot(Bitmap bitmap)
        {
            using var thumbnail = TSCutter.GUI.Utils.Media.ImageUtil.CreateThumbnail(bitmap, 320, 180);
            using var output = new MemoryStream();
            thumbnail.Save(output);
            return output.ToArray();
        }
    }

    [Fact]
    public async Task MissingVideoKeepsMediaInfoAvailableWithoutEnablingEditing()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(new LocalizationService());
            var vm = new MainWindowViewModel(
                new DialogService(DispatchProxy.Create<IDialogManager, UnusedDialogs>()), new MemoryConfiguration())
                { VideoPath = "audio-only.mp4" };
            try
            {
                var retain = typeof(MainWindowViewModel).GetMethod("TryRetainFileForToolsAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                Assert.True(await (Task<bool>)retain.Invoke(vm, [new NoVideoStreamException()])!);
                Assert.Equal("audio-only.mp4", vm.VideoPath);
                Assert.True(vm.IsToolsOnly);
                Assert.True(vm.ShowMediaInfoClickCommand.CanExecute(null));
                Assert.True(vm.CloseVideoClickCommand.CanExecute(null));
                Assert.False(vm.CanUseCurrentFileTools);
                Assert.False(vm.IsVideoInitialized);
                Assert.False(vm.AddClipCommand.CanExecute(null));
                Assert.False(vm.MarkClipStartCommand.CanExecute(null));
                Assert.False(vm.MarkClipEndCommand.CanExecute(null));
                Assert.False(vm.SaveVideoClickCommand.CanExecute(null));
                Assert.False(vm.ExportAllCommand.CanExecute(null));
                Assert.False(vm.MergeSelectedClipsCommand.CanExecute(null));
                Assert.False(vm.RemuxExportOptionsCommand.CanExecute(null));
                Assert.False(vm.SaveFrameClickCommand.CanExecute(null));
                Assert.Contains(LocalizationManager.Instance.String_NoVideoStream, vm.ToolsOnlyMessage);
                Assert.Contains(LocalizationManager.Instance.String_ToolsOnly_MediaHint, vm.ToolsOnlyMessage);
                vm.Close();
                Assert.False(vm.ShowMediaInfoClickCommand.CanExecute(null));
            }
            finally { vm.Close(); }
            return true;
        }, CancellationToken.None);
    }

    private static void PublishFrame(MainWindowViewModel vm, VideoDynamicRange range) =>
        typeof(MainWindowViewModel).GetMethod("ApplyDecodeResult", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [new DecodeResult { VideoDynamicRange = range }]);

    private sealed class CountingLease(Bitmap bitmap) : IBitmapFrameLease
    {
        public Bitmap Bitmap => bitmap;
        public int ReturnCount { get; private set; }
        public void Dispose() => ReturnCount++;
    }

    private static byte[] RenderButton(Button button)
    {
        using var image = new RenderTargetBitmap(new PixelSize(
            (int)Math.Ceiling(button.Bounds.Width), (int)Math.Ceiling(button.Bounds.Height)));
        image.Render(button);
        using var stream = new MemoryStream();
        image.Save(stream);
        return stream.ToArray();
    }

    private sealed class MemoryConfiguration : IConfigurationService
    {
        public AppConfig CurrentConfig { get; } = new();
        public int SaveCount { get; private set; }
        public void Load() { }
        public void Save() => SaveCount++;
        public void ApplyTheme(string theme) { }
        public void ApplyLanguage(string language) { }
    }

    public class UnusedDialogs : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => null;
    }
}
