using System;
using Avalonia.Threading;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels.Editing;

namespace TSCutter.GUI.Views.Editing;

public partial class ExportPreparationWindow : ClassicWindow
{
    private ExportPreparationWindowViewModel? viewModel;
    private readonly DispatcherTimer progressTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    public ExportPreparationWindow()
    {
        InitializeComponent();
        progressTimer.Tick += (_, _) => viewModel?.RefreshProgress();
        Closing += (_, e) => viewModel?.OnClosing(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (viewModel is not null) viewModel.RequestClose -= Close;
        base.OnDataContextChanged(e);
        viewModel = DataContext as ExportPreparationWindowViewModel;
        if (viewModel is not null) viewModel.RequestClose += Close;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (viewModel is not null)
        {
            progressTimer.Start();
            _ = viewModel.ObserveCompletionAsync();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        progressTimer.Stop();
        if (viewModel is not null) viewModel.RequestClose -= Close;
        viewModel = null;
        base.OnClosed(e);
    }
}
