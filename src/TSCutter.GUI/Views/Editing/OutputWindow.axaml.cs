using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
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
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    // 帧精确导出的计划条在首次进度到达时出现，窗口按内容增高，避免挤压进度区域。
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OutputWindowViewModel.ShowsFrameAccuratePlan) && viewModel?.ShowsFrameAccuratePlan == true)
            SizeToContent = SizeToContent.Height;
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
        viewModel.PropertyChanged -= OnViewModelPropertyChanged;
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
