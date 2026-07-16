using System;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

public partial class TsCheckWindow : ClassicWindow
{
    public TsCheckWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object? sender, EventArgs eventArgs)
    {
        if (DataContext is TsCheckWindowViewModel viewModel && viewModel.StartCommand.CanExecute(null))
            viewModel.StartCommand.Execute(null);
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        if (DataContext is TsCheckWindowViewModel viewModel)
            viewModel.OnClosed();
    }
}
