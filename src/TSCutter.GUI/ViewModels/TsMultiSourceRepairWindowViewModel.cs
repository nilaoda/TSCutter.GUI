using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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

public partial class TsMultiSourceRepairWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    private static readonly TimeSpan IntensiveStatusDelay = TimeSpan.FromMilliseconds(500);
    private readonly IDialogService _dialogService;
    private readonly TsMultiSourceRepairService _repairService = new();
    private readonly TsCheckTextFormatter _text = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private TsMultiSourceAnalysisResult? _analysis;
    private TsRepairOutputResult? _lastOutputResult;
    private TsRepairMapWindowViewModel? _repairMapViewModel;
    private CancellationTokenSource? _intensiveStatusDelayCancellation;
    private TsMultiSourceProgress _pendingIntensiveProgress;
    private int _lastIntensiveTaskCompleted;
    private bool _isIntensiveStatusVisible;
    private bool _isClosing;

    public TsMultiSourceRepairWindowViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
        StatusText = GetReadyStatusText();
        Sources.CollectionChanged += OnSourcesChanged;
        App.LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    public bool? DialogResult { get; }
    public ObservableCollection<TsRepairSourceItem> Sources { get; } = [];
    public ObservableCollection<TsRepairTrackItem> Tracks { get; } = [];
    public ObservableCollection<TsRepairLargeGapItem> LargeGaps { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSourceCommand))]
    private TsRepairSourceItem? _selectedSource;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    private TsRepairSourceItem? _selectedReference;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAnalyze))]
    [NotifyPropertyChangedFor(nameof(CanOutput))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyPropertyChangedFor(nameof(CanConfigureTimeline))]
    [NotifyPropertyChangedFor(nameof(IsLargeGapPromptVisible), nameof(CanMatchLargeGaps))]
    [NotifyCanExecuteChangedFor(nameof(AddSourcesCommand), nameof(RemoveSourceCommand), nameof(AnalyzeCommand),
        nameof(OutputCommand), nameof(CancelCommand), nameof(OpenRepairMapCommand),
        nameof(MatchLargeGapsCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOutput))]
    [NotifyPropertyChangedFor(nameof(CanOpenRepairMap))]
    [NotifyPropertyChangedFor(nameof(IsLargeGapPromptVisible), nameof(CanMatchLargeGaps))]
    [NotifyCanExecuteChangedFor(nameof(OutputCommand), nameof(OpenRepairMapCommand),
        nameof(MatchLargeGapsCommand))]
    private bool _hasAnalysis;

    [ObservableProperty]
    private bool _keepServiceInformation = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimelineNormalizationNote))]
    private bool _autoNormalizeTimeline = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLargeGapPromptVisible), nameof(CanOutput))]
    [NotifyCanExecuteChangedFor(nameof(MatchLargeGapsCommand), nameof(OutputCommand))]
    private bool _isLargeGapMatchingCompleted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLargeGaps), nameof(IsLargeGapPromptVisible),
        nameof(LargeGapMatchButtonText))]
    private int _largeGapCount;

    [ObservableProperty]
    private int _selectedRepairTabIndex;

    [ObservableProperty]
    private string _largeGapSummaryText = string.Empty;

    [ObservableProperty]
    private bool _isLargeGapSummarySuccess;

    [ObservableProperty]
    private bool _isLargeGapSummaryWarning;

    [ObservableProperty]
    private bool _isLargeGapSummaryError;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _progressText = "0 / 0";

    [ObservableProperty]
    private string _speedText = $"{CommonUtil.FormatFileSize(0)}/s";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlainStatusVisible))]
    private bool _isAnalysisSummaryVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlainStatusVisible))]
    private bool _isOutputSummaryVisible;

    [ObservableProperty]
    private string _outputSummaryText = string.Empty;

    [ObservableProperty]
    private string _outputVerificationText = string.Empty;

    [ObservableProperty]
    private bool _isOutputVerificationPassed;

    [ObservableProperty]
    private bool _hasOutputRemainingErrors;

    public bool IsPlainStatusVisible => !IsAnalysisSummaryVisible && !IsOutputSummaryVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnalysisClean), nameof(IsAnalysisIssue))]
    private int _analysisIssueCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnalysisFullyRepairable), nameof(IsAnalysisPartiallyRepairable),
        nameof(IsAnalysisNotRepairable))]
    private int _analysisRepairableCount;

    public bool IsAnalysisClean => AnalysisIssueCount == 0;
    public bool IsAnalysisIssue => AnalysisIssueCount > 0;
    public bool IsAnalysisFullyRepairable => AnalysisIssueCount == 0 ||
                                             AnalysisRepairableCount == AnalysisIssueCount;
    public bool IsAnalysisPartiallyRepairable => AnalysisRepairableCount > 0 &&
                                                 AnalysisRepairableCount < AnalysisIssueCount;
    public bool IsAnalysisNotRepairable => AnalysisIssueCount > 0 && AnalysisRepairableCount == 0;

    public bool CanAnalyze => !IsBusy && Sources.Count >= 2 && SelectedReference is not null;
    public bool CanOutput => !IsBusy && HasAnalysis &&
                             (HasSelectedStandardRepair() || HasSelectedLargeGapRepair());
    public bool CanCancel => IsBusy;
    public bool CanOpenRepairMap => !IsBusy && HasAnalysis;
    public bool CanConfigureTimeline => !IsBusy;
    public bool HasLargeGaps => LargeGapCount > 0;
    public bool IsLargeGapPromptVisible => HasAnalysis && HasLargeGaps &&
                                           !IsLargeGapMatchingCompleted && !IsBusy;
    public bool CanMatchLargeGaps => !IsBusy && HasAnalysis && HasLargeGaps &&
                                     !IsLargeGapMatchingCompleted;
    public string LargeGapMatchButtonText => string.Format(
        _text.Strings.String_TsRepair_LargeGap_MatchCount, LargeGapCount);
    public string TimelineNormalizationNote => AutoNormalizeTimeline
        ? _text.Strings.String_TsRepair_TimelineNormalizationNote
        : _text.Strings.String_TsRepair_TimelineNormalizationDisabledNote;

    private bool HasSelectedStandardRepair() => Tracks.Any(item =>
        item.IsSelected && item.Analysis.RepairableIssueCount > 0);

    private bool HasSelectedLargeGapRepair()
    {
        if (!IsLargeGapMatchingCompleted)
            return false;
        var selectedPids = Tracks.Where(item => item.IsSelected)
            .Select(item => item.Analysis.ReferencePid)
            .ToHashSet();
        return selectedPids.Count > 0 && LargeGaps.Any(item => item.IsSelected &&
            item.Analysis.Candidates.Any(candidate => candidate.Tracks.Any(track =>
                selectedPids.Contains(track.ReferencePid))));
    }

    [RelayCommand(CanExecute = nameof(CanModifySources))]
    private async Task AddSourcesAsync()
    {
        var settings = new OpenFileDialogSettings
        {
            Title = _text.Strings.String_TsRepair_OpenFiles,
            AllowMultiple = true,
            Filters = [new(_text.Strings.String_TsFiles, ["ts"])]
        };
        var selected = await _dialogService.ShowOpenFilesDialogAsync(this, settings);
        var changed = false;
        foreach (var file in selected)
        {
            var path = file.LocalPath;
            if (!File.Exists(path) || Sources.Any(item => PathsEqual(item.FilePath, path)))
                continue;
            Sources.Add(new TsRepairSourceItem(Path.GetFullPath(path))
            {
                StatusText = _text.Strings.String_TsRepair_Source_Ready
            });
            changed = true;
        }
        if (SelectedReference is null && Sources.Count > 0)
            SelectedReference = Sources[0];
        if (changed)
            InvalidateAnalysis();
    }

    private bool CanModifySources() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRemoveSource))]
    private void RemoveSource()
    {
        var source = SelectedSource;
        if (source is null)
            return;
        var wasReference = ReferenceEquals(source, SelectedReference);
        Sources.Remove(source);
        SelectedSource = null;
        if (wasReference)
            SelectedReference = Sources.FirstOrDefault();
        InvalidateAnalysis();
    }

    private bool CanRemoveSource() => !IsBusy && SelectedSource is not null;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        if (!CanAnalyze || SelectedReference is null || Sources.Any(item => !File.Exists(item.FilePath)))
        {
            StatusText = _text.Strings.String_TsRepair_Status_NeedSources;
            return;
        }

        var cancellationTokenSource = new CancellationTokenSource();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = cancellationTokenSource;
        IsBusy = true;
        CloseRepairMap();
        HasAnalysis = false;
        IsAnalysisSummaryVisible = false;
        IsOutputSummaryVisible = false;
        _analysis = null;
        _lastOutputResult = null;
        Tracks.Clear();
        LargeGaps.Clear();
        LargeGapCount = 0;
        LargeGapSummaryText = string.Empty;
        IsLargeGapMatchingCompleted = false;
        SelectedRepairTabIndex = 0;
        Percent = 0;
        ProgressText = "0 / 0";
        SpeedText = $"{CommonUtil.FormatFileSize(0)}/s";
        try
        {
            var paths = Sources.Select(item => item.FilePath).ToArray();
            var progress = new Progress<TsMultiSourceProgress>(value =>
            {
                if (_isClosing)
                    return;
                Percent = value.Percent;
                ProgressText = $"{CommonUtil.FormatFileSize(value.BytesProcessed)} / {CommonUtil.FormatFileSize(value.FileSize)}";
                if (value.IntensiveTaskCount == 0)
                {
                    _lastIntensiveTaskCompleted = 0;
                    SpeedText = $"{CommonUtil.FormatFileSize(value.BytesPerSecond)}/s";
                }
                else
                {
                    _lastIntensiveTaskCompleted = Math.Max(
                        _lastIntensiveTaskCompleted, value.IntensiveTaskCompleted);
                }
                UpdateAnalysisStatus(value);
            });
            var sourceCompleted = new Progress<TsRepairSourceCompleted>(UpdateCompletedSourceRow);
            _analysis = await _repairService.AnalyzeAsync(
                paths, SelectedReference.FilePath, AutoNormalizeTimeline, progress, sourceCompleted,
                cancellationTokenSource.Token);
            ResetIntensiveStatusDelay();
            if (_isClosing)
                return;
            UpdateSourceRows(_analysis);
            BuildTrackRows(_analysis);
            BuildLargeGapRows(_analysis);
            if (_analysis.LargeGaps.Count > 0)
                SelectedRepairTabIndex = 1;
            HasAnalysis = true;
            Percent = 100;
            AnalysisIssueCount = _analysis.TotalGapCount;
            AnalysisRepairableCount = _analysis.RepairableGapCount;
            IsAnalysisSummaryVisible = true;
            RefreshLargeGapSummary();
        }
        catch (OperationCanceledException)
        {
            ResetIntensiveStatusDelay();
            if (!_isClosing)
                StatusText = _text.Strings.String_TsRepair_Status_Cancelled;
        }
        catch (Exception exception)
        {
            ResetIntensiveStatusDelay();
            if (!_isClosing)
                StatusText = string.Format(_text.Strings.String_TsRepair_Status_Failed, exception.Message);
        }
        finally
        {
            ResetIntensiveStatusDelay();
            if (ReferenceEquals(_cancellationTokenSource, cancellationTokenSource))
                _cancellationTokenSource = null;
            cancellationTokenSource.Dispose();
            IsBusy = false;
            NotifyCommands();
        }
    }

    private void UpdateAnalysisStatus(TsMultiSourceProgress progress)
    {
        var intensive = progress.IsIntensiveAnalysis || progress.IntensiveTaskCount > 0;
        if (!intensive)
        {
            ResetIntensiveStatusDelay();
            StatusText = FormatAnalysisStatus(progress, intensive: false);
            return;
        }

        _pendingIntensiveProgress = progress;
        if (_isIntensiveStatusVisible)
        {
            StatusText = FormatAnalysisStatus(progress, intensive: true);
            return;
        }

        // 防抖期间仍显示当前来源的普通分析文案，避免短暂状态造成文字闪烁。
        StatusText = FormatAnalysisStatus(progress, intensive: false);
        if (_intensiveStatusDelayCancellation is not null)
            return;
        var cancellation = new CancellationTokenSource();
        _intensiveStatusDelayCancellation = cancellation;
        _ = ShowIntensiveStatusAfterDelayAsync(cancellation);
    }

    [RelayCommand(CanExecute = nameof(CanMatchLargeGaps))]
    private async Task MatchLargeGapsAsync()
    {
        var analysis = _analysis;
        if (analysis is null || analysis.LargeGaps.Count == 0)
            return;

        var cancellationTokenSource = new CancellationTokenSource();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = cancellationTokenSource;
        IsBusy = true;
        IsAnalysisSummaryVisible = false;
        IsOutputSummaryVisible = false;
        Percent = 0;
        try
        {
            var progress = new Progress<TsMultiSourceProgress>(value =>
            {
                if (_isClosing)
                    return;
                Percent = value.Percent;
                ProgressText = $"{CommonUtil.FormatFileSize(value.BytesProcessed)} / " +
                               CommonUtil.FormatFileSize(value.FileSize);
                SpeedText = $"{CommonUtil.FormatFileSize(value.BytesPerSecond)}/s";
                UpdateAnalysisStatus(value);
            });
            await _repairService.MatchLargeGapsAsync(
                analysis, progress, cancellationTokenSource.Token);
            if (_isClosing)
                return;
            IsLargeGapMatchingCompleted = true;
            Percent = 100;
            BuildLargeGapRows(analysis);
            RefreshLargeGapSummary();
            SelectedRepairTabIndex = 1;
            IsAnalysisSummaryVisible = true;
            RefreshOpenRepairMap();
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
                StatusText = _text.Strings.String_TsRepair_Status_Cancelled;
        }
        catch (Exception exception)
        {
            if (!_isClosing)
                StatusText = string.Format(
                    _text.Strings.String_TsRepair_Status_Failed, exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_cancellationTokenSource, cancellationTokenSource))
                _cancellationTokenSource = null;
            cancellationTokenSource.Dispose();
            IsBusy = false;
            NotifyCommands();
        }
    }

    private async Task ShowIntensiveStatusAfterDelayAsync(CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            await Task.Delay(IntensiveStatusDelay, token);
            if (!ReferenceEquals(_intensiveStatusDelayCancellation, cancellation))
                return;
            _intensiveStatusDelayCancellation = null;
            if (_isClosing || !IsBusy)
                return;
            _isIntensiveStatusVisible = true;
            StatusText = FormatAnalysisStatus(_pendingIntensiveProgress, intensive: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 密集状态未持续到阈值时保持普通分析文案，不让提示在界面上一闪而过。
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private string FormatAnalysisStatus(TsMultiSourceProgress progress, bool intensive)
    {
        var format = intensive
            ? progress.IntensiveTaskCount > 0
                ? _text.Strings.String_TsRepair_Status_AnalyzingParallel
                : _text.Strings.String_TsRepair_Status_AnalyzingIntensive
            : progress.Phase switch
            {
                TsMultiSourceProgressPhase.ReferenceScan =>
                    _text.Strings.String_TsRepair_Status_ScanningReference,
                TsMultiSourceProgressPhase.DonorTimelineAnalysis =>
                    _text.Strings.String_TsRepair_Status_AnalyzingDonorTimeline,
                TsMultiSourceProgressPhase.DonorScan =>
                    _text.Strings.String_TsRepair_Status_ScanningDonor,
                TsMultiSourceProgressPhase.DonorMatchingScan =>
                    _text.Strings.String_TsRepair_Status_RescanningDonor,
                TsMultiSourceProgressPhase.LargeGapMatching =>
                    _text.Strings.String_TsRepair_Status_MatchingLargeGaps,
                _ => _text.Strings.String_TsRepair_Status_Analyzing
            };
        return string.Format(
            format,
            progress.SourceIndex + 1,
            progress.SourceCount,
            Path.GetFileName(progress.FilePath),
            _lastIntensiveTaskCompleted,
            progress.IntensiveTaskCount);
    }

    private void ResetIntensiveStatusDelay()
    {
        var cancellation = _intensiveStatusDelayCancellation;
        _intensiveStatusDelayCancellation = null;
        cancellation?.Cancel();
        _isIntensiveStatusVisible = false;
    }

    [RelayCommand(CanExecute = nameof(CanOutput))]
    private async Task OutputAsync()
    {
        var analysis = _analysis;
        if (analysis is null)
            return;
        var selectedPids = Tracks.Where(item => item.IsSelected)
            .Select(item => item.Analysis.ReferencePid)
            .ToHashSet();
        if (selectedPids.Count == 0)
        {
            StatusText = _text.Strings.String_TsRepair_Status_NoTrack;
            return;
        }
        if (!HasSelectedStandardRepair() && !HasSelectedLargeGapRepair())
        {
            StatusText = _text.Strings.String_TsRepair_Status_NoRepairSelected;
            return;
        }

        var referencePath = analysis.ReferenceSource.FilePath;
        var settings = new SaveFileDialogSettings
        {
            Title = _text.Strings.String_TsRepair_SaveTitle,
            SuggestedStartLocation = new DesktopDialogStorageFolder(Path.GetDirectoryName(referencePath)!),
            SuggestedFileName = Path.GetFileNameWithoutExtension(referencePath) + "_repaired.ts",
            Filters = [new(_text.Strings.String_TsFiles, ["ts"])],
            DefaultExtension = "ts"
        };
        var selected = await _dialogService.ShowSaveFileDialogAsync(this, settings);
        if (selected?.Path is null)
            return;
        var outputPath = selected.Path.LocalPath;
        if (analysis.Sources.Any(source => PathsEqual(source.FilePath, outputPath)))
        {
            StatusText = _text.Strings.String_TsRepair_Status_OutputIsSource;
            return;
        }

        var cancellationTokenSource = new CancellationTokenSource();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = cancellationTokenSource;
        IsBusy = true;
        IsAnalysisSummaryVisible = false;
        IsOutputSummaryVisible = false;
        Percent = 0;
        ProgressText = $"{CommonUtil.FormatFileSize(0)} / {CommonUtil.FormatFileSize(analysis.ReferenceSource.Catalog.FileSize)}";
        SpeedText = $"{CommonUtil.FormatFileSize(0)}/s";
        StatusText = _text.Strings.String_TsRepair_Status_Outputting;
        try
        {
            var plan = _repairService.BuildOutputPlan(
                analysis, selectedPids, KeepServiceInformation, GetSelectedLargeGapOffsets());
            var progress = new Progress<TsFilterProgress>(value =>
            {
                if (_isClosing)
                    return;
                Percent = value.Percent;
                ProgressText = $"{CommonUtil.FormatFileSize(value.BytesProcessed)} / {CommonUtil.FormatFileSize(value.FileSize)}";
                SpeedText = $"{CommonUtil.FormatFileSize(value.BytesPerSecond)}/s";
            });
            var result = await _repairService.OutputAsync(
                plan, outputPath, progress, cancellationTokenSource.Token);
            if (_isClosing)
                return;
            Percent = 100;
            SetOutputSummary(result);
            _lastOutputResult = result;
            RefreshOpenRepairMap(preferActual: true);
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
                StatusText = _text.Strings.String_TsRepair_Status_Cancelled;
        }
        catch (TsRepairException exception)
        {
            if (!_isClosing)
                StatusText = string.Format(
                    _text.Strings.String_TsRepair_Status_Failed, FormatRepairError(exception));
        }
        catch (TsFilterException exception)
        {
            if (!_isClosing)
                StatusText = string.Format(
                    _text.Strings.String_TsRepair_Status_Failed, FormatFilterError(exception));
        }
        catch (Exception exception)
        {
            if (!_isClosing)
                StatusText = string.Format(_text.Strings.String_TsRepair_Status_Failed, exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_cancellationTokenSource, cancellationTokenSource))
                _cancellationTokenSource = null;
            cancellationTokenSource.Dispose();
            IsBusy = false;
            NotifyCommands();
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in Tracks)
            item.IsSelected = true;
        OutputCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearAll()
    {
        foreach (var item in Tracks)
            item.IsSelected = false;
        OutputCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSelectAllLargeGaps))]
    private void SelectAllLargeGaps()
    {
        foreach (var item in LargeGaps.Where(item => item.CanSelect))
            item.IsSelected = true;
    }

    private bool CanSelectAllLargeGaps() => !IsBusy && IsLargeGapMatchingCompleted &&
                                            LargeGaps.Any(item => item.CanSelect && !item.IsSelected);

    [RelayCommand(CanExecute = nameof(CanClearAllLargeGaps))]
    private void ClearAllLargeGaps()
    {
        foreach (var item in LargeGaps)
            item.IsSelected = false;
    }

    private bool CanClearAllLargeGaps() => !IsBusy && IsLargeGapMatchingCompleted &&
                                           LargeGaps.Any(item => item.IsSelected);

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancellationTokenSource?.Cancel();

    [RelayCommand(CanExecute = nameof(CanOpenRepairMap))]
    private void OpenRepairMap()
    {
        var analysis = _analysis;
        if (analysis is null)
            return;
        var selectedPids = Tracks.Where(item => item.IsSelected)
            .Select(item => item.Analysis.ReferencePid)
            .ToHashSet();
        var selectedLargeGapOffsets = GetSelectedLargeGapOffsets();
        if (_repairMapViewModel is not null && _dialogService.Activate(_repairMapViewModel))
        {
            _repairMapViewModel.Refresh(
                analysis, selectedPids, selectedLargeGapOffsets, _lastOutputResult);
            return;
        }

        _repairMapViewModel = new TsRepairMapWindowViewModel(
            analysis, selectedPids, selectedLargeGapOffsets, _lastOutputResult);
        _dialogService.Show(this, _repairMapViewModel);
    }

    private void RefreshOpenRepairMap()
    {
        RefreshOpenRepairMap(preferActual: false);
    }

    private void RefreshOpenRepairMap(bool preferActual)
    {
        var analysis = _analysis;
        if (analysis is null || _repairMapViewModel is null)
            return;
        var selectedPids = Tracks.Where(item => item.IsSelected)
            .Select(item => item.Analysis.ReferencePid)
            .ToHashSet();
        _repairMapViewModel.Refresh(
            analysis, selectedPids, GetSelectedLargeGapOffsets(),
            _lastOutputResult, preferActual);
    }

    private IReadOnlySet<long> GetSelectedLargeGapOffsets() =>
        IsLargeGapMatchingCompleted
            ? LargeGaps.Where(item => item.IsSelected)
                .Select(item => item.Analysis.ReferenceInsertOffset).ToHashSet()
            : new HashSet<long>();

    private void BuildTrackRows(TsMultiSourceAnalysisResult analysis)
    {
        Tracks.Clear();
        foreach (var track in analysis.Tracks.OrderBy(item => item.ProgramNumber).ThenBy(item => item.ReferencePid))
        {
            var row = new TsRepairTrackItem
            {
                Analysis = track,
                PidText = $"0x{track.ReferencePid:X4}",
                ProgramText = track.ProgramNumber.ToString(),
                StreamText = FormatTrack(track),
                RepairSourcesText = FormatRepairSources(track)
            };
            row.SelectionChanged += OutputCommand.NotifyCanExecuteChanged;
            row.SelectionChanged += RefreshOpenRepairMap;
            Tracks.Add(row);
        }
    }

    private void BuildLargeGapRows(TsMultiSourceAnalysisResult analysis)
    {
        var previousSelections = LargeGaps.ToDictionary(
            item => item.Analysis.ReferenceInsertOffset, item => item.IsSelected);
        LargeGaps.Clear();
        var originPts90k = analysis.LargeGapTimelineStartPts90k;
        foreach (var gap in analysis.LargeGaps.OrderBy(item => item.ReferenceMissingStartPts90k))
        {
            var startSeconds = originPts90k == long.MinValue
                ? 0
                : Math.Max(0, (gap.ReferenceMissingStartPts90k - originPts90k) / 90_000.0);
            var endSeconds = startSeconds + gap.MissingDuration90k / 90_000.0;
            var trackTexts = gap.Tracks
                .Select(boundary => analysis.Tracks.FirstOrDefault(track =>
                    track.ReferencePid == boundary.ReferencePid))
                .Where(track => track is not null)
                .Select(track => $"0x{track!.ReferencePid:X4} {FormatTrack(track)}")
                .ToArray();
            var status = !IsLargeGapMatchingCompleted
                ? TsRepairLargeGapViewStatus.Pending
                : gap.IsFullyRepairable
                    ? TsRepairLargeGapViewStatus.Full
                    : gap.IsPartiallyRepairable
                        ? TsRepairLargeGapViewStatus.Partial
                        : TsRepairLargeGapViewStatus.None;
            var sourceText = gap.Candidates.Count == 0
                ? _text.Strings.String_TsRepair_LargeGap_NoSource
                : string.Join(Environment.NewLine, gap.Candidates
                    .OrderByDescending(candidate => candidate.Tracks.Count)
                    .Select(candidate => Path.GetFileName(candidate.SourcePath))
                    .Distinct(StringComparer.Ordinal));
            var resultText = status switch
            {
                TsRepairLargeGapViewStatus.Pending =>
                    _text.Strings.String_TsRepair_LargeGap_ResultPending,
                TsRepairLargeGapViewStatus.Full =>
                    _text.Strings.String_TsRepair_LargeGap_ResultFull,
                TsRepairLargeGapViewStatus.Partial =>
                    _text.Strings.String_TsRepair_LargeGap_ResultPartial,
                _ => _text.Strings.String_TsRepair_LargeGap_ResultNone
            };
            var row = new TsRepairLargeGapItem
            {
                Analysis = gap,
                TimeText = string.Format(
                    _text.Strings.String_TsRepair_LargeGap_TimeRange,
                    TsCheckEvent.FormatTime(startSeconds), TsCheckEvent.FormatTime(endSeconds)),
                DurationText = TsCheckEvent.FormatTime(gap.MissingDuration90k / 90_000.0),
                TracksText = string.Join(
                    _text.Strings.String_TsRepair_Status_ItemSeparator, trackTexts),
                SourceText = sourceText,
                ResultText = resultText,
                Status = status,
                // 匹配和写入是两个独立决定。即使找到了完整候选，也必须由用户明确勾选后才会输出。
                IsSelected = previousSelections.TryGetValue(
                    gap.ReferenceInsertOffset, out var selected) && selected
            };
            row.SelectionChanged += OnLargeGapSelectionChanged;
            LargeGaps.Add(row);
        }
        LargeGapCount = LargeGaps.Count;
        NotifyLargeGapSelectionCommands();
    }

    private void OnLargeGapSelectionChanged()
    {
        OutputCommand.NotifyCanExecuteChanged();
        NotifyLargeGapSelectionCommands();
        RefreshOpenRepairMap();
    }

    private void NotifyLargeGapSelectionCommands()
    {
        SelectAllLargeGapsCommand.NotifyCanExecuteChanged();
        ClearAllLargeGapsCommand.NotifyCanExecuteChanged();
    }

    private void RefreshLargeGapSummary()
    {
        IsLargeGapSummarySuccess = false;
        IsLargeGapSummaryWarning = false;
        IsLargeGapSummaryError = false;
        if (_analysis is not { } analysis || analysis.LargeGaps.Count == 0)
        {
            LargeGapSummaryText = string.Empty;
            return;
        }

        var durationText = TsCheckEvent.FormatTime(
            analysis.LargeGapDuration90k / 90_000.0);
        if (!IsLargeGapMatchingCompleted)
        {
            LargeGapSummaryText = string.Format(
                _text.Strings.String_TsRepair_LargeGap_SummaryDetected,
                analysis.LargeGaps.Count, durationText);
            IsLargeGapSummaryWarning = true;
            return;
        }

        var fullCount = analysis.LargeGaps.Count(item => item.IsFullyRepairable);
        var partialCount = analysis.LargeGaps.Count(item => item.IsPartiallyRepairable);
        if (fullCount + partialCount == 0)
        {
            LargeGapSummaryText = string.Format(
                _text.Strings.String_TsRepair_LargeGap_SummaryMatchedNone,
                analysis.LargeGaps.Count, durationText);
            IsLargeGapSummaryError = true;
            return;
        }
        LargeGapSummaryText = string.Format(
            _text.Strings.String_TsRepair_LargeGap_SummaryMatched,
            analysis.LargeGaps.Count, fullCount, partialCount, durationText);
        IsLargeGapSummarySuccess = fullCount == analysis.LargeGaps.Count;
        IsLargeGapSummaryWarning = !IsLargeGapSummarySuccess;
    }

    private string FormatTrack(TsRepairTrackAnalysis track) => _text.FormatStreamType(
        track.StreamType, track.MpegAudioLayer, track.SupplementaryStreamType, track.Language);

    private string FormatRepairSources(TsRepairTrackAnalysis track)
    {
        if (track.Matches.Count == 0)
            return _text.Strings.String_TsRepair_Match_None;
        var result = string.Empty;
        foreach (var match in track.Matches)
        {
            var confidence = match.Confidence switch
            {
                TsRepairMatchConfidence.PacketFingerprint =>
                    _text.Strings.String_TsRepair_Match_Fingerprint,
                TsRepairMatchConfidence.ElementaryStreamFingerprint =>
                    _text.Strings.String_TsRepair_Match_ElementaryFingerprint,
                _ => _text.Strings.String_TsRepair_Match_Metadata
            };
            var item = string.Format(
                _text.Strings.String_TsRepair_Match_Item,
                Path.GetFileName(match.SourcePath), $"0x{match.SourcePid:X4}", confidence);
            result = result.Length == 0 ? item : result + Environment.NewLine + item;
        }
        return result;
    }

    private void UpdateSourceRows(TsMultiSourceAnalysisResult analysis)
    {
        foreach (var row in Sources)
        {
            var source = analysis.Sources.First(item => PathsEqual(item.FilePath, row.FilePath));
            row.ErrorCount = source.ContinuityErrors + source.TransportErrors + source.PesSizeErrors;
            row.StatusText = source.Catalog.Programs.Count == 0
                ? _text.Strings.String_TsRepair_Source_NoProgram
                : source.IsReference
                    ? _text.Strings.String_TsRepair_Source_Reference
                    : _text.Strings.String_TsRepair_Source_Analyzed;
        }
    }

    private void UpdateCompletedSourceRow(TsRepairSourceCompleted source)
    {
        if (_isClosing)
            return;
        var row = Sources.FirstOrDefault(item => PathsEqual(item.FilePath, source.FilePath));
        if (row is null)
            return;
        row.ErrorCount = source.ErrorCount;
        row.StatusText = !source.HasProgram
            ? _text.Strings.String_TsRepair_Source_NoProgram
            : source.IsReference
                ? _text.Strings.String_TsRepair_Source_Reference
                : _text.Strings.String_TsRepair_Source_Analyzed;
    }

    private void InvalidateAnalysis()
    {
        CloseRepairMap();
        _analysis = null;
        _lastOutputResult = null;
        HasAnalysis = false;
        IsAnalysisSummaryVisible = false;
        IsOutputSummaryVisible = false;
        Tracks.Clear();
        LargeGaps.Clear();
        LargeGapCount = 0;
        LargeGapSummaryText = string.Empty;
        IsLargeGapMatchingCompleted = false;
        SelectedRepairTabIndex = 0;
        foreach (var source in Sources)
        {
            source.ErrorCount = 0;
            source.StatusText = _text.Strings.String_TsRepair_Source_Ready;
        }
        StatusText = GetReadyStatusText();
        NotifyCommands();
    }

    partial void OnSelectedReferenceChanged(TsRepairSourceItem? value)
    {
        if (_analysis is not null)
            InvalidateAnalysis();
    }

    partial void OnAutoNormalizeTimelineChanged(bool value)
    {
        if (IsBusy)
            return;
        if (_analysis is not null)
            InvalidateAnalysis();
        else
            StatusText = GetReadyStatusText();
    }

    private void OnSourcesChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        OnPropertyChanged(nameof(CanAnalyze));
        AnalyzeCommand.NotifyCanExecuteChanged();
        AddSourcesCommand.NotifyCanExecuteChanged();
        RemoveSourceCommand.NotifyCanExecuteChanged();
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(TimelineNormalizationNote));
        OnPropertyChanged(nameof(LargeGapMatchButtonText));
        if (_analysis is not null)
        {
            UpdateSourceRows(_analysis);
            foreach (var row in Tracks)
            {
                row.StreamText = FormatTrack(row.Analysis);
                row.RepairSourcesText = FormatRepairSources(row.Analysis);
            }
            BuildLargeGapRows(_analysis);
            RefreshLargeGapSummary();
        }
        if (IsOutputSummaryVisible)
            RefreshOutputSummaryText();
        else if (_analysis is null)
        {
            foreach (var source in Sources)
                source.StatusText = _text.Strings.String_TsRepair_Source_Ready;
            StatusText = GetReadyStatusText();
        }
    }

    private string GetReadyStatusText() => string.Concat(
        _text.Strings.String_TsRepair_Status_Ready,
        Environment.NewLine,
        _text.Strings.String_TsRepair_Status_ReadyNote,
        Environment.NewLine,
        TimelineNormalizationNote);

    private int _outputRepairedGapCount;
    private int _outputRepairedRegionCount;
    private int _outputRepairedLargeGapCount;
    private long _outputRepairedLargeGapDuration90k;
    private long _outputRepairedPacketCount;
    private long _outputBytesWritten;
    private long _outputRemainingErrorCount;

    private void SetOutputSummary(TsRepairOutputResult result)
    {
        _outputRepairedGapCount = result.RepairedGapCount;
        _outputRepairedRegionCount = result.RepairedPesRegionCount;
        _outputRepairedLargeGapCount = result.RepairedLargeGapCount;
        _outputRepairedLargeGapDuration90k = result.RepairedLargeGapDuration90k;
        _outputRepairedPacketCount = result.RepairedPacketCount;
        _outputBytesWritten = result.FilterResult.BytesWritten;
        _outputRemainingErrorCount = result.RemainingErrorCount;
        RefreshOutputSummaryText();
        IsOutputSummaryVisible = true;
    }

    private void RefreshOutputSummaryText()
    {
        if (_outputRepairedGapCount == 0 && _outputRepairedRegionCount == 0 &&
            _outputRepairedLargeGapCount == 0)
        {
            OutputSummaryText = string.Format(
                _text.Strings.String_TsRepair_Status_OutputNoRepair,
                CommonUtil.FormatFileSize(_outputBytesWritten));
        }
        else
        {
            var repairedItems = new List<string>(3);
            if (_outputRepairedGapCount > 0)
            {
                repairedItems.Add(string.Format(
                    _text.Strings.String_TsRepair_Status_RepairedGaps,
                    _outputRepairedGapCount));
            }
            if (_outputRepairedRegionCount > 0)
            {
                repairedItems.Add(string.Format(
                    _text.Strings.String_TsRepair_Status_RepairedRegions,
                    _outputRepairedRegionCount));
            }
            if (_outputRepairedLargeGapCount > 0)
            {
                repairedItems.Add(string.Format(
                    _text.Strings.String_TsRepair_Status_RepairedLargeGaps,
                    _outputRepairedLargeGapCount,
                    TsCheckEvent.FormatTime(_outputRepairedLargeGapDuration90k / 90_000.0)));
            }
            OutputSummaryText = string.Format(
                _text.Strings.String_TsRepair_Status_OutputSummary,
                string.Join(_text.Strings.String_TsRepair_Status_ItemSeparator, repairedItems),
                _outputRepairedPacketCount,
                CommonUtil.FormatFileSize(_outputBytesWritten));
        }

        IsOutputVerificationPassed = _outputRemainingErrorCount == 0;
        HasOutputRemainingErrors = _outputRemainingErrorCount > 0;
        OutputVerificationText = IsOutputVerificationPassed
            ? _text.Strings.String_TsRepair_Status_VerificationPassed
            : string.Format(
                _text.Strings.String_TsRepair_Status_VerificationRemaining,
                _outputRemainingErrorCount);
    }

    private string FormatRepairError(TsRepairException exception)
    {
        var format = exception.Code switch
        {
            TsRepairErrorCode.OutputIsSource => _text.Strings.String_TsRepair_Status_OutputIsSource,
            TsRepairErrorCode.InvalidRepairData => _text.Strings.String_TsRepair_Error_InvalidData,
            TsRepairErrorCode.SourceChanged => _text.Strings.String_TsRepair_Error_SourceChanged,
            TsRepairErrorCode.ReferenceChanged => _text.Strings.String_TsRepair_Error_ReferenceChanged,
            TsRepairErrorCode.NoImprovement => _text.Strings.String_TsRepair_Error_NoImprovement,
            _ => _text.Strings.String_TsRepair_Error_InvalidData
        };
        return string.Format(format, exception.Arguments);
    }

    private string FormatFilterError(TsFilterException exception)
    {
        var format = exception.Code switch
        {
            TsFilterErrorCode.SameFile => _text.Strings.String_TsFilter_Error_SameFile,
            TsFilterErrorCode.SyncLost => _text.Strings.String_TsFilter_Error_SyncLost,
            TsFilterErrorCode.PatTooLarge => _text.Strings.String_TsFilter_Error_PatTooLarge,
            TsFilterErrorCode.PmtTooLarge => _text.Strings.String_TsFilter_Error_PmtTooLarge,
            _ => _text.Strings.String_TsRepair_Error_InvalidData
        };
        return string.Format(format, exception.Arguments);
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanAnalyze));
        OnPropertyChanged(nameof(CanOutput));
        OnPropertyChanged(nameof(CanCancel));
        AddSourcesCommand.NotifyCanExecuteChanged();
        RemoveSourceCommand.NotifyCanExecuteChanged();
        AnalyzeCommand.NotifyCanExecuteChanged();
        OutputCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        OpenRepairMapCommand.NotifyCanExecuteChanged();
        MatchLargeGapsCommand.NotifyCanExecuteChanged();
        NotifyLargeGapSelectionCommands();
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public void OnClosing(CancelEventArgs eventArgs) => ReleaseForClose();

    public void OnClosed() => ReleaseForClose();

    private void ReleaseForClose()
    {
        if (_isClosing)
            return;
        _isClosing = true;
        ResetIntensiveStatusDelay();
        App.LocalizationService.LanguageChanged -= OnLanguageChanged;
        Sources.CollectionChanged -= OnSourcesChanged;
        _cancellationTokenSource?.Cancel();
        _analysis = null;
        _lastOutputResult = null;
        CloseRepairMap();
        Tracks.Clear();
        LargeGaps.Clear();
        Sources.Clear();
    }

    private void CloseRepairMap()
    {
        var viewModel = _repairMapViewModel;
        _repairMapViewModel = null;
        if (viewModel is not null)
            _dialogService.Close(viewModel);
    }
}
