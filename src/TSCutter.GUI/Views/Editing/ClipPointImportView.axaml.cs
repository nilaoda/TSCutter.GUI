using System;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels.Editing;

namespace TSCutter.GUI.Views.Editing;

public partial class ClipPointImportView : ClassicWindow
{
    private ClipPointImportViewModel? viewModel;

    public ClipPointImportView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (viewModel is not null)
            viewModel.RequestClose -= Close;
        base.OnDataContextChanged(e);
        viewModel = DataContext as ClipPointImportViewModel;
        if (viewModel is not null)
            viewModel.RequestClose += Close;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (viewModel is not null)
            viewModel.RequestClose -= Close;
        viewModel = null;
        base.OnClosed(e);
    }
}
