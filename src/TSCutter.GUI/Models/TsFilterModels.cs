using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TSCutter.GUI.Models;

public enum TsFilterErrorCode
{
    SameFile,
    SyncLost,
    PatTooLarge,
    PmtTooLarge
}

public sealed class TsFilterException(TsFilterErrorCode code, params object[] arguments) : Exception
{
    public TsFilterErrorCode Code { get; } = code;
    public object[] Arguments { get; } = arguments;
}

public sealed partial class TsFilterPidItem : ObservableObject
{
    public required int Pid { get; init; }
    public required string PidText { get; init; }
    public required string StreamText { get; init; }
    public required string ProgramText { get; init; }
    public required string SamplePacketCountText { get; init; }
    public required long SamplePacketCount { get; init; }
    public bool IsProgramStream { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WillOutput))]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WillOutput))]
    private bool _isAutoIncluded;

    [ObservableProperty]
    private string _outputReason = string.Empty;

    public bool WillOutput => IsSelected || IsAutoIncluded;
    public event Action? SelectionChanged;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();
}

public sealed class TsFilterPlan
{
    public HashSet<int> ExplicitPids { get; } = [];
    public HashSet<int> EffectivePids { get; } = [];
    public HashSet<int> SelectedPrograms { get; } = [];
    public HashSet<int> PcrOnlyPids { get; } = [];
    public HashSet<int> ServiceInformationPids { get; } = [];
    public Dictionary<int, byte[]> PsiSections { get; } = [];
    public long EstimatedPacketCount { get; set; }
}

public readonly record struct TsFilterProgress(
    long BytesProcessed,
    long FileSize,
    long BytesWritten,
    long PacketsWritten,
    double BytesPerSecond,
    TimeSpan Elapsed)
{
    public double Percent => FileSize > 0 ? BytesProcessed * 100.0 / FileSize : 0;
}

public sealed class TsFilterResult
{
    public required long BytesProcessed { get; init; }
    public required long BytesWritten { get; init; }
    public required long PacketsWritten { get; init; }
    public required TimeSpan Elapsed { get; init; }
}
