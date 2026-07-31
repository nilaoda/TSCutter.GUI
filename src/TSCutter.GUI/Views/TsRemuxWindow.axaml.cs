using System;
using Avalonia.Controls;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

public partial class TsRemuxWindow : ClassicWindow
{
    public TsRemuxWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (DataContext is not TsRemuxWindowViewModel { IsBusy: true } viewModel)
            return;
        viewModel.CancelCommand.Execute(null);
        eventArgs.Cancel = true;
    }

    private void OnLoaded(object? sender, EventArgs eventArgs)
    {
        if (DataContext is TsRemuxWindowViewModel viewModel && viewModel.ProbeCommand.CanExecute(null))
            viewModel.ProbeCommand.Execute(null);
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        if (DataContext is TsRemuxWindowViewModel viewModel)
            viewModel.OnClosed();
    }
}
