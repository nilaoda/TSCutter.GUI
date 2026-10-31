using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using Splat;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.Views;
using TSCutter.GUI.Themes;
using Classic.Avalonia.Theme;
using Xunit;

namespace TSCutter.GUI.Tests.Player;

/// <summary>
/// 播放快捷键与全屏布局验证；输入控件保留自身操作，
/// 全屏切换复用播放会话并恢复原编辑布局。
/// </summary>
[Collection("Avalonia UI")]
public sealed class MainWindowPlaybackInputTests
{
    private static MainWindowViewModel CreateViewModel()
    {
        // 调用方需先建立 Splat 作用域并注册 ILocalizationService。
        return new MainWindowViewModel(
            new DialogService(
                DispatchProxy.Create<IDialogManager, UnusedDialogs>()),
            new MemoryConfiguration());
    }

    public class UnusedDialogs : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => null;
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

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(30);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [NativeMediaFact("keyframes")]
    public async Task DoubleClickCompletesItsPointerSequenceBeforeSwitchingLayouts()
    {
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(new LocalizationService());
            var vm = CreateViewModel();
            var window = new MainWindow { DataContext = vm };
            window.Show();
            try
            {
                vm.VideoPath = Path.Combine(NativeTestEnvironment.FixtureDirectory("keyframes"), "fixed.mp4");
                Assert.True(await (Task<bool>)typeof(MainWindowViewModel).GetMethod("LoadVideoAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, new object?[] { null })!);
                var viewer = window.FindControl<TSCutter.GUI.Controls.ImageViewer>("ImageViewer")!;
                foreach (var entering in new[] { true, false })
                {
                    Dispatcher.UIThread.RunJobs();
                    var point = viewer.TranslatePoint(new Point(viewer.Bounds.Width / 2, viewer.Bounds.Height / 2), window)!.Value;
                    window.MouseDown(point, MouseButton.Left);
                    window.MouseUp(point, MouseButton.Left);
                    window.MouseDown(point, MouseButton.Left);
                    // 双击第二次按下时仍在原布局；按下/释放序列不可跨越窗口切换。
                    Assert.Equal(!entering, window.IsVideoFullScreen);
                    window.MouseUp(point, MouseButton.Left);
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(entering, window.IsVideoFullScreen);
                    if (entering)
                    {
                        Assert.Same(window.FindControl<Grid>("FullScreenSurface"), window.FocusManager!.GetFocusedElement());
                        // 不额外点击或激活窗口，全屏后的第一个空格与方向键直接生效。
                        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                        await WaitForAsync(() => vm.IsPlaybackPlaying);
                        Assert.True(vm.IsPlaybackPlaying);
                        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                        await WaitForAsync(() => !vm.IsPlaybackActive && !vm.IsDecoding);
                        Assert.False(vm.IsPlaybackActive);
                        var beforeSeek = vm.CurrentTime;
                        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
                        window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
                        await WaitForAsync(() => !vm.IsDecoding && vm.CurrentTime > beforeSeek);
                        Assert.True(vm.CurrentTime > beforeSeek);
                    }
                    var mute = window.FindControl<Button>(entering ? "FullScreenMuteButton" : "PreviewMuteButton")!;
                    point = mute.TranslatePoint(new Point(mute.Bounds.Width / 2, mute.Bounds.Height / 2), window)!.Value;
                    var muted = vm.PreviewMuted;
                    window.MouseDown(point, MouseButton.Left);
                    window.MouseUp(point, MouseButton.Left);
                    Assert.Equal(!muted, vm.PreviewMuted);
                }
                return true;
            }
            finally
            {
                window.Close();
                vm.Close();
            }
        }, System.Threading.CancellationToken.None);
    }

    [NativeMediaFact("keyframes")]
    public async Task FullScreenKeepsPlaybackAndRestoresTheEditorAcrossThemes()
    {
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await headless.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(new LocalizationService());
            var vm = CreateViewModel();
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                Assert.False(window.ToggleVideoFullScreenCommand.CanExecute(null));
                vm.VideoPath = Path.Combine(NativeTestEnvironment.FixtureDirectory("keyframes"), "fixed.mp4");
                Assert.True(await (Task<bool>)typeof(MainWindowViewModel).GetMethod("LoadVideoAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, new object?[] { null })!);
                var viewer = window.FindControl<TSCutter.GUI.Controls.ImageViewer>("ImageViewer")!;
                var editor = window.FindControl<Grid>("EditingSurface")!;
                var fullScreenHost = window.FindControl<Border>("FullScreenVideoHost")!;
                viewer.AutoFit = false;
                vm.ZoomFactor = 0.75;
                vm.OffsetX = 12;
                vm.OffsetY = 8;
                foreach (var theme in new[] { ClassicTheme.Standard, CustomTheme.LunaBlue, CustomTheme.Aero, CustomTheme.Windows8,
                             CustomTheme.Windows10, CustomTheme.Windows11, CustomTheme.MacOS, CustomTheme.MacOSDark })
                {
                    window.RequestedThemeVariant = theme;
                    Dispatcher.UIThread.RunJobs();
                    var decorations = window.SystemDecorations;
                    window.KeyPressQwerty(PhysicalKey.F11, RawInputModifiers.None);
                    Dispatcher.UIThread.RunJobs();
                    Assert.True(window.IsVideoFullScreen);
                    Assert.Equal(OperatingSystem.IsMacOS() ? SystemDecorations.Full : decorations, window.SystemDecorations);
                    Assert.False(editor.IsVisible);
                    Assert.Same(viewer, fullScreenHost.Child);
                    Assert.Equal(window.ClientSize, window.FindControl<Grid>("FullScreenSurface")!.Bounds.Size);
                    var fitted = TSCutter.GUI.Controls.ImageViewer.CalculateFitTransform(viewer.Bounds.Size,
                        viewer.SourcePixelSize, window.RenderScaling)!.Value;
                    Assert.Equal(fitted.Zoom, viewer.Zoom, 6);
                    Assert.False(vm.IsPlaybackActive);
                    // 切换后直接点击，不先点击空白区或显式激活窗口。
                    var muteButton = window.FindControl<Button>("FullScreenMuteButton")!;
                    var mutePoint = muteButton.TranslatePoint(new Point(18, 18), window)!.Value;
                    var muted = vm.PreviewMuted;
                    window.MouseDown(mutePoint, MouseButton.Left);
                    window.MouseUp(mutePoint, MouseButton.Left);
                    Assert.Equal(!muted, vm.PreviewMuted);
                    Assert.True(window.FindControl<Button>("ExitVideoFullScreenButton")!.IsEffectivelyVisible);
                    Assert.True(window.FindControl<Slider>("FullScreenVolumeSlider")!.Bounds.Width > 0);
                    window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                    Dispatcher.UIThread.RunJobs();
                    Assert.False(window.IsVideoFullScreen);
                    Assert.Equal(WindowState.Normal, window.WindowState);
                    Assert.Equal(decorations, window.SystemDecorations);
                    Assert.True(editor.IsVisible);
                    Assert.Null(fullScreenHost.Child);
                    Assert.Equal(0.75, viewer.Zoom);
                    Assert.Equal(12, viewer.OffsetX);
                    Assert.Equal(8, viewer.OffsetY);
                    var editorMute = window.FindControl<Button>("PreviewMuteButton")!;
                    mutePoint = editorMute.TranslatePoint(new Point(14, 16), window)!.Value;
                    window.MouseDown(mutePoint, MouseButton.Left);
                    window.MouseUp(mutePoint, MouseButton.Left);
                    Assert.Equal(muted, vm.PreviewMuted);
                }

                // 从原生标题主题进入后再切到无装饰主题，也要保持全屏装饰覆盖。
                window.RequestedThemeVariant = CustomTheme.MacOSDark;
                window.ToggleVideoFullScreenCommand.Execute(null);
                window.RequestedThemeVariant = CustomTheme.Windows8;
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(SystemDecorations.Full, window.SystemDecorations);
                window.ToggleVideoFullScreenCommand.Execute(null);
                Assert.Equal(OperatingSystem.IsMacOS() ? SystemDecorations.None : SystemDecorations.Full,
                    window.SystemDecorations);

                await vm.TogglePlaybackCommand.ExecuteAsync(null);
                await WaitForAsync(() => vm.IsPlaybackPlaying);
                var sessionField = typeof(MainWindowViewModel).GetField("playbackSession",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;
                var session = sessionField.GetValue(vm);
                Assert.NotNull(session);
                window.WindowState = WindowState.Maximized;
                window.ToggleVideoFullScreenCommand.Execute(null);
                await Task.Delay(150);
                Assert.True(window.IsVideoFullScreen);
                Assert.True(vm.IsPlaybackPlaying);
                Assert.Same(session, sessionField.GetValue(vm));
                window.Activate();
                window.MouseMove(new Point(100, 100));
                await Task.Delay(3400);
                Dispatcher.UIThread.RunJobs();
                Assert.False(window.FindControl<Border>("FullScreenControls")!.IsHitTestVisible);
                window.MouseMove(new Point(100, 100));
                Dispatcher.UIThread.RunJobs();
                Assert.True(window.FindControl<Border>("FullScreenControls")!.IsHitTestVisible);
                var seek = window.FindControl<Slider>("FullScreenSeekSlider")!;
                Assert.True(seek.Focus());
                Assert.Same(seek, window.FocusManager!.GetFocusedElement());
                window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
                await WaitForAsync(() => !vm.IsTimelineScrubActive && vm.IsPlaybackPlaying);
                Assert.True(vm.IsPlaybackPlaying, "全屏进度跳转后未恢复播放");
                Assert.True(vm.CurrentTime >= 4);
                Assert.Same(session, sessionField.GetValue(vm));
                window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Assert.Equal(WindowState.Maximized, window.WindowState);
                Assert.True(vm.IsPlaybackPlaying);
                Assert.Same(session, sessionField.GetValue(vm));
                window.ToggleVideoFullScreenCommand.Execute(null);
                seek.Focus();
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                await WaitForAsync(() => !vm.IsPlaybackActive);
                Assert.False(vm.IsPlaybackActive);
                var point = seek.TranslatePoint(new Point(seek.Bounds.Width * 0.25, seek.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                await WaitForAsync(() => !vm.IsTimelineScrubActive && !vm.IsDecoding);
                Assert.False(vm.IsPlaybackActive);
                Assert.InRange(vm.CurrentTime, 2, 4);
                var volume = window.FindControl<Slider>("FullScreenVolumeSlider")!;
                volume.Focus();
                vm.PreviewVolume = 0.5;
                window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
                Assert.Equal(0.55, vm.PreviewVolume, 6);
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                await WaitForAsync(() => vm.IsPlaybackPlaying);
                Assert.True(vm.IsPlaybackPlaying);
                Assert.Same(session, sessionField.GetValue(vm));
                // 关闭文件应退出由系统或应用进入的全屏布局。
                window.WindowState = WindowState.FullScreen;
                Assert.True(window.IsVideoFullScreen);
                await vm.CloseVideoClickCommand.ExecuteAsync(null);
                Assert.False(window.IsVideoFullScreen);
                Assert.True(editor.IsVisible);
                return true;
            }
            finally
            {
                window.Close();
                vm.Close();
            }
        }, System.Threading.CancellationToken.None);
    }

    [NativeMediaFact("keyframes")]
    public async Task SpaceTogglesPlaybackButNotWhileTextInputFocused()
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
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                Assert.True(await (Task<bool>)load.Invoke(vm, new object?[] { null })!);
                Assert.True(vm.TogglePlaybackCommand.CanExecute(null));

                // 鼠标点击播放后按钮持有焦点，空格的松开不能再次触发按钮。
                var play = window.FindControl<Button>("PlayPauseButton")!;
                var point = play.TranslatePoint(new Point(play.Bounds.Width / 2, play.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Assert.True(play.Focus());
                await WaitForAsync(() => vm.IsPlaybackPlaying);
                Assert.True(vm.IsPlaybackPlaying);
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                await WaitForAsync(() => !vm.IsPlaybackActive && vm.TogglePlaybackCommand.CanExecute(null));
                window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                await Task.Delay(150);
                Dispatcher.UIThread.RunJobs();
                Assert.False(vm.IsPlaybackActive);

                // 下一次完整按键正常恢复播放。
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                await WaitForAsync(() => vm.IsPlaybackPlaying);
                Assert.True(vm.IsPlaybackPlaying);
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                await WaitForAsync(() => !vm.IsPlaybackActive && vm.TogglePlaybackCommand.CanExecute(null));
                // 长按产生的重复按下不得恢复播放。
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                await Task.Delay(150);
                Dispatcher.UIThread.RunJobs();
                Assert.False(vm.IsPlaybackActive);

                // 文本框持有焦点时，空格属于文本输入，不触发播放。
                // （headless 键盘模拟不产生文本合成，仅验证不抢占。）
                var textBox = new TextBox { Text = "a" };
                var host = new StackPanel { Children = { textBox } };
                window.Content = host;
                textBox.Focus();
                Dispatcher.UIThread.RunJobs();
                window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                await Task.Delay(150);
                Dispatcher.UIThread.RunJobs();
                Assert.False(vm.IsPlaybackActive);
                return true;
            }
            finally
            {
                window.Content = null;
                window.Close();
            }
        }, System.Threading.CancellationToken.None);
    }
}
