using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

public partial class KeyFrameOverviewWindow : ClassicWindow
{
    private KeyFrameOverviewWindowViewModel? subscribedViewModel;

    public KeyFrameOverviewWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.RequestClose -= ViewModel_OnRequestClose;
            subscribedViewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
            OverviewCanvas.TileActivated -= subscribedViewModel.SelectTile;
        }

        subscribedViewModel = DataContext as KeyFrameOverviewWindowViewModel;
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.RequestClose += ViewModel_OnRequestClose;
            subscribedViewModel.PropertyChanged += ViewModel_OnPropertyChanged;
            OverviewCanvas.TileActivated += subscribedViewModel.SelectTile;
        }
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        if (DataContext is not KeyFrameOverviewWindowViewModel viewModel)
            return;
        await viewModel.InitializeAsync();
        UpdateVisibleRange();
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (subscribedViewModel is null)
            return;

        var viewModel = subscribedViewModel;
        var owner = Owner
            ?? (Avalonia.Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        viewModel.RequestClose -= ViewModel_OnRequestClose;
        viewModel.PropertyChanged -= ViewModel_OnPropertyChanged;
        OverviewCanvas.TileActivated -= viewModel.SelectTile;

        await viewModel.OnClosedAsync();

        // 等待总览资源释放后再恢复 owner，避免模态关闭流程覆盖焦点。
        Dispatcher.UIThread.Post(() =>
        {
            RestoreOwnerFocus(owner);
            // Classic/Avalonia 11 在关闭模态窗口时可能稍后才完成原生激活，
            // 再尝试一次可避免第一次键盘输入只用于激活 owner。
            DispatcherTimer.RunOnce(
                () => RestoreOwnerFocus(owner),
                TimeSpan.FromMilliseconds(80),
                DispatcherPriority.Input);
        }, DispatcherPriority.Input);
    }

    private static void RestoreOwnerFocus(WindowBase? owner)
    {
        if (owner is MainWindow mainWindow)
            mainWindow.RestoreFocusAfterOverview();
        else
        {
            owner?.Activate();
            owner?.Focus();
        }
    }

    private void ViewModel_OnRequestClose(object? sender, EventArgs e) => Close();

    private void Window_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is KeyFrameOverviewWindowViewModel viewModel)
            viewModel.CancelWork();
    }

    private void OverviewScrollViewer_OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        OverviewCanvas.VerticalOffset = OverviewScrollViewer.Offset.Y;
        OverviewCanvas.ViewportHeight = OverviewScrollViewer.Bounds.Height;
        UpdateVisibleRange();
    }

    private void OverviewCanvas_OnSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateVisibleRange();

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(KeyFrameOverviewWindowViewModel.Tiles)
            or nameof(KeyFrameOverviewWindowViewModel.ThumbnailWidth))
        {
            // 先让绑定和布局完成，再重新计算当前可视范围。
            Dispatcher.UIThread.Post(UpdateVisibleRange);
        }
    }

    private void UpdateVisibleRange()
    {
        if (DataContext is not KeyFrameOverviewWindowViewModel viewModel)
            return;
        OverviewCanvas.ViewportHeight = OverviewScrollViewer.Bounds.Height;
        var range = OverviewCanvas.GetVisibleRange();
        viewModel.RequestVisibleRange(range.Start, range.End);
    }
}
