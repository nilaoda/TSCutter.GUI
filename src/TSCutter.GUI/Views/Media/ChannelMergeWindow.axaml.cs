using System;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels.Media;

namespace TSCutter.GUI.Views.Media;

public partial class ChannelMergeWindow : ClassicWindow
{
    private ChannelMergeWindowViewModel? _viewModel;

    public ChannelMergeWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null) _viewModel.RequestClose -= Close;
        base.OnDataContextChanged(e);
        _viewModel = DataContext as ChannelMergeWindowViewModel;
        if (_viewModel is not null) _viewModel.RequestClose += Close;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.RequestClose -= Close;
            _viewModel.CancelAnalysis();
        }
        _viewModel = null;
        base.OnClosed(e);
    }
}
