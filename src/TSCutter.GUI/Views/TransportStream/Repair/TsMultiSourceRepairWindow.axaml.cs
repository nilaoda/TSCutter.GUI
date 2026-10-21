using System;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels.TransportStream.Repair;

namespace TSCutter.GUI.Views.TransportStream.Repair;

public partial class TsMultiSourceRepairWindow : ClassicWindow
{
    public TsMultiSourceRepairWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        if (DataContext is TsMultiSourceRepairWindowViewModel viewModel)
            viewModel.OnClosed();
    }
}
