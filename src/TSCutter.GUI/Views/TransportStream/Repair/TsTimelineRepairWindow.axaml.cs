using System;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels.TransportStream.Repair;

namespace TSCutter.GUI.Views.TransportStream.Repair;

public partial class TsTimelineRepairWindow : ClassicWindow
{
    public TsTimelineRepairWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object? sender, EventArgs eventArgs)
    {
        if (DataContext is TsTimelineRepairWindowViewModel viewModel &&
            viewModel.AnalyzeCommand.CanExecute(null))
        {
            viewModel.AnalyzeCommand.Execute(null);
        }
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        if (DataContext is TsTimelineRepairWindowViewModel viewModel)
            viewModel.OnClosed();
    }
}
