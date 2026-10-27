using System;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels.Discs;

namespace TSCutter.GUI.Views.Discs;

public partial class DiscTitleSelectionView : ClassicWindow
{
    private DiscTitleSelectionViewModel? _viewModel;
    public DiscTitleSelectionView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.RequestClose -= Close;
        base.OnDataContextChanged(e);
        _viewModel = DataContext as DiscTitleSelectionViewModel;
        if (_viewModel is not null)
            _viewModel.RequestClose += Close;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.RequestClose -= Close;
        _viewModel = null;
        base.OnClosed(e);
    }
}
