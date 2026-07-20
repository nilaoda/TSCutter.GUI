using System;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

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
