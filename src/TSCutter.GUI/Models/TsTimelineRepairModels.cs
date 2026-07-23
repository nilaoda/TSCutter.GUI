using System;
using System.Collections.Generic;

namespace TSCutter.GUI.Models;

public enum TsTimelineIssueKind
{
    TemporaryPcrOffset,
    GradualPcrDrift,
    PersistentClockDiscontinuity
}

public enum TsTimelineRepairErrorCode
{
    OutputMatchesSource,
    SourceChanged,
    SyncLost,
    NoSync
}

public sealed class TsTimelineRepairException(
    TsTimelineRepairErrorCode code,
    params object[] arguments) : Exception
{
    public TsTimelineRepairErrorCode Code { get; } = code;
    public object[] Arguments { get; } = arguments;
}

public sealed class TsTimelineRepairIssue
{
    public required TsTimelineIssueKind Kind { get; init; }
    public required int PcrPid { get; init; }
    public required long StartPacket { get; init; }
    public required long EndPacket { get; init; }
    public required double StartTimeSeconds { get; init; }
    public required double EndTimeSeconds { get; init; }
    public required double CorrectionSeconds { get; init; }
    public required bool AffectsStreamTimestamps { get; init; }
    public bool IsRepairable { get; init; } = true;
}

public sealed class TsTimelineRepairAnalysis
{
    public required string FilePath { get; init; }
    public required long FileSize { get; init; }
    public required long SyncOffset { get; init; }
    public required TsCheckResult CheckResult { get; init; }
    public List<TsTimelineRepairIssue> Issues { get; } = [];
    internal List<TsTimelineCorrectionSegment> Segments { get; } = [];
    public int RepairableIssueCount => Issues.FindAll(item => item.IsRepairable).Count;
    public bool HasTimestampCorrections => Issues.Exists(item => item.AffectsStreamTimestamps);
}

internal sealed class TsTimelineCorrectionSegment
{
    public required int PcrPid { get; init; }
    public required long StartPacket { get; init; }
    public required long EndPacketExclusive { get; init; }
    public required long StartAnchorPacket { get; init; }
    public required long EndAnchorPacket { get; init; }
    public required long StartAnchorPcr90k { get; init; }
    public required long EndAnchorPcr90k { get; init; }
    public required long ConstantOffset90k { get; init; }
    public required long TimestampStartCorrection90k { get; init; }
    public required long TimestampEndCorrection90k { get; init; }
    public required bool UseInterpolation { get; init; }
    public required bool AffectsStreamTimestamps { get; init; }

    public long GetCorrection90k(long packetIndex, long current90k)
    {
        if (packetIndex < StartPacket || packetIndex >= EndPacketExclusive)
            return 0;
        if (!UseInterpolation || EndAnchorPacket <= StartAnchorPacket)
            return ConstantOffset90k;

        var ratio = (packetIndex - StartAnchorPacket) / (double)(EndAnchorPacket - StartAnchorPacket);
        var expected = StartAnchorPcr90k +
                       (long)Math.Round((EndAnchorPcr90k - StartAnchorPcr90k) * ratio);
        return expected - current90k;
    }
}

public readonly record struct TsTimelineRepairProgress(
    long BytesProcessed,
    long FileSize,
    double BytesPerSecond,
    TimeSpan Elapsed,
    bool IsOutput)
{
    public double Percent => FileSize > 0 ? BytesProcessed * 100.0 / FileSize : 0;
}

public sealed class TsTimelineRepairResult
{
    public required string OutputPath { get; init; }
    public required long FileSize { get; init; }
    public required int RepairedIssueCount { get; init; }
    public required long RewrittenPcrCount { get; init; }
    public required long RewrittenTimestampCount { get; init; }
    public required int RemainingPcrErrorCount { get; init; }
    public required int RemainingPcrWarningCount { get; init; }
    public required TimeSpan Elapsed { get; init; }
}

public sealed class TsTimelineRepairIssueView
{
    public required TsTimelineRepairIssue Item { get; init; }
    public required string TimeText { get; init; }
    public required string KindText { get; init; }
    public required string PcrPidText { get; init; }
    public required string RangeText { get; init; }
    public required string CorrectionText { get; init; }
    public required string TimestampText { get; init; }
    public required string RepairableText { get; init; }
}
