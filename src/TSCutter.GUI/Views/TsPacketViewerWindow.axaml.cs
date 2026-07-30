using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Models;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

public partial class TsPacketViewerWindow : ClassicWindow
{
    private TsPacketViewerWindowViewModel? _subscribedViewModel;

    public TsPacketViewerWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs eventArgs)
    {
        if (_subscribedViewModel is not null)
            _subscribedViewModel.SelectionRequested -= OnSelectionRequested;
        _subscribedViewModel = DataContext as TsPacketViewerWindowViewModel;
        if (_subscribedViewModel is not null)
            _subscribedViewModel.SelectionRequested += OnSelectionRequested;
    }

    private async void OnLoaded(object? sender, EventArgs eventArgs)
    {
        if (DataContext is TsPacketViewerWindowViewModel viewModel)
            await viewModel.InitializeAsync();
    }

    private async void OnClosed(object? sender, EventArgs eventArgs)
    {
        if (_subscribedViewModel is not null)
            _subscribedViewModel.SelectionRequested -= OnSelectionRequested;
        if (DataContext is TsPacketViewerWindowViewModel viewModel)
            await viewModel.OnClosedAsync();
    }

    private void PacketGrid_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (PacketGrid.SelectedItem is TsPacketViewerRow row &&
            DataContext is TsPacketViewerWindowViewModel viewModel &&
            !ReferenceEquals(viewModel.SelectedPacket, row))
            viewModel.SelectedPacket = row;
    }

    private void PacketGrid_OnLoadingRow(object? sender, DataGridRowEventArgs eventArgs)
    {
        var item = eventArgs.Row.DataContext as TsPacketViewerRow;
        SetClass(eventArgs.Row, "errorRow", item?.HasError == true);
        SetClass(eventArgs.Row, "warningRow", item?.HasWarning == true);
    }

    private static void SetClass(StyledElement element, string className, bool enabled)
    {
        if (enabled)
        {
            if (!element.Classes.Contains(className))
                element.Classes.Add(className);
        }
        else
        {
            element.Classes.Remove(className);
        }
    }

    private void OnSelectionRequested(TsPacketViewerRow row)
    {
        Dispatcher.UIThread.Post(() =>
        {
            PacketGrid.SelectedItem = row;
            PacketGrid.ScrollIntoView(row, null);
        }, DispatcherPriority.Loaded);
    }
}
