using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

public partial class TsRepairMapWindow : ClassicWindow
{
    public TsRepairMapWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
        Opened += (_, _) => UpdateTimelineWidth();
    }

    private void Close_OnClick(object? sender, RoutedEventArgs eventArgs) => Close();

    private void TimelineScrollViewer_OnSizeChanged(object? sender, SizeChangedEventArgs eventArgs) =>
        UpdateTimelineWidth();

    private void Zoom_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs) =>
        UpdateTimelineWidth();

    private void UpdateTimelineWidth()
    {
        if (DataContext is not TsRepairMapWindowViewModel viewModel)
            return;
        // 100% 始终铺满当前可视区域；更高倍率以该区域为基准扩展，窗口缩放后不会留下空白边栏。
        var viewportWidth = Math.Max(100, TimelineScrollViewer.Bounds.Width - 2);
        RepairTimeline.Width = viewportWidth * viewModel.SelectedZoom.Percent / 100.0;
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        if (DataContext is TsRepairMapWindowViewModel viewModel)
            viewModel.OnClosed();
    }
}
