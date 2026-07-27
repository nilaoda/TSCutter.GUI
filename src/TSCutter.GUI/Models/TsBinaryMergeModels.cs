using System;
using System.Collections.Generic;

namespace TSCutter.GUI.Models;

internal enum TsBinaryMergeProgressPhase
{
    Validating,
    Searching,
    Verifying,
    Writing
}

internal sealed record TsBinaryMergeSourceSnapshot(
    string FilePath,
    long FileSize,
    DateTime LastWriteTimeUtc);

internal sealed class TsBinaryMergeJoinAnalysis
{
    public required int SourceIndex { get; init; }
    public required int PreviousSourceIndex { get; init; }
    public required long OverlapBytes { get; init; }
    public required long AppendOffset { get; init; }
    public required bool HasReliableOverlap { get; init; }
    public bool IsFullyContained { get; init; }
}

internal sealed class TsBinaryMergeAnalysis
{
    public required IReadOnlyList<TsBinaryMergeSourceSnapshot> Sources { get; init; }
    public required IReadOnlyList<TsBinaryMergeJoinAnalysis> Joins { get; init; }
    public required long EstimatedOutputBytes { get; init; }
    public bool HasUnmatchedJoins { get; init; }
}

internal readonly record struct TsBinaryMergeProgress(
    TsBinaryMergeProgressPhase Phase,
    int CurrentSourceIndex,
    int SourceCount,
    long BytesProcessed,
    long TotalBytes,
    double BytesPerSecond,
    double Percent,
    TsBinaryMergeJoinAnalysis? CompletedJoin = null);

internal sealed class TsBinaryMergeResult
{
    public required string OutputPath { get; init; }
    public required long OutputBytes { get; init; }
    public required int SourceCount { get; init; }
    public required long RemovedOverlapBytes { get; init; }
    public required int UnmatchedJoinCount { get; init; }
    public required TimeSpan Elapsed { get; init; }
}

internal enum TsBinaryMergeErrorCode
{
    TooFewSources,
    SourceMissing,
    SourceChanged,
    InvalidPacketStructure,
    OutputMatchesSource,
    AnalysisRequired,
    AnalysisSourceMismatch,
    UnmatchedJoin
}

internal sealed class TsBinaryMergeException(
    TsBinaryMergeErrorCode code,
    params object[] arguments) : Exception
{
    public TsBinaryMergeErrorCode Code { get; } = code;
    public object[] Arguments { get; } = arguments;
}
