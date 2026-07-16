using System;
using System.Collections.Generic;
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

public partial class TsFilterWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    private const long MaxProbeBytes = 64L * 1024 * 1024;
    private readonly IDialogService _dialogService;
    private readonly TsCheckTextFormatter _text = new();
    private readonly List<TsFilterPidItem> _allPids = [];
    private CancellationTokenSource? _cancellationTokenSource;
    private TsCheckResult? _catalog;
    private TsFilterPlan? _plan;
    private bool _isClosing;
    private bool _updatingSelection;
    private int _generation;

    public TsFilterWindowViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
        StatusText = LocalizationManager.Instance.String_TsFilter_Status_Ready;
    }

    public bool? DialogResult { get; }
    public string FilePath { get; set; } = string.Empty;
    public ObservableCollection<TsFilterPidItem> Pids { get; } = [];
    public string FileSizeText => File.Exists(FilePath)
        ? CommonUtil.FormatFileSize(new FileInfo(FilePath).Length)
        : "-";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOutput), nameof(CanSelect), nameof(CanCancel))]
    [NotifyCanExecuteChangedFor(nameof(OutputCommand), nameof(CancelCommand), nameof(SelectAllCommand), nameof(ClearAllCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isProbing;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _progressText = "-";

    [ObservableProperty]
    private string _speedText = $"{CommonUtil.FormatFileSize(0)}/s";

    [ObservableProperty]
    private string _probeSummaryText = "-";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProbeSummaryVisible))]
    private bool _hasProbeResult;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProbeSummaryVisible))]
    private bool _showOutputProgress;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private bool _showAllPids;

    [ObservableProperty]
    private bool _keepServiceInformation = true;

    public bool CanOutput => !IsBusy && _catalog is not null && SelectedCount > 0;
    public bool CanSelect => !IsBusy && _catalog is not null;
    public bool CanCancel => IsBusy;
    public bool IsProbeSummaryVisible => HasProbeResult && !ShowOutputProgress;

    [RelayCommand]
    private async Task ProbeAsync()
    {
        if (IsBusy || !File.Exists(FilePath))
            return;

        var cancellationTokenSource = new CancellationTokenSource();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = cancellationTokenSource;
        var generation = ++_generation;
        IsBusy = true;
        IsProbing = true;
        HasProbeResult = false;
        ShowOutputProgress = false;
        Percent = 0;
        Pids.Clear();
        _catalog = null;
        _plan = null;
        StatusText = LocalizationManager.Instance.String_TsFilter_Status_Probing;

        try
        {
            var progress = new Progress<TsCheckProgress>(value =>
            {
                if (_isClosing || generation != _generation)
                    return;
                ProgressText = CommonUtil.FormatFileSize(value.BytesScanned);
                SpeedText = $"{CommonUtil.FormatFileSize(value.BytesPerSecond)}/s";
            });
            var analyzer = new TsStreamAnalyzer();
            var options = new TsStreamAnalyzeOptions
            {
                InventoryOnly = true,
                MaxBytes = MaxProbeBytes,
                StablePacketCount = 8_192
            };
            var result = await Task.Run(() => analyzer.AnalyzeAsync(
                FilePath, progress, cancellationTokenSource.Token, options));
            if (_isClosing || generation != _generation)
                return;
            if (result.WasCancelled)
            {
                StatusText = LocalizationManager.Instance.String_TsFilter_Status_ProbeCancelled;
                return;
            }

            _catalog = result;
            BuildPidRows(result);
            var streamCount = _allPids.Count(item => item.IsProgramStream);
            ProbeSummaryText = string.Format(
                LocalizationManager.Instance.String_TsFilter_ProbeSummary,
                CommonUtil.FormatFileSize(result.BytesScanned), streamCount, result.Pids.Count);
            HasProbeResult = true;
            ProgressText = CommonUtil.FormatFileSize(result.BytesScanned);
            StatusText = result.Programs.Count > 0
                ? string.Format(LocalizationManager.Instance.String_TsFilter_Status_ProbeCompleted, streamCount)
                : LocalizationManager.Instance.String_TsFilter_Status_NoPrograms;
        }
        catch (Exception exception)
        {
            if (!_isClosing && generation == _generation)
                StatusText = string.Format(LocalizationManager.Instance.String_TsFilter_Status_Failed, exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_cancellationTokenSource, cancellationTokenSource))
                _cancellationTokenSource = null;
            cancellationTokenSource.Dispose();
            IsProbing = false;
            IsBusy = false;
            NotifyCommandStates();
        }
    }

    private void BuildPidRows(TsCheckResult result)
    {
        foreach (var row in _allPids)
            row.SelectionChanged -= OnSelectionChanged;
        _allPids.Clear();
        _updatingSelection = true;
        try
        {
            foreach (var pid in result.Pids.Values.OrderBy(item => item.Pid))
            {
                var isProgramStream = result.Programs.Values.Any(program => program.Streams.ContainsKey(pid.Pid));
                var row = new TsFilterPidItem
                {
                    Pid = pid.Pid,
                    PidText = pid.PidText,
                    StreamText = _text.FormatPidDescription(
                        pid.Pid, pid.ProgramNumber, pid.StreamType, pid.MpegAudioLayer,
                        pid.SupplementaryStreamType, pid.Language,
                        pid.IsPcrPid, pid.IsPmtPid),
                    ProgramText = pid.ProgramNumber?.ToString() ?? "-",
                    SamplePacketCount = pid.PacketCount,
                    SamplePacketCountText = pid.PacketCount.ToString("N0"),
                    IsProgramStream = isProgramStream,
                    IsSelected = isProgramStream
                };
                row.SelectionChanged += OnSelectionChanged;
                _allPids.Add(row);
            }
        }
        finally
        {
            _updatingSelection = false;
        }
        RefreshVisiblePids();
        RecalculatePlan();
    }

    partial void OnShowAllPidsChanged(bool value) => RefreshVisiblePids();

    partial void OnKeepServiceInformationChanged(bool value) => RecalculatePlan();

    private void RefreshVisiblePids()
    {
        Pids.Clear();
        foreach (var row in _allPids)
        {
            if (ShowAllPids || row.IsProgramStream)
                Pids.Add(row);
        }
    }

    private void OnSelectionChanged()
    {
        if (!_updatingSelection)
            RecalculatePlan();
    }

    private void RecalculatePlan()
    {
        if (_catalog is null)
            return;
        var selected = _allPids.Where(item => item.IsSelected).Select(item => item.Pid).ToHashSet();
        _plan = new TsStreamFilterService().BuildPlan(_catalog, selected, KeepServiceInformation);
        _updatingSelection = true;
        try
        {
            foreach (var row in _allPids)
            {
                row.IsAutoIncluded = !row.IsSelected && _plan.EffectivePids.Contains(row.Pid);
                row.OutputReason = _plan.PsiSections.ContainsKey(row.Pid)
                    ? LocalizationManager.Instance.String_TsFilter_Output_RebuiltTable
                    : _plan.PcrOnlyPids.Contains(row.Pid)
                        ? LocalizationManager.Instance.String_TsFilter_Output_PcrOnly
                        : _plan.ServiceInformationPids.Contains(row.Pid)
                            ? LocalizationManager.Instance.String_TsFilter_Output_ServiceInformation
                        : row.IsSelected
                            ? LocalizationManager.Instance.String_TsFilter_Output_Selected
                            : LocalizationManager.Instance.String_TsFilter_Output_Excluded;
            }
        }
        finally
        {
            _updatingSelection = false;
        }
        SelectedCount = selected.Count;
        OnPropertyChanged(nameof(CanOutput));
        OutputCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSelect))]
    private void SelectAll()
    {
        _updatingSelection = true;
        foreach (var row in Pids)
            row.IsSelected = true;
        _updatingSelection = false;
        RecalculatePlan();
    }

    [RelayCommand(CanExecute = nameof(CanSelect))]
    private void ClearAll()
    {
        _updatingSelection = true;
        foreach (var row in Pids)
            row.IsSelected = false;
        _updatingSelection = false;
        RecalculatePlan();
    }

    [RelayCommand(CanExecute = nameof(CanOutput))]
    private async Task OutputAsync()
    {
        var catalog = _catalog;
        var plan = _plan;
        if (catalog is null || plan is null || IsBusy)
            return;

        var settings = new SaveFileDialogSettings
        {
            Title = LocalizationManager.Instance.String_TsFilter_SaveTitle,
            SuggestedStartLocation = new DesktopDialogStorageFolder(Path.GetDirectoryName(FilePath)!),
            SuggestedFileName = Path.GetFileNameWithoutExtension(FilePath) + "_filtered.ts",
            Filters = [new(LocalizationManager.Instance.String_TsFiles, ["ts"])],
            DefaultExtension = "ts"
        };
        var selected = await _dialogService.ShowSaveFileDialogAsync(this, settings);
        if (selected?.Path is null)
            return;
        var outputPath = selected.Path.LocalPath;
        if (Path.GetFullPath(outputPath) == Path.GetFullPath(FilePath))
        {
            StatusText = LocalizationManager.Instance.String_TsFilter_Status_SameFile;
            return;
        }

        var cancellationTokenSource = new CancellationTokenSource();
        _cancellationTokenSource = cancellationTokenSource;
        var generation = ++_generation;
        IsBusy = true;
        ShowOutputProgress = true;
        Percent = 0;
        StatusText = LocalizationManager.Instance.String_TsFilter_Status_Filtering;
        try
        {
            var progress = new Progress<TsFilterProgress>(value =>
            {
                if (_isClosing || generation != _generation)
                    return;
                Percent = value.Percent;
                ProgressText = $"{CommonUtil.FormatFileSize(value.BytesProcessed)} / {CommonUtil.FormatFileSize(value.FileSize)}";
                SpeedText = $"{CommonUtil.FormatFileSize(value.BytesPerSecond)}/s";
            });
            var service = new TsStreamFilterService();
            var result = await Task.Run(() => service.FilterAsync(
                FilePath, outputPath, catalog, plan, progress, cancellationTokenSource.Token));
            if (_isClosing || generation != _generation)
                return;
            Percent = 100;
            StatusText = string.Format(
                LocalizationManager.Instance.String_TsFilter_Status_Completed,
                CommonUtil.FormatFileSize(result.BytesWritten));
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
                StatusText = LocalizationManager.Instance.String_TsFilter_Status_Cancelled;
        }
        catch (TsFilterException exception)
        {
            if (!_isClosing)
                StatusText = string.Format(
                    LocalizationManager.Instance.String_TsFilter_Status_Failed,
                    FormatFilterError(exception));
        }
        catch (Exception exception)
        {
            if (!_isClosing)
                StatusText = string.Format(LocalizationManager.Instance.String_TsFilter_Status_Failed, exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_cancellationTokenSource, cancellationTokenSource))
                _cancellationTokenSource = null;
            cancellationTokenSource.Dispose();
            IsBusy = false;
            NotifyCommandStates();
        }
    }

    private static string FormatFilterError(TsFilterException exception)
    {
        var strings = LocalizationManager.Instance;
        var format = exception.Code switch
        {
            TsFilterErrorCode.SameFile => strings.String_TsFilter_Error_SameFile,
            TsFilterErrorCode.SyncLost => strings.String_TsFilter_Error_SyncLost,
            TsFilterErrorCode.PatTooLarge => strings.String_TsFilter_Error_PatTooLarge,
            TsFilterErrorCode.PmtTooLarge => strings.String_TsFilter_Error_PmtTooLarge,
            _ => strings.String_TsFilter_Status_Failed
        };
        return string.Format(format, exception.Arguments);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancellationTokenSource?.Cancel();

    private void NotifyCommandStates()
    {
        OutputCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        ClearAllCommand.NotifyCanExecuteChanged();
    }

    public void OnClosing(CancelEventArgs eventArgs) => ReleaseForClose();
    public void OnClosed() => ReleaseForClose();

    private void ReleaseForClose()
    {
        if (_isClosing)
            return;
        _isClosing = true;
        _generation++;
        _cancellationTokenSource?.Cancel();
        foreach (var row in _allPids)
            row.SelectionChanged -= OnSelectionChanged;
        Pids.Clear();
        _allPids.Clear();
        _catalog = null;
        _plan = null;
    }
}
