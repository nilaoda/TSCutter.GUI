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

public partial class TsServiceFilterWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    private const long MaxProbeBytes = 64L * 1024 * 1024;
    private readonly IDialogService _dialogService;
    private readonly TsCheckTextFormatter _text = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private TsCheckResult? _catalog;
    private bool _isClosing;
    private bool _updatingSelection;
    private int _generation;

    public TsServiceFilterWindowViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
        StatusText = LocalizationManager.Instance.String_TsServiceFilter_Status_Ready;
    }

    public bool? DialogResult { get; }
    public string FilePath { get; set; } = string.Empty;
    public ObservableCollection<TsServiceFilterItem> Services { get; } = [];
    public string FileSizeText => File.Exists(FilePath)
        ? CommonUtil.FormatFileSize(new FileInfo(FilePath).Length)
        : "-";

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

    public bool CanOutput => !IsBusy && _catalog is not null && SelectedCount > 0 && Directory.Exists(OutputFolder);
    public bool CanSelect => !IsBusy && _catalog is not null;
    public bool CanCancel => IsBusy;

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
        Services.Clear();
        _catalog = null;
        OutputFolder = Path.GetDirectoryName(FilePath) ?? string.Empty;
        StatusText = LocalizationManager.Instance.String_TsServiceFilter_Status_Probing;

        try
        {
            var analyzer = new TsStreamAnalyzer();
            var options = new TsStreamAnalyzeOptions
            {
                InventoryOnly = true,
                IncludeServiceMetadata = true,
                MaxBytes = MaxProbeBytes,
                StablePacketCount = 8_192
            };
            var result = await Task.Run(() => analyzer.AnalyzeAsync(
                FilePath, null, cancellationTokenSource.Token, options));
            if (_isClosing || generation != _generation)
                return;
            if (result.WasCancelled)
            {
                StatusText = LocalizationManager.Instance.String_TsServiceFilter_Status_ProbeCancelled;
                return;
            }

            _catalog = result;
            BuildServiceRows(result);
            ProbeSummaryText = string.Format(
                LocalizationManager.Instance.String_TsServiceFilter_ProbeSummary,
                CommonUtil.FormatFileSize(result.BytesScanned), Services.Count);
            StatusText = Services.Count > 0
                ? string.Format(LocalizationManager.Instance.String_TsServiceFilter_Status_ProbeCompleted, Services.Count)
                : LocalizationManager.Instance.String_TsServiceFilter_Status_NoServices;
        }
        catch (Exception exception)
        {
            if (!_isClosing && generation == _generation)
                StatusText = string.Format(LocalizationManager.Instance.String_TsServiceFilter_Status_Failed, exception.Message);
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

    private void BuildServiceRows(TsCheckResult result)
    {
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceName = Path.GetFileNameWithoutExtension(FilePath);
        var serviceIds = result.Programs.Keys.Concat(result.Services.Keys).Distinct().OrderBy(item => item);
        foreach (var serviceId in serviceIds)
        {
            result.Programs.TryGetValue(serviceId, out var program);
            result.Services.TryGetValue(serviceId, out var service);
            var canExtract = program is not null && program.StreamDefinitions.Count > 0;
            var displayName = string.IsNullOrWhiteSpace(service?.ServiceName)
                ? $"Service_{serviceId}"
                : service.ServiceName.Trim();
            var fileStem = SanitizeFileName($"{sourceName}_{displayName}");
            if (string.IsNullOrWhiteSpace(fileStem))
                fileStem = $"{sourceName}_Service_{serviceId}";
            var uniqueStem = fileStem;
            var suffix = 2;
            while (!usedNames.Add(uniqueStem + ".ts"))
                uniqueStem = $"{fileStem}_{suffix++}";

            var trackSummary = canExtract
                ? string.Join(" / ", program!.Streams.Keys
                    .OrderBy(pid => pid)
                    .Select(pid => result.Pids.TryGetValue(pid, out var summary)
                        ? _text.FormatPidDescription(
                            pid, summary.ProgramNumber, summary.StreamType, summary.MpegAudioLayer,
                            summary.SupplementaryStreamType, summary.Language,
                            summary.IsPcrPid, summary.IsPmtPid)
                        : $"PID 0x{pid:X4}"))
                : LocalizationManager.Instance.String_TsServiceFilter_Tracks_Unavailable;
            var row = new TsServiceFilterItem
            {
                ServiceId = serviceId,
                ServiceIdText = $"{serviceId} (0x{serviceId:X})",
                ServiceName = displayName,
                ProviderName = string.IsNullOrWhiteSpace(service?.ProviderName) ? "-" : service.ProviderName,
                ServiceTypeText = FormatServiceType(service?.ServiceType ?? 0),
                PmtPidText = program is null ? "-" : $"0x{program.PmtPid:X4}",
                TrackSummary = trackSummary,
                CanExtract = canExtract,
                OutputFileName = uniqueStem + ".ts"
            };
            row.SelectionChanged += OnSelectionChanged;
            Services.Add(row);
        }
        UpdateSelectedCount();
    }

    private static string FormatServiceType(byte type)
    {
        var strings = LocalizationManager.Instance;
        return type switch
        {
            0x01 or 0x11 or 0x16 or 0x19 or 0x1F => strings.String_TsServiceFilter_Type_Television,
            0x02 or 0x0A => strings.String_TsServiceFilter_Type_Radio,
            0x0C => strings.String_TsServiceFilter_Type_Data,
            _ => string.Format(strings.String_TsServiceFilter_Type_Unknown, type)
        };
    }

    private void OnSelectionChanged()
    {
        if (!_updatingSelection)
            UpdateSelectedCount();
    }

    private void UpdateSelectedCount()
    {
        SelectedCount = Services.Count(item => item.IsSelected);
        OnPropertyChanged(nameof(CanOutput));
        OutputCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSelect))]
    private void SelectAll()
    {
        _updatingSelection = true;
        foreach (var row in Services)
            row.IsSelected = row.CanExtract;
        _updatingSelection = false;
        UpdateSelectedCount();
    }

    [RelayCommand(CanExecute = nameof(CanSelect))]
    private void ClearAll()
    {
        _updatingSelection = true;
        foreach (var row in Services)
            row.IsSelected = false;
        _updatingSelection = false;
        UpdateSelectedCount();
    }

    [RelayCommand(CanExecute = nameof(CanSelect))]
    private async Task BrowseAsync()
    {
        var settings = new OpenFolderDialogSettings
        {
            Title = LocalizationManager.Instance.String_TsServiceFilter_SelectFolder,
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
        if (!TryBuildOutputs(out var outputs, out var validationError))
        {
            StatusText = validationError;
            return;
        }

        var cancellationTokenSource = new CancellationTokenSource();
        _cancellationTokenSource = cancellationTokenSource;
        var generation = ++_generation;
        IsBusy = true;
        ShowOutputProgress = true;
        Percent = 0;
        StatusText = string.Format(
            LocalizationManager.Instance.String_TsServiceFilter_Status_Filtering, outputs.Count);
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
            var service = new TsServiceFilterService();
            var result = await Task.Run(() => service.FilterAsync(
                FilePath, catalog, outputs, progress, cancellationTokenSource.Token));
            if (_isClosing || generation != _generation)
                return;
            Percent = 100;
            StatusText = string.Format(
                LocalizationManager.Instance.String_TsServiceFilter_Status_Completed,
                result.OutputCount, CommonUtil.FormatFileSize(result.BytesWritten));
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
                StatusText = LocalizationManager.Instance.String_TsServiceFilter_Status_Cancelled;
        }
        catch (Exception exception)
        {
            if (!_isClosing)
            {
                var message = exception is TsFilterException filterException
                    ? FormatFilterError(filterException)
                    : exception.Message;
                StatusText = string.Format(LocalizationManager.Instance.String_TsServiceFilter_Status_Failed, message);
            }
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

    private bool TryBuildOutputs(out List<TsServiceFilterOutput> outputs, out string error)
    {
        outputs = [];
        error = string.Empty;
        if (!Directory.Exists(OutputFolder))
        {
            error = LocalizationManager.Instance.String_TsServiceFilter_Error_OutputFolder;
            return false;
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Services.Where(item => item.IsSelected))
        {
            var fileName = row.OutputFileName.Trim();
            if (string.IsNullOrWhiteSpace(fileName) || fileName != Path.GetFileName(fileName) ||
                fileName.IndexOfAny(InvalidFileNameCharacters) >= 0 ||
                fileName != fileName.TrimEnd('.', ' ') || IsReservedFileName(fileName))
            {
                error = string.Format(LocalizationManager.Instance.String_TsServiceFilter_Error_FileName, row.ServiceName);
                return false;
            }
            if (!string.Equals(Path.GetExtension(fileName), ".ts", StringComparison.OrdinalIgnoreCase))
                fileName += ".ts";
            var outputPath = Path.GetFullPath(Path.Combine(OutputFolder, fileName));
            if (!paths.Add(outputPath))
            {
                error = LocalizationManager.Instance.String_TsServiceFilter_Error_DuplicateFileName;
                return false;
            }
            if (File.Exists(outputPath))
            {
                error = string.Format(LocalizationManager.Instance.String_TsServiceFilter_Error_OutputExists, fileName);
                return false;
            }
            outputs.Add(new TsServiceFilterOutput { ServiceId = row.ServiceId, OutputPath = outputPath });
        }
        return outputs.Count > 0;
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
            TsFilterErrorCode.SdtTooLarge => strings.String_TsServiceFilter_Error_SdtTooLarge,
            TsFilterErrorCode.DuplicateOutputPath => strings.String_TsServiceFilter_Error_DuplicateFileName,
            TsFilterErrorCode.MissingProgram => strings.String_TsServiceFilter_Error_MissingProgram,
            TsFilterErrorCode.OutputExists => strings.String_TsServiceFilter_Error_OutputExists,
            _ => strings.String_TsServiceFilter_Status_Failed
        };
        return string.Format(format, exception.Arguments);
    }

    private static readonly char[] InvalidFileNameCharacters =
        Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*".ToCharArray()).Distinct().ToArray();

    private static string SanitizeFileName(string value)
    {
        var invalid = InvalidFileNameCharacters.ToHashSet();
        var characters = value.Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character)
            .ToArray();
        return new string(characters).Trim().TrimEnd('.', ' ');
    }

    private static bool IsReservedFileName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).TrimEnd('.', ' ');
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                    stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               stem[3] is >= '1' and <= '9';
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancellationTokenSource?.Cancel();

    private void NotifyCommandStates()
    {
        OutputCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        ClearAllCommand.NotifyCanExecuteChanged();
        BrowseCommand.NotifyCanExecuteChanged();
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
        foreach (var row in Services)
            row.SelectionChanged -= OnSelectionChanged;
        Services.Clear();
        _catalog = null;
    }
}
