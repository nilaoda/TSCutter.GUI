using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TSCutter.GUI.Models;

public enum TsRemuxOutputMode
{
    Compact,
    PreservePacketCount
}

public sealed record TsRemuxLanguageOption(string Code, string DisplayName);

public readonly record struct TsRemuxChangeSummary(
    int SelectedServices,
    int SelectedTracks,
    int ChangedIdentifiers,
    int ChangedMetadata,
    int ChangedLanguages,
    int ReorderedTracks);

public enum TsRemuxErrorCode
{
    SameFile,
    SyncLost,
    NoServiceSelected,
    NoTrackSelected,
    InvalidServiceId,
    InvalidPid,
    DuplicateServiceId,
    DuplicatePid,
    InvalidLanguage,
    MissingProgram,
    EncryptedServiceUnsupported,
    PatTooLarge,
    PmtTooLarge,
    SdtTooLarge,
    MetadataRequiresServiceInformationSlots,
    PreserveEpgRequiresUnchangedServices,
    InsufficientPsiPacketSlots
}

public sealed class TsRemuxException(TsRemuxErrorCode code, params object[] arguments) : Exception
{
    public TsRemuxErrorCode Code { get; } = code;
    public object[] Arguments { get; } = arguments;
}

public sealed class TsRemuxConfiguration
{
    public required IReadOnlyList<TsRemuxServiceConfiguration> Services { get; init; }
    public bool KeepEpg { get; init; } = true;
    public TsRemuxOutputMode OutputMode { get; init; }
}

public sealed class TsRemuxServiceConfiguration
{
    public required int SourceServiceId { get; init; }
    public required int OutputServiceId { get; init; }
    public required int OutputPmtPid { get; init; }
    public byte? OutputServiceType { get; init; }
    public required bool WriteServiceName { get; init; }
    public required string ServiceName { get; init; }
    public required bool WriteProviderName { get; init; }
    public required string ProviderName { get; init; }
    public required IReadOnlyList<TsRemuxTrackConfiguration> Tracks { get; init; }
}

public sealed record TsRemuxServiceTypeOption(byte? Value, string DisplayName);

public sealed class TsRemuxTrackConfiguration
{
    public required int SourcePid { get; init; }
    public required int OutputPid { get; init; }
    public required bool Keep { get; init; }
    public string? OutputLanguageCode { get; init; }
    public int Order { get; init; }
}

internal sealed class TsRemuxPlan
{
    public required TsCheckResult Catalog { get; init; }
    public required TsRemuxOutputMode OutputMode { get; init; }
    public required bool KeepEpg { get; init; }
    public required IReadOnlyList<TsRemuxProgramPlan> Programs { get; init; }
    public required IReadOnlyDictionary<int, int> SourcePidMap { get; init; }
    public required IReadOnlyDictionary<int, int> SourcePidServiceIds { get; init; }
    public required IReadOnlySet<int> FullPayloadSourcePids { get; init; }
    public required IReadOnlySet<int> PcrOnlySourcePids { get; init; }
    public required IReadOnlyDictionary<int, byte[]> StaticSectionsBySourcePid { get; init; }
    public required IReadOnlyDictionary<int, int> ServiceIdMap { get; init; }
    public required bool NeedsSdt { get; init; }
    public required bool InjectSdtAfterPat { get; init; }
    public required bool PreserveEitPackets { get; init; }
}

internal sealed class TsRemuxProgramPlan
{
    public required int SourceServiceId { get; init; }
    public required int OutputServiceId { get; init; }
    public required int SourcePmtPid { get; init; }
    public required int OutputPmtPid { get; init; }
    public required int SourcePcrPid { get; init; }
    public required int OutputPcrPid { get; init; }
    public required IReadOnlyList<TsRemuxStreamPlan> Streams { get; init; }
    public required byte[] ServiceDescriptors { get; init; }
    public required TsServiceSummary? SourceService { get; init; }
}

internal sealed class TsRemuxStreamPlan
{
    public required int SourcePid { get; init; }
    public required int OutputPid { get; init; }
    public required TsStreamDefinition Definition { get; init; }
}

public readonly record struct TsRemuxProgress(
    long BytesProcessed,
    long FileSize,
    long BytesWritten,
    long PacketsWritten,
    double BytesPerSecond,
    TimeSpan Elapsed)
{
    public double Percent => FileSize > 0 ? BytesProcessed * 100.0 / FileSize : 0;
}

public sealed class TsRemuxResult
{
    public required long BytesProcessed { get; init; }
    public required long BytesWritten { get; init; }
    public required long PacketsWritten { get; init; }
    public required long TransportErrors { get; init; }
    public required long ContinuityErrors { get; init; }
    public required TimeSpan Elapsed { get; init; }
}

public sealed partial class TsRemuxServiceItem : ObservableObject
{
    public required int SourceServiceId { get; init; }
    public required string SourceServiceIdText { get; init; }
    public required string OriginalServiceName { get; init; }
    public required string OriginalProviderName { get; init; }
    public required string SourcePmtPidText { get; init; }
    public required bool IsEncrypted { get; init; }
    public required TsServiceSummary? SourceService { get; init; }
    public required ObservableCollection<TsRemuxTrackItem> Tracks { get; init; }

    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    private string _outputServiceIdText = string.Empty;

    [ObservableProperty]
    private string _outputPmtPidText = string.Empty;

    [ObservableProperty]
    private TsRemuxServiceTypeOption? _selectedServiceType;

    [ObservableProperty]
    private bool _writeServiceName;

    [ObservableProperty]
    private string _serviceName = string.Empty;

    [ObservableProperty]
    private bool _writeProviderName;

    [ObservableProperty]
    private string _providerName = string.Empty;

    [ObservableProperty]
    private string _trackSummary = string.Empty;

    public event Action? SelectionChanged;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();
    partial void OnOutputServiceIdTextChanged(string value) => SelectionChanged?.Invoke();
    partial void OnOutputPmtPidTextChanged(string value) => SelectionChanged?.Invoke();
    partial void OnSelectedServiceTypeChanged(TsRemuxServiceTypeOption? value) => SelectionChanged?.Invoke();
    partial void OnWriteServiceNameChanged(bool value) => SelectionChanged?.Invoke();
    partial void OnServiceNameChanged(string value) => SelectionChanged?.Invoke();
    partial void OnWriteProviderNameChanged(bool value) => SelectionChanged?.Invoke();
    partial void OnProviderNameChanged(string value) => SelectionChanged?.Invoke();
}

public sealed partial class TsRemuxTrackItem : ObservableObject
{
    public required int SourcePid { get; init; }
    public required string SourcePidText { get; init; }
    public required string OriginalLanguageCode { get; init; }
    public required bool IsPcrSource { get; init; }
    public required bool IsPcrOnly { get; init; }
    public required TsStreamDefinition? Definition { get; init; }
    public required int OriginalOrder { get; init; }
    public required IReadOnlyList<TsRemuxLanguageOption> LanguageOptions { get; init; }

    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    private string _outputPidText = string.Empty;

    [ObservableProperty]
    private string _streamText = string.Empty;

    [ObservableProperty]
    private string _typeText = string.Empty;

    [ObservableProperty]
    private string _roleText = string.Empty;

    [ObservableProperty]
    private string _bitrateText = "-";

    [ObservableProperty]
    private TsRemuxLanguageOption? _selectedLanguage;

    public event Action? SelectionChanged;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();
    partial void OnOutputPidTextChanged(string value) => SelectionChanged?.Invoke();
    partial void OnSelectedLanguageChanged(TsRemuxLanguageOption? value) => SelectionChanged?.Invoke();
}
