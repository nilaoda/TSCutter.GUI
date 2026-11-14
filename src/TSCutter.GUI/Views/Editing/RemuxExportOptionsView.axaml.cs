using System;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels.Editing;

namespace TSCutter.GUI.Views.Editing;

public partial class RemuxExportOptionsView : ClassicWindow
{
    private RemuxExportOptionsViewModel? _viewModel;
    public RemuxExportOptionsView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null) _viewModel.RequestClose -= Close;
        base.OnDataContextChanged(e);
        _viewModel = DataContext as RemuxExportOptionsViewModel;
        if (_viewModel is not null) _viewModel.RequestClose += Close;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.RequestClose -= Close;
            // 用标题栏关闭时同样停止静音检测。
            _viewModel.StopSilenceDetection();
        }
        _viewModel = null;
        base.OnClosed(e);
    }
}
