using System;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
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

    private void Window_OnClosing(object? sender, WindowClosingEventArgs e)
    {
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
}
