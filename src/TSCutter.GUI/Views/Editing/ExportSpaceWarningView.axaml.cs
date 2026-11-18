using Avalonia.Interactivity;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels.Editing;

namespace TSCutter.GUI.Views.Editing;

public partial class ExportSpaceWarningView : ClassicWindow
{
    public ExportSpaceWarningView()
    {
        InitializeComponent();
        // 默认按钮为“取消”：直接按回车不会在空间不足时继续导出。
        Opened += (_, _) => CancelButton.Focus();
    }

    private void CancelClick(object? sender, RoutedEventArgs e) => Close();

    private void ContinueClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ExportSpaceWarningViewModel warning)
            warning.Confirm();
        Close();
    }
}
