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
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Rendering;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Themes;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels.Application;
using TSCutter.GUI.ViewModels.Editing;
using TSCutter.GUI.Views.Application;
using TSCutter.GUI.Views.Editing;
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
                    foreach (var theme in new[] { ClassicTheme.Standard, CustomTheme.Windows11, CustomTheme.Windows11Dark })
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
                        var setting = settingsWindow.GetVisualDescendants().OfType<CheckBox>()
                            .Single(box => box.Content?.ToString() == LocalizationManager.Instance.String_Settings_EnableSdrToneMapping);
                        var bottom = setting.TranslatePoint(new Point(0, setting.Bounds.Height), settingsWindow)!.Value.Y;
                        Assert.InRange(bottom, 1, settingsWindow.Bounds.Height - 40);
                    }
                }
                finally { settingsWindow.Close(); window.Close(); }
            }
            finally { vm.Close(); }
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
            try
            {
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
                    await vm.ToggleHardwareDecodingCommand.ExecuteAsync(null);
                Assert.Equal(pts, CurrentPts());
                Assert.True(vm.IsVideoInitialized);
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
