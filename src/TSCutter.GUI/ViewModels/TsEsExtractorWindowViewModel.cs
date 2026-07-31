using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
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

public partial class TsEsExtractorWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    private const long MaxProbeBytes = 64L * 1024 * 1024;
    private readonly IDialogService _dialogService;
    private readonly TsCheckTextFormatter _text = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private TsCheckResult? _catalog;
    private bool _isClosing;
    private bool _updatingSelection;
    private int _generation;
    private StatusKind _statusKind = StatusKind.Ready;
    private object[] _statusArguments = [];

    public TsEsExtractorWindowViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
        SetStatus(StatusKind.Ready);
        App.LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    public bool? DialogResult { get; }
    public string FilePath { get; set; } = string.Empty;
    public string WindowTitle => $"{LocalizationManager.Instance.String_TsEsExtractor_Title} - {Path.GetFileName(FilePath)}";
    public string FileSizeText => File.Exists(FilePath)
        ? CommonUtil.FormatFileSize(new FileInfo(FilePath).Length)
        : "-";
    public ObservableCollection<TsEsExtractorTrackItem> Tracks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOutput), nameof(CanSelect), nameof(CanCancel))]
    [NotifyCanExecuteChangedFor(nameof(OutputCommand), nameof(CancelCommand), nameof(SelectAllCommand), nameof(ClearAllCommand), nameof(BrowseCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _showOutputProgress;

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
    private int _selectedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOutput))]
    [NotifyCanExecuteChangedFor(nameof(OutputCommand))]
    private string _outputFolder = string.Empty;

    public bool CanOutput => !IsBusy && _catalog is not null && SelectedCount > 0 &&
                             Directory.Exists(OutputFolder) && SelectedPathsAreValid();
    public bool CanSelect => !IsBusy && _catalog is not null;
    public bool CanCancel => IsBusy;
    public bool CanBrowse => !IsBusy;

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
        ShowOutputProgress = false;
        Percent = 0;
        ReleaseRows();
        _catalog = null;
        OutputFolder = Path.GetDirectoryName(FilePath) ?? string.Empty;
        SetStatus(StatusKind.Probing);

        try
        {
            var analyzer = new TsStreamAnalyzer();
            var options = new TsStreamAnalyzeOptions
            {
                InventoryOnly = true,
                IncludeServiceMetadata = true,
                MinimumBytes = TsStreamAnalyzeOptions.StandardProbeBytes,
                MaxBytes = MaxProbeBytes,
                StablePacketCount = 8_192,
                Features = TsStreamAnalyzeFeatures.Bitrate
            };
            var result = await Task.Run(() => analyzer.AnalyzeAsync(
                FilePath, null, cancellationTokenSource.Token, options));
            if (_isClosing || generation != _generation)
                return;
            if (result.WasCancelled)
            {
                SetStatus(StatusKind.ProbeCancelled);
                return;
            }

            _catalog = result;
            BuildTrackRows(result);
            ProbeSummaryText = string.Format(
                LocalizationManager.Instance.String_TsEsExtractor_ProbeSummary,
                CommonUtil.FormatFileSize(result.BytesScanned), Tracks.Count);
            if (Tracks.Count > 0)
                SetStatus(StatusKind.ProbeCompleted, Tracks.Count);
            else
                SetStatus(StatusKind.NoTracks);
        }
        catch (Exception exception)
        {
            if (!_isClosing && generation == _generation)
                SetStatus(StatusKind.Failed, exception.Message);
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

    private void BuildTrackRows(TsCheckResult result)
    {
        var sourceName = Path.GetFileNameWithoutExtension(FilePath);
        var seenPids = new HashSet<int>();
        _updatingSelection = true;
        try
        {
            foreach (var program in result.Programs.Values.OrderBy(item => item.ProgramNumber))
            {
                foreach (var stream in program.Streams.OrderBy(item => item.Key))
                {
                    if (!seenPids.Add(stream.Key))
                        continue;
                    result.Pids.TryGetValue(stream.Key, out var summary);
                    var streamType = summary?.StreamType ?? stream.Value;
                    var extension = TsElementaryStreamUtil.GetFileExtension(
                        streamType, summary?.MpegAudioLayer, summary?.SupplementaryStreamType);
                    var row = new TsEsExtractorTrackItem
                    {
                        Pid = stream.Key,
                        PidText = $"0x{stream.Key:X4}",
                        ProgramText = FormatProgram(result, program.ProgramNumber),
                        StreamText = _text.FormatStreamType(
                            streamType, summary?.MpegAudioLayer, summary?.SupplementaryStreamType),
                        LanguageText = string.IsNullOrWhiteSpace(summary?.Language) ? "-" : summary.Language,
                        BitrateText = CommonUtil.FormatOptionalBitrate(summary?.Bitrate ?? 0),
                        StreamType = streamType,
                        MpegAudioLayer = summary?.MpegAudioLayer,
                        SupplementaryStreamType = summary?.SupplementaryStreamType,
                        OutputFileName = BuildOutputFileName(sourceName, stream.Key, extension)
                    };
                    row.SelectionChanged += OnSelectionChanged;
                    Tracks.Add(row);
                }
            }
        }
        finally
        {
            _updatingSelection = false;
        }
        RecalculateSelection();
    }

    private static string BuildOutputFileName(string sourceName, int pid, string extension)
    {
        var suffix = $"_0x{pid:X4}{extension}";
        const int suggestedNameByteLimit = 220;
        var sourceByteLimit = suggestedNameByteLimit - Encoding.UTF8.GetByteCount(suffix);
        var length = sourceName.Length;
        while (length > 1 && Encoding.UTF8.GetByteCount(sourceName.AsSpan(0, length)) > sourceByteLimit)
        {
            length--;
            if (length > 0 && char.IsHighSurrogate(sourceName[length - 1]))
                length--;
        }
        if (length < sourceName.Length)
            sourceName = sourceName[..length];
        return sourceName + suffix;
    }

    private static string FormatProgram(TsCheckResult result, int programNumber)
    {
        if (result.Services.TryGetValue(programNumber, out var service) &&
            !string.IsNullOrWhiteSpace(service.ServiceName))
            return $"{programNumber} - {service.ServiceName}";
        return programNumber.ToString();
    }

    private void OnSelectionChanged()
    {
        if (!_updatingSelection)
            RecalculateSelection();
    }

    private void RecalculateSelection()
    {
        SelectedCount = Tracks.Count(item => item.IsSelected);
        OnPropertyChanged(nameof(CanOutput));
        OutputCommand.NotifyCanExecuteChanged();
    }

    private bool SelectedPathsAreValid()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in Tracks)
        {
            if (!track.IsSelected)
                continue;
            var name = track.OutputFileName.Trim();
            if (name.Length == 0 || name != Path.GetFileName(name) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !names.Add(name))
                return false;
        }
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanSelect))]
    private void SelectAll()
    {
        _updatingSelection = true;
        foreach (var track in Tracks)
            track.IsSelected = true;
        _updatingSelection = false;
        RecalculateSelection();
    }

    [RelayCommand(CanExecute = nameof(CanSelect))]
    private void ClearAll()
    {
        _updatingSelection = true;
        foreach (var track in Tracks)
            track.IsSelected = false;
        _updatingSelection = false;
        RecalculateSelection();
    }

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task BrowseAsync()
    {
        if (IsBusy)
            return;
        var settings = new OpenFolderDialogSettings
        {
            Title = LocalizationManager.Instance.String_TsEsExtractor_SelectFolder,
            SuggestedStartLocation = Directory.Exists(OutputFolder)
                ? new DesktopDialogStorageFolder(OutputFolder)
                : null
        };
        var selected = await _dialogService.ShowOpenFolderDialogAsync(this, settings);
        if (selected?.Path is not null)
            OutputFolder = selected.Path.LocalPath;
    }

    [RelayCommand(CanExecute = nameof(CanOutput))]
    private async Task OutputAsync()
    {
        var catalog = _catalog;
        if (catalog is null || IsBusy)
            return;

        var outputs = Tracks.Where(item => item.IsSelected)
            .Select(item => new TsEsExtractionOutput
            {
                Pid = item.Pid,
                OutputPath = Path.Combine(OutputFolder, item.OutputFileName.Trim())
            })
            .ToArray();
        var cancellationTokenSource = new CancellationTokenSource();
        _cancellationTokenSource = cancellationTokenSource;
        var generation = ++_generation;
        IsBusy = true;
        ShowOutputProgress = true;
        Percent = 0;
        ProgressText = $"{CommonUtil.FormatFileSize(0)} / {CommonUtil.FormatFileSize(catalog.FileSize)}";
        SpeedText = $"{CommonUtil.FormatFileSize(0)}/s";
        SetStatus(StatusKind.Extracting);

        try
        {
            var extractionProgress = new Progress<TsEsExtractionProgress>(value =>
            {
                if (_isClosing || generation != _generation)
                    return;
                Percent = value.Percent;
                ProgressText = $"{CommonUtil.FormatFileSize(value.BytesProcessed)} / {CommonUtil.FormatFileSize(value.FileSize)}";
                SpeedText = $"{CommonUtil.FormatFileSize(value.BytesPerSecond)}/s";
            });
            var service = new TsEsExtractorService();
            var result = await Task.Run(() => service.ExtractAsync(
                FilePath, outputs, catalog.SyncOffset, extractionProgress, cancellationTokenSource.Token));
            if (_isClosing || generation != _generation)
                return;
            Percent = 100;
            if (result.AnomalyCount == 0)
            {
                SetStatus(StatusKind.Completed,
                    result.Tracks.Length, CommonUtil.FormatFileSize(result.BytesWritten));
            }
            else
            {
                SetStatus(StatusKind.CompletedWithAnomalies,
                    result.Tracks.Length, CommonUtil.FormatFileSize(result.BytesWritten), result.AnomalyCount);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
                SetStatus(StatusKind.Cancelled);
        }
        catch (TsEsExtractionException exception)
        {
            if (!_isClosing)
                SetStatus(StatusKind.Failed, exception);
        }
        catch (Exception exception)
        {
            if (!_isClosing)
                SetStatus(StatusKind.Failed, exception.Message);
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

    private static string FormatExtractionError(TsEsExtractionException exception)
    {
        var strings = LocalizationManager.Instance;
        var format = exception.Code switch
        {
            TsEsExtractionErrorCode.NoOutputs => strings.String_TsEsExtractor_Error_NoOutputs,
            TsEsExtractionErrorCode.DuplicatePid => strings.String_TsEsExtractor_Error_DuplicatePid,
            TsEsExtractionErrorCode.InvalidOutputPath => strings.String_TsEsExtractor_Error_InvalidOutputPath,
            TsEsExtractionErrorCode.DuplicateOutputPath => strings.String_TsEsExtractor_Error_DuplicateOutputPath,
            TsEsExtractionErrorCode.OutputExists => strings.String_TsEsExtractor_Error_OutputExists,
            TsEsExtractionErrorCode.SameAsSource => strings.String_TsEsExtractor_Error_SameAsSource,
            _ => strings.String_TsEsExtractor_Status_Failed
        };
        return string.Format(format, exception.Arguments);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancellationTokenSource?.Cancel();

    private void SetStatus(StatusKind kind, params object[] arguments)
    {
        _statusKind = kind;
        _statusArguments = arguments;
        var strings = LocalizationManager.Instance;
        var format = kind switch
        {
            StatusKind.Ready => strings.String_TsEsExtractor_Status_Ready,
            StatusKind.Probing => strings.String_TsEsExtractor_Status_Probing,
            StatusKind.ProbeCancelled => strings.String_TsEsExtractor_Status_ProbeCancelled,
            StatusKind.ProbeCompleted => strings.String_TsEsExtractor_Status_ProbeCompleted,
            StatusKind.NoTracks => strings.String_TsEsExtractor_Status_NoTracks,
            StatusKind.Extracting => strings.String_TsEsExtractor_Status_Extracting,
            StatusKind.Completed => strings.String_TsEsExtractor_Status_Completed,
            StatusKind.CompletedWithAnomalies => strings.String_TsEsExtractor_Status_CompletedWithAnomalies,
            StatusKind.Cancelled => strings.String_TsEsExtractor_Status_Cancelled,
            _ => strings.String_TsEsExtractor_Status_Failed
        };
        StatusText = kind == StatusKind.Failed && arguments is [TsEsExtractionException extractionException]
            ? string.Format(format, FormatExtractionError(extractionException))
            : arguments.Length == 0
                ? format
                : string.Format(format, arguments);
    }

    private void NotifyCommandStates()
    {
        OutputCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        ClearAllCommand.NotifyCanExecuteChanged();
        BrowseCommand.NotifyCanExecuteChanged();
    }

    public void OnClosing(CancelEventArgs eventArgs)
    {
        if (!IsBusy)
            return;
        Cancel();
        eventArgs.Cancel = true;
    }

    public void OnClosed()
    {
        if (_isClosing)
            return;
        _isClosing = true;
        App.LocalizationService.LanguageChanged -= OnLanguageChanged;
        _generation++;
        _cancellationTokenSource?.Cancel();
        ReleaseRows();
        _catalog = null;
    }

    private void ReleaseRows()
    {
        foreach (var track in Tracks)
            track.SelectionChanged -= OnSelectionChanged;
        Tracks.Clear();
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(WindowTitle));
        SetStatus(_statusKind, _statusArguments);
        if (_catalog is not null)
        {
            foreach (var track in Tracks)
            {
                track.StreamText = _text.FormatStreamType(
                    track.StreamType, track.MpegAudioLayer, track.SupplementaryStreamType);
            }
            ProbeSummaryText = string.Format(
                LocalizationManager.Instance.String_TsEsExtractor_ProbeSummary,
                CommonUtil.FormatFileSize(_catalog.BytesScanned), Tracks.Count);
        }
    }

    private enum StatusKind
    {
        Ready,
        Probing,
        ProbeCancelled,
        ProbeCompleted,
        NoTracks,
        Extracting,
        Completed,
        CompletedWithAnomalies,
        Cancelled,
        Failed
    }
}
