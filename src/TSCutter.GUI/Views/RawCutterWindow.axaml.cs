using System;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

public partial class RawCutterWindow : ClassicWindow
{
    public RawCutterWindow()
    {
        InitializeComponent();
        Loaded += OnInitialized;
        Closed += OnClosed;
    }

    private void OnInitialized(object? sender, EventArgs e)
    {
        if (DataContext is RawCutterWindowViewModel vm)
        {
            vm.RequestClose += Close;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (DataContext is not RawCutterWindowViewModel viewModel)
            return;

        viewModel.RequestClose -= Close;
        viewModel.OnClosed();
    }
}
