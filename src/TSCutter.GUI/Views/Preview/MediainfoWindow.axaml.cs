using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels.Preview;

namespace TSCutter.GUI.Views.Preview;

public partial class MediainfoWindow : ClassicWindow
{
    public MediainfoWindow()
    {
        InitializeComponent();
    }

    private void SegmentList_OnTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is SegmentListItem item)
            JumpTo(item);
    }

    private void SegmentList_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is ListBox { SelectedItem: SegmentListItem item })
            e.Handled = JumpTo(item);
    }

    // 章节或录制中的节目：记下跳转目标并关闭对话框，由主窗口定位到它的开始。
    private bool JumpTo(SegmentListItem item)
    {
        if (item.Segment is null || DataContext is not MediainfoWindowViewModel viewModel)
            return false;
        viewModel.JumpTarget = item.Segment;
        Close();
        return true;
    }
}
