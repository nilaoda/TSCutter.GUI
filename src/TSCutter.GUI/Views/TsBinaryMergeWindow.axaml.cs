using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

public partial class TsBinaryMergeWindow : ClassicWindow
{
    private TsBinaryMergeWindowViewModel? _subscribedViewModel;

    public TsBinaryMergeWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, Files_OnDragOver);
        AddHandler(DragDrop.DropEvent, Files_OnDrop);
        DataContextChanged += OnDataContextChanged;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object? sender, EventArgs eventArgs)
    {
        if (_subscribedViewModel is not null)
            _subscribedViewModel.SelectionRestoreRequested -= OnSelectionRestoreRequested;
        _subscribedViewModel = DataContext as TsBinaryMergeWindowViewModel;
        if (_subscribedViewModel is not null)
            _subscribedViewModel.SelectionRestoreRequested += OnSelectionRestoreRequested;
    }

    private void OnSelectionRestoreRequested(IReadOnlyList<TsBinaryMergeFileItem> items)
    {
        Dispatcher.UIThread.Post(() =>
        {
            FileGrid.SelectedItems.Clear();
            foreach (var item in items)
                FileGrid.SelectedItems.Add(item);
            if (items.Count > 0)
                FileGrid.ScrollIntoView(items[0], null);
        }, DispatcherPriority.Loaded);
    }

    private void FileGrid_OnSelectionChanged(object? sender, SelectionChangedEventArgs eventArgs)
    {
        if (DataContext is TsBinaryMergeWindowViewModel viewModel)
            viewModel.SetSelectedFiles(FileGrid.SelectedItems.Cast<TsBinaryMergeFileItem>());
    }

    private void Files_OnDragOver(object? sender, DragEventArgs eventArgs)
    {
        eventArgs.DragEffects = GetDroppedPaths(eventArgs).Any(TsFileDropHelper.IsSupportedPath)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        eventArgs.Handled = true;
    }

    private async void Files_OnDrop(object? sender, DragEventArgs eventArgs)
    {
        var paths = GetDroppedPaths(eventArgs);
        eventArgs.Handled = true;
        if (paths.Length == 0 || DataContext is not TsBinaryMergeWindowViewModel viewModel)
            return;

        // 文件夹只枚举第一层，且放到后台执行，避免大量分片或网络目录阻塞界面。
        var files = await Task.Run(() => TsFileDropHelper.ExpandPaths(paths));
        if (files.Length > 0)
            await viewModel.AddFilesAsync(files);
    }

    private static string[] GetDroppedPaths(DragEventArgs eventArgs)
    {
        if (!eventArgs.Data.Contains(DataFormats.Files))
            return [];
        return eventArgs.Data.GetFiles()?
            .Select(item => item.Path.LocalPath)
            .ToArray() ?? [];
    }

    private void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        if (DataContext is not TsBinaryMergeWindowViewModel { IsBusy: true } viewModel)
            return;
        viewModel.CancelCommand.Execute(null);
        eventArgs.Cancel = true;
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        if (_subscribedViewModel is not null)
        {
            _subscribedViewModel.SelectionRestoreRequested -= OnSelectionRestoreRequested;
            _subscribedViewModel = null;
        }
        if (DataContext is TsBinaryMergeWindowViewModel viewModel)
            viewModel.OnClosed();
    }
}
