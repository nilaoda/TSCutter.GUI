using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.Themes;

namespace TSCutter.GUI.Views;

// 全屏只改变窗口布局，同一个预览控件继续持有画面和字幕，不改变播放会话。
public partial class MainWindow
{
    public static readonly StyledProperty<bool> IsVideoFullScreenProperty =
        AvaloniaProperty.Register<MainWindow, bool>(nameof(IsVideoFullScreen));

    public bool IsVideoFullScreen
    {
        get => GetValue(IsVideoFullScreenProperty);
        private set => SetValue(IsVideoFullScreenProperty, value);
    }

    public RelayCommand ToggleVideoFullScreenCommand { get; }

    private readonly DispatcherTimer fullScreenIdleTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Cursor fullScreenHiddenCursor = new(StandardCursorType.None);
    private WindowState fullScreenRestoreState = WindowState.Normal;
    private IInputElement? fullScreenRestoreFocus;
    private (double Zoom, double X, double Y, bool AutoFit) fullScreenRestoreView;
    private long fullScreenLastInput;
    private bool fullScreenSeeking;
    private IPointer? fullScreenDoubleClickPointer;
    private bool fullScreenDoubleClickState;
    private IDisposable? fullScreenDecorationsOverride;
    private IDisposable? fullScreenTransitionNotification;
    private bool fullScreenNativeTransition;
    private WindowState? fullScreenPendingState;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        // Classic 在 macOS 上使用无装饰窗口。AppKit 无法让它真正铺满屏幕，
        // 必须在 Avalonia 把全屏状态传给原生窗口之前临时补齐装饰。
        if (change.Property == WindowStateProperty && change.NewValue is WindowState.FullScreen
            && OperatingSystem.IsMacOS())
            fullScreenDecorationsOverride ??= SetValue(SystemDecorationsProperty,
                SystemDecorations.Full, BindingPriority.Animation);
        if (change.Property == WindowStateProperty && OperatingSystem.IsMacOS()
            && (change.NewValue is WindowState.FullScreen || change.OldValue is WindowState.FullScreen)
            && TryGetPlatformHandle() is { HandleDescriptor: "NSWindow" })
            fullScreenNativeTransition = true;
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && change.NewValue is not WindowState.FullScreen)
        {
            fullScreenDecorationsOverride?.Dispose();
            fullScreenDecorationsOverride = null;
        }
    }

    private void InitializeFullScreenPlayback()
    {
        Opened += (_, _) => fullScreenTransitionNotification ??= MacOSFullScreenNotifications.Observe(this, entered =>
            Dispatcher.UIThread.Post(() =>
            {
                if (!IsVisible)
                    return;
                fullScreenNativeTransition = false;
                if (!entered && WindowState != WindowState.FullScreen)
                {
                    // AppKit 动画结束会再次计算窗口样式，随后同步主题装饰与无边框缩放能力。
                    // Native 的退出回调可能保留装饰缓存，重发一次缩放属性以同步原生外观。
                    using (SetValue(CanResizeProperty, !CanResize, BindingPriority.Animation)) { }
                    MacOsBackdropClip.EnsureResizableWindow(this);
                }
                if (fullScreenPendingState is { } requested)
                {
                    fullScreenPendingState = null;
                    if (requested != WindowState && (requested != WindowState.FullScreen
                        || DataContext is MainWindowViewModel { IsVideoInitialized: true }))
                        RequestFullScreenWindowState(requested);
                }
            }, DispatcherPriority.Background));
        PropertyChanged += (_, change) =>
        {
            if (change.Property != WindowStateProperty)
                return;
            if (WindowState == WindowState.FullScreen)
            {
                if (change.OldValue is WindowState.Normal or WindowState.Maximized)
                    fullScreenRestoreState = (WindowState)change.OldValue;
                EnterVideoFullScreenLayout();
            }
            else
                ExitVideoFullScreenLayout();
        };
        fullScreenIdleTimer.Tick += (_, _) => UpdateFullScreenControls();
        FullScreenControls.GotFocus += (_, _) => ShowFullScreenControls();
        FullScreenSurface.AddHandler(PointerMovedEvent, (_, _) => ShowFullScreenControls(), RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, FullScreenVideoPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, FullScreenVideoReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        ImageViewer.PointerCaptureLost += (_, e) =>
        {
            if (ReferenceEquals(fullScreenDoubleClickPointer, e.Pointer))
                fullScreenDoubleClickPointer = null;
        };
        FullScreenSeekSlider.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(FullScreenSeekSlider).Properties.IsLeftButtonPressed)
                BeginFullScreenSeek();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        FullScreenSeekSlider.AddHandler(PointerReleasedEvent, async (_, _) => await FinishFullScreenSeekAsync(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        FullScreenSeekSlider.PointerCaptureLost += async (_, _) => await FinishFullScreenSeekAsync();
        // Avalonia 先匹配 KeyBindings，再发送路由事件；局部绑定优先于窗口的关键帧快捷键。
        foreach (var key in new[] { Key.Left, Key.Right, Key.Home, Key.End, Key.PageUp, Key.PageDown })
            FullScreenSeekSlider.KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(key), Command = new RelayCommand(() => AdjustFullScreenSeek(key))
            });
        var togglePlayback = new RelayCommand(TogglePlaybackFromSpace,
            () => DataContext is MainWindowViewModel { CanTogglePlayback: true });
        foreach (var key in new[] { Key.Left, Key.Right })
            FullScreenVolumeSlider.KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(key), Command = new RelayCommand(() =>
                {
                    ShowFullScreenControls();
                    ViewModel.PreviewVolume = Math.Clamp(ViewModel.PreviewVolume + (key == Key.Left ? -0.05 : 0.05), 0, 1);
                })
            });
        FullScreenSeekSlider.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Space), Command = togglePlayback });
        FullScreenVolumeSlider.KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Space), Command = togglePlayback });
        FullScreenSeekSlider.AddHandler(KeyUpEvent, async (_, _) => await FinishFullScreenSeekAsync(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        Deactivated += (_, _) => ShowFullScreenControls();
        Closed += (_, _) =>
        {
            fullScreenIdleTimer.Stop();
            fullScreenTransitionNotification?.Dispose();
            fullScreenTransitionNotification = null;
            fullScreenDecorationsOverride?.Dispose();
            fullScreenDecorationsOverride = null;
            fullScreenHiddenCursor.Dispose();
        };
    }

    private void ToggleVideoFullScreen()
    {
        var requested = fullScreenPendingState ?? WindowState;
        if (requested == WindowState.FullScreen)
            RequestFullScreenWindowState(fullScreenRestoreState);
        else if (DataContext is MainWindowViewModel { IsVideoInitialized: true })
            RequestFullScreenWindowState(WindowState.FullScreen);
    }

    private void RequestFullScreenWindowState(WindowState requested)
    {
        // AppKit 动画尚未结束时，反向请求只记录最后的目标，避免原生窗口忽略切换。
        if (fullScreenNativeTransition)
            fullScreenPendingState = requested;
        else
            WindowState = requested;
    }

    private bool HandleFullScreenKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsVideoFullScreen)
        {
            ToggleVideoFullScreen();
            e.Handled = true;
            return true;
        }
        var toggle = e.Key == Key.F11 && e.KeyModifiers == KeyModifiers.None
            || OperatingSystem.IsMacOS() && e.Key == Key.F
                && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Meta);
        if (!toggle || !ToggleVideoFullScreenCommand.CanExecute(null))
            return false;
        ToggleVideoFullScreenCommand.Execute(null);
        e.Handled = true;
        return true;
    }

    private void FullScreenVideoPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount != 2 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || e.Source is not Visual visual || !visual.GetSelfAndVisualAncestors().Contains(ImageViewer)
            || !ToggleVideoFullScreenCommand.CanExecute(null))
            return;
        fullScreenDoubleClickPointer = e.Pointer;
        fullScreenDoubleClickState = IsVideoFullScreen;
        e.Pointer.Capture(ImageViewer);
        e.Handled = true;
    }

    private void FullScreenVideoReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!ReferenceEquals(fullScreenDoubleClickPointer, e.Pointer) || e.InitialPressMouseButton != MouseButton.Left)
            return;
        fullScreenDoubleClickPointer = null;
        var expectedState = fullScreenDoubleClickState;
        e.Handled = true;
        // 与 Avalonia 11 关键帧总览的双击处理一致：先完成释放并解除捕获，
        // 再在本次输入事件结束后切换，避免旧输入状态吃掉新布局的首击。
        e.Pointer.Capture(null);
        if (!ImageViewer.Bounds.Contains(e.GetPosition(ImageViewer)))
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible && IsVideoFullScreen == expectedState && ToggleVideoFullScreenCommand.CanExecute(null))
                ToggleVideoFullScreen();
        }, DispatcherPriority.Background);
    }

    private void EnterVideoFullScreenLayout()
    {
        if (IsVideoFullScreen || DataContext is not MainWindowViewModel { IsVideoInitialized: true })
            return;
        fullScreenRestoreFocus = FocusManager?.GetFocusedElement();
        fullScreenRestoreView = (ImageViewer.Zoom, ImageViewer.OffsetX, ImageViewer.OffsetY, ImageViewer.AutoFit);
        PreviewImageHost.Children.Remove(ImageViewer);
        FullScreenVideoHost.Child = ImageViewer;
        EditingSurface.IsVisible = false;
        FullScreenSurface.IsVisible = true;
        IsVideoFullScreen = true;
        Classes.Set("video-fullscreen", true);
        ImageViewer.FitCommand.Execute(null);
        FullScreenSurface.Focus();
        ShowFullScreenControls();
        fullScreenIdleTimer.Start();
        ToggleVideoFullScreenCommand.NotifyCanExecuteChanged();
    }

    private void ExitVideoFullScreenLayout()
    {
        if (!IsVideoFullScreen)
            return;
        fullScreenIdleTimer.Stop();
        FullScreenSurface.Cursor = null;
        FullScreenVideoHost.Child = null;
        PreviewImageHost.Children.Insert(1, ImageViewer);
        FullScreenSurface.IsVisible = false;
        EditingSurface.IsVisible = true;
        IsVideoFullScreen = false;
        Classes.Set("video-fullscreen", false);
        // 自动适应由原预览区域重新计算；手动缩放则恢复进入前的变换。
        ImageViewer.AutoFit = fullScreenRestoreView.AutoFit;
        if (fullScreenRestoreView.AutoFit)
            ImageViewer.FitCommand.Execute(null);
        else
        {
            ImageViewer.Zoom = fullScreenRestoreView.Zoom;
            ImageViewer.OffsetX = fullScreenRestoreView.X;
            ImageViewer.OffsetY = fullScreenRestoreView.Y;
        }
        fullScreenRestoreFocus?.Focus();
        fullScreenRestoreFocus = null;
        _ = FinishFullScreenSeekAsync();
        ToggleVideoFullScreenCommand.NotifyCanExecuteChanged();
    }

    private void FullScreenViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsVideoInitialized))
        {
            ToggleVideoFullScreenCommand.NotifyCanExecuteChanged();
            if (DataContext is not MainWindowViewModel { IsVideoInitialized: true } && IsVideoFullScreen)
                RequestFullScreenWindowState(fullScreenRestoreState);
            else if (WindowState == WindowState.FullScreen)
                EnterVideoFullScreenLayout();
        }
        if (e.PropertyName == nameof(MainWindowViewModel.IsPlaybackPlaying))
            ShowFullScreenControls();
    }

    private void ShowFullScreenControls()
    {
        fullScreenLastInput = Stopwatch.GetTimestamp();
        FullScreenControls.Opacity = 1;
        FullScreenControls.IsHitTestVisible = true;
        FullScreenSurface.Cursor = null;
    }

    private void UpdateFullScreenControls()
    {
        if (!IsVideoFullScreen || !IsActive || fullScreenSeeking || FullScreenControls.IsPointerOver
            || DataContext is not MainWindowViewModel { IsPlaybackPlaying: true }
            || Stopwatch.GetElapsedTime(fullScreenLastInput) < TimeSpan.FromSeconds(3))
            return;
        FullScreenControls.Opacity = 0;
        FullScreenControls.IsHitTestVisible = false;
        FullScreenSurface.Cursor = fullScreenHiddenCursor;
    }

    private void AdjustFullScreenSeek(Key key)
    {
        if (!BeginFullScreenSeek())
            return;
        var value = key switch
        {
            Key.Home => 0,
            Key.End => FullScreenSeekSlider.Maximum,
            Key.Left => FullScreenSeekSlider.Value - 5,
            Key.Right => FullScreenSeekSlider.Value + 5,
            Key.PageDown => FullScreenSeekSlider.Value - 30,
            _ => FullScreenSeekSlider.Value + 30
        };
        FullScreenSeekSlider.SetCurrentValue(Slider.ValueProperty,
            Math.Clamp(value, 0, FullScreenSeekSlider.Maximum));
    }

    private bool BeginFullScreenSeek()
    {
        if (fullScreenSeeking)
            return true;
        if (DataContext is not MainWindowViewModel { IsVideoInitialized: true, IsDecoding: false } vm
            || !MainTimeline.IsEnabled)
            return false;
        ShowFullScreenControls();
        fullScreenSeeking = true;
        vm.BeginTimelineScrub();
        return true;
    }

    private async Task FinishFullScreenSeekAsync()
    {
        if (!fullScreenSeeking)
            return;
        fullScreenSeeking = false;
        var time = FullScreenSeekSlider.Value;
        await SeekPreviewAsync(time, () => ViewModel.SeekToTimeAndDrawFrameAsync(TimeSpan.FromSeconds(time)));
        ShowFullScreenControls();
    }
}
