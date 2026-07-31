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

public partial class TsRemuxWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    private const long MaxProbeBytes = 64L * 1024 * 1024;
    private readonly IDialogService _dialogService;
    private readonly TsCheckTextFormatter _text = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private TsCheckResult? _catalog;
    private bool _isClosing;
    private bool _updatingSelection;
    private int _generation;
    private static readonly string[] LanguageCodes =
    [
        "", "und", "chi", "eng", "jpn", "kor", "fre", "ger", "spa", "ita"
    ];

    public TsRemuxWindowViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
        BuildServiceTypeOptions();
        BuildLanguageOptions();
        StatusText = LocalizationManager.Instance.String_TsRemux_Status_Ready;
        App.LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    public bool? DialogResult { get; }
    public string FilePath { get; set; } = string.Empty;
    public string WindowTitle => string.IsNullOrEmpty(FilePath)
        ? LocalizationManager.Instance.String_TsRemux_Title
        : $"{LocalizationManager.Instance.String_TsRemux_Title} - {Path.GetFileName(FilePath)}";
    public string FileSizeText => File.Exists(FilePath)
        ? CommonUtil.FormatFileSize(new FileInfo(FilePath).Length)
        : "-";
    public ObservableCollection<TsRemuxServiceItem> Services { get; } = [];
    public ObservableCollection<TsRemuxServiceTypeOption> ServiceTypeOptions { get; } = [];
    public ObservableCollection<TsRemuxLanguageOption> LanguageOptions { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOutput), nameof(CanEdit), nameof(CanCancel), nameof(CanEditPids), nameof(CanMoveTrackUp), nameof(CanMoveTrackDown))]
    [NotifyCanExecuteChangedFor(nameof(OutputCommand), nameof(CancelCommand), nameof(SelectAllTracksCommand), nameof(ClearAllTracksCommand), nameof(MoveTrackUpCommand), nameof(MoveTrackDownCommand), nameof(AutoAssignPidsCommand), nameof(RestoreSourcePidsCommand))]
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
    [NotifyPropertyChangedFor(nameof(HasSelectedService), nameof(CanOutput), nameof(CanMoveTrackUp), nameof(CanMoveTrackDown))]
    [NotifyCanExecuteChangedFor(nameof(OutputCommand), nameof(SelectAllTracksCommand), nameof(ClearAllTracksCommand), nameof(MoveTrackUpCommand), nameof(MoveTrackDownCommand), nameof(AutoAssignPidsCommand), nameof(RestoreSourcePidsCommand))]
    private TsRemuxServiceItem? _selectedService;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMoveTrackUp), nameof(CanMoveTrackDown))]
    [NotifyCanExecuteChangedFor(nameof(MoveTrackUpCommand), nameof(MoveTrackDownCommand))]
    private TsRemuxTrackItem? _selectedTrack;

    [ObservableProperty]
    private bool _keepEpg = true;

    [ObservableProperty]
    private bool _preservePacketCount;

    public bool CanOutput => !IsBusy && _catalog is not null &&
                             Services.Any(item => item.IsSelected) &&
                             Services.Where(item => item.IsSelected).All(item =>
                                 item.Tracks.Any(track => !track.IsPcrOnly && track.IsSelected));
    public bool CanEdit => !IsBusy && _catalog is not null;
    public bool CanCancel => IsBusy;
    public bool HasSelectedService => SelectedService is not null;
    public bool CanEditPids => !IsBusy && _catalog is not null && Services.Count > 0;
    public bool CanMoveTrackUp => CanMoveTrack(-1);
    public bool CanMoveTrackDown => CanMoveTrack(1);
    public string ChangeSummaryText => FormatChangeSummary(BuildChangeSummary());

    partial void OnSelectedServiceChanged(TsRemuxServiceItem? value) => SelectedTrack = null;

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
        UnsubscribeRows();
        Services.Clear();
        SelectedService = null;
        _catalog = null;
        StatusText = LocalizationManager.Instance.String_TsRemux_Status_Probing;

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
                StatusText = LocalizationManager.Instance.String_TsRemux_Status_ProbeCancelled;
                return;
            }

            _catalog = result;
            BuildRows(result);
            ProbeSummaryText = string.Format(
                LocalizationManager.Instance.String_TsRemux_ProbeSummary,
                CommonUtil.FormatFileSize(result.BytesScanned), Services.Count,
                Services.Sum(item => item.Tracks.Count(track => !track.IsPcrOnly)));
            StatusText = Services.Count > 0
                ? LocalizationManager.Instance.String_TsRemux_Status_ProbeCompleted
                : LocalizationManager.Instance.String_TsRemux_Status_NoServices;
        }
        catch (Exception exception)
        {
            if (!_isClosing && generation == _generation)
                StatusText = string.Format(LocalizationManager.Instance.String_TsRemux_Status_Failed, exception.Message);
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

    private void BuildRows(TsCheckResult result)
    {
        UnsubscribeRows();
        _updatingSelection = true;
        try
        {
            foreach (var program in result.Programs.Values.OrderBy(item => item.ProgramNumber))
            {
                result.Services.TryGetValue(program.ProgramNumber, out var service);
                var tracks = new List<TsRemuxTrackItem>();
                var order = 0;
                // Dictionary 保留 PMT 解析时的插入顺序，这里不能按 PID 排序，否则未编辑也会改变轨道顺序。
                foreach (var definition in program.StreamDefinitions)
                {
                    result.Pids.TryGetValue(definition.Key, out var summary);
                    var track = new TsRemuxTrackItem
                    {
                        SourcePid = definition.Key,
                        SourcePidText = $"0x{definition.Key:X4}",
                        OutputPidText = $"0x{definition.Key:X4}",
                        StreamText = FormatTrackText(result, definition.Key),
                        TypeText = FormatTrackType(result, definition.Key),
                        RoleText = FormatTrackRole(result, definition.Key, definition.Value,
                            definition.Key == program.PcrPid),
                        BitrateText = CommonUtil.FormatOptionalBitrate(summary?.Bitrate ?? 0),
                        OriginalLanguageCode = summary?.Language ?? string.Empty,
                        SelectedLanguage = FindOrAddLanguageOption(summary?.Language),
                        IsPcrSource = definition.Key == program.PcrPid,
                        IsPcrOnly = false,
                        Definition = definition.Value,
                        OriginalOrder = order++,
                        LanguageOptions = LanguageOptions
                    };
                    track.SelectionChanged += OnSelectionChanged;
                    tracks.Add(track);
                }
                // PCR PID 可能不在 PMT 的 ES 列表中，也可能正好位于用户准备删除的轨道上。
                // 单独保留这条映射，输出计划才能在必要时生成不含媒体负载的纯 PCR 包。
                if (program.PcrPid >= 0 && tracks.All(item => item.SourcePid != program.PcrPid))
                {
                    var track = new TsRemuxTrackItem
                    {
                        SourcePid = program.PcrPid,
                        SourcePidText = $"0x{program.PcrPid:X4}",
                        OutputPidText = $"0x{program.PcrPid:X4}",
                        StreamText = LocalizationManager.Instance.String_TsRemux_Track_PcrOnly,
                        TypeText = "-",
                        RoleText = LocalizationManager.Instance.String_TsRemux_Role_PcrClock,
                        BitrateText = CommonUtil.FormatOptionalBitrate(
                            result.Pids.TryGetValue(program.PcrPid, out var pcrSummary) ? pcrSummary.Bitrate : 0),
                        OriginalLanguageCode = string.Empty,
                        IsPcrSource = true,
                        IsPcrOnly = true,
                        Definition = null,
                        OriginalOrder = order,
                        LanguageOptions = LanguageOptions
                    };
                    track.SelectionChanged += OnSelectionChanged;
                    tracks.Add(track);
                }

                // 只有实际读取到 service_descriptor 中的字段才默认启用写入；缺失字段保持缺失，
                // 用户主动勾选后才会在新的 SDT 中创建对应值。
                var hasName = !string.IsNullOrWhiteSpace(service?.ServiceName);
                var hasProvider = !string.IsNullOrWhiteSpace(service?.ProviderName);
                var row = new TsRemuxServiceItem
                {
                    SourceServiceId = program.ProgramNumber,
                    SourceServiceIdText = $"{program.ProgramNumber} (0x{program.ProgramNumber:X})",
                    OutputServiceIdText = program.ProgramNumber.ToString(),
                    SourcePmtPidText = $"0x{program.PmtPid:X4}",
                    OutputPmtPidText = $"0x{program.PmtPid:X4}",
                    SelectedServiceType = service is not null && service.ServiceType > 0
                        ? FindServiceTypeOption(service.ServiceType)
                        : null,
                    OriginalServiceName = hasName ? service!.ServiceName : "-",
                    OriginalProviderName = hasProvider ? service!.ProviderName : "-",
                    ServiceName = service?.ServiceName ?? string.Empty,
                    ProviderName = service?.ProviderName ?? string.Empty,
                    WriteServiceName = hasName,
                    WriteProviderName = hasProvider,
                    TrackSummary = string.Format(
                        LocalizationManager.Instance.String_TsRemux_TrackCount,
                        tracks.Count(item => !item.IsPcrOnly)),
                    IsEncrypted = program.StreamDefinitions.Keys.Any(pid =>
                        result.Pids.TryGetValue(pid, out var pidSummary) &&
                        pidSummary.ScrambledPayloadPacketCount > 0),
                    SourceService = service,
                    Tracks = new ObservableCollection<TsRemuxTrackItem>(tracks)
                };
                row.SelectionChanged += OnSelectionChanged;
                Services.Add(row);
            }
        }
        finally
        {
            _updatingSelection = false;
        }
        SelectedService = Services.FirstOrDefault();
        NotifySelectionState();
    }

    private string FormatTrackText(TsCheckResult result, int pid)
    {
        if (!result.Pids.TryGetValue(pid, out var summary))
            return $"PID 0x{pid:X4}";
        return _text.FormatPidDescription(
            pid, summary.ProgramNumber, summary.StreamType, summary.MpegAudioLayer,
            summary.SupplementaryStreamType, summary.Language,
            summary.IsPcrPid, summary.IsPmtPid);
    }

    private string FormatTrackType(TsCheckResult result, int pid)
    {
        if (!result.Pids.TryGetValue(pid, out var summary))
            return LocalizationManager.Instance.String_TsCheck_Stream_Unknown;
        return _text.FormatStreamType(
            summary.StreamType, summary.MpegAudioLayer, summary.SupplementaryStreamType);
    }

    private static string FormatTrackRole(
        TsCheckResult result,
        int pid,
        TsStreamDefinition definition,
        bool isPcrSource)
    {
        // 角色只表达轨道用途，编码、语言和 PCR 载体状态分别展示，避免信息挤在同一列。
        result.Pids.TryGetValue(pid, out var summary);
        var supplementary = summary?.SupplementaryStreamType;
        var role = supplementary is
                       TsSupplementaryStreamType.DvbSubtitle or TsSupplementaryStreamType.DvbTeletext or
                       TsSupplementaryStreamType.AribCaption ||
                   definition.StreamType is TsStreamTypes.HdmvPgsSubtitle or TsStreamTypes.HdmvTextSubtitle
            ? LocalizationManager.Instance.String_TsRemux_Role_Subtitle
            : TsStreamTypes.IsVideo(definition.StreamType)
                ? LocalizationManager.Instance.String_TsRemux_Role_Video
                : TsStreamTypes.IsAudio(definition.StreamType, supplementary)
                    ? LocalizationManager.Instance.String_TsRemux_Role_Audio
                    : LocalizationManager.Instance.String_TsRemux_Role_Data;
        return isPcrSource
            ? string.Format(LocalizationManager.Instance.String_TsRemux_Role_WithPcr, role)
            : role;
    }

    private void OnSelectionChanged()
    {
        if (!_updatingSelection)
            NotifySelectionState();
    }

    private void NotifySelectionState()
    {
        OnPropertyChanged(nameof(CanOutput));
        OnPropertyChanged(nameof(CanEditPids));
        OnPropertyChanged(nameof(CanMoveTrackUp));
        OnPropertyChanged(nameof(CanMoveTrackDown));
        OnPropertyChanged(nameof(ChangeSummaryText));
        OutputCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEditPids))]
    private void AutoAssignPids()
    {
        if (IsBusy || _catalog is null)
            return;

        var used = new HashSet<int>();
        var assignedSourcePids = new Dictionary<int, int>();
        foreach (var service in Services)
        {
            if (!TsPidAllocator.TryTakeNext(used, out var pmtPid))
                return;
            service.OutputPmtPidText = $"0x{pmtPid:X4}";
            foreach (var track in service.Tracks)
            {
                if (!assignedSourcePids.TryGetValue(track.SourcePid, out var outputPid))
                {
                    if (!TsPidAllocator.TryTakeNext(used, out outputPid))
                        return;
                    assignedSourcePids.Add(track.SourcePid, outputPid);
                }
                track.OutputPidText = $"0x{outputPid:X4}";
            }
        }
        StatusText = LocalizationManager.Instance.String_TsRemux_Status_PidsAssigned;
        NotifySelectionState();
    }

    [RelayCommand(CanExecute = nameof(CanEditPids))]
    private void RestoreSourcePids()
    {
        if (IsBusy || _catalog is null)
            return;

        foreach (var service in Services)
        {
            service.OutputPmtPidText = service.SourcePmtPidText;
            foreach (var track in service.Tracks)
                track.OutputPidText = track.SourcePidText;
        }
        StatusText = LocalizationManager.Instance.String_TsRemux_Status_PidsRestored;
        NotifySelectionState();
    }

    private void UnsubscribeRows()
    {
        foreach (var service in Services)
        {
            service.SelectionChanged -= OnSelectionChanged;
            foreach (var track in service.Tracks)
                track.SelectionChanged -= OnSelectionChanged;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelectedService))]
    private void SelectAllTracks()
    {
        if (SelectedService is null || IsBusy)
            return;
        _updatingSelection = true;
        try
        {
            foreach (var track in SelectedService.Tracks.Where(item => !item.IsPcrOnly))
                track.IsSelected = true;
        }
        finally
        {
            _updatingSelection = false;
        }
        NotifySelectionState();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedService))]
    private void ClearAllTracks()
    {
        if (SelectedService is null || IsBusy)
            return;
        _updatingSelection = true;
        try
        {
            foreach (var track in SelectedService.Tracks.Where(item => !item.IsPcrOnly))
                track.IsSelected = false;
        }
        finally
        {
            _updatingSelection = false;
        }
        NotifySelectionState();
    }

    [RelayCommand(CanExecute = nameof(CanMoveTrackUp))]
    private void MoveTrackUp()
    {
        if (SelectedService is null || SelectedTrack is null || SelectedTrack.IsPcrOnly || IsBusy)
            return;
        var index = SelectedService.Tracks.IndexOf(SelectedTrack);
        if (index <= 0)
            return;
        SelectedService.Tracks.Move(index, index - 1);
        NotifySelectionState();
    }

    [RelayCommand(CanExecute = nameof(CanMoveTrackDown))]
    private void MoveTrackDown()
    {
        if (SelectedService is null || SelectedTrack is null || SelectedTrack.IsPcrOnly || IsBusy)
            return;
        var index = SelectedService.Tracks.IndexOf(SelectedTrack);
        if (index < 0 || index >= SelectedService.Tracks.Count - 1)
            return;
        SelectedService.Tracks.Move(index, index + 1);
        NotifySelectionState();
    }

    private bool CanMoveTrack(int delta)
    {
        if (IsBusy || SelectedService is null || SelectedTrack is null || SelectedTrack.IsPcrOnly)
            return false;
        var index = SelectedService.Tracks.IndexOf(SelectedTrack);
        var target = index + delta;
        return index >= 0 && target >= 0 && target < SelectedService.Tracks.Count &&
               !SelectedService.Tracks[target].IsPcrOnly;
    }

    [RelayCommand(CanExecute = nameof(CanOutput))]
    private async Task OutputAsync()
    {
        var catalog = _catalog;
        if (catalog is null || IsBusy)
            return;
        if (!TryBuildConfiguration(out var configuration, out var validationError))
        {
            if (!string.IsNullOrEmpty(validationError))
                StatusText = validationError;
            return;
        }

        var settings = new SaveFileDialogSettings
        {
            Title = LocalizationManager.Instance.String_TsRemux_SaveTitle,
            SuggestedStartLocation = new DesktopDialogStorageFolder(Path.GetDirectoryName(FilePath)!),
            SuggestedFileName = Path.GetFileNameWithoutExtension(FilePath) + "_remux.ts",
            Filters = [new(LocalizationManager.Instance.String_TsFiles, ["ts"])],
            DefaultExtension = "ts"
        };
        var selected = await _dialogService.ShowSaveFileDialogAsync(this, settings);
        if (selected?.Path is null)
            return;

        var cancellationTokenSource = new CancellationTokenSource();
        _cancellationTokenSource = cancellationTokenSource;
        var generation = ++_generation;
        IsBusy = true;
        ShowOutputProgress = true;
        Percent = 0;
        StatusText = LocalizationManager.Instance.String_TsRemux_Status_Writing;
        try
        {
            var progress = new Progress<TsRemuxProgress>(value =>
            {
                if (_isClosing || generation != _generation)
                    return;
                Percent = value.Percent;
                ProgressText = $"{CommonUtil.FormatFileSize(value.BytesProcessed)} / {CommonUtil.FormatFileSize(value.FileSize)}";
                SpeedText = $"{CommonUtil.FormatFileSize(value.BytesPerSecond)}/s";
            });
            var service = new TsRemuxService();
            var result = await Task.Run(() => service.RemuxAsync(
                FilePath, selected.Path.LocalPath, catalog, configuration,
                progress, cancellationTokenSource.Token));
            if (_isClosing || generation != _generation)
                return;
            Percent = 100;
            StatusText = result.TransportErrors + result.ContinuityErrors == 0
                ? string.Format(LocalizationManager.Instance.String_TsRemux_Status_Completed,
                    CommonUtil.FormatFileSize(result.BytesWritten))
                : string.Format(LocalizationManager.Instance.String_TsRemux_Status_CompletedWithWarnings,
                    CommonUtil.FormatFileSize(result.BytesWritten),
                    result.TransportErrors + result.ContinuityErrors);
        }
        catch (OperationCanceledException)
        {
            if (!_isClosing)
                StatusText = LocalizationManager.Instance.String_TsRemux_Status_Cancelled;
        }
        catch (TsRemuxException exception)
        {
            if (!_isClosing)
                StatusText = FormatError(exception);
        }
        catch (Exception exception)
        {
            if (!_isClosing)
                StatusText = string.Format(LocalizationManager.Instance.String_TsRemux_Status_Failed, exception.Message);
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

    private bool TryBuildConfiguration(out TsRemuxConfiguration configuration, out string error)
    {
        error = string.Empty;
        var services = new List<TsRemuxServiceConfiguration>();
        foreach (var item in Services.Where(item => item.IsSelected))
        {
            if (!TsNumberParser.TryParse(item.OutputServiceIdText, out var serviceId) ||
                !TsNumberParser.TryParse(item.OutputPmtPidText, out var pmtPid))
            {
                error = LocalizationManager.Instance.String_TsRemux_Error_InvalidNumber;
                configuration = null!;
                return false;
            }
            var tracks = new List<TsRemuxTrackConfiguration>();
            foreach (var track in item.Tracks)
            {
                if (!track.IsSelected && !track.IsPcrSource)
                    continue;
                if (!TsNumberParser.TryParse(track.OutputPidText, out var outputPid))
                {
                    error = LocalizationManager.Instance.String_TsRemux_Error_InvalidNumber;
                    configuration = null!;
                    return false;
                }
                tracks.Add(new TsRemuxTrackConfiguration
                {
                    SourcePid = track.SourcePid,
                    OutputPid = outputPid,
                    Keep = !track.IsPcrOnly && track.IsSelected,
                    OutputLanguageCode = track.IsPcrOnly ? null : track.SelectedLanguage?.Code,
                    Order = item.Tracks.IndexOf(track)
                });
            }
            services.Add(new TsRemuxServiceConfiguration
            {
                SourceServiceId = item.SourceServiceId,
                OutputServiceId = serviceId,
                OutputPmtPid = pmtPid,
                OutputServiceType = item.SelectedServiceType?.Value,
                WriteServiceName = item.WriteServiceName,
                ServiceName = item.ServiceName.Trim(),
                WriteProviderName = item.WriteProviderName,
                ProviderName = item.ProviderName.Trim(),
                Tracks = tracks
            });
        }
        configuration = new TsRemuxConfiguration
        {
            Services = services,
            KeepEpg = KeepEpg,
            OutputMode = PreservePacketCount
                ? TsRemuxOutputMode.PreservePacketCount
                : TsRemuxOutputMode.Compact
        };
        try
        {
            new TsRemuxService().BuildPlan(_catalog!, configuration);
            return true;
        }
        catch (TsRemuxException exception)
        {
            error = FormatError(exception);
            return false;
        }
    }

    private static string FormatError(TsRemuxException exception)
    {
        var strings = LocalizationManager.Instance;
        var format = exception.Code switch
        {
            TsRemuxErrorCode.SameFile => strings.String_TsRemux_Error_SameFile,
            TsRemuxErrorCode.SyncLost => strings.String_TsRemux_Error_SyncLost,
            TsRemuxErrorCode.NoServiceSelected => strings.String_TsRemux_Error_NoService,
            TsRemuxErrorCode.NoTrackSelected => strings.String_TsRemux_Error_NoTrack,
            TsRemuxErrorCode.InvalidServiceId => strings.String_TsRemux_Error_InvalidServiceId,
            TsRemuxErrorCode.InvalidPid => strings.String_TsRemux_Error_InvalidPid,
            TsRemuxErrorCode.DuplicateServiceId => strings.String_TsRemux_Error_DuplicateServiceId,
            TsRemuxErrorCode.DuplicatePid => strings.String_TsRemux_Error_DuplicatePid,
            TsRemuxErrorCode.InvalidLanguage => strings.String_TsRemux_Error_InvalidLanguage,
            TsRemuxErrorCode.EncryptedServiceUnsupported => strings.String_TsRemux_Error_Encrypted,
            TsRemuxErrorCode.MetadataRequiresServiceInformationSlots => strings.String_TsRemux_Error_PreserveMetadata,
            TsRemuxErrorCode.PreserveEpgRequiresUnchangedServices => strings.String_TsRemux_Error_PreserveEpg,
            TsRemuxErrorCode.InsufficientPsiPacketSlots => strings.String_TsRemux_Error_PreservePsiSlots,
            TsRemuxErrorCode.MissingProgram => strings.String_TsRemux_Error_MissingProgram,
            TsRemuxErrorCode.PatTooLarge => strings.String_TsRemux_Error_PatTooLarge,
            TsRemuxErrorCode.PmtTooLarge => strings.String_TsRemux_Error_PmtTooLarge,
            TsRemuxErrorCode.SdtTooLarge => strings.String_TsRemux_Error_SdtTooLarge,
            _ => strings.String_TsRemux_Status_Failed
        };
        return string.Format(format, exception.Arguments);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancellationTokenSource?.Cancel();

    private void NotifyCommandStates()
    {
        OutputCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        SelectAllTracksCommand.NotifyCanExecuteChanged();
        ClearAllTracksCommand.NotifyCanExecuteChanged();
        MoveTrackUpCommand.NotifyCanExecuteChanged();
        MoveTrackDownCommand.NotifyCanExecuteChanged();
        AutoAssignPidsCommand.NotifyCanExecuteChanged();
        RestoreSourcePidsCommand.NotifyCanExecuteChanged();
    }

    public void OnClosed()
    {
        if (_isClosing)
            return;
        _isClosing = true;
        App.LocalizationService.LanguageChanged -= OnLanguageChanged;
        _generation++;
        _cancellationTokenSource?.Cancel();
        UnsubscribeRows();
        Services.Clear();
        SelectedService = null;
        _catalog = null;
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(WindowTitle));
        var selectedTypes = Services.ToDictionary(item => item.SourceServiceId,
            item => item.SelectedServiceType?.Value);
        // 同一个源 PID 可能被多个节目共享，直接按 PID 建字典会在切换语言时抛出重复键异常。
        var selectedLanguages = Services.SelectMany(item => item.Tracks)
            .Select(item => (Track: item, Code: item.SelectedLanguage?.Code))
            .ToArray();
        BuildServiceTypeOptions();
        BuildLanguageOptions();
        foreach (var code in selectedLanguages.Select(item => item.Code)
                     .Where(item => !string.IsNullOrWhiteSpace(item)).Distinct())
            FindOrAddLanguageOption(code);
        foreach (var service in Services)
        {
            var value = selectedTypes.GetValueOrDefault(service.SourceServiceId);
            service.SelectedServiceType = value is { } type
                ? FindServiceTypeOption(type)
                : null;
        }
        foreach (var (track, code) in selectedLanguages)
            track.SelectedLanguage = FindLanguageOption(code) ?? LanguageOptions[0];
        if (_catalog is null)
            return;
        OnPropertyChanged(nameof(ChangeSummaryText));
        foreach (var service in Services)
        {
            service.TrackSummary = string.Format(
                LocalizationManager.Instance.String_TsRemux_TrackCount,
                service.Tracks.Count(item => !item.IsPcrOnly));
            foreach (var track in service.Tracks.Where(item => item.IsPcrOnly))
            {
                track.StreamText = LocalizationManager.Instance.String_TsRemux_Track_PcrOnly;
                track.RoleText = LocalizationManager.Instance.String_TsRemux_Role_PcrClock;
            }
            foreach (var track in service.Tracks.Where(item => !item.IsPcrOnly))
            {
                track.StreamText = FormatTrackText(_catalog, track.SourcePid);
                track.TypeText = FormatTrackType(_catalog, track.SourcePid);
                track.RoleText = FormatTrackRole(
                    _catalog, track.SourcePid, track.Definition!, track.IsPcrSource);
            }
        }
        ProbeSummaryText = string.Format(
            LocalizationManager.Instance.String_TsRemux_ProbeSummary,
            CommonUtil.FormatFileSize(_catalog.BytesScanned), Services.Count,
            Services.Sum(item => item.Tracks.Count(track => !track.IsPcrOnly)));
    }

    private TsRemuxChangeSummary BuildChangeSummary()
    {
        var selectedServices = Services.Where(item => item.IsSelected).ToArray();
        var selectedTracks = selectedServices
            .SelectMany(item => item.Tracks.Where(track => track.IsSelected && !track.IsPcrOnly)).Count();
        var changedIdentifiers = selectedServices.Count(item =>
                                     !HasSameNumber(item.OutputServiceIdText, item.SourceServiceId)) +
                                 selectedServices.Count(item =>
                                     !HasSameNumber(item.OutputPmtPidText, item.SourcePmtPidText)) +
                                 selectedServices.SelectMany(item => item.Tracks)
                                     .Count(track => (track.IsSelected || track.IsPcrSource) &&
                                                     !HasSameNumber(track.OutputPidText, track.SourcePid));
        var changedMetadata = selectedServices.Sum(CountMetadataChanges);
        var changedLanguages = selectedServices.SelectMany(item => item.Tracks)
            .Where(track => track.IsSelected && !track.IsPcrOnly)
            .Count(track => !string.Equals(track.SelectedLanguage?.Code, track.OriginalLanguageCode,
                StringComparison.OrdinalIgnoreCase));
        var reordered = selectedServices.Sum(item => item.Tracks.Where(track => !track.IsPcrOnly)
            .Select((track, index) => track.OriginalOrder == index ? 0 : 1).Sum());
        return new TsRemuxChangeSummary(selectedServices.Length, selectedTracks, changedIdentifiers,
            changedMetadata, changedLanguages, reordered);
    }

    private static string FormatChangeSummary(TsRemuxChangeSummary summary)
    {
        var parts = new List<string>(6);
        AddSummaryPart(parts, summary.SelectedServices,
            LocalizationManager.Instance.String_TsRemux_ChangeSummary_Services);
        AddSummaryPart(parts, summary.SelectedTracks,
            LocalizationManager.Instance.String_TsRemux_ChangeSummary_Tracks);
        AddSummaryPart(parts, summary.ChangedIdentifiers,
            LocalizationManager.Instance.String_TsRemux_ChangeSummary_Identifiers);
        AddSummaryPart(parts, summary.ChangedMetadata,
            LocalizationManager.Instance.String_TsRemux_ChangeSummary_Metadata);
        AddSummaryPart(parts, summary.ChangedLanguages,
            LocalizationManager.Instance.String_TsRemux_ChangeSummary_Languages);
        AddSummaryPart(parts, summary.ReorderedTracks,
            LocalizationManager.Instance.String_TsRemux_ChangeSummary_Reordered);
        return string.Join(" / ", parts);
    }

    private static void AddSummaryPart(List<string> parts, int count, string format)
    {
        if (count > 0)
            parts.Add(string.Format(format, count));
    }

    private static bool HasSameNumber(string text, int expected) =>
        TsNumberParser.TryParse(text, out var value) && value == expected;

    private static bool HasSameNumber(string left, string right) =>
        TsNumberParser.TryParse(left, out var leftValue) &&
        TsNumberParser.TryParse(right, out var rightValue) && leftValue == rightValue;

    private static int CountMetadataChanges(TsRemuxServiceItem service)
    {
        var sourceName = service.SourceService?.ServiceName ?? string.Empty;
        var sourceProvider = service.SourceService?.ProviderName ?? string.Empty;
        var sourceHasName = !string.IsNullOrWhiteSpace(sourceName);
        var sourceHasProvider = !string.IsNullOrWhiteSpace(sourceProvider);
        var count = service.SelectedServiceType?.Value is { } type &&
                    type != service.SourceService?.ServiceType ? 1 : 0;
        if (service.WriteServiceName != sourceHasName ||
            service.WriteServiceName && !string.Equals(service.ServiceName.Trim(), sourceName, StringComparison.Ordinal))
            count++;
        if (service.WriteProviderName != sourceHasProvider ||
            service.WriteProviderName && !string.Equals(service.ProviderName.Trim(), sourceProvider, StringComparison.Ordinal))
            count++;
        return count;
    }

    private void BuildServiceTypeOptions()
    {
        ServiceTypeOptions.Clear();
        AddServiceTypeOption(null, LocalizationManager.Instance.String_TsRemux_ServiceType_Unspecified);
        AddServiceTypeOption(0x01, LocalizationManager.Instance.String_TsRemux_ServiceType_Television);
        AddServiceTypeOption(0x02, LocalizationManager.Instance.String_TsRemux_ServiceType_Radio);
        AddServiceTypeOption(0x03, LocalizationManager.Instance.String_TsRemux_ServiceType_Teletext);
        AddServiceTypeOption(0x04, LocalizationManager.Instance.String_TsRemux_ServiceType_NvodReference);
        AddServiceTypeOption(0x05, LocalizationManager.Instance.String_TsRemux_ServiceType_NvodTimeShifted);
        AddServiceTypeOption(0x06, LocalizationManager.Instance.String_TsRemux_ServiceType_Mosaic);
        AddServiceTypeOption(0x0A, LocalizationManager.Instance.String_TsRemux_ServiceType_FmRadio);
        AddServiceTypeOption(0x0C, LocalizationManager.Instance.String_TsRemux_ServiceType_Data);
        AddServiceTypeOption(0x11, LocalizationManager.Instance.String_TsRemux_ServiceType_Mpeg2HdTelevision);
        AddServiceTypeOption(0x16, LocalizationManager.Instance.String_TsRemux_ServiceType_AdvancedHdTelevision);
        AddServiceTypeOption(0x19, LocalizationManager.Instance.String_TsRemux_ServiceType_AdvancedData);
        AddServiceTypeOption(0x1F, LocalizationManager.Instance.String_TsRemux_ServiceType_UhdTelevision);
    }

    private void AddServiceTypeOption(byte? value, string name)
    {
        ServiceTypeOptions.Add(new TsRemuxServiceTypeOption(
            value, value is { } item ? $"{name} (0x{item:X2})" : name));
    }

    private void BuildLanguageOptions()
    {
        LanguageOptions.Clear();
        foreach (var code in LanguageCodes)
        {
            var name = GetLanguageDisplayName(code);
            LanguageOptions.Add(new TsRemuxLanguageOption(
                code, code.Length == 0 ? name : $"{name} ({code})"));
        }
    }

    private static string GetLanguageDisplayName(string code) => code switch
    {
        "" => LocalizationManager.Instance.String_TsRemux_Language_None,
        "und" => LocalizationManager.Instance.String_TsRemux_Language_Undetermined,
        "chi" => LocalizationManager.Instance.String_TsRemux_Language_Chinese,
        "eng" => LocalizationManager.Instance.String_TsRemux_Language_English,
        "jpn" => LocalizationManager.Instance.String_TsRemux_Language_Japanese,
        "kor" => LocalizationManager.Instance.String_TsRemux_Language_Korean,
        "fre" => LocalizationManager.Instance.String_TsRemux_Language_French,
        "ger" => LocalizationManager.Instance.String_TsRemux_Language_German,
        "spa" => LocalizationManager.Instance.String_TsRemux_Language_Spanish,
        "ita" => LocalizationManager.Instance.String_TsRemux_Language_Italian,
        _ => code
    };

    private TsRemuxLanguageOption? FindLanguageOption(string? code) =>
        LanguageOptions.FirstOrDefault(item =>
            string.Equals(item.Code, code?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase));

    private TsRemuxLanguageOption? FindOrAddLanguageOption(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return LanguageOptions.FirstOrDefault(item => item.Code.Length == 0);
        var normalized = code.Trim().ToLowerInvariant();
        var option = FindLanguageOption(normalized);
        if (option is not null)
            return option;
        option = new TsRemuxLanguageOption(normalized, normalized);
        LanguageOptions.Add(option);
        return option;
    }

    private TsRemuxServiceTypeOption FindServiceTypeOption(byte value)
    {
        var option = ServiceTypeOptions.FirstOrDefault(item => item.Value == value);
        if (option is not null)
            return option;
        option = new TsRemuxServiceTypeOption(
            value,
            string.Format(LocalizationManager.Instance.String_TsRemux_ServiceType_Unknown, value));
        ServiceTypeOptions.Add(option);
        return option;
    }
}
