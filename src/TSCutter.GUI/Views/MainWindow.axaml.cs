using System;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Classic.Avalonia.Theme;
using CommunityToolkit.Mvvm.Messaging;
using TSCutter.GUI.Controls;
using TSCutter.GUI.Models;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

public partial class MainWindow : ClassicWindow
{
    private MainWindowViewModel ViewModel => (DataContext as MainWindowViewModel)!;
    private bool _restoreFocusOnActivation;
    private bool _closePromptActive;
    private bool _closeApproved;
    private readonly DispatcherTimer _dropMaskWatchdog = new()
    {
        Interval = TimeSpan.FromMilliseconds(500)
    };
    private DateTime _lastDragActivityUtc;
    
    public MainWindow()
    {
        InitializeComponent();

        WeakReferenceMessenger.Default.Register<FitMessage>(this, (r, m) =>
        {
            if (ImageViewer.FitCommand.CanExecute(null))
            {
                ImageViewer.FitCommand.Execute(null);
            }
        });

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.Clips.CollectionChanged += OnClipsCollectionChanged;
            }
        };
        Activated += MainWindow_OnActivated;
        Deactivated += (_, _) => HideDropMask();
        DropMask.PointerPressed += (_, _) => HideDropMask();
        DropSurface.AddHandler(DragDrop.DragEnterEvent, DropSurface_OnDragActive,
            RoutingStrategies.Bubble, handledEventsToo: true);
        DropSurface.AddHandler(DragDrop.DragOverEvent, DropSurface_OnDragActive,
            RoutingStrategies.Bubble, handledEventsToo: true);
        DropSurface.AddHandler(DragDrop.DragLeaveEvent, DropSurface_OnDragLeave,
            RoutingStrategies.Bubble, handledEventsToo: true);
        DropSurface.AddHandler(DragDrop.DropEvent, DropSurface_OnDrop,
            RoutingStrategies.Bubble, handledEventsToo: true);
        _dropMaskWatchdog.Tick += (_, _) =>
        {
            if (DateTime.UtcNow - _lastDragActivityUtc > TimeSpan.FromSeconds(3))
                HideDropMask();
        };
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
        if (!DropSurface.Bounds.Contains(e.GetPosition(DropSurface)))
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

    private async void Timeline_OnSeekRequested(object? sender, double time)
    {
        if (!ViewModel.IsVideoInitialized)
        {
            MainTimeline.CompletePendingSeek();
            return;
        }

        try
        {
            MainTimeline.IsEnabled = false;
            await ViewModel.CompleteScrubPreviewAsync();
            await ViewModel.SeekToTimeAndDrawFrameAsync(TimeSpan.FromSeconds(time));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
        }
        finally
        {
            MainTimeline.CompletePendingSeek();
            MainTimeline.IsEnabled = true;
        }
    }

    private void Timeline_OnScrubStarted(object? sender, EventArgs e) =>
        ViewModel.BeginScrubPreview();

    private void Timeline_OnScrubPreviewRequested(object? sender, double time) =>
        ViewModel.RequestScrubPreview(time, ImageViewer.GetDecodeTargetSize());

    private void Timeline_OnScrubCanceled(object? sender, EventArgs e) =>
        ViewModel.CancelScrubPreview();

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
