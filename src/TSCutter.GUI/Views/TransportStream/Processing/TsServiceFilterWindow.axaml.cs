using System;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels.TransportStream.Processing;

namespace TSCutter.GUI.Views.TransportStream.Processing;

public partial class TsServiceFilterWindow : ClassicWindow
{
    public TsServiceFilterWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object? sender, EventArgs eventArgs)
    {
        if (DataContext is TsServiceFilterWindowViewModel viewModel && viewModel.ProbeCommand.CanExecute(null))
            viewModel.ProbeCommand.Execute(null);
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        if (DataContext is TsServiceFilterWindowViewModel viewModel)
            viewModel.OnClosed();
    }
}
