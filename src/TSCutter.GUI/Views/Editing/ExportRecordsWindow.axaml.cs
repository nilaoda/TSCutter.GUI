using System;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels.Editing;

namespace TSCutter.GUI.Views.Editing;

public partial class ExportRecordsWindow : ClassicWindow
{
    private ExportRecordsWindowViewModel? viewModel;

    public ExportRecordsWindow()
    {
        InitializeComponent();
        Opened += (_, _) => viewModel?.OnOpened();
    }

    protected override void OnClosed(EventArgs e)
    {
        DetachViewModel();
        base.OnClosed(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        DetachViewModel();
        base.OnDataContextChanged(e);
        viewModel = DataContext as ExportRecordsWindowViewModel;
        if (viewModel is not null)
        {
            viewModel.RequestClose += Close;
            if (IsVisible)
                viewModel.OnOpened();
        }
    }

    private void DetachViewModel()
    {
        if (viewModel is null)
            return;
        viewModel.RequestClose -= Close;
        viewModel.OnClosed();
        viewModel = null;
    }
}
