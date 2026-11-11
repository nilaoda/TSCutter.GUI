using System;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels.Editing;

namespace TSCutter.GUI.Views.Editing;

public partial class ExportResultWindow : ClassicWindow
{
    private ExportResultWindowViewModel? viewModel;

    public ExportResultWindow()
    {
        InitializeComponent();
        // 打开时焦点在关闭按钮上，回车或 Esc 都能直接关闭。
        Opened += (_, _) => CloseButton.Focus();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (viewModel is not null)
            viewModel.RequestClose -= Close;
        base.OnDataContextChanged(e);
        viewModel = DataContext as ExportResultWindowViewModel;
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
