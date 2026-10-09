using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Classic.Avalonia.Theme;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.Views;

public partial class TsBatchCheckWindow : ClassicWindow
{
    public TsBatchCheckWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, Files_OnDragOver);
        AddHandler(DragDrop.DropEvent, Files_OnDrop);
        Closed += (_, _) => (DataContext as TsBatchCheckWindowViewModel)?.OnClosed();
    }

    private static string[] GetDroppedPaths(DragEventArgs args) =>
        args.DataTransfer.TryGetFiles()?.Select(item => item.Path.LocalPath).ToArray() ?? [];

    private void Files_OnDragOver(object? sender, DragEventArgs args)
    {
        args.DragEffects = GetDroppedPaths(args).Any(TsFileDropHelper.IsSupportedPath)
            ? DragDropEffects.Copy : DragDropEffects.None;
        args.Handled = true;
    }

    private async void Files_OnDrop(object? sender, DragEventArgs args)
    {
        var paths = GetDroppedPaths(args);
        args.Handled = true;
        if (paths.Length == 0 || DataContext is not TsBatchCheckWindowViewModel viewModel) return;
        await viewModel.AddFilesAsync(paths, expandFolders: true);
    }

    private void FileGrid_OnSelectionChanged(object? sender, SelectionChangedEventArgs args) =>
        (DataContext as TsBatchCheckWindowViewModel)?.SetSelectedFiles(FileGrid.SelectedItems.Cast<TsBatchCheckFileItem>());

    private void FileGrid_OnDoubleTapped(object? sender, TappedEventArgs args)
    {
        if (DataContext is TsBatchCheckWindowViewModel viewModel && viewModel.ViewResultCommand.CanExecute(null))
            viewModel.ViewResultCommand.Execute(null);
    }

    private void Close_OnClick(object? sender, RoutedEventArgs args) => Close();
}
