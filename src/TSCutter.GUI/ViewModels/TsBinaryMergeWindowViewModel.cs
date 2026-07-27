using System;
using System.Collections.Generic;
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

public partial class TsBinaryMergeWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    private const decimal DefaultMaximumSearchMiB = 1024;
    private readonly IDialogService _dialogService;
    private readonly TsBinaryMergeService _service = new();
    private readonly List<TsBinaryMergeFileItem> _selectedFiles = [];
    private CancellationTokenSource? _cancellationTokenSource;
    private TsBinaryMergeAnalysis? _analysis;
    private bool _isClosing;
    private int _generation;

    public TsBinaryMergeWindowViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
        StatusText = LocalizationManager.Instance.String_TsBinaryMerge_Status_Ready;
        App.LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    public bool? DialogResult { get; }
    public string WindowTitle => LocalizationManager.Instance.String_TsBinaryMerge_Title;
    public BulkObservableCollection<TsBinaryMergeFileItem> Files { get; } = [];
    public event Action<IReadOnlyList<TsBinaryMergeFileItem>>? SelectionRestoreRequested;

    public bool IsEmpty => Files.Count == 0;
    public string FileSummaryText => string.Format(
        LocalizationManager.Instance.String_TsBinaryMerge_FileSummary,
        Files.Count,
        CommonUtil.FormatFileSize(Files.Sum(item => item.FileSize)));
    public bool IsOverlapMode
    {
        get => !IsDirectMode;
        set
        {
            if (value)
                IsDirectMode = false;
        }
    }
    public bool HasAnalysis => _analysis is not null;
    public bool HasUnmatchedJoins => _analysis?.HasUnmatchedJoins == true;
    public bool CanModifyFiles => !IsBusy;
    public bool CanAnalyze => !IsBusy && IsOverlapMode && Files.Count >= 2;
    public bool CanMerge => !IsBusy && Files.Count >= 2 &&
                            !string.IsNullOrWhiteSpace(OutputPath) &&
                            (IsDirectMode ||
                             _analysis is not null &&
                             (!_analysis.HasUnmatchedJoins || AppendUnmatchedSources));
    public bool CanCancel => IsBusy;
    public bool CanRemove => !IsBusy && _selectedFiles.Count > 0;
    public bool CanMoveUp => CanRemove && _selectedFiles.Any(item => Files.IndexOf(item) > 0);
    public bool CanMoveDown => CanRemove && _selectedFiles.Any(item =>
        Files.IndexOf(item) >= 0 && Files.IndexOf(item) < Files.Count - 1);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverlapMode), nameof(CanAnalyze), nameof(CanMerge))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(MergeCommand))]
    private bool _isDirectMode = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMerge))]
    [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
    private bool _appendUnmatchedSources;

    [ObservableProperty]
    private decimal _maximumSearchMiB = DefaultMaximumSearchMiB;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMerge))]
    [NotifyCanExecuteChangedFor(nameof(MergeCommand))]
    private string _outputPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanModifyFiles), nameof(CanAnalyze), nameof(CanMerge),
        nameof(CanCancel), nameof(CanRemove), nameof(CanMoveUp), nameof(CanMoveDown))]
    [NotifyCanExecuteChangedFor(nameof(AddFilesCommand), nameof(RemoveFilesCommand),
        nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(NaturalSortCommand),
        nameof(ClearCommand), nameof(AnalyzeCommand), nameof(BrowseOutputCommand),
        nameof(MergeCommand), nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _progressText = "-";

    [ObservableProperty]
    private string _speedText = $"{CommonUtil.FormatFileSize(0)}/s";

    [ObservableProperty]
    private string _analysisSummaryText = string.Empty;

    partial void OnIsDirectModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsOverlapMode));
        InvalidateAnalysis();
        ResetProgressDisplay();
        StatusText = GetModeReadyStatus();
    }

    partial void OnMaximumSearchMiBChanged(decimal value)
    {
        var normalized = Math.Clamp(value, 64, 16_384);
        if (normalized != value)
        {
            MaximumSearchMiB = normalized;
            return;
        }
        InvalidateAnalysis();
    }

    partial void OnAppendUnmatchedSourcesChanged(bool value)
    {
        OnPropertyChanged(nameof(CanMerge));
        MergeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanModifyFiles))]
    private async Task AddFilesAsync()
    {
        var settings = new OpenFileDialogSettings
        {
            Title = LocalizationManager.Instance.String_TsBinaryMerge_AddFilesTitle,
            AllowMultiple = true,
            Filters = [new(LocalizationManager.Instance.String_TsFiles, ["ts"])]
        };
        var selected = await _dialogService.ShowOpenFilesDialogAsync(this, settings);
        await AddFilesAsync(selected.Select(item => item.LocalPath));
    }

    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        if (IsBusy || _isClosing)
            return;

        var comparer = GetPathComparer();
        var existing = Files.Select(item => item.FilePath).ToHashSet(comparer);
        var candidates = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Where(path => string.Equals(Path.GetExtension(path), ".ts", StringComparison.OrdinalIgnoreCase))
            .Distinct(comparer)
            .Where(path => !existing.Contains(path))
            .ToArray();
        if (candidates.Length == 0)
            return;

        IsBusy = true;
        StatusText = LocalizationManager.Instance.String_TsBinaryMerge_Status_ReadingFiles;
        try
        {
            var loaded = await Task.Run(() => LoadFileItems(candidates));
            var items = loaded.Where(item => item is not null).Select(item => item!).ToArray();
            if (items.Length > 0)
            {
                var wasEmpty = Files.Count == 0;
                Files.AddRange(items);
                UpdateOrderAndSummary();
                InvalidateAnalysis();
                if (wasEmpty && string.IsNullOrWhiteSpace(OutputPath))
                    OutputPath = BuildDefaultOutputPath(items[0].FilePath);
            }
            var skipped = candidates.Length - items.Length;
            StatusText = skipped == 0
                ? string.Format(
                    LocalizationManager.Instance.String_TsBinaryMerge_Status_FilesAdded,
                    items.Length)
                : string.Format(
                    LocalizationManager.Instance.String_TsBinaryMerge_Status_FilesAddedWithSkipped,
                    items.Length,
                    skipped);
        }
        finally
        {
            IsBusy = false;
            NotifyCommandStates();
        }
    }

    private static TsBinaryMergeFileItem?[] LoadFileItems(IReadOnlyList<string> paths)
    {
        var result = new TsBinaryMergeFileItem?[paths.Count];
        Parallel.For(0, paths.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, index =>
        {
            try
            {
                var info = new FileInfo(paths[index]);
                if (info.Exists)
                    result[index] = new TsBinaryMergeFileItem(info.FullName, info.Length);
            }
            catch
            {
                // 单个文件无法读取时跳过，其余拖入文件仍可继续加入列表。
            }
        });
        return result;
    }

    public void SetSelectedFiles(IEnumerable<TsBinaryMergeFileItem> selected)
    {
        _selectedFiles.Clear();
        _selectedFiles.AddRange(selected.Where(Files.Contains));
        NotifyCommandStates();
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void RemoveFiles()
    {
        foreach (var item in _selectedFiles.ToArray())
            Files.Remove(item);
        _selectedFiles.Clear();
        UpdateOrderAndSummary();
        InvalidateAnalysis();
        NotifyCommandStates();
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp()
    {
        var selectedItems = _selectedFiles.ToArray();
        var selected = selectedItems.ToHashSet();
        var reordered = Files.ToList();
        for (var index = 1; index < reordered.Count; index++)
        {
            if (selected.Contains(reordered[index]) && !selected.Contains(reordered[index - 1]))
                (reordered[index - 1], reordered[index]) = (reordered[index], reordered[index - 1]);
        }
        ApplyReorderedFiles(reordered, selectedItems);
    }

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown()
    {
        var selectedItems = _selectedFiles.ToArray();
        var selected = selectedItems.ToHashSet();
        var reordered = Files.ToList();
        for (var index = reordered.Count - 2; index >= 0; index--)
        {
            if (selected.Contains(reordered[index]) && !selected.Contains(reordered[index + 1]))
                (reordered[index], reordered[index + 1]) = (reordered[index + 1], reordered[index]);
        }
        ApplyReorderedFiles(reordered, selectedItems);
    }

    [RelayCommand(CanExecute = nameof(CanModifyFiles))]
    private async Task NaturalSortAsync()
    {
        if (Files.Count < 2)
            return;
        IsBusy = true;
        StatusText = LocalizationManager.Instance.String_TsBinaryMerge_Status_Sorting;
        try
        {
            var selectedItems = _selectedFiles.ToArray();
            var snapshot = Files.ToArray();
            var sorted = await Task.Run(() => snapshot
                .OrderBy(item => item.FileName, NaturalStringComparer.Instance)
                .ThenBy(item => item.FilePath, NaturalStringComparer.Instance)
                .ToArray());
            ApplyReorderedFiles(sorted, selectedItems);
            StatusText = LocalizationManager.Instance.String_TsBinaryMerge_Status_Sorted;
        }
        finally
        {
            IsBusy = false;
            NotifyCommandStates();
        }
    }

    [RelayCommand(CanExecute = nameof(CanModifyFiles))]
    private void Clear()
    {
        Files.Clear();
        _selectedFiles.Clear();
        OutputPath = string.Empty;
        ResetProgressDisplay();
        UpdateOrderAndSummary();
        InvalidateAnalysis();
        StatusText = LocalizationManager.Instance.String_TsBinaryMerge_Status_Ready;
        NotifyCommandStates();
    }

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        var cancellation = BeginOperation();
        var generation = ++_generation;
        _analysis = null;
        OnPropertyChanged(nameof(HasAnalysis));
        OnPropertyChanged(nameof(HasUnmatchedJoins));
        PrepareRowsForAnalysis();
        AnalysisSummaryText = string.Empty;
        StatusText = LocalizationManager.Instance.String_TsBinaryMerge_Status_Analyzing;
        try
        {
            var progress = new Progress<TsBinaryMergeProgress>(value =>
            {
                if (_isClosing || generation != _generation)
                    return;
                ApplyProgress(value);
                if (value.CompletedJoin is not null)
                    ApplyJoin(value.CompletedJoin);
            });
            var paths = Files.Select(item => item.FilePath).ToArray();
            var maximumBytes = checked((long)MaximumSearchMiB * 1024 * 1024);
            var result = await _service.AnalyzeOverlapsAsync(
                paths,
                maximumBytes,
                progress,
                cancellation.Token);
            if (_isClosing || generation != _generation)
                return;
            _analysis = result;
            Percent = 100;
            RefreshAnalysisSummary();
            OnPropertyChanged(nameof(HasAnalysis));
            OnPropertyChanged(nameof(HasUnmatchedJoins));
            OnPropertyChanged(nameof(CanMerge));
            MergeCommand.NotifyCanExecuteChanged();
            StatusText = result.HasUnmatchedJoins
                ? LocalizationManager.Instance.String_TsBinaryMerge_Status_AnalysisWarning
                : LocalizationManager.Instance.String_TsBinaryMerge_Status_AnalysisCompleted;
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
                StatusText = LocalizationManager.Instance.String_TsBinaryMerge_Status_Cancelled;
        }
        catch (Exception exception)
        {
            if (!_isClosing)
                StatusText = string.Format(
                    LocalizationManager.Instance.String_TsBinaryMerge_Status_Failed,
                    FormatError(exception));
        }
        finally
        {
            EndOperation(cancellation);
        }
    }

    [RelayCommand(CanExecute = nameof(CanModifyFiles))]
    private async Task BrowseOutputAsync()
    {
        var firstPath = Files.FirstOrDefault()?.FilePath;
        var settings = new SaveFileDialogSettings
        {
            Title = LocalizationManager.Instance.String_TsBinaryMerge_SaveTitle,
            SuggestedStartLocation = firstPath is not null
                ? new DesktopDialogStorageFolder(Path.GetDirectoryName(firstPath)!)
                : null,
            SuggestedFileName = string.IsNullOrWhiteSpace(OutputPath)
                ? firstPath is null ? "merged.ts" : Path.GetFileName(BuildDefaultOutputPath(firstPath))
                : Path.GetFileName(OutputPath),
            Filters =
            [
                new(LocalizationManager.Instance.String_TsFiles, ["ts"]),
                new(LocalizationManager.Instance.String_AllFiles, "*")
            ],
            DefaultExtension = "ts"
        };
        var selected = await _dialogService.ShowSaveFileDialogAsync(this, settings);
        if (selected?.Path is not null)
            OutputPath = selected.Path.LocalPath;
    }

    [RelayCommand(CanExecute = nameof(CanMerge))]
    private async Task MergeAsync()
    {
        var cancellation = BeginOperation();
        var generation = ++_generation;
        Percent = 0;
        StatusText = LocalizationManager.Instance.String_TsBinaryMerge_Status_Writing;
        try
        {
            var progress = new Progress<TsBinaryMergeProgress>(value =>
            {
                if (_isClosing || generation != _generation)
                    return;
                ApplyProgress(value);
            });
            var result = await _service.MergeAsync(
                Files.Select(item => item.FilePath).ToArray(),
                OutputPath,
                IsOverlapMode ? _analysis : null,
                AppendUnmatchedSources,
                progress,
                cancellation.Token);
            if (_isClosing || generation != _generation)
                return;
            Percent = 100;
            ProgressText = $"{CommonUtil.FormatFileSize(result.OutputBytes)} / " +
                           CommonUtil.FormatFileSize(result.OutputBytes);
            StatusText = IsDirectMode
                ? string.Format(
                    LocalizationManager.Instance.String_TsBinaryMerge_Status_DirectCompleted,
                    result.SourceCount,
                    CommonUtil.FormatFileSize(result.OutputBytes))
                : result.UnmatchedJoinCount == 0
                    ? string.Format(
                        LocalizationManager.Instance.String_TsBinaryMerge_Status_OverlapCompleted,
                        result.SourceCount,
                        CommonUtil.FormatFileSize(result.RemovedOverlapBytes),
                        CommonUtil.FormatFileSize(result.OutputBytes))
                    : string.Format(
                        LocalizationManager.Instance.String_TsBinaryMerge_Status_OverlapCompletedWithFallback,
                        result.SourceCount,
                        CommonUtil.FormatFileSize(result.RemovedOverlapBytes),
                        CommonUtil.FormatFileSize(result.OutputBytes),
                        result.UnmatchedJoinCount);
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
                StatusText = LocalizationManager.Instance.String_TsBinaryMerge_Status_Cancelled;
        }
        catch (Exception exception)
        {
            if (!_isClosing)
                StatusText = string.Format(
                    LocalizationManager.Instance.String_TsBinaryMerge_Status_Failed,
                    FormatError(exception));
        }
        finally
        {
            EndOperation(cancellation);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancellationTokenSource?.Cancel();

    private CancellationTokenSource BeginOperation()
    {
        _cancellationTokenSource?.Dispose();
        var cancellation = new CancellationTokenSource();
        _cancellationTokenSource = cancellation;
        IsBusy = true;
        Percent = 0;
        ProgressText = "-";
        SpeedText = $"{CommonUtil.FormatFileSize(0)}/s";
        return cancellation;
    }

    private void EndOperation(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_cancellationTokenSource, cancellation))
            _cancellationTokenSource = null;
        cancellation.Dispose();
        IsBusy = false;
        NotifyCommandStates();
    }

    private void ResetProgressDisplay()
    {
        Percent = 0;
        ProgressText = "-";
        SpeedText = $"{CommonUtil.FormatFileSize(0)}/s";
    }

    private string GetModeReadyStatus()
    {
        if (Files.Count < 2)
            return LocalizationManager.Instance.String_TsBinaryMerge_Status_Ready;
        return IsDirectMode
            ? LocalizationManager.Instance.String_TsBinaryMerge_Status_DirectReady
            : LocalizationManager.Instance.String_TsBinaryMerge_Status_OverlapReady;
    }

    private void ApplyProgress(TsBinaryMergeProgress value)
    {
        Percent = value.Percent;
        ProgressText = value.Phase == TsBinaryMergeProgressPhase.Writing && value.TotalBytes > 0
            ? $"{CommonUtil.FormatFileSize(value.BytesProcessed)} / {CommonUtil.FormatFileSize(value.TotalBytes)}"
            : CommonUtil.FormatFileSize(value.BytesProcessed);
        SpeedText = $"{CommonUtil.FormatFileSize(value.BytesPerSecond)}/s";
        StatusText = value.Phase switch
        {
            TsBinaryMergeProgressPhase.Validating => string.Format(
                LocalizationManager.Instance.String_TsBinaryMerge_Status_Validating,
                value.CurrentSourceIndex + 1,
                value.SourceCount),
            TsBinaryMergeProgressPhase.Searching => string.Format(
                LocalizationManager.Instance.String_TsBinaryMerge_Status_Searching,
                value.CurrentSourceIndex,
                value.SourceCount - 1),
            TsBinaryMergeProgressPhase.Verifying => string.Format(
                LocalizationManager.Instance.String_TsBinaryMerge_Status_Verifying,
                value.CurrentSourceIndex,
                value.SourceCount - 1),
            _ => string.Format(
                LocalizationManager.Instance.String_TsBinaryMerge_Status_WritingFile,
                value.CurrentSourceIndex + 1,
                value.SourceCount)
        };
    }

    private void PrepareRowsForAnalysis()
    {
        foreach (var item in Files)
        {
            item.OverlapText = "-";
            item.WriteRangeText = "-";
            item.StatusText = IsDirectMode
                ? LocalizationManager.Instance.String_TsBinaryMerge_Row_Ready
                : LocalizationManager.Instance.String_TsBinaryMerge_Row_Pending;
        }
        if (Files.Count > 0)
        {
            Files[0].WriteRangeText = FormatWriteRange(0, Files[0].FileSize);
            Files[0].StatusText = LocalizationManager.Instance.String_TsBinaryMerge_Row_First;
        }
    }

    private void ApplyJoin(TsBinaryMergeJoinAnalysis join)
    {
        if (join.SourceIndex < 1 || join.SourceIndex >= Files.Count)
            return;
        var item = Files[join.SourceIndex];
        if (!join.HasReliableOverlap)
        {
            item.OverlapText = LocalizationManager.Instance.String_TsBinaryMerge_Value_NotFound;
            item.WriteRangeText = FormatWriteRange(0, item.FileSize);
            item.StatusText = LocalizationManager.Instance.String_TsBinaryMerge_Row_Unmatched;
            return;
        }

        item.OverlapText = CommonUtil.FormatFileSize(join.OverlapBytes);
        if (join.IsFullyContained)
        {
            item.WriteRangeText = LocalizationManager.Instance.String_TsBinaryMerge_Value_Skipped;
            item.StatusText = LocalizationManager.Instance.String_TsBinaryMerge_Row_Duplicate;
            return;
        }

        item.WriteRangeText = FormatWriteRange(join.AppendOffset, item.FileSize);
        item.StatusText = string.Format(
            LocalizationManager.Instance.String_TsBinaryMerge_Row_Matched,
            Files[join.PreviousSourceIndex].FileName);
    }

    private string FormatWriteRange(long start, long end) => string.Format(
        LocalizationManager.Instance.String_TsBinaryMerge_Value_WriteRange,
        CommonUtil.FormatFileSize(start),
        CommonUtil.FormatFileSize(end));

    private void RefreshAnalysisSummary()
    {
        var analysis = _analysis;
        if (analysis is null)
        {
            AnalysisSummaryText = string.Empty;
            return;
        }
        var matched = analysis.Joins.Count(item => item.HasReliableOverlap);
        var removed = analysis.Joins.Sum(item => item.AppendOffset);
        AnalysisSummaryText = string.Format(
            LocalizationManager.Instance.String_TsBinaryMerge_AnalysisSummary,
            matched,
            analysis.Joins.Count,
            CommonUtil.FormatFileSize(removed),
            CommonUtil.FormatFileSize(analysis.EstimatedOutputBytes));
    }

    private void RefreshAnalysisRows()
    {
        if (_analysis is null)
            return;
        PrepareRowsForAnalysis();
        foreach (var join in _analysis.Joins)
            ApplyJoin(join);
        RefreshAnalysisSummary();
    }

    private void UpdateOrderAndSummary()
    {
        for (var index = 0; index < Files.Count; index++)
            Files[index].Order = index + 1;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(FileSummaryText));
        OnPropertyChanged(nameof(CanAnalyze));
        OnPropertyChanged(nameof(CanMerge));
    }

    private void ApplyReorderedFiles(
        IReadOnlyList<TsBinaryMergeFileItem> reordered,
        IReadOnlyList<TsBinaryMergeFileItem> selectedItems)
    {
        // DataGrid 对集合 Move 通知不会始终刷新行位置，因此用一次 Reset 明确应用新顺序。
        Files.ReplaceAll(reordered);
        _selectedFiles.Clear();
        _selectedFiles.AddRange(selectedItems.Where(Files.Contains));
        UpdateOrderAndSummary();
        InvalidateAnalysis();
        SelectionRestoreRequested?.Invoke(_selectedFiles.ToArray());
        NotifyCommandStates();
    }

    private void InvalidateAnalysis()
    {
        _analysis = null;
        AnalysisSummaryText = string.Empty;
        foreach (var item in Files)
        {
            item.OverlapText = "-";
            item.WriteRangeText = "-";
            item.StatusText = IsDirectMode
                ? LocalizationManager.Instance.String_TsBinaryMerge_Row_Ready
                : LocalizationManager.Instance.String_TsBinaryMerge_Row_Pending;
        }
        OnPropertyChanged(nameof(HasAnalysis));
        OnPropertyChanged(nameof(HasUnmatchedJoins));
        OnPropertyChanged(nameof(CanMerge));
        NotifyCommandStates();
    }

    private void NotifyCommandStates()
    {
        AddFilesCommand.NotifyCanExecuteChanged();
        RemoveFilesCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        NaturalSortCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
        AnalyzeCommand.NotifyCanExecuteChanged();
        BrowseOutputCommand.NotifyCanExecuteChanged();
        MergeCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private static string BuildDefaultOutputPath(string firstPath) => Path.Combine(
        Path.GetDirectoryName(firstPath)!,
        Path.GetFileNameWithoutExtension(firstPath) + "_merged.ts");

    private static StringComparer GetPathComparer() => OperatingSystem.IsLinux()
        ? StringComparer.Ordinal
        : StringComparer.OrdinalIgnoreCase;

    private static string FormatError(Exception exception)
    {
        if (exception is not TsBinaryMergeException mergeException)
            return exception.Message;
        var strings = LocalizationManager.Instance;
        var format = mergeException.Code switch
        {
            TsBinaryMergeErrorCode.TooFewSources => strings.String_TsBinaryMerge_Error_TooFewSources,
            TsBinaryMergeErrorCode.SourceMissing => strings.String_TsBinaryMerge_Error_SourceMissing,
            TsBinaryMergeErrorCode.SourceChanged => strings.String_TsBinaryMerge_Error_SourceChanged,
            TsBinaryMergeErrorCode.InvalidPacketStructure => strings.String_TsBinaryMerge_Error_InvalidPacketStructure,
            TsBinaryMergeErrorCode.OutputMatchesSource => strings.String_TsBinaryMerge_Error_OutputMatchesSource,
            TsBinaryMergeErrorCode.AnalysisRequired => strings.String_TsBinaryMerge_Error_AnalysisRequired,
            TsBinaryMergeErrorCode.AnalysisSourceMismatch => strings.String_TsBinaryMerge_Error_AnalysisSourceMismatch,
            TsBinaryMergeErrorCode.UnmatchedJoin => strings.String_TsBinaryMerge_Error_UnmatchedJoin,
            _ => exception.Message
        };
        return mergeException.Arguments.Length > 0
            ? string.Format(format, mergeException.Arguments)
            : format;
    }

    public void OnClosed()
    {
        if (_isClosing)
            return;
        _isClosing = true;
        App.LocalizationService.LanguageChanged -= OnLanguageChanged;
        _generation++;
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        Files.Clear();
        _selectedFiles.Clear();
        _analysis = null;
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(FileSummaryText));
        RefreshAnalysisRows();
    }
}
