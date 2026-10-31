using System;
using Avalonia.Controls;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Themes;
using TSCutter.GUI.ViewModels.Player;

namespace TSCutter.GUI.Views.Player;

public partial class PlaybackBenchmarkWindow : ClassicWindow
{
    private PlaybackBenchmarkWindowViewModel? viewModel;
    public PlaybackBenchmarkWindow()
    {
        InitializeComponent();
        Opened += (_, _) => viewModel?.OnOpened();
        Closing += (_, args) =>
        {
            if (viewModel is not { IsBusy: true } vm)
                return;
            args.Cancel = true;
            WindowCommands.CloseWhenIdle(this, vm, () => vm.IsBusy);
            vm.CancelCommand.Execute(null);
        };
    }
    protected override void OnDataContextChanged(EventArgs e)
    {
        viewModel?.OnClosed();
        base.OnDataContextChanged(e);
        viewModel = DataContext as PlaybackBenchmarkWindowViewModel;
        if (IsVisible)
            viewModel?.OnOpened();
    }
    protected override void OnClosed(EventArgs e)
    {
        viewModel?.OnClosed();
        viewModel = null;
        base.OnClosed(e);
    }
}
