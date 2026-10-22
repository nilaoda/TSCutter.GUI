using System;
using Avalonia.Interactivity;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels.Editing;

namespace TSCutter.GUI.Views.Editing;

public partial class TsExportWarningView : ClassicWindow
{
    public TsExportWarningView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is TsExportWarningViewModel vm) FilesList.ItemsSource = vm.FileNames;
    }

    private void Complete(TsProgramTableExportChoice choice)
    {
        if (DataContext is TsExportWarningViewModel vm)
            vm.Select(choice);
        Close();
    }

    private void ReturnClick(object? sender, RoutedEventArgs e) => Complete(TsProgramTableExportChoice.Cancel);
    private void ContinueClick(object? sender, RoutedEventArgs e) => Complete(TsProgramTableExportChoice.Original);
    private void SupplementClick(object? sender, RoutedEventArgs e) => Complete(TsProgramTableExportChoice.Supplement);
}
