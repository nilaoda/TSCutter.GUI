using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Classic.Avalonia.Theme;
using TSCutter.GUI.ViewModels.Editing;

namespace TSCutter.GUI.Views.Editing;

public partial class OutputWindow : ClassicWindow
{
    public OutputWindow()
    {
        InitializeComponent();
        Loaded += OnInitialized;
    }
    
    private OutputWindowViewModel? viewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        DetachViewModel();
        base.OnDataContextChanged(e);
        viewModel = DataContext as OutputWindowViewModel;
        if (viewModel is null) return;
        viewModel.RequestClose += Close;
        viewModel.ConfirmProgramTableWarningAsync = ConfirmProgramTableWarningAsync;
    }

    protected override void OnClosed(EventArgs e)
    {
        DetachViewModel();
        base.OnClosed(e);
    }

    private void DetachViewModel()
    {
        if (viewModel is null) return;
        viewModel.RequestClose -= Close;
        viewModel.ConfirmProgramTableWarningAsync = null;
        viewModel = null;
    }

    private async Task<TsProgramTableExportChoice> ConfirmProgramTableWarningAsync(IReadOnlyList<string> files, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var warning = new TsExportWarningViewModel { FileNames = files };
        var dialog = new TsExportWarningView { DataContext = warning };
        using var registration = token.Register(() => Dispatcher.UIThread.Post(() => dialog.Close()));
        await dialog.ShowDialog(this);
        return warning.Choice;
    }

    private void OnInitialized(object? sender, EventArgs e)
    {
        if (DataContext is OutputWindowViewModel vm)
        {
            if (vm.IsBatchMode)
                Height = 280;
        }
    }
}
