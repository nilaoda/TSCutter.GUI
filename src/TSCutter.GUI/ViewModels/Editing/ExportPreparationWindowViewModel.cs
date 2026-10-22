using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.ViewModels.Editing;

public readonly record struct ExportPreparationProgress(int Completed, int Total, string FileName);

public partial class ExportPreparationWindowViewModel : ViewModelBase, IModalDialogViewModel, IProgress<ExportPreparationProgress>
{
    internal static readonly TimeSpan DisplayDelay = TimeSpan.FromMilliseconds(500);
    public string Caption { get; set; } = LocalizationManager.Instance.String_TsExport_Checking;
    public bool CanCancel { get; set; } = true;
    public bool? DialogResult { get; private set; }
    internal Task Operation { get; set; } = Task.CompletedTask;
    internal Action? CancelOperation { get; set; }
    public event Action? RequestClose;
    private readonly Lock progressSync = new();
    private ExportPreparationProgress latestProgress;

    [ObservableProperty] private string currentFileName = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent), nameof(ProgressText))]
    private int completed;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent), nameof(ProgressText), nameof(IsIndeterminate))]
    private int total;

    public double Percent => Total > 0 ? Completed * 100.0 / Total : 0;
    public string ProgressText => Total > 0 ? $"{Completed} / {Total}" : "";
    public bool IsIndeterminate => Total <= 1;

    void IProgress<ExportPreparationProgress>.Report(ExportPreparationProgress value)
    {
        lock (progressSync) latestProgress = value;
    }

    internal void RefreshProgress()
    {
        ExportPreparationProgress value;
        lock (progressSync) value = latestProgress;
        CurrentFileName = value.FileName ?? "";
        Total = value.Total;
        Completed = value.Completed;
    }

    internal async Task ObserveCompletionAsync()
    {
        RefreshProgress();
        try { await Operation; DialogResult = true; }
        catch { DialogResult = false; } // 调用方统一处理任务的错误和取消。
        RefreshProgress();
        RequestClose?.Invoke();
    }

    [RelayCommand] private void Cancel() { if (CanCancel) CancelOperation?.Invoke(); }

    internal void OnClosing(CancelEventArgs e)
    {
        if (Operation.IsCompleted) return;
        e.Cancel = true;
        Cancel();
    }
}
