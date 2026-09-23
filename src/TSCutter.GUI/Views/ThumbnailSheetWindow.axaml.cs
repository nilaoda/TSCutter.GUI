using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

public partial class ThumbnailSheetWindow : ClassicWindow
{
    private ThumbnailSheetWindowViewModel? subscribedViewModel;

    public ThumbnailSheetWindow()
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
        }

        subscribedViewModel = DataContext as ThumbnailSheetWindowViewModel;
        if (subscribedViewModel is not null)
        {
            subscribedViewModel.RequestClose += ViewModel_OnRequestClose;
            subscribedViewModel.PropertyChanged += ViewModel_OnPropertyChanged;
        }

        FitPreviewToView();
    }

    private async void OnLoaded(object? sender, EventArgs e)
    {
        if (DataContext is not ThumbnailSheetWindowViewModel viewModel)
            return;
        await viewModel.InitializeAsync();
        FitPreviewToView();
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

        await viewModel.OnClosedAsync();

        // 等资源释放后再恢复 owner 焦点，避免模态关闭流程覆盖焦点。
        Dispatcher.UIThread.Post(() =>
        {
            RestoreOwnerFocus(owner);
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

    private void ViewModel_OnRequestClose() => Close();

    private void ViewModel_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ThumbnailSheetWindowViewModel.PreviewImage))
            FitPreviewToView();
    }

    private void FitPreviewToView()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (PreviewImageViewer.Image is null)
                return;
            if (PreviewImageViewer.FitCommand.CanExecute(null))
                PreviewImageViewer.FitCommand.Execute(null);
        }, DispatcherPriority.Loaded);
    }

    private void Window_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is ThumbnailSheetWindowViewModel viewModel)
            viewModel.CancelWork();
    }
}
