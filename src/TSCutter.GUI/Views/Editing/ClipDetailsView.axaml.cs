using System;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels.Editing;

namespace TSCutter.GUI.Views.Editing;

public partial class ClipDetailsView : ClassicWindow
{
    private ClipDetailsViewModel? _viewModel;
    public ClipDetailsView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null) _viewModel.RequestClose -= Close;
        base.OnDataContextChanged(e);
        _viewModel = DataContext as ClipDetailsViewModel;
        if (_viewModel is not null) _viewModel.RequestClose += Close;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null) _viewModel.RequestClose -= Close;
        _viewModel = null;
        base.OnClosed(e);
    }
}
