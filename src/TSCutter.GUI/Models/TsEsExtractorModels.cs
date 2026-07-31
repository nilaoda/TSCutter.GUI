using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TSCutter.GUI.Models;

public sealed partial class TsEsExtractorTrackItem : ObservableObject
{
    public required int Pid { get; init; }
    public required string PidText { get; init; }
    public required string ProgramText { get; init; }
    public required string LanguageText { get; init; }
    public required string BitrateText { get; init; }
    public required byte StreamType { get; init; }
    public TsMpegAudioLayer? MpegAudioLayer { get; init; }
    public TsSupplementaryStreamType? SupplementaryStreamType { get; init; }

    [ObservableProperty]
    private string _streamText = string.Empty;

    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    private string _outputFileName = string.Empty;

    public event Action? SelectionChanged;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();
    partial void OnOutputFileNameChanged(string value) => SelectionChanged?.Invoke();
}

public sealed class TsEsExtractionOutput
{
    public required int Pid { get; init; }
    public required string OutputPath { get; init; }
}

public readonly record struct TsEsExtractionProgress(
    long BytesProcessed,
    long FileSize,
    long BytesWritten,
    double BytesPerSecond,
    TimeSpan Elapsed)
{
    public double Percent => FileSize > 0 ? BytesProcessed * 100.0 / FileSize : 0;
}

public sealed class TsEsExtractionTrackResult
{
    public required int Pid { get; init; }
    public required string OutputPath { get; init; }
    public required long BytesWritten { get; init; }
    public required long TransportErrors { get; init; }
    public required long ContinuityErrors { get; init; }
    public required long DuplicatePackets { get; init; }
    public required long InvalidPackets { get; init; }
    public required long MalformedPesHeaders { get; init; }
    public required long ScrambledPackets { get; init; }
}

public sealed class TsEsExtractionResult
{
    public required long BytesProcessed { get; init; }
    public required long BytesWritten { get; init; }
    public required TimeSpan Elapsed { get; init; }
    public required TsEsExtractionTrackResult[] Tracks { get; init; }
    public required long SyncLossBytes { get; init; }
    public long AnomalyCount { get; init; }
}

public enum TsEsExtractionErrorCode
{
    NoOutputs,
    DuplicatePid,
    InvalidOutputPath,
    DuplicateOutputPath,
    OutputExists,
    SameAsSource
}

public sealed class TsEsExtractionException(TsEsExtractionErrorCode code, params object[] arguments) : Exception
{
    public TsEsExtractionErrorCode Code { get; } = code;
    public object[] Arguments { get; } = arguments;
}
