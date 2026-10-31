using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Classic.Avalonia.Theme;
using CommunityToolkit.Mvvm.Messaging;
using TSCutter.GUI.Controls;
using TSCutter.GUI.Themes;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.ViewModels.Preview;

namespace TSCutter.GUI.Views;

public partial class MainWindow : ClassicWindow
{
    // 隐藏的节目/字幕列不保留宽度，其余选择器按比例共享同一行。
    public static FuncValueConverter<bool, GridLength> OptionalTrackColumnWidthConverter { get; } =
        new(visible => visible ? new GridLength(1, GridUnitType.Star) : new GridLength(0));

    // 提前创建禁用条目，鼠标和未展开时的键盘导航都不能选中不支持的音轨。
    public static FuncValueConverter<IReadOnlyList<PreviewAudioTrackOption>?, ComboBoxItem[]?> AudioTrackItemsConverter { get; } =
        new(items => items?.Select(item =>
            new ComboBoxItem { Content = item, Tag = item, IsEnabled = item.CanDecode }).ToArray());

    private MainWindowViewModel ViewModel => (DataContext as MainWindowViewModel)!;
    private MainWindowViewModel? _boundViewModel;
    private bool _restoreFocusOnActivation;
    private bool _closePromptActive;
    private bool _closeApproved;
    private bool _playbackSpacePressed;
    private readonly DispatcherTimer _dropMaskWatchdog = new()
    {
        Interval = TimeSpan.FromMilliseconds(500)
    };
    private DateTime _lastDragActivityUtc;
    
    public MainWindow()
    {
        ToggleVideoFullScreenCommand = new(ToggleVideoFullScreen,
            () => IsVideoFullScreen || DataContext is MainWindowViewModel { IsVideoInitialized: true });
        InitializeComponent();
        InitializeFullScreenPlayback();

        // Windows 平面主题的窗口内菜单和标题区占用更多高度，启动时为 16:9 预览补足空间。
        if (CustomTheme.IsWindows8(ActualThemeVariant) || CustomTheme.IsWindows10(ActualThemeVariant) ||
            CustomTheme.IsWindows11(ActualThemeVariant))
            Height = 772;

        if (OperatingSystem.IsMacOS())
        {
            var menuBar = new MacOSMenuBar(this, AppMenu, AppMenuFrame);
            Closed += (_, _) => menuBar.Dispose();
        }

        WeakReferenceMessenger.Default.Register<FitMessage>(this, (r, m) =>
        {
            if (ImageViewer.FitCommand.CanExecute(null))
            {
                ImageViewer.FitCommand.Execute(null);
            }
        });

        DataContextChanged += (_, _) => BindViewModel(DataContext as MainWindowViewModel);
        // XAML 可能已在 InitializeComponent 中设置 DataContext，首次创建也需要绑定。
        BindViewModel(DataContext as MainWindowViewModel);
        Closed += (_, _) => BindViewModel(null);
        ImageViewer.SizeChanged += (_, _) => (DataContext as MainWindowViewModel)?.UpdatePlaybackPresentationSize();
        ScalingChanged += (_, _) => (DataContext as MainWindowViewModel)?.UpdatePlaybackPresentationSize();
        Activated += MainWindow_OnActivated;
        Deactivated += (_, _) =>
        {
            _playbackSpacePressed = false;
            HideDropMask();
        };
        AddHandler(PointerPressedEvent, (_, _) => HideDropMask(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        // 播放空格接管完整的按下/松开序列：Button 在 KeyUp 仍可能触发点击，
        // 仅拦截 KeyDown 无法避免重复切换；文本输入控件在处理函数内排除。
        AddHandler(KeyDownEvent, MainWindow_OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, MainWindow_OnPreviewKeyUp, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragEnterEvent, DropSurface_OnDragActive,
            RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragOverEvent, DropSurface_OnDragActive,
            RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DragLeaveEvent, DropSurface_OnDragLeave,
            RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(DragDrop.DropEvent, DropSurface_OnDrop,
            RoutingStrategies.Bubble, handledEventsToo: true);
        _dropMaskWatchdog.Tick += (_, _) =>
        {
            if (DateTime.UtcNow - _lastDragActivityUtc > TimeSpan.FromSeconds(3))
                HideDropMask();
        };
    }

    private Avalonia.PixelSize GetPlaybackTargetSize() => new(
        Math.Max(1, (int)Math.Ceiling(ImageViewer.Bounds.Width * RenderScaling)),
        Math.Max(1, (int)Math.Ceiling(ImageViewer.Bounds.Height * RenderScaling)));

    private void BindViewModel(MainWindowViewModel? vm)
    {
        if (ReferenceEquals(vm, _boundViewModel))
            return;
        if (_boundViewModel is { } previous)
        {
            previous.Clips.CollectionChanged -= OnClipsCollectionChanged;
            previous.PropertyChanged -= FullScreenViewModelChanged;
            if (previous.PlaybackTargetSizeProvider == GetPlaybackTargetSize)
                previous.PlaybackTargetSizeProvider = null;
        }
        _boundViewModel = vm;
        ToggleVideoFullScreenCommand.NotifyCanExecuteChanged();
        if (vm is null)
            return;
        vm.PlaybackTargetSizeProvider = GetPlaybackTargetSize;
        vm.UpdatePlaybackPresentationSize();
        vm.Clips.CollectionChanged += OnClipsCollectionChanged;
        vm.PropertyChanged += FullScreenViewModelChanged;
        if (KeyFrameStripView.Bounds.Width >= 24)
            vm.KeyFrameStrip.SetVisibleCount(KeyFrameStripView.VisibleCapacity);
    }

    private void MainWindow_OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && _playbackSpacePressed)
        {
            e.Handled = true;
            return;
        }
        if (HandleFullScreenKey(e))
            return;
        if (e.Key != Key.Space || e.KeyModifiers != KeyModifiers.None)
            return;
        var vm = DataContext as MainWindowViewModel;
        if (vm is null || !vm.CanTogglePlayback)
            return;
        if (IsTextInputFocused())
            return;
        e.Handled = true;
        TogglePlaybackFromSpace();
    }

    private void TogglePlaybackFromSpace()
    {
        if (_playbackSpacePressed)
            return;
        _playbackSpacePressed = true;
        ViewModel.TogglePlaybackCommand.Execute(null);
    }

    private void MainWindow_OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !_playbackSpacePressed)
            return;
        _playbackSpacePressed = false;
        e.Handled = true;
    }

    // 聚焦链上出现文本输入、下拉、菜单等控件时不抢占空格，保留其正常交互。
    private bool IsTextInputFocused()
    {
        var focused = FocusManager?.GetFocusedElement() as Avalonia.Visual;
        for (Avalonia.Visual? visual = focused; visual is not null && !ReferenceEquals(visual, this);
             visual = visual.GetVisualParent())
        {
            if (visual is TextBox or NumericUpDown or AutoCompleteBox or ComboBox or Menu
                or MenuItem or CalendarDatePicker or Slider)
                return true;
        }
        return false;
    }

    private void DropSurface_OnDragActive(object? sender, DragEventArgs e)
    {
        ViewModel.DragOverCommand.Execute(e);
        if (e.DragEffects == DragDropEffects.None)
        {
            HideDropMask();
            return;
        }

        _lastDragActivityUtc = DateTime.UtcNow;
        DropMask.IsVisible = true;
        _dropMaskWatchdog.Start();
    }

    private void DropSurface_OnDragLeave(object? sender, DragEventArgs e)
    {
        // 拖入遮挡窗口时，离开事件的坐标仍可能位于本窗口范围内。
        HideDropMask();
    }

    private void DropSurface_OnDrop(object? sender, DragEventArgs e)
    {
        HideDropMask();
        ViewModel.DropCommand.Execute(e);
    }

    private void HideDropMask()
    {
        DropMask.IsVisible = false;
        _dropMaskWatchdog.Stop();
    }

    private void OnClipsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ClipsScrollViewer.ScrollToEnd();
            }, DispatcherPriority.Loaded);
        }
    }

    private async void Window_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        HideDropMask();
        if (!_closeApproved && ViewModel.HasUnsavedProjectChanges)
        {
            e.Cancel = true;
            if (_closePromptActive) return;
            _closePromptActive = true;
            try
            {
                if (await ViewModel.ConfirmProjectReplacementAsync())
                {
                    _closeApproved = true;
                    Close();
                }
            }
            finally
            {
                _closePromptActive = false;
            }
            return;
        }
        ViewModel.Close();
    }

    private void ClipCard_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: PickedClip clip })
        {
            ViewModel.SelectClip(clip, e.KeyModifiers);
        }
    }

    private void QueueRemove_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is TextBlock { DataContext: ExportQueueItem item })
        {
            ViewModel.RemoveFromQueueCommand.Execute(item);
        }
    }

    private async void Timeline_OnSeekRequested(object? sender, double time) =>
        await SeekPreviewAsync(time, () => ViewModel.SeekToTimeAndDrawFrameAsync(TimeSpan.FromSeconds(time)));

    private async Task SeekPreviewAsync(double time, Func<Task> seek)
    {
        if (!ViewModel.IsVideoInitialized)
        {
            MainTimeline.CompletePendingSeek();
            return;
        }

        try
        {
            MainTimeline.IsEnabled = false;
            if (!ViewModel.IsTimelineScrubActive)
                ViewModel.BeginTimelineScrub();
            ViewModel.BeginKeyFrameStripScrub();
            ViewModel.RequestKeyFrameStrip(time, immediate: true);
            await ViewModel.CompleteScrubPreviewAsync();
            await seek();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
        }
        finally
        {
            ViewModel.EndKeyFrameStripScrub(ViewModel.CurrentTime);
            ViewModel.EndTimelineScrub();
            MainTimeline.CompletePendingSeek();
            MainTimeline.IsEnabled = true;
        }
    }

    private void Timeline_OnScrubStarted(object? sender, EventArgs e)
    {
        ViewModel.BeginTimelineScrub();
        ViewModel.BeginKeyFrameStripScrub();
        ViewModel.BeginScrubPreview();
    }

    private void Timeline_OnScrubPositionChanged(object? sender, double time) =>
        ViewModel.RequestKeyFrameStrip(time);

    private void PreviewVolumeSlot_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // 模板直接使用 Width 布置轨道，必须先限制实际传入值，避免滑块被放到可见区域之外。
        PreviewVolumeSlider.Width = Math.Clamp(e.NewSize.Width, PreviewVolumeSlider.MinWidth, PreviewVolumeSlider.MaxWidth);
    }

    private void KeyFrameStrip_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && KeyFrameStripView.Bounds.Width >= 24)
            vm.KeyFrameStrip.SetVisibleCount(KeyFrameStripView.VisibleCapacity);
    }

    private async void KeyFrameStrip_OnSeekRequested(object? sender, KeyFrameStripItem frame)
    {
        if (MainTimeline.IsEffectivelyEnabled && !ViewModel.IsDecoding && ViewModel.CanTogglePreviewOptions)
            await SeekPreviewAsync(frame.Timestamp.TotalSeconds, () => ViewModel.SeekToKeyFrameAndDrawFrameAsync(frame.Pts));
    }

    private void Timeline_OnScrubPreviewRequested(object? sender, double time) =>
        ViewModel.RequestScrubPreview(time, ImageViewer.GetDecodeTargetSize());

    private async void Timeline_OnScrubCanceled(object? sender, EventArgs e)
    {
        var vm = ViewModel;
        try
        {
            // 取消拖动也需等预览工作退出，避免恢复播放后旧预览再覆盖画面。
            await vm.CompleteScrubPreviewAsync();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
        }
        finally
        {
            vm.EndKeyFrameStripScrub(vm.CurrentTime);
            vm.EndTimelineScrub();
        }
    }

    private void Timeline_OnPanRequested(object? sender, double start) =>
        ViewModel.TimelineViewport.ViewStart = start;

    private void Timeline_OnZoomRequested(object? sender, TimelineZoomRequestEventArgs e) =>
        ViewModel.TimelineViewport.SetZoomLevel(e.ZoomLevel, e.AnchorTime);

    private void Timeline_OnFitRequested(object? sender, EventArgs e) =>
        ViewModel.TimelineViewport.Fit();

    public void RestoreFocusAfterOverview()
    {
        _restoreFocusOnActivation = true;
        Activate();
        IsEnabled = true;
        MainTimeline.Focus(NavigationMethod.Unspecified, KeyModifiers.None);
        DispatcherTimer.RunOnce(
            () =>
            {
                if (_restoreFocusOnActivation)
                {
                    _restoreFocusOnActivation = false;
                    RestoreFocusCore();
                }
            },
            TimeSpan.FromMilliseconds(120),
            DispatcherPriority.Input);
    }

    private void MainWindow_OnActivated(object? sender, EventArgs e)
    {
        if (!_restoreFocusOnActivation)
            return;
        _restoreFocusOnActivation = false;
        Dispatcher.UIThread.Post(RestoreFocusCore, DispatcherPriority.Input);
    }

    private void RestoreFocusCore()
    {
        Activate();
        IsEnabled = true;
        MainTimeline.Focus(NavigationMethod.Unspecified, KeyModifiers.None);
    }
}
