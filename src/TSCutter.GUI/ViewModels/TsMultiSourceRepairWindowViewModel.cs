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
    private readonly IDialogService _dialogService;
    private readonly TsMultiSourceRepairService _repairService = new();
    private readonly TsCheckTextFormatter _text = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private TsMultiSourceAnalysisResult? _analysis;
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
    [NotifyCanExecuteChangedFor(nameof(AddSourcesCommand), nameof(RemoveSourceCommand), nameof(AnalyzeCommand),
        nameof(OutputCommand), nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOutput))]
    [NotifyCanExecuteChangedFor(nameof(OutputCommand))]
    private bool _hasAnalysis;

    [ObservableProperty]
    private bool _keepServiceInformation = true;

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
    public bool CanOutput => !IsBusy && HasAnalysis && Tracks.Any(item => item.IsSelected);
    public bool CanCancel => IsBusy;

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
        HasAnalysis = false;
        IsAnalysisSummaryVisible = false;
        IsOutputSummaryVisible = false;
        _analysis = null;
        Tracks.Clear();
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
                SpeedText = $"{CommonUtil.FormatFileSize(value.BytesPerSecond)}/s";
                StatusText = string.Format(
                    value.IsIntensiveAnalysis
                        ? _text.Strings.String_TsRepair_Status_AnalyzingIntensive
                        : _text.Strings.String_TsRepair_Status_Analyzing,
                    value.SourceIndex + 1, value.SourceCount, Path.GetFileName(value.FilePath));
            });
            var sourceCompleted = new Progress<TsRepairSourceCompleted>(UpdateCompletedSourceRow);
            _analysis = await _repairService.AnalyzeAsync(
                paths, SelectedReference.FilePath, progress, sourceCompleted, cancellationTokenSource.Token);
            if (_isClosing)
                return;
            UpdateSourceRows(_analysis);
            BuildTrackRows(_analysis);
            HasAnalysis = true;
            Percent = 100;
            AnalysisIssueCount = _analysis.TotalGapCount;
            AnalysisRepairableCount = _analysis.RepairableGapCount;
            IsAnalysisSummaryVisible = true;
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
                StatusText = _text.Strings.String_TsRepair_Status_Cancelled;
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
            var plan = _repairService.BuildOutputPlan(analysis, selectedPids, KeepServiceInformation);
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

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancellationTokenSource?.Cancel();

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
            Tracks.Add(row);
        }
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
        _analysis = null;
        HasAnalysis = false;
        IsAnalysisSummaryVisible = false;
        IsOutputSummaryVisible = false;
        Tracks.Clear();
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

    private void OnSourcesChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        OnPropertyChanged(nameof(CanAnalyze));
        AnalyzeCommand.NotifyCanExecuteChanged();
        AddSourcesCommand.NotifyCanExecuteChanged();
        RemoveSourceCommand.NotifyCanExecuteChanged();
    }

    private void OnLanguageChanged()
    {
        if (_analysis is not null)
        {
            UpdateSourceRows(_analysis);
            foreach (var row in Tracks)
            {
                row.StreamText = FormatTrack(row.Analysis);
                row.RepairSourcesText = FormatRepairSources(row.Analysis);
            }
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
        _text.Strings.String_TsRepair_Status_ReadyNote);

    private int _outputRepairedGapCount;
    private int _outputRepairedRegionCount;
    private long _outputRepairedPacketCount;
    private long _outputBytesWritten;
    private long _outputRemainingErrorCount;

    private void SetOutputSummary(TsRepairOutputResult result)
    {
        _outputRepairedGapCount = result.RepairedGapCount;
        _outputRepairedRegionCount = result.RepairedPesRegionCount;
        _outputRepairedPacketCount = result.RepairedPacketCount;
        _outputBytesWritten = result.FilterResult.BytesWritten;
        _outputRemainingErrorCount = result.RemainingErrorCount;
        RefreshOutputSummaryText();
        IsOutputSummaryVisible = true;
    }

    private void RefreshOutputSummaryText()
    {
        if (_outputRepairedGapCount == 0 && _outputRepairedRegionCount == 0)
        {
            OutputSummaryText = string.Format(
                _text.Strings.String_TsRepair_Status_OutputNoRepair,
                CommonUtil.FormatFileSize(_outputBytesWritten));
        }
        else
        {
            var repairedItems = new List<string>(2);
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
        App.LocalizationService.LanguageChanged -= OnLanguageChanged;
        Sources.CollectionChanged -= OnSourcesChanged;
        _cancellationTokenSource?.Cancel();
        _analysis = null;
        Tracks.Clear();
        Sources.Clear();
    }
}
