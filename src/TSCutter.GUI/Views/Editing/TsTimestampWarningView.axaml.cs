using Avalonia.Interactivity;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels.Editing;

namespace TSCutter.GUI.Views.Editing;

public partial class TsTimestampWarningView : ClassicWindow
{
    public TsTimestampWarningView() => InitializeComponent();

    private void CancelClick(object? sender, RoutedEventArgs e) => Close();

    private void ContinueClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is TsTimestampWarningViewModel warning)
            warning.Confirm();
        Close();
    }
}
