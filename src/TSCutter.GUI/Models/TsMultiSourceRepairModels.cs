using System;
using System.Collections.Generic;
using System.Linq;

namespace TSCutter.GUI.Models;

public enum TsRepairErrorCode
{
    OutputIsSource,
    InvalidRepairData,
    SourceChanged,
    ReferenceChanged,
    NoImprovement
}

public sealed class TsRepairException(TsRepairErrorCode code, params object[] arguments) : Exception
{
    public TsRepairErrorCode Code { get; } = code;
    public object[] Arguments { get; } = arguments;
}

public enum TsRepairMatchConfidence
{
    None,
    Metadata,
    PacketFingerprint,
    ElementaryStreamFingerprint
}

public enum TsRepairElementaryKind
{
    None,
    Audio,
    MpegAudio,
    AacAdts,
    AacLatm,
    Av3a,
    MpegVideo,
    Cavs,
    Avs2,
    Avs3,
    H264,
    H265
}

public sealed class TsRepairSourceAnalysis
{
    public required string FilePath { get; init; }
    public required TsCheckResult Catalog { get; init; }
    public bool IsReference { get; init; }
    public int ContinuityErrors { get; set; }
    public int TransportErrors { get; set; }
    public int PesSizeErrors { get; set; }
    internal TsTimelineRepairAnalysis? TimelineAnalysis { get; set; }
}

public sealed class TsRepairTrackMatch
{
    public required string SourcePath { get; init; }
    public required int SourcePid { get; init; }
    public TsRepairMatchConfidence Confidence { get; set; }
    public int FingerprintMatches { get; set; }
    public long? TimestampOffset90k { get; set; }
    public int RepairedGapCount { get; set; }
}

public sealed class TsRepairTrackAnalysis
{
    public required int ReferencePid { get; init; }
    public required int ProgramNumber { get; init; }
    public required byte StreamType { get; init; }
    public TsMpegAudioLayer? MpegAudioLayer { get; init; }
    public TsSupplementaryStreamType? SupplementaryStreamType { get; init; }
    public string? Language { get; init; }
    public long PayloadPacketCount { get; set; }
    public long FirstPts90k { get; set; } = long.MaxValue;
    public long LastPts90k { get; set; } = long.MinValue;
    public int ContinuityErrorCount { get; set; }
    public int TransportErrorCount { get; set; }
    public int PesSizeErrorCount { get; set; }
    public List<TsRepairTrackMatch> Matches { get; } = [];
    public List<TsRepairGap> Gaps { get; } = [];
    public List<TsRepairPesRegion> PesRegions { get; } = [];
    public int RepairableGapCount { get; set; }
    public int RepairablePesRegionCount { get; set; }
    public int IssueCount => Gaps.Count + PesRegions.Count;
    public int RepairableIssueCount => RepairableGapCount + RepairablePesRegionCount;
}

public readonly record struct TsRepairPesSignature(ulong Hash, int ElementaryLength);

public enum TsRepairPesRegionReason
{
    PesSizeMismatch,
    CorrelatedVideoElementaryMismatch
}

public sealed class TsRepairPesRegion
{
    public required int ReferencePid { get; init; }
    public required long ReferenceStartOffset { get; init; }
    public required long ReferenceEndOffset { get; init; }
    public required int ReferenceStartContinuityCounter { get; init; }
    public required int ReferencePacketCount { get; init; }
    public required long ReferenceFirstPts90k { get; set; }
    public required long ReferenceLastPts90k { get; set; }
    public required long[] ReferencePts90k { get; set; }
    internal long[] ReferencePtsFileOffsets { get; init; } = [];
    public required int MismatchCount { get; init; }
    public TsRepairPesRegionReason Reason { get; init; } = TsRepairPesRegionReason.PesSizeMismatch;
    public required TsRepairPesSignature[] BeforeAnchor { get; init; }
    public required TsRepairPesSignature[] ReferenceSignatures { get; init; }
    public required TsRepairPesSignature[] AfterAnchor { get; init; }
    public long[] ReferencePesStartOffsets { get; init; } = [];
    public long[] ReferencePesEndOffsets { get; init; } = [];
    public int[] ReferencePesStartContinuityCounters { get; init; } = [];
    public int[] ReferencePesPacketCounts { get; init; } = [];
    public List<TsRepairPesRegionCandidate> Candidates { get; } = [];
}

public sealed class TsRepairPesRegionCandidate
{
    public required string SourcePath { get; init; }
    public required int SourcePid { get; init; }
    public required long SourceStartOffset { get; init; }
    public required long SourceEndOffset { get; init; }
    public required int PacketCount { get; init; }
    public required long TimestampOffset90k { get; init; }
    public required int ElementaryLength { get; init; }
    public required int FingerprintMatches { get; init; }
    public long? ReferenceStartOffset { get; init; }
    public long? ReferenceEndOffset { get; init; }
    public int? ReferenceStartContinuityCounter { get; init; }
    public int? ReferencePacketCount { get; init; }
}

public sealed class TsRepairGap
{
    public required int ReferencePid { get; init; }
    public required long ReferenceInsertOffset { get; init; }
    public required int ExpectedContinuityCounter { get; init; }
    public required int MissingPacketModulo { get; init; }
    public long ReferencePts90k { get; set; } = long.MinValue;
    public required ulong[] BeforeAnchor { get; init; }
    public required ulong[] AfterAnchor { get; init; }
    public byte[]? BeforeElementaryAnchor { get; init; }
    public byte[]? BeforeElementarySuffix { get; init; }
    public int BeforeElementaryRepeatedSuffixLength { get; init; }
    public byte BeforeElementaryRepeatedSuffixByte { get; init; }
    public byte[]? AfterElementaryAnchor { get; init; }
    public int? ElementaryBytesUntilPesEnd { get; init; }
    public int? ElementaryBytesUntilFrameEnd { get; init; }
    public TsRepairElementaryKind ElementaryKind { get; init; }
    public long[] ReferenceDiscardOffsets { get; init; } = [];
    public List<TsRepairGapCandidate> Candidates { get; } = [];
}

public sealed class TsRepairGapCandidate
{
    public required string SourcePath { get; init; }
    public required int SourcePid { get; init; }
    public long[] SourcePacketOffsets { get; init; } = [];
    public byte[]? ElementaryPayload { get; init; }
    public TsRepairPesBoundary[] PesBoundaries { get; init; } = [];
    public int SynthesizedPacketCount { get; init; }
    public int SourcePayloadPacketCount { get; init; }
}

public sealed class TsRepairLargeGapTrackBoundary
{
    public required int ReferencePid { get; init; }
    public required long ReferenceMissingStartPts90k { get; set; }
    public required long ReferenceAfterPts90k { get; set; }
    public required long ReferenceAfterOffset { get; init; }
    public required int ReferenceAfterContinuityCounter { get; init; }
    public ulong[] BeforePacketAnchor { get; init; } = [];
    public ulong[] AfterPacketAnchor { get; init; } = [];
}

public sealed class TsRepairLargeGapTrackCandidate
{
    public required int ReferencePid { get; init; }
    public required int SourcePid { get; init; }
    public required int SourcePacketCount { get; init; }
    public required int SourcePayloadPacketCount { get; init; }
    public required long TimestampOffset90k { get; init; }
    public required long SourceStartOffset { get; init; }
    public required long SourceEndOffset { get; init; }
}

public sealed class TsRepairLargeGapCandidate
{
    public required string SourcePath { get; init; }
    public required long SourceStartOffset { get; init; }
    public required long SourceEndOffset { get; init; }
    public required long TimestampOffset90k { get; init; }
    public List<TsRepairLargeGapTrackCandidate> Tracks { get; } = [];
}

public sealed class TsRepairLargeGap
{
    public required int ProgramNumber { get; init; }
    public required int AnchorReferencePid { get; init; }
    public required long ReferenceInsertOffset { get; init; }
    public required long ReferenceBeforePts90k { get; set; }
    public long ReferenceOriginalMissingStartPts90k { get; set; } = long.MinValue;
    public required long ReferenceMissingStartPts90k { get; set; }
    public required long ReferenceAfterPts90k { get; set; }
    public required TsRepairPesSignature[] BeforeAnchor { get; init; }
    public required TsRepairPesSignature[] AfterAnchor { get; init; }
    public List<TsRepairLargeGapTrackBoundary> Tracks { get; } = [];
    public List<TsRepairLargeGapCandidate> Candidates { get; } = [];
    public bool IncludesPrecedingDamage =>
        ReferenceOriginalMissingStartPts90k != long.MinValue &&
        ReferenceMissingStartPts90k < ReferenceOriginalMissingStartPts90k;
    public long MissingDuration90k => Math.Max(0, ReferenceAfterPts90k - ReferenceMissingStartPts90k);
    public bool IsFullyRepairable => Candidates.Any(candidate => candidate.Tracks.Count >= Tracks.Count);
    public bool IsPartiallyRepairable => !IsFullyRepairable && Candidates.Any(candidate => candidate.Tracks.Count > 0);
}

public readonly record struct TsRepairPesBoundary(int ElementaryOffset, byte[] PesHeader);

public sealed class TsMultiSourceAnalysisResult
{
    public required TsRepairSourceAnalysis ReferenceSource { get; init; }
    public List<TsRepairSourceAnalysis> Sources { get; } = [];
    public List<TsRepairTrackAnalysis> Tracks { get; } = [];
    public List<TsRepairLargeGap> LargeGaps { get; } = [];
    public List<TsBroadcastTimeAnchor> ReferenceBroadcastTimes { get; } = [];
    public long TimelineStartPts90k { get; set; } = long.MinValue;
    public long TimelineEndPts90k { get; set; } = long.MinValue;
    public long LargeGapTimelineStartPts90k { get; set; } = long.MinValue;
    public int TotalGapCount => GetIssueCount(repairableOnly: false);
    public int RepairableGapCount => GetIssueCount(repairableOnly: true);
    public int RepairableLargeGapCount => LargeGaps.Count(item => item.Candidates.Count > 0);
    public long LargeGapDuration90k => LargeGaps.Sum(item => item.MissingDuration90k);

    private int GetIssueCount(bool repairableOnly)
    {
        var result = 0;
        foreach (var track in Tracks)
            result += repairableOnly ? track.RepairableIssueCount : track.IssueCount;
        return result;
    }
}

public enum TsMultiSourceProgressPhase
{
    ReferenceScan,
    DonorTimelineAnalysis,
    DonorScan,
    DonorMatchingScan,
    LargeGapMatching
}

public readonly record struct TsMultiSourceProgress(
    int SourceIndex,
    int SourceCount,
    string FilePath,
    long BytesProcessed,
    long FileSize,
    double BytesPerSecond,
    TimeSpan Elapsed,
    bool IsIntensiveAnalysis = false,
    int IntensiveTaskCompleted = 0,
    int IntensiveTaskCount = 0,
    TsMultiSourceProgressPhase Phase = TsMultiSourceProgressPhase.DonorMatchingScan)
{
    public double Percent => FileSize > 0 ? BytesProcessed * 100.0 / FileSize : 0;
}

public readonly record struct TsRepairSourceCompleted(
    string FilePath,
    bool IsReference,
    bool HasProgram,
    int ContinuityErrors,
    int TransportErrors,
    int PesSizeErrors)
{
    public int ErrorCount => ContinuityErrors + TransportErrors + PesSizeErrors;
}

public sealed class TsRepairOutputPlan
{
    public required TsMultiSourceAnalysisResult Analysis { get; init; }
    public HashSet<int> SelectedPids { get; } = [];
    public HashSet<long> SelectedLargeGapOffsets { get; } = [];
    public bool IncludeServiceInformation { get; init; } = true;
    public Dictionary<long, List<TsPacketInsertion>> Insertions { get; } = [];
    public Dictionary<long, List<TsLargeGapInsertion>> LargeGapInsertions { get; } = [];
    public List<TsPacketReplacement> Replacements { get; } = [];
    public HashSet<long> DiscardPacketOffsets { get; } = [];
    public int RepairedGapCount { get; set; }
    public long RepairedPacketCount { get; set; }
    public int RepairedPesRegionCount { get; set; }
    public int RepairedLargeGapCount { get; set; }
    public long RepairedLargeGapDuration90k { get; set; }
}

public sealed class TsLargeGapTrackInsertion
{
    public required int SourcePid { get; init; }
    public required int TargetPid { get; init; }
    public required int StartContinuityCounter { get; init; }
    public required int SourcePacketCount { get; init; }
    public required int SourcePayloadPacketCount { get; init; }
    public required long TimestampOffset90k { get; init; }
    public required long SourceStartOffset { get; init; }
    public required long SourceEndOffset { get; init; }
    public required long ReferenceDiscardStartOffset { get; init; }
    public required long ReferenceDiscardEndOffset { get; init; }
}

public sealed class TsLargeGapInsertion
{
    public required string SourcePath { get; init; }
    public required long SourceStartOffset { get; init; }
    public required long SourceEndOffset { get; init; }
    public required long Duration90k { get; init; }
    public List<TsLargeGapTrackInsertion> Tracks { get; } = [];
}

public sealed class TsPacketInsertion
{
    public required string SourcePath { get; init; }
    public required int SourcePid { get; init; }
    public required int TargetPid { get; init; }
    public required int StartContinuityCounter { get; init; }
    public long[] SourcePacketOffsets { get; init; } = [];
    public byte[]? ElementaryPayload { get; init; }
    public TsRepairPesBoundary[] PesBoundaries { get; init; } = [];
    public int SynthesizedPacketCount { get; init; }
    public long TimestampOffset90k { get; init; }
}

public sealed class TsPacketReplacement
{
    public required string SourcePath { get; init; }
    public required int SourcePid { get; init; }
    public required int TargetPid { get; init; }
    public required long ReferenceStartOffset { get; init; }
    public required long ReferenceEndOffset { get; init; }
    public required int StartContinuityCounter { get; init; }
    public required int ReferencePacketCount { get; init; }
    public required long SourceStartOffset { get; init; }
    public required long SourceEndOffset { get; init; }
    public required int PacketCount { get; init; }
    public required long TimestampOffset90k { get; init; }
    public bool ElementaryPayloadOnly { get; init; }
    public int ElementaryLength { get; init; }
}

public sealed class TsRepairOutputResult
{
    public required TsFilterResult FilterResult { get; init; }
    public required int RepairedGapCount { get; init; }
    public required long RepairedPacketCount { get; init; }
    public required int RepairedPesRegionCount { get; init; }
    public required int RepairedLargeGapCount { get; init; }
    public required long RepairedLargeGapDuration90k { get; init; }
    public required long ReferenceErrorCount { get; init; }
    public required long RemainingErrorCount { get; init; }
    public required TsRepairOutputPlan Plan { get; init; }
}
