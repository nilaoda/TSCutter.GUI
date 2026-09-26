using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Models;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

public partial class FrameSearchWindow : ClassicWindow
{
    private FrameSearchWindowViewModel? viewModel;

    public FrameSearchWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (viewModel is not null) viewModel.RequestClose -= OnRequestClose;
            viewModel = DataContext as FrameSearchWindowViewModel;
            if (viewModel is not null) viewModel.RequestClose += OnRequestClose;
        };
        Loaded += (_, _) => viewModel?.Start();
        Closed += async (_, _) =>
        {
            if (viewModel is null) return;
            viewModel.RequestClose -= OnRequestClose;
            await viewModel.OnClosedAsync();
        };
    }

    private void FrameButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FrameSearchPreviewFrame frame })
            viewModel?.SelectFrame(frame);
    }

    private void Window_OnClosing(object? sender, WindowClosingEventArgs e) => viewModel?.CancelWork();
    private void OnRequestClose() => Close();
}
