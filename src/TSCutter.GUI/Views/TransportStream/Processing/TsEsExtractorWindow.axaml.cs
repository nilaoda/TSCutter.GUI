using System;
using Avalonia.Controls;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Themes;
using TSCutter.GUI.ViewModels.TransportStream.Processing;

namespace TSCutter.GUI.Views.TransportStream.Processing;

public partial class TsEsExtractorWindow : ClassicWindow
{
    public TsEsExtractorWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnLoaded(object? sender, EventArgs eventArgs)
    {
        if (DataContext is TsEsExtractorWindowViewModel viewModel && viewModel.ProbeCommand.CanExecute(null))
            viewModel.ProbeCommand.Execute(null);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (DataContext is not TsEsExtractorWindowViewModel viewModel)
            return;
        if (viewModel.IsBusy)
            WindowCommands.CloseWhenIdle(this, viewModel, () => viewModel.IsBusy);
        viewModel.OnClosing(eventArgs);
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        if (DataContext is TsEsExtractorWindowViewModel viewModel)
            viewModel.OnClosed();
    }
}
