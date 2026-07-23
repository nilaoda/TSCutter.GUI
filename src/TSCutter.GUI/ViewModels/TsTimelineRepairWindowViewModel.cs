using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.FileSystem;
using HanumanInstitute.MvvmDialogs.FrameworkDialogs;
using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.ViewModels;

public partial class TsTimelineRepairWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    private readonly IDialogService _dialogService;
    private readonly TsTimelineRepairService _service = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private TsCheckResult? _existingCheckResult;
    private TsTimelineRepairAnalysis? _analysis;
    private TsTimelineRepairResult? _lastResult;
    private Exception? _lastException;
    private bool _wasCancelled;
    private bool _isRepairOperation;
    private bool _isClosing;
    private int _generation;

    public TsTimelineRepairWindowViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
        StatusText = LocalizationManager.Instance.String_TsTimelineRepair_Status_Ready;
        App.LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    public bool? DialogResult { get; }
    public ObservableCollection<TsTimelineRepairIssueView> Issues { get; } = [];

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRepair))]
    [NotifyCanExecuteChangedFor(nameof(RepairCommand))]
    private string _outputPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAnalyze), nameof(CanChooseOutput), nameof(CanRepair), nameof(CanCancel))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(ChooseOutputCommand), nameof(RepairCommand), nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRepair))]
    [NotifyCanExecuteChangedFor(nameof(RepairCommand))]
    private bool _hasRepairableIssues;

    [ObservableProperty]
    private bool _synchronizePtsDts = true;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _summaryText = "-";

    [ObservableProperty]
    private string _progressText = "-";

    [ObservableProperty]
    private string _speedText = $"{CommonUtil.FormatFileSize(0)}/s";

    [ObservableProperty]
    private string _elapsedText = "00:00:00.000";

    [ObservableProperty]
    private bool _isSuccessStatus;

    [ObservableProperty]
    private bool _isWarningStatus;

    [ObservableProperty]
    private bool _isErrorStatus;

    public string FileSizeText => File.Exists(FilePath)
        ? CommonUtil.FormatFileSize(new FileInfo(FilePath).Length)
        : "-";

    public bool CanAnalyze => !IsBusy && File.Exists(FilePath);
    public bool CanChooseOutput => !IsBusy;
    public bool CanRepair => !IsBusy && HasRepairableIssues && !string.IsNullOrWhiteSpace(OutputPath);
    public bool CanCancel => IsBusy;

    public void Initialize(string filePath, TsCheckResult? existingCheckResult = null)
    {
        FilePath = Path.GetFullPath(filePath);
        OutputPath = BuildDefaultOutputPath(FilePath);
        _existingCheckResult = existingCheckResult;
        OnPropertyChanged(nameof(FileSizeText));
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        if (IsBusy || !File.Exists(FilePath))
            return;

        var cancellationTokenSource = BeginOperation();
        var generation = ++_generation;
        IsBusy = true;
        HasRepairableIssues = false;
        _lastResult = null;
        _lastException = null;
        _wasCancelled = false;
        _isRepairOperation = false;
        Issues.Clear();
        Percent = 0;
        SummaryText = "-";
        SetStatus(AnalyzeStatusText);
        try
        {
            var progress = CreateProgress(generation);
            var analysis = await Task.Run(() => _service.AnalyzeAsync(
                FilePath, _existingCheckResult, progress, cancellationTokenSource.Token));
            if (_isClosing || generation != _generation)
                return;

            _analysis = analysis;
            _existingCheckResult = analysis.CheckResult;
            RebuildIssueRows();
            HasRepairableIssues = analysis.RepairableIssueCount > 0;
            Percent = 100;
            SummaryText = string.Format(
                LocalizationManager.Instance.String_TsTimelineRepair_Summary,
                analysis.Issues.Count,
                analysis.Issues.Select(item => item.PcrPid).Distinct().Count(),
                analysis.Issues.Count(item => item.AffectsStreamTimestamps));
            SetStatus(HasRepairableIssues
                    ? string.Format(LocalizationManager.Instance.String_TsTimelineRepair_Status_AnalysisCompleted,
                        analysis.RepairableIssueCount)
                    : LocalizationManager.Instance.String_TsTimelineRepair_Status_NoIssues,
                success: !HasRepairableIssues,
                warning: HasRepairableIssues);
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing && generation == _generation)
            {
                _wasCancelled = true;
                SetStatus(LocalizationManager.Instance.String_TsTimelineRepair_Status_Cancelled, warning: true);
            }
        }
        catch (Exception exception)
        {
            if (!_isClosing && generation == _generation)
            {
                _lastException = exception;
                SetStatus(string.Format(LocalizationManager.Instance.String_TsTimelineRepair_Status_Failed,
                    FormatException(exception)), error: true);
            }
        }
        finally
        {
            EndOperation(cancellationTokenSource);
        }
    }

    [RelayCommand(CanExecute = nameof(CanChooseOutput))]
    private async Task ChooseOutputAsync()
    {
        var selected = await _dialogService.ShowSaveFileDialogAsync(this, new SaveFileDialogSettings
        {
            Title = LocalizationManager.Instance.String_TsTimelineRepair_Output_Title,
            SuggestedStartLocation = new DesktopDialogStorageFolder(Path.GetDirectoryName(FilePath)!),
            SuggestedFileName = Path.GetFileName(OutputPath),
            Filters = [new(LocalizationManager.Instance.String_TsFiles, ["ts"])],
            DefaultExtension = "ts"
        });
        if (selected?.Path is not null)
            OutputPath = selected.Path.LocalPath;
    }

    [RelayCommand(CanExecute = nameof(CanRepair))]
    private async Task RepairAsync()
    {
        var analysis = _analysis;
        if (analysis is null || IsBusy)
            return;

        var cancellationTokenSource = BeginOperation();
        var generation = ++_generation;
        IsBusy = true;
        _lastResult = null;
        _lastException = null;
        _wasCancelled = false;
        _isRepairOperation = true;
        Percent = 0;
        SetStatus(LocalizationManager.Instance.String_TsTimelineRepair_Status_Repairing);
        try
        {
            var progress = CreateProgress(generation);
            var result = await Task.Run(() => _service.RepairAsync(
                analysis, OutputPath, SynchronizePtsDts, progress, cancellationTokenSource.Token));
            if (_isClosing || generation != _generation)
                return;

            Percent = 100;
            _lastResult = result;
            ProgressText = $"{CommonUtil.FormatFileSize(result.FileSize)} / {CommonUtil.FormatFileSize(result.FileSize)}";
            ElapsedText = result.Elapsed.ToString(@"hh\:mm\:ss\.fff");
            if (result.RemainingPcrErrorCount == 0 && result.RemainingPcrWarningCount == 0)
            {
                SetStatus(string.Format(LocalizationManager.Instance.String_TsTimelineRepair_Status_Completed,
                    result.RepairedIssueCount, result.RewrittenPcrCount, result.RewrittenTimestampCount), success: true);
            }
            else
            {
                SetStatus(string.Format(LocalizationManager.Instance.String_TsTimelineRepair_Status_CompletedWarning,
                    result.RemainingPcrErrorCount, result.RemainingPcrWarningCount), warning: true);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing && generation == _generation)
            {
                _wasCancelled = true;
                SetStatus(LocalizationManager.Instance.String_TsTimelineRepair_Status_Cancelled, warning: true);
            }
        }
        catch (Exception exception)
        {
            if (!_isClosing && generation == _generation)
            {
                _lastException = exception;
                SetStatus(string.Format(LocalizationManager.Instance.String_TsTimelineRepair_Status_Failed,
                    FormatException(exception)), error: true);
            }
        }
        finally
        {
            EndOperation(cancellationTokenSource);
        }
    }

    private Progress<TsTimelineRepairProgress> CreateProgress(int generation) => new(value =>
    {
        if (_isClosing || generation != _generation)
            return;
        Percent = value.Percent;
        ProgressText = $"{CommonUtil.FormatFileSize(value.BytesProcessed)} / {CommonUtil.FormatFileSize(value.FileSize)}";
        SpeedText = $"{CommonUtil.FormatFileSize(value.BytesPerSecond)}/s";
        ElapsedText = value.Elapsed.ToString(@"hh\:mm\:ss\.fff");
    });

    private void RebuildIssueRows()
    {
        Issues.Clear();
        if (_analysis is null)
            return;

        foreach (var item in _analysis.Issues)
        {
            Issues.Add(new TsTimelineRepairIssueView
            {
                Item = item,
                TimeText = TsCheckEvent.FormatTime(item.StartTimeSeconds),
                KindText = item.Kind switch
                {
                    TsTimelineIssueKind.TemporaryPcrOffset =>
                        LocalizationManager.Instance.String_TsTimelineRepair_Kind_TemporaryOffset,
                    TsTimelineIssueKind.GradualPcrDrift =>
                        LocalizationManager.Instance.String_TsTimelineRepair_Kind_GradualDrift,
                    _ => LocalizationManager.Instance.String_TsTimelineRepair_Kind_PersistentDiscontinuity
                },
                PcrPidText = $"0x{item.PcrPid:X4}",
                RangeText = $"{TsCheckEvent.FormatTime(item.StartTimeSeconds)} - {TsCheckEvent.FormatTime(item.EndTimeSeconds)}",
                CorrectionText = $"{item.CorrectionSeconds:+0.000;-0.000;0.000} s",
                TimestampText = item.AffectsStreamTimestamps
                    ? LocalizationManager.Instance.String_TsTimelineRepair_Value_TimestampCorrection
                    : LocalizationManager.Instance.String_TsTimelineRepair_Value_NotNeeded,
                RepairableText = item.IsRepairable
                    ? LocalizationManager.Instance.String_TsTimelineRepair_Value_Yes
                    : LocalizationManager.Instance.String_TsTimelineRepair_Value_No
            });
        }
    }

    private CancellationTokenSource BeginOperation()
    {
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = new CancellationTokenSource();
        return _cancellationTokenSource;
    }

    private void EndOperation(CancellationTokenSource source)
    {
        if (ReferenceEquals(_cancellationTokenSource, source))
            _cancellationTokenSource = null;
        source.Dispose();
        IsBusy = false;
    }

    private void SetStatus(string text, bool success = false, bool warning = false, bool error = false)
    {
        StatusText = text;
        IsSuccessStatus = success;
        IsWarningStatus = warning;
        IsErrorStatus = error;
    }

    private string AnalyzeStatusText => _existingCheckResult is null
        ? LocalizationManager.Instance.String_TsTimelineRepair_Status_Analyzing
        : LocalizationManager.Instance.String_TsTimelineRepair_Status_CollectingSamples;

    private void OnLanguageChanged()
    {
        RebuildIssueRows();
        if (_analysis is not null)
        {
            SummaryText = string.Format(
                LocalizationManager.Instance.String_TsTimelineRepair_Summary,
                _analysis.Issues.Count,
                _analysis.Issues.Select(item => item.PcrPid).Distinct().Count(),
                _analysis.Issues.Count(item => item.AffectsStreamTimestamps));
        }
        RefreshLocalizedStatus();
    }

    private void RefreshLocalizedStatus()
    {
        if (IsBusy)
        {
            SetStatus(_isRepairOperation
                ? LocalizationManager.Instance.String_TsTimelineRepair_Status_Repairing
                : AnalyzeStatusText);
            return;
        }
        if (_lastException is not null)
        {
            SetStatus(string.Format(LocalizationManager.Instance.String_TsTimelineRepair_Status_Failed,
                FormatException(_lastException)), error: true);
            return;
        }
        if (_wasCancelled)
        {
            SetStatus(LocalizationManager.Instance.String_TsTimelineRepair_Status_Cancelled, warning: true);
            return;
        }
        if (_lastResult is { } result)
        {
            if (result.RemainingPcrErrorCount == 0 && result.RemainingPcrWarningCount == 0)
            {
                SetStatus(string.Format(LocalizationManager.Instance.String_TsTimelineRepair_Status_Completed,
                    result.RepairedIssueCount, result.RewrittenPcrCount, result.RewrittenTimestampCount), success: true);
            }
            else
            {
                SetStatus(string.Format(LocalizationManager.Instance.String_TsTimelineRepair_Status_CompletedWarning,
                    result.RemainingPcrErrorCount, result.RemainingPcrWarningCount), warning: true);
            }
            return;
        }
        if (_analysis is not null)
        {
            SetStatus(HasRepairableIssues
                    ? string.Format(LocalizationManager.Instance.String_TsTimelineRepair_Status_AnalysisCompleted,
                        _analysis.RepairableIssueCount)
                    : LocalizationManager.Instance.String_TsTimelineRepair_Status_NoIssues,
                success: !HasRepairableIssues,
                warning: HasRepairableIssues);
            return;
        }
        SetStatus(LocalizationManager.Instance.String_TsTimelineRepair_Status_Ready);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancellationTokenSource?.Cancel();

    public void OnClosing(CancelEventArgs eventArgs) => ReleaseForClose();
    public void OnClosed() => ReleaseForClose();

    private void ReleaseForClose()
    {
        if (_isClosing)
            return;
        _isClosing = true;
        App.LocalizationService.LanguageChanged -= OnLanguageChanged;
        _generation++;
        _cancellationTokenSource?.Cancel();
        _analysis = null;
        _existingCheckResult = null;
        Issues.Clear();
    }

    private static string BuildDefaultOutputPath(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath)!;
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var candidate = Path.Combine(directory, name + "_timeline_fixed.ts");
        for (var suffix = 2; File.Exists(candidate); suffix++)
            candidate = Path.Combine(directory, $"{name}_timeline_fixed_{suffix}.ts");
        return candidate;
    }

    private static string FormatException(Exception exception)
    {
        if (exception is not TsTimelineRepairException repairException)
            return exception.Message;
        return repairException.Code switch
        {
            TsTimelineRepairErrorCode.OutputMatchesSource =>
                LocalizationManager.Instance.String_TsTimelineRepair_Error_SameFile,
            TsTimelineRepairErrorCode.SourceChanged =>
                LocalizationManager.Instance.String_TsTimelineRepair_Error_SourceChanged,
            TsTimelineRepairErrorCode.SyncLost => string.Format(
                LocalizationManager.Instance.String_TsTimelineRepair_Error_SyncLost,
                repairException.Arguments),
            TsTimelineRepairErrorCode.NoSync =>
                LocalizationManager.Instance.String_TsTimelineRepair_Error_NoSync,
            _ => exception.Message
        };
    }
}
