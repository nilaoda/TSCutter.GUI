using System;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels.Media;

namespace TSCutter.GUI.Views.Media;

public partial class DolbyEOutputWindow : ClassicWindow
{
    private DolbyEOutputWindowViewModel? _viewModel;

    public DolbyEOutputWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null) _viewModel.RequestClose -= Close;
        base.OnDataContextChanged(e);
        _viewModel = DataContext as DolbyEOutputWindowViewModel;
        if (_viewModel is not null) _viewModel.RequestClose += Close;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null) _viewModel.RequestClose -= Close;
        _viewModel = null;
        base.OnClosed(e);
    }
}
