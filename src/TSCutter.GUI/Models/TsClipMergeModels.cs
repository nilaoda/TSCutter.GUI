using System;
using System.Collections.Generic;

namespace TSCutter.GUI.Models;

internal readonly record struct TsClipMergeRange(
    long StartPosition,
    long EndPosition,
    double StartTimeSeconds,
    double EndTimeSeconds);

internal sealed class TsClipMergeRequest
{
    public required string SourcePath { get; init; }
    public required string OutputPath { get; init; }
    public required IReadOnlyList<TsClipMergeRange> Ranges { get; init; }
}

internal readonly record struct TsClipMergeProgress(
    long BytesProcessed,
    long TotalBytes,
    double BytesPerSecond,
    TimeSpan Elapsed)
{
    public double Percent => TotalBytes > 0 ? BytesProcessed * 100.0 / TotalBytes : 0;
}

internal sealed class TsClipMergeResult
{
    public required string OutputPath { get; init; }
    public required long OutputBytes { get; init; }
    public required int SegmentCount { get; init; }
    public required long RewrittenPcrCount { get; init; }
    public required long RewrittenTimestampCount { get; init; }
    public required long RewrittenContinuityCount { get; init; }
    public required TimeSpan Elapsed { get; init; }
}

internal enum TsClipMergeErrorCode
{
    OutputMatchesSource,
    SourceChanged,
    NoSync,
    InvalidRange
}

internal sealed class TsClipMergeException(
    TsClipMergeErrorCode code,
    params object[] arguments) : Exception
{
    public TsClipMergeErrorCode Code { get; } = code;
    public object[] Arguments { get; } = arguments;
}
