using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.FrameworkDialogs;
using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.ViewModels;

public partial class TsPacketViewerWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    private const int WindowPacketCount = 256;
    private readonly IDialogService _dialogService;
    private readonly TsCheckTextFormatter _text = new();
    private readonly TsPacketViewerService _service = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private TsPacketViewerSession? _session;
    private long? _initialPacketIndex;
    private bool _isClosing;
    private int _generation;

    public TsPacketViewerWindowViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
        StatusText = LocalizationManager.Instance.String_TsPacketViewer_Status_Ready;
        App.LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    public bool? DialogResult { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private string _filePath = string.Empty;
    public string WindowTitle => string.IsNullOrEmpty(FilePath)
        ? LocalizationManager.Instance.String_TsPacketViewer_Title
        : $"{LocalizationManager.Instance.String_TsPacketViewer_Title} - {Path.GetFileName(FilePath)}";
    public ObservableCollection<TsPacketViewerRow> Packets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanNavigate))]
    [NotifyCanExecuteChangedFor(nameof(JumpPacketCommand), nameof(JumpOffsetCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPacketCommand), nameof(NextPacketCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousWindowCommand), nameof(NextWindowCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousSamePidCommand), nameof(NextSamePidCommand), nameof(OpenFileCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _fileSummaryText = "-";

    [ObservableProperty]
    private string _packetInputText = "0";

    [ObservableProperty]
    private string _offsetInputText = "0x0";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPacketData))]
    [NotifyPropertyChangedFor(nameof(SelectedPacketSummaryText))]
    [NotifyPropertyChangedFor(nameof(CanNavigate))]
    private TsPacketViewerRow? _selectedPacket;

    [ObservableProperty]
    private IReadOnlyList<TsPacketFieldItem> _fields = [];

    [ObservableProperty]
    private TsPacketFieldItem? _selectedField;

    [ObservableProperty]
    private bool _useStandardFieldNames;

    public byte[]? SelectedPacketData => SelectedPacket?.Data;
    public bool CanNavigate => !IsBusy && _session is not null && SelectedPacket is not null;
    public string SelectedPacketSummaryText => SelectedPacket is null
        ? "-"
        : string.Format(
            LocalizationManager.Instance.String_TsPacketViewer_SelectedSummary,
            SelectedPacket.PacketText,
            SelectedPacket.PidText,
            SelectedPacket.StreamText,
            SelectedPacket.OffsetText);

    public event Action<TsPacketViewerRow>? SelectionRequested;

    public void Initialize(string filePath, long packetIndex)
    {
        FilePath = filePath;
        _initialPacketIndex = Math.Max(0, packetIndex);
    }

    public async Task InitializeAsync()
    {
        if (!string.IsNullOrEmpty(FilePath))
            await OpenPathAsync(FilePath, _initialPacketIndex).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanOpenFile))]
    private async Task OpenFileAsync()
    {
        var settings = new OpenFileDialogSettings
        {
            Title = LocalizationManager.Instance.String_TsPacketViewer_OpenFile,
            Filters = [new(LocalizationManager.Instance.String_TsFiles, ["ts"])]
        };
        var result = await _dialogService.ShowOpenFilesDialogAsync(this, settings);
        if (result.Any())
            await OpenPathAsync(result[0].LocalPath).ConfigureAwait(true);
    }

    private bool CanOpenFile() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private async Task JumpPacketAsync()
    {
        if (_session is null ||
            !long.TryParse(PacketInputText.Replace(",", string.Empty), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var packetIndex) ||
            packetIndex < 0 || packetIndex >= _session.TotalPackets)
        {
            StatusText = LocalizationManager.Instance.String_TsPacketViewer_Status_InvalidPacket;
            return;
        }
        await LoadAroundAsync(packetIndex).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private async Task JumpOffsetAsync()
    {
        if (_session is null || !TryParseOffset(OffsetInputText, out var offset) ||
            offset < _session.SyncOffset || offset >= _session.FileSize)
        {
            StatusText = LocalizationManager.Instance.String_TsPacketViewer_Status_InvalidOffset;
            return;
        }
        var packetIndex = Math.Min(
            _session.TotalPackets - 1,
            (offset - _session.SyncOffset) / TsUtil.TsPacketSize);
        await LoadAroundAsync(packetIndex).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private Task PreviousSamePidAsync() => FindSamePidAsync(false);

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private Task NextSamePidAsync() => FindSamePidAsync(true);

    [RelayCommand(CanExecute = nameof(CanMovePreviousPacket))]
    private Task PreviousPacketAsync() => LoadAroundAsync(SelectedPacket!.PacketIndex - 1);

    private bool CanMovePreviousPacket() =>
        CanNavigate && SelectedPacket!.PacketIndex > 0;

    [RelayCommand(CanExecute = nameof(CanMoveNextPacket))]
    private Task NextPacketAsync() => LoadAroundAsync(SelectedPacket!.PacketIndex + 1);

    private bool CanMoveNextPacket() =>
        CanNavigate && SelectedPacket!.PacketIndex < _session!.TotalPackets - 1;

    [RelayCommand(CanExecute = nameof(CanMovePreviousWindow))]
    private Task PreviousWindowAsync() => LoadAroundAsync(
        Math.Max(0, (SelectedPacket?.PacketIndex ?? 0) - WindowPacketCount));

    private bool CanMovePreviousWindow() =>
        CanNavigate && SelectedPacket!.PacketIndex > 0;

    [RelayCommand(CanExecute = nameof(CanMoveNextWindow))]
    private Task NextWindowAsync() => LoadAroundAsync(
        Math.Min(_session!.TotalPackets - 1, SelectedPacket!.PacketIndex + WindowPacketCount));

    private bool CanMoveNextWindow() =>
        CanNavigate && SelectedPacket!.PacketIndex < _session!.TotalPackets - 1;

    private async Task OpenPathAsync(string path, long? initialPacketIndex = null)
    {
        var generation = ++_generation;
        var cancellation = ReplaceCancellation();
        IsBusy = true;
        StatusText = LocalizationManager.Instance.String_TsPacketViewer_Status_Opening;
        try
        {
            var session = await _service.OpenAsync(path, cancellation.Token).ConfigureAwait(true);
            if (_isClosing)
            {
                await _service.DisposeAsync().ConfigureAwait(true);
                return;
            }
            if (generation != _generation)
                return;
            _session = session;
            FilePath = session.FilePath;
            FileSummaryText = string.Format(
                LocalizationManager.Instance.String_TsPacketViewer_FileSummary,
                CommonUtil.FormatFileSize(session.FileSize),
                session.SyncOffset.ToString("N0"),
                session.TotalPackets.ToString("N0"));
            OnPropertyChanged(nameof(WindowTitle));
            // 快速检查传入的是扫描时的 0-based 包号；文件边界变化时仍在当前会话范围内安全收敛。
            var targetPacket = Math.Clamp(initialPacketIndex ?? 0, 0, Math.Max(0, session.TotalPackets - 1));
            await LoadAroundCoreAsync(targetPacket, generation, cancellation.Token).ConfigureAwait(true);
            StatusText = LocalizationManager.Instance.String_TsPacketViewer_Status_Ready;
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidDataException)
        {
            _session = null;
            Packets.Clear();
            SelectedPacket = null;
            StatusText = LocalizationManager.Instance.String_TsPacketViewer_Status_NoSync;
        }
        catch (Exception exception)
        {
            _session = null;
            Packets.Clear();
            SelectedPacket = null;
            StatusText = string.Format(
                LocalizationManager.Instance.String_TsPacketViewer_Status_Failed,
                exception.Message);
        }
        finally
        {
            if (generation == _generation)
                IsBusy = false;
        }
    }

    private async Task LoadAroundAsync(long packetIndex)
    {
        if (_session is null)
            return;
        var existing = Packets.FirstOrDefault(item => item.PacketIndex == packetIndex);
        if (existing is not null)
        {
            SelectPacket(existing);
            return;
        }

        var generation = ++_generation;
        var cancellation = ReplaceCancellation();
        IsBusy = true;
        try
        {
            await LoadAroundCoreAsync(packetIndex, generation, cancellation.Token).ConfigureAwait(true);
            StatusText = LocalizationManager.Instance.String_TsPacketViewer_Status_Ready;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText = string.Format(
                LocalizationManager.Instance.String_TsPacketViewer_Status_Failed,
                exception.Message);
        }
        finally
        {
            if (generation == _generation)
                IsBusy = false;
        }
    }

    private async Task LoadAroundCoreAsync(long packetIndex, int generation, CancellationToken cancellationToken)
    {
        var session = _session ?? throw new InvalidOperationException();
        var maximumStart = Math.Max(0, session.TotalPackets - WindowPacketCount);
        var start = Math.Clamp(packetIndex - WindowPacketCount / 2, 0, maximumStart);
        var warmupStart = Math.Max(0, start - WindowPacketCount);
        var warmupCount = (int)(start - warmupStart);
        var packetData = await _service.ReadWindowAsync(
            warmupStart, warmupCount + WindowPacketCount, cancellationToken).ConfigureAwait(true);
        if (_isClosing || generation != _generation)
            return;

        var rows = CreateRows(packetData).Skip(warmupCount);
        Packets.Clear();
        foreach (var row in rows)
            Packets.Add(row);
        var selected = Packets.FirstOrDefault(item => item.PacketIndex == packetIndex) ?? Packets.FirstOrDefault();
        if (selected is not null)
            SelectPacket(selected);
    }

    private async Task FindSamePidAsync(bool forward)
    {
        if (_session is null || SelectedPacket is null)
            return;
        var generation = ++_generation;
        var cancellation = ReplaceCancellation();
        var pid = SelectedPacket.Info.Pid;
        IsBusy = true;
        StatusText = LocalizationManager.Instance.String_TsPacketViewer_Status_Searching;
        try
        {
            var packetIndex = await _service.FindSamePidAsync(
                SelectedPacket.PacketIndex, pid, forward, cancellation.Token).ConfigureAwait(true);
            if (_isClosing || generation != _generation)
                return;
            if (packetIndex is null)
            {
                StatusText = LocalizationManager.Instance.String_TsPacketViewer_Status_PidNotFound;
                return;
            }
            await LoadAroundCoreAsync(packetIndex.Value, generation, cancellation.Token).ConfigureAwait(true);
            StatusText = LocalizationManager.Instance.String_TsPacketViewer_Status_Ready;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText = string.Format(
                LocalizationManager.Instance.String_TsPacketViewer_Status_Failed,
                exception.Message);
        }
        finally
        {
            if (generation == _generation)
                IsBusy = false;
        }
    }

    private IReadOnlyList<TsPacketViewerRow> CreateRows(IReadOnlyList<TsPacketData> packets)
    {
        var continuityStates = new Dictionary<int, TsPacketData>();
        var rows = new List<TsPacketViewerRow>(packets.Count);
        foreach (var packet in packets)
        {
            var info = packet.Info;
            var severity = !info.IsValid || info.TransportError
                ? TsCheckSeverity.Error
                : TsCheckSeverity.Info;

            if (!info.IsValid || info.TransportError || info.Discontinuity)
                continuityStates.Remove(info.Pid);

            // 与快速扫描保持同一 CC 语义：仅带负载的非空包参与连续性判断。
            if (severity != TsCheckSeverity.Error && info.HasPayload && info.Pid != 0x1FFF)
            {
                var updateBaseline = true;
                if (continuityStates.TryGetValue(info.Pid, out var previous))
                {
                    var expected = (previous.Info.ContinuityCounter + 1) & 0x0F;
                    if (info.ContinuityCounter == previous.Info.ContinuityCounter)
                    {
                        severity = packet.Data.AsSpan(4).SequenceEqual(previous.Data.AsSpan(4))
                            ? TsCheckSeverity.Warning
                            : TsCheckSeverity.Error;
                        updateBaseline = false;
                    }
                    else if (info.ContinuityCounter != expected)
                    {
                        severity = TsCheckSeverity.Error;
                    }
                }
                if (updateBaseline)
                    continuityStates[info.Pid] = packet;
            }
            rows.Add(CreateRow(packet, severity));
        }
        return rows;
    }

    private TsPacketViewerRow CreateRow(TsPacketData packet, TsCheckSeverity severity = TsCheckSeverity.Info)
    {
        var info = packet.Info;
        return new TsPacketViewerRow
        {
            PacketIndex = packet.PacketIndex,
            FileOffset = packet.FileOffset,
            Data = packet.Data,
            Info = info,
            StreamText = info.IsValid
                ? FormatPid(info.Pid)
                : string.Format(LocalizationManager.Instance.String_TsPacketViewer_InvalidPacket, FormatPid(info.Pid)),
            TimestampText = packet.TimestampText,
            Severity = severity,
            AdaptationText = FormatAdaptation(info.AdaptationControl)
        };
    }

    private string FormatPid(int pid)
    {
        if (_session?.Catalog.Pids.TryGetValue(pid, out var summary) == true)
        {
            return _text.FormatPidDescription(
                pid, summary.ProgramNumber, summary.StreamType, summary.MpegAudioLayer,
                summary.SupplementaryStreamType, summary.Language, summary.IsPcrPid, summary.IsPmtPid);
        }
        TsCheckProgramSummary? pmt = _session?.Catalog.Programs.Values.FirstOrDefault(item => item.PmtPid == pid);
        return _text.FormatPidDescription(pid, pmt?.ProgramNumber, null, null, null, null, false, pmt is not null);
    }

    private static string FormatAdaptation(int value) => value switch
    {
        0 => LocalizationManager.Instance.String_TsPacketViewer_Value_Adaptation_Reserved,
        1 => LocalizationManager.Instance.String_TsPacketViewer_Value_Adaptation_Payload,
        2 => LocalizationManager.Instance.String_TsPacketViewer_Value_Adaptation_Only,
        _ => LocalizationManager.Instance.String_TsPacketViewer_Value_Adaptation_Both
    };

    private void SelectPacket(TsPacketViewerRow packet)
    {
        SelectedPacket = packet;
        SelectionRequested?.Invoke(packet);
    }

    partial void OnSelectedPacketChanged(TsPacketViewerRow? value)
    {
        if (value is null)
        {
            Fields = [];
            SelectedField = null;
            return;
        }
        PacketInputText = value.PacketIndex.ToString(CultureInfo.InvariantCulture);
        OffsetInputText = value.OffsetText;
        RefreshFields();
        JumpPacketCommand.NotifyCanExecuteChanged();
        JumpOffsetCommand.NotifyCanExecuteChanged();
        PreviousSamePidCommand.NotifyCanExecuteChanged();
        NextSamePidCommand.NotifyCanExecuteChanged();
        PreviousPacketCommand.NotifyCanExecuteChanged();
        NextPacketCommand.NotifyCanExecuteChanged();
        PreviousWindowCommand.NotifyCanExecuteChanged();
        NextWindowCommand.NotifyCanExecuteChanged();
    }

    partial void OnUseStandardFieldNamesChanged(bool value) => RefreshFields();

    private void RefreshFields()
    {
        if (SelectedPacket is null)
        {
            Fields = [];
            SelectedField = null;
            return;
        }
        Fields = TsPacketFieldBuilder.Build(
                SelectedPacket.Data, SelectedPacket.Info, IsPsiPid(SelectedPacket.Info.Pid))
            .Select(ConvertField)
            .ToArray();
        SelectedField = Fields.FirstOrDefault();
    }

    private TsPacketFieldItem ConvertField(TsPacketFieldDefinition definition)
    {
        var value = definition.ValueKind == TsPacketFieldValueKind.None
            ? definition.Value
            : GetFieldValue(definition.ValueKind, UseStandardFieldNames);
        var range = definition.HighBit is { } highBit && definition.LowBit is { } lowBit
            ? definition.ByteLength == 1
                ? string.Format(
                    LocalizationManager.Instance.String_TsPacketViewer_Range_Bits,
                    definition.StartByte,
                    highBit,
                    lowBit)
                : string.Format(
                    LocalizationManager.Instance.String_TsPacketViewer_Range_ByteBits,
                    definition.StartByte,
                    definition.StartByte + definition.ByteLength - 1,
                    highBit,
                    lowBit)
            : definition.ByteLength == 1
                ? string.Format(LocalizationManager.Instance.String_TsPacketViewer_Range_Byte, definition.StartByte)
                : string.Format(
                    LocalizationManager.Instance.String_TsPacketViewer_Range_Bytes,
                    definition.StartByte,
                    definition.StartByte + definition.ByteLength - 1);
        return new TsPacketFieldItem
        {
            Name = UseStandardFieldNames
                ? GetStandardFieldName(definition.Kind)
                : GetFieldName(definition.Kind),
            Value = value,
            RangeText = range,
            StartByte = definition.StartByte,
            ByteLength = definition.ByteLength,
            Children = definition.Children.Select(ConvertField).ToArray()
        };
    }

    private static string GetStandardFieldName(TsPacketFieldKind kind) => kind switch
    {
        TsPacketFieldKind.Header => "ts_header",
        TsPacketFieldKind.SyncByte => "sync_byte",
        TsPacketFieldKind.TransportErrorIndicator => "transport_error_indicator",
        TsPacketFieldKind.PayloadUnitStartIndicator => "payload_unit_start_indicator",
        TsPacketFieldKind.TransportPriority => "transport_priority",
        TsPacketFieldKind.Pid => "pid",
        TsPacketFieldKind.ScramblingControl => "transport_scrambling_control",
        TsPacketFieldKind.AdaptationControl => "adaptation_field_control",
        TsPacketFieldKind.ContinuityCounter => "continuity_counter",
        TsPacketFieldKind.Adaptation => "adaptation_field",
        TsPacketFieldKind.AdaptationLength => "adaptation_field_length",
        TsPacketFieldKind.DiscontinuityIndicator => "discontinuity_indicator",
        TsPacketFieldKind.RandomAccessIndicator => "random_access_indicator",
        TsPacketFieldKind.ElementaryStreamPriority => "elementary_stream_priority_indicator",
        TsPacketFieldKind.PcrFlag => "pcr_flag",
        TsPacketFieldKind.OpcrFlag => "opcr_flag",
        TsPacketFieldKind.SplicingPointFlag => "splicing_point_flag",
        TsPacketFieldKind.PrivateDataFlag => "transport_private_data_flag",
        TsPacketFieldKind.AdaptationExtensionFlag => "adaptation_field_extension_flag",
        TsPacketFieldKind.Pcr => "pcr",
        TsPacketFieldKind.Payload => "payload",
        TsPacketFieldKind.PesHeader => "pes_header",
        TsPacketFieldKind.StartCodePrefix => "packet_start_code_prefix",
        TsPacketFieldKind.StreamId => "stream_id",
        TsPacketFieldKind.PesPacketLength => "pes_packet_length",
        TsPacketFieldKind.PesFlags => "pes_flags",
        TsPacketFieldKind.PesHeaderLength => "pes_header_data_length",
        TsPacketFieldKind.Pts => "pts",
        TsPacketFieldKind.Dts => "dts",
        TsPacketFieldKind.PointerField => "pointer_field",
        TsPacketFieldKind.TableId => "table_id",
        TsPacketFieldKind.SectionLength => "section_length",
        _ => kind.ToString()
    };

    private static string GetFieldName(TsPacketFieldKind kind)
    {
        var strings = LocalizationManager.Instance;
        return kind switch
        {
            TsPacketFieldKind.Header => strings.String_TsPacketViewer_Field_Header,
            TsPacketFieldKind.SyncByte => strings.String_TsPacketViewer_Field_SyncByte,
            TsPacketFieldKind.TransportErrorIndicator => strings.String_TsPacketViewer_Field_Tei,
            TsPacketFieldKind.PayloadUnitStartIndicator => strings.String_TsPacketViewer_Field_Pusi,
            TsPacketFieldKind.TransportPriority => strings.String_TsPacketViewer_Field_Priority,
            TsPacketFieldKind.Pid => strings.String_TsPacketViewer_Field_Pid,
            TsPacketFieldKind.ScramblingControl => strings.String_TsPacketViewer_Field_Scrambling,
            TsPacketFieldKind.AdaptationControl => strings.String_TsPacketViewer_Field_AdaptationControl,
            TsPacketFieldKind.ContinuityCounter => strings.String_TsPacketViewer_Field_Continuity,
            TsPacketFieldKind.Adaptation => strings.String_TsPacketViewer_Field_Adaptation,
            TsPacketFieldKind.AdaptationLength => strings.String_TsPacketViewer_Field_AdaptationLength,
            TsPacketFieldKind.DiscontinuityIndicator => strings.String_TsPacketViewer_Field_Discontinuity,
            TsPacketFieldKind.RandomAccessIndicator => strings.String_TsPacketViewer_Field_RandomAccess,
            TsPacketFieldKind.ElementaryStreamPriority => strings.String_TsPacketViewer_Field_EsPriority,
            TsPacketFieldKind.PcrFlag => strings.String_TsPacketViewer_Field_PcrFlag,
            TsPacketFieldKind.OpcrFlag => strings.String_TsPacketViewer_Field_OpcrFlag,
            TsPacketFieldKind.SplicingPointFlag => strings.String_TsPacketViewer_Field_SpliceFlag,
            TsPacketFieldKind.PrivateDataFlag => strings.String_TsPacketViewer_Field_PrivateDataFlag,
            TsPacketFieldKind.AdaptationExtensionFlag => strings.String_TsPacketViewer_Field_ExtensionFlag,
            TsPacketFieldKind.Pcr => strings.String_TsPacketViewer_Field_Pcr,
            TsPacketFieldKind.Payload => strings.String_TsPacketViewer_Field_Payload,
            TsPacketFieldKind.PesHeader => strings.String_TsPacketViewer_Field_PesHeader,
            TsPacketFieldKind.StartCodePrefix => strings.String_TsPacketViewer_Field_StartCode,
            TsPacketFieldKind.StreamId => strings.String_TsPacketViewer_Field_StreamId,
            TsPacketFieldKind.PesPacketLength => strings.String_TsPacketViewer_Field_PesLength,
            TsPacketFieldKind.PesFlags => strings.String_TsPacketViewer_Field_PesFlags,
            TsPacketFieldKind.PesHeaderLength => strings.String_TsPacketViewer_Field_PesHeaderLength,
            TsPacketFieldKind.Pts => strings.String_TsPacketViewer_Field_Pts,
            TsPacketFieldKind.Dts => strings.String_TsPacketViewer_Field_Dts,
            TsPacketFieldKind.PointerField => strings.String_TsPacketViewer_Field_Pointer,
            TsPacketFieldKind.TableId => strings.String_TsPacketViewer_Field_TableId,
            TsPacketFieldKind.SectionLength => strings.String_TsPacketViewer_Field_SectionLength,
            _ => kind.ToString()
        };
    }

    private static string GetFieldValue(TsPacketFieldValueKind kind, bool useStandardName)
    {
        if (useStandardName)
        {
            return kind switch
            {
                TsPacketFieldValueKind.AdaptationReserved => "reserved",
                TsPacketFieldValueKind.PayloadOnly => "payload_only",
                TsPacketFieldValueKind.AdaptationOnly => "adaptation_only",
                TsPacketFieldValueKind.AdaptationAndPayload => "adaptation_and_payload",
                _ => string.Empty
            };
        }
        var strings = LocalizationManager.Instance;
        return kind switch
        {
            TsPacketFieldValueKind.AdaptationReserved => strings.String_TsPacketViewer_Value_Adaptation_Reserved,
            TsPacketFieldValueKind.PayloadOnly => strings.String_TsPacketViewer_Value_Adaptation_Payload,
            TsPacketFieldValueKind.AdaptationOnly => strings.String_TsPacketViewer_Value_Adaptation_Only,
            TsPacketFieldValueKind.AdaptationAndPayload => strings.String_TsPacketViewer_Value_Adaptation_Both,
            _ => string.Empty
        };
    }

    private static bool TryParseOffset(string text, out long value)
    {
        text = text.Trim().Replace(",", string.Empty);
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private bool IsPsiPid(int pid) =>
        pid <= 0x001F || pid == 0x1FFB ||
        _session?.Catalog.Programs.Values.Any(program => program.PmtPid == pid) == true;

    private CancellationTokenSource ReplaceCancellation()
    {
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = new CancellationTokenSource();
        return _cancellationTokenSource;
    }

    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(SelectedPacketSummaryText));
        if (_session is not null)
        {
            FileSummaryText = string.Format(
                LocalizationManager.Instance.String_TsPacketViewer_FileSummary,
                CommonUtil.FormatFileSize(_session.FileSize),
                _session.SyncOffset.ToString("N0"),
                _session.TotalPackets.ToString("N0"));
            var selectedPacketIndex = SelectedPacket?.PacketIndex;
            var packets = Packets.Select(row => (
                Data: new TsPacketData
                {
                    PacketIndex = row.PacketIndex,
                    FileOffset = row.FileOffset,
                    Data = row.Data,
                    Info = row.Info,
                    TimestampText = row.TimestampText
                },
                row.Severity)).ToArray();
            Packets.Clear();
            foreach (var packet in packets)
                Packets.Add(CreateRow(packet.Data, packet.Severity));
            var selected = Packets.FirstOrDefault(row => row.PacketIndex == selectedPacketIndex) ?? Packets.FirstOrDefault();
            if (selected is not null)
                SelectPacket(selected);
            if (!IsBusy)
                StatusText = LocalizationManager.Instance.String_TsPacketViewer_Status_Ready;
        }
    }

    public async Task OnClosedAsync()
    {
        _isClosing = true;
        _generation++;
        App.LocalizationService.LanguageChanged -= OnLanguageChanged;
        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;
        await _service.DisposeAsync().ConfigureAwait(false);
    }
}
