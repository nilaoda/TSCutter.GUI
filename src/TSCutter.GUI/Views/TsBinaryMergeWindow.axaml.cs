using System;
using System.Collections.Generic;
using System.IO;
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
        eventArgs.DragEffects = GetDroppedPaths(eventArgs).Any(IsSupportedDroppedPath)
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
        var files = await Task.Run(() => ExpandDroppedTsFiles(paths));
        if (files.Length > 0)
            await viewModel.AddFilesAsync(files);
    }

    private static string[] GetDroppedPaths(DragEventArgs eventArgs)
    {
#pragma warning disable CS0618
        if (!eventArgs.Data.Contains(DataFormats.Files))
            return [];
        return eventArgs.Data.GetFiles()?
            .Select(item => item.Path.LocalPath)
            .ToArray() ?? [];
#pragma warning restore CS0618
    }

    private static bool IsSupportedDroppedPath(string path) =>
        Directory.Exists(path) || IsTsFile(path);

    private static string[] ExpandDroppedTsFiles(IReadOnlyList<string> paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (IsTsFile(path))
            {
                files.Add(path);
                continue;
            }
            if (!Directory.Exists(path))
                continue;
            try
            {
                files.AddRange(Directory.EnumerateFiles(path)
                    .Where(HasTsExtension)
                    .OrderBy(item => Path.GetFileName(item) ?? string.Empty, NaturalStringComparer.Instance)
                    .ThenBy(item => item, NaturalStringComparer.Instance));
            }
            catch
            {
                // 单个目录无法枚举时跳过，其余拖入内容仍可继续处理。
            }
        }
        return files.ToArray();
    }

    private static bool IsTsFile(string path) => File.Exists(path) && HasTsExtension(path);

    private static bool HasTsExtension(string path) =>
        string.Equals(Path.GetExtension(path), ".ts", StringComparison.OrdinalIgnoreCase);

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
