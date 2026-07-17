using System;
using Avalonia.Threading;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Controls;
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

    private void Timeline_OnEventSelected(object? sender, TsCheckTimelineEventSelectedEventArgs eventArgs)
    {
        if (DataContext is TsCheckWindowViewModel viewModel)
        {
            viewModel.SelectedTimelineEvent = eventArgs.Item;
            viewModel.ViewMode = TsCheckViewMode.Details;
        }
        // 视图切换完成后再滚动，否则详情表格尚未参与布局，无法可靠定位选中行。
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is TsCheckWindowViewModel { SelectedEventRow: { } row })
                DetailsGrid.ScrollIntoView(row, null);
        }, DispatcherPriority.Loaded);
    }
}
