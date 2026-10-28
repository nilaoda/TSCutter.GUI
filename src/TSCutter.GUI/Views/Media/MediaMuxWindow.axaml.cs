using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Classic.Avalonia.Theme;
using Classic.CommonControls.Dialogs;
using TSCutter.GUI.Themes;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels.Media;

namespace TSCutter.GUI.Views.Media;

public partial class MediaMuxWindow : ClassicWindow
{
    private static readonly DataFormat<string> TrackFormat = DataFormat.CreateStringApplicationFormat("TSCutter.MediaMux.Track");
    private readonly string _dragId = Guid.NewGuid().ToString("N");
    private MediaMuxWindowViewModel? _viewModel;
    private MediaMuxTrackItem? _dragged;
    private Point _dragStart;
    private bool _dragging;

    public MediaMuxWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Closing += OnClosing;
        TrackGrid.TemplateApplied += (_, _) => Dispatcher.UIThread.Post(RestoreTrackSelection, DispatcherPriority.Loaded);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.ConfirmOverwriteAsync = null;
        base.OnDataContextChanged(e);
        _viewModel = DataContext as MediaMuxWindowViewModel;
        if (_viewModel is not null)
            _viewModel.ConfirmOverwriteAsync = async path => await MessageBox.ShowDialog(this,
                string.Format(LocalizationManager.Instance.String_MediaMux_Overwrite, Path.GetFileName(path)),
                LocalizationManager.Instance.String_MediaMux_Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == MessageBoxResult.Yes;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_viewModel is not { IsBusy: true } vm)
            return;
        e.Cancel = true;
        WindowCommands.CloseWhenIdle(this, vm, () => vm.IsBusy);
        vm.CancelCommand.Execute(null);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel?.OnClosed();
        _viewModel = null;
        base.OnClosed(e);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = _viewModel?.CanEdit != true ? DragDropEffects.None :
            e.DataTransfer.TryGetValue(TrackFormat) == _dragId && _dragged is not null ? DragDropEffects.Move :
            e.DataTransfer.TryGetFiles()?.Any(file => File.Exists(file.Path.LocalPath)) == true ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (_viewModel?.CanEdit != true)
            return;
        e.Handled = true;
        if (_dragged is not null && e.DataTransfer.TryGetValue(TrackFormat) == _dragId)
        {
            var target = (e.Source as Visual)?.GetVisualAncestors().OfType<DataGridRow>().FirstOrDefault()?.DataContext as MediaMuxTrackItem;
            target ??= _viewModel.Tracks.LastOrDefault();
            if (target is not null)
                _viewModel.MoveTrack(_dragged, target);
            return;
        }
        var paths = e.DataTransfer.TryGetFiles()?.Select(file => file.Path.LocalPath).ToArray() ?? [];
        await _viewModel.AddPathsAsync(paths);
    }

    private void RestoreTrackSelection()
    {
        if (_viewModel?.SelectedTrack is { } track && _viewModel.Tracks.Contains(track))
            TrackGrid.SelectedItem = track;
    }

    private void TrackGrid_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // 模板切换会短暂清空选择，只接收真正选中的轨道；随后恢复当前轨道的高亮。
        if (_viewModel is not null && TrackGrid.SelectedItem is MediaMuxTrackItem track)
            _viewModel.SelectedTrack = track;
    }

    private void LanguageChoice_OnChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: MediaMuxLanguageChoice language } choices && _viewModel?.SelectedTrack is { } track)
        {
            track.Language = language.Code;
            LanguagePickerButton.Flyout?.Hide();
            choices.SelectedItem = null;
        }
    }

    private void TrackHandle_OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { Tag: MediaMuxTrackItem track } handle && _viewModel?.CanEdit == true &&
            e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragged = track;
            _dragStart = e.GetPosition(this);
            _viewModel.SelectedTrack = track;
            TrackGrid.Focus();
            e.Pointer.Capture(handle);
            e.Handled = true;
        }
    }

    private void TrackHandle_OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging || e.InitialPressMouseButton != MouseButton.Left)
            return;
        _dragged = null;
        e.Pointer.Capture(null);
    }

    private void TrackHandle_OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_dragging)
            _dragged = null;
    }

    private async void TrackHandle_OnMoved(object? sender, PointerEventArgs e)
    {
        var delta = e.GetPosition(this) - _dragStart;
        if (_dragging || _dragged is null || _viewModel?.CanEdit != true || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ||
            Math.Abs(delta.X) + Math.Abs(delta.Y) < 5)
            return;
        var transfer = new DataTransfer();
        var item = new DataTransferItem();
        item.Set(TrackFormat, _dragId);
        transfer.Add(item);
        _dragging = true;
        e.Pointer.Capture(null);
        try { await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move); }
        finally
        {
            _dragged = null;
            _dragging = false;
        }
    }
}
