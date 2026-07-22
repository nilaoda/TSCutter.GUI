using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Models;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

public partial class TsRepairMapWindow : ClassicWindow
{
    private TsRepairMapWindowViewModel? _subscribedViewModel;

    public TsRepairMapWindow()
    {
        InitializeComponent();
        Closed += OnClosed;
        Opened += (_, _) => UpdateTimelineWidth();
    }

    protected override void OnDataContextChanged(EventArgs eventArgs)
    {
        if (_subscribedViewModel is not null)
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnDataContextChanged(eventArgs);
        _subscribedViewModel = DataContext as TsRepairMapWindowViewModel;
        if (_subscribedViewModel is not null)
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
        RebuildSourceColumns();
    }

    private void Close_OnClick(object? sender, RoutedEventArgs eventArgs) => Close();

    private void TimelineScrollViewer_OnSizeChanged(object? sender, SizeChangedEventArgs eventArgs) =>
        UpdateTimelineWidth();

    private void Zoom_OnValueChanged(object? sender, RangeBaseValueChangedEventArgs eventArgs) =>
        UpdateTimelineWidth(eventArgs.NewValue);

    private void UpdateTimelineWidth(double? zoomPercent = null)
    {
        if (DataContext is not TsRepairMapWindowViewModel viewModel)
            return;
        // 100% 始终铺满当前可视区域；更高倍率以该区域为基准扩展，窗口缩放后不会留下空白边栏。
        var viewportWidth = Math.Max(100, TimelineScrollViewer.Bounds.Width - 2);
        RepairTimeline.Width = viewportWidth * (zoomPercent ?? viewModel.ZoomPercent) / 100.0;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(TsRepairMapWindowViewModel.MatrixSources))
            RebuildSourceColumns();
    }

    private void RebuildSourceColumns()
    {
        if (SourceMatrixGrid is null)
            return;
        while (SourceMatrixGrid.Columns.Count > 3)
            SourceMatrixGrid.Columns.RemoveAt(SourceMatrixGrid.Columns.Count - 1);
        if (DataContext is not TsRepairMapWindowViewModel viewModel)
            return;

        for (var sourceIndex = 0; sourceIndex < viewModel.MatrixSources.Count; sourceIndex++)
        {
            var index = sourceIndex;
            var source = viewModel.MatrixSources[index];
            SourceMatrixGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = CreateSourceHeader(source),
                Width = new DataGridLength(180),
                IsReadOnly = true,
                CanUserSort = false,
                CellTemplate = new FuncDataTemplate<TsRepairMapMatrixRowView>(
                    (row, _) => CreateSourceCell(row, index),
                    supportsRecycling: false)
            });
        }
    }

    private static Control CreateSourceHeader(TsRepairMapSourceView source)
    {
        var panel = new StackPanel
        {
            Spacing = 1,
            Margin = new Thickness(4, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(new TextBlock
        {
            Text = source.Label,
            FontWeight = FontWeight.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        panel.Children.Add(new TextBlock
        {
            Text = source.FileName,
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        panel.Children.Add(new TextBlock
        {
            Text = source.CoverageText,
            FontSize = 10,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        ToolTip.SetTip(panel, $"{source.FileName}{Environment.NewLine}{source.CoverageText}");
        return panel;
    }

    private Control CreateSourceCell(TsRepairMapMatrixRowView row, int sourceIndex)
    {
        if ((uint)sourceIndex >= (uint)row.SourceCells.Count)
            return new TextBlock();
        var cell = row.SourceCells[sourceIndex];
        var textBlock = new TextBlock
        {
            Text = cell.DisplayText,
            Margin = new Thickness(6, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        textBlock.Classes.Add(cell.Status switch
        {
            TsRepairMapSourceCellStatus.Chosen => "matrixChosen",
            TsRepairMapSourceCellStatus.Available => "matrixAvailable",
            _ => "matrixNone"
        });
        ToolTip.SetTip(textBlock, cell.TooltipText);
        textBlock.Tapped += (_, eventArgs) => SelectSourceCell(row, cell, eventArgs);
        return textBlock;
    }

    private void SelectSourceCell(
        TsRepairMapMatrixRowView row,
        TsRepairMapSourceCellView cell,
        TappedEventArgs eventArgs)
    {
        if (DataContext is not TsRepairMapWindowViewModel viewModel)
            return;
        // 先同步行选择，再记录具体来源；否则切换到另一行时会清除刚选中的候选详情。
        SourceMatrixGrid.SelectedItem = row;
        viewModel.SelectedRegion = row.Region;
        viewModel.SelectedSourceCell = cell;
        eventArgs.Handled = true;
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel = null;
        }
        if (DataContext is TsRepairMapWindowViewModel viewModel)
            viewModel.OnClosed();
    }
}
