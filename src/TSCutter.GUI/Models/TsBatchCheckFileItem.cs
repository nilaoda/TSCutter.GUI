using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Models;

public enum TsBatchCheckState { Waiting, Scanning, Completed, Cancelled, Failed }

public sealed partial class TsBatchCheckFileItem(string filePath, long fileSize) : ObservableObject
{
    public string FilePath { get; } = filePath;
    public string FileName => Path.GetFileName(FilePath);
    public string FileSizeText => CommonUtil.FormatFileSize(FileSize);
    public TsCheckResult? Result { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileSizeText))]
    private long _fileSize = fileSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private TsBatchCheckState _state;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private long _bytesScanned;

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedText))]
    private TimeSpan _elapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText))]
    private string _failure = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText), nameof(LogStatusText))]
    private string _logPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailText), nameof(LogStatusText))]
    private string _logError = string.Empty;

    public string ElapsedText => TsCheckEvent.FormatTime(Elapsed.TotalSeconds);
    public string LogStatusText => !string.IsNullOrEmpty(LogError)
        ? LocalizationManager.Instance.String_TsBatchCheck_LogStatus_Failed :
        !string.IsNullOrEmpty(LogPath) ? LocalizationManager.Instance.String_TsBatchCheck_LogStatus_Saved : "—";
    public string VerdictText => Result is null ? "—" : new TsCheckTextFormatter().FormatVerdict(Result);
    public bool IsPass => Result?.Verdict == TsCheckVerdict.Pass;
    public bool IsWarning => Result?.Verdict == TsCheckVerdict.Warning;
    public bool IsError => Result?.Verdict == TsCheckVerdict.Error || State == TsBatchCheckState.Failed;
    public string DetailText => !string.IsNullOrEmpty(Failure) ? Failure :
        !string.IsNullOrEmpty(LogError) ? string.Format(LocalizationManager.Instance.String_TsBatchCheck_LogFailed, LogError) :
        !string.IsNullOrEmpty(LogPath) ? string.Format(LocalizationManager.Instance.String_TsBatchCheck_LogSaved, LogPath) : string.Empty;
    public string StatusText => State switch
    {
        TsBatchCheckState.Scanning => LocalizationManager.Instance.String_TsBatchCheck_State_Scanning,
        TsBatchCheckState.Completed => LocalizationManager.Instance.String_TsBatchCheck_State_Completed,
        TsBatchCheckState.Cancelled => LocalizationManager.Instance.String_TsBatchCheck_State_Cancelled,
        TsBatchCheckState.Failed => LocalizationManager.Instance.String_TsBatchCheck_State_Failed,
        _ => LocalizationManager.Instance.String_TsBatchCheck_State_Waiting
    };

    public void UpdateProgress(TsCheckProgress progress)
    {
        FileSize = progress.FileSize;
        BytesScanned = progress.BytesScanned;
        Percent = progress.Percent;
        ErrorCount = progress.ErrorCount;
        WarningCount = progress.WarningCount;
        Elapsed = progress.Elapsed;
    }

    public void Complete(TsCheckResult result)
    {
        Result = result;
        FileSize = result.FileSize;
        BytesScanned = result.BytesScanned;
        Percent = FileSize > 0 ? BytesScanned * 100.0 / FileSize : 100;
        ErrorCount = result.ErrorCount;
        WarningCount = result.WarningCount;
        Elapsed = result.Elapsed;
        State = result.WasCancelled ? TsBatchCheckState.Cancelled : TsBatchCheckState.Completed;
        RefreshLocalizedText();
    }

    public void Reset()
    {
        Result = null;
        State = TsBatchCheckState.Waiting;
        Percent = 0;
        BytesScanned = 0;
        ErrorCount = WarningCount = 0;
        Elapsed = TimeSpan.Zero;
        Failure = LogPath = LogError = string.Empty;
        RefreshLocalizedText();
    }

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(VerdictText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(LogStatusText));
        OnPropertyChanged(nameof(IsPass));
        OnPropertyChanged(nameof(IsWarning));
        OnPropertyChanged(nameof(IsError));
    }
}
