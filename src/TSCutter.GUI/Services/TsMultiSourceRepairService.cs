using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.Services;

public sealed class TsMultiSourceRepairService
{
    private const int PacketSize = TsStreamAnalyzer.PacketSize;
    private const long TimestampWrap = 1L << 33;
    private const int ReadBufferSize = PacketSize * 32_768;
    private const int ParallelReadBufferSize = PacketSize * 8_192;
    private const int AnchorLength = 4;
    private const int ElementaryAnchorLength = 128;
    private const int MaxElementaryAnchorSuffixLength = MaxElementaryRepairBytes;
    private const int MaxElementaryRepeatedSuffixLength = 256 * 1024;
    private const int ElementaryAnchorHistoryLength =
        ElementaryAnchorLength + MaxElementaryRepeatedSuffixLength;
    private const int ElementarySampleLength = 64;
    private const int AudioElementarySampleInterval = 64 * 1024;
    private const int VideoElementarySampleInterval = 1024 * 1024;
    private const int MaxElementarySamples = 256;
    private const int MinimumElementarySampleMatches = 3;
    private const int FingerprintSampleInterval = 2_048;
    private const int MaxFingerprintSamples = 2_048;
    private const int MaxRepairPacketsPerGap = 256;
    private const int MaxElementaryRepairPackets = 15;
    private const int MaxElementaryRepairBytes = MaxElementaryRepairPackets * 184;
    private const int MaxElementaryRepairGapsPerMapping = 1_024;
    private const long ElementaryGapSearchPadding90k = 2L * 90_000;
    private const long DonorTimestampIndexInterval90k = 45_000;
    private const long MaxElementarySearchWindowDuration90k = 60L * 90_000;
    private const int MaxElementaryGapsPerSearchTask = 64;
    private const int MaxParallelElementarySearchTasks = 4;
    private const int MaxReferenceGaps = 50_000;
    private const int PesRegionAnchorCount = 2;
    private const int VideoPesRegionAnchorCount = 8;
    private const int PesRegionClosingGoodPesCount = 48;
    private const int MaxPesRegionPackets = 4_096;
    private const int MaxVideoPesRegionPackets = 131_072;
    private const int MaxPesRegionsPerTrack = 4_096;
    private const int MaxIndexedVideoPes = 500_000;
    private const int MaxVideoRegionCandidatesPerSource = 32;
    private const long CorrelatedVideoPadding90k = 45_000;
    private const int LargeGapAnchorCount = 4;
    private const int LargeGapRecentDeltaCount = 31;
    private const int MaxLargeGapsPerTrack = 4_096;
    private const long MinimumLargeGap90k = 90_000;
    private const long MaximumLargeGap90k = 10L * 60 * 90_000;
    private const long LargeGapMatchTolerance90k = 18_000;
    private const long LargeGapPesBoundaryTolerance90k = 900;
    private const long LargeGapTrackCorrelationTolerance90k = 90_000;
    private const long LargeGapRangeInspectionPaddingBytes = 8L * 1024 * 1024;
    private const long LargeGapIncidentHistoryWindow90k = 60L * 90_000;
    private const long LargeGapIncidentWeakMaximumLookback90k = 15L * 90_000;
    private const long LargeGapIncidentMaximumHealthyInterval90k = 2L * 90_000;
    private const long LargeGapIncidentCorrelatedTrackWindow90k = 90_000;
    private const int MaxLargeGapIncidentSnapshotsPerTrack = 64;
    private const int MaxLargeGapIncidentHistoryPesPerTrack = 16_384;
    private const ulong ElementaryHashBase = 257;
    private static readonly ulong ElementaryHashOldestFactor =
        ComputePower(ElementaryHashBase, ElementaryAnchorLength - 1);
    private static readonly ulong ElementarySampleHashOldestFactor =
        ComputePower(ElementaryHashBase, ElementarySampleLength - 1);

    public async Task<TsMultiSourceAnalysisResult> AnalyzeAsync(
        IReadOnlyList<string> sourcePaths,
        string referencePath,
        bool normalizeTimeline = true,
        IProgress<TsMultiSourceProgress>? progress = null,
        IProgress<TsRepairSourceCompleted>? sourceCompleted = null,
        CancellationToken cancellationToken = default)
    {
        if (sourcePaths.Count < 2)
            throw new ArgumentException("At least two source files are required.", nameof(sourcePaths));

        var normalizedReference = Path.GetFullPath(referencePath);
        var sources = new List<TsRepairSourceAnalysis>(sourcePaths.Count);
        foreach (var path in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedPath = Path.GetFullPath(path);
            var analyzer = new TsStreamAnalyzer();
            // 多源工具只需先探测节目目录；详细事件、码率时间轴和完整指纹均由后续按需扫描负责。
            var catalog = await Task.Run(() => analyzer.AnalyzeAsync(
                normalizedPath, cancellationToken: cancellationToken,
                options: new TsStreamAnalyzeOptions
                {
                    InventoryOnly = true,
                    MaxBytes = 256L * 1024 * 1024,
                    Features = TsStreamAnalyzeFeatures.None
                }), cancellationToken).ConfigureAwait(false);
            sources.Add(new TsRepairSourceAnalysis
            {
                FilePath = normalizedPath,
                Catalog = catalog,
                IsReference = PathsEqual(normalizedPath, normalizedReference)
            });
        }

        var reference = sources.FirstOrDefault(item => item.IsReference) ??
                        throw new ArgumentException("The reference file must be present in the source list.", nameof(referencePath));

        // 参考源在自己的主扫描中同步采集 PCR，扫描结束后再回写已记录的 PTS，
        // 避免从网络盘重复读取参考文件。辅助源则在各自主扫描前按需准备虚拟时间轴。
        var timelineService = new TsTimelineRepairService();
        var result = new TsMultiSourceAnalysisResult { ReferenceSource = reference };
        result.Sources.AddRange(sources);
        var referenceStates = BuildReferenceTracks(reference, result.Tracks);
        var referencePcrCollector = normalizeTimeline
            ? new ReferencePcrCollector(reference.Catalog.SyncOffset)
            : null;

        var scanOrdinal = 0;
        await ScanReferenceAsync(reference, referenceStates, result.ReferenceBroadcastTimes,
                referencePcrCollector, scanOrdinal++, sources.Count, progress, cancellationToken)
            .ConfigureAwait(false);
        foreach (var state in referenceStates.Values)
            state.CompletePesRegions();
        var rawTimedTracks = result.Tracks
            .Where(track => track.FirstPts90k != long.MaxValue)
            .ToArray();
        if (rawTimedTracks.Length > 0)
            result.LargeGapTimelineStartPts90k = rawTimedTracks.Min(track => track.FirstPts90k);
        if (referencePcrCollector is not null)
        {
            reference.TimelineAnalysis = TsTimelineRepairService.BuildAnalysisFromPcrSamples(
                reference.FilePath, reference.Catalog, referencePcrCollector.Samples);
            ApplyReferenceTimelineNormalization(
                reference, referenceStates.Values, result.ReferenceBroadcastTimes);
        }
        // 先把音频 PES 故障关联到同时间的视频 ES 区域，复合大段缺口才能从
        // 同次事故中最早的视频异常开始，而不是只看到稍后的音频/CC 异常。
        CreateCorrelatedVideoRegions(referenceStates.Values);
        CreateLargeGaps(referenceStates.Values, result.LargeGaps);
        foreach (var track in result.Tracks)
        {
            track.PesSizeErrorCount = track.PesRegions
                .Where(region => region.Reason == TsRepairPesRegionReason.PesSizeMismatch)
                .Sum(region => region.MismatchCount);
        }
        reference.PesSizeErrors = result.Tracks.Sum(track => track.PesSizeErrorCount);
        ReportSourceCompleted(reference, sourceCompleted);

        for (var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
        {
            var source = sources[sourceIndex];
            if (source.IsReference)
                continue;
            if (normalizeTimeline && source.Catalog.SyncOffset >= 0 &&
                source.Catalog.Programs.Count > 0)
            {
                var sourceOrdinal = scanOrdinal;
                var timelineProgress = progress is null
                    ? null
                    : new Progress<TsTimelineRepairProgress>(value => progress.Report(
                        new TsMultiSourceProgress(
                            sourceOrdinal, sources.Count, source.FilePath,
                            value.BytesProcessed, value.FileSize,
                            value.BytesPerSecond, value.Elapsed,
                            Phase: TsMultiSourceProgressPhase.DonorTimelineAnalysis)));
                source.TimelineAnalysis = await timelineService.AnalyzeAsync(
                        source.FilePath, source.Catalog, timelineProgress, cancellationToken)
                    .ConfigureAwait(false);
            }
            var mappings = BuildTrackMappings(source, referenceStates);
            if (mappings.Count == 0)
            {
                scanOrdinal++;
                ReportSourceCompleted(source, sourceCompleted);
                continue;
            }
            await ScanDonorAsync(source, mappings, scanOrdinal++, sources.Count, progress, cancellationToken)
                .ConfigureAwait(false);
            ReportSourceCompleted(source, sourceCompleted);
        }

        foreach (var track in result.Tracks)
        {
            var repairable = 0;
            foreach (var gap in track.Gaps)
            {
                if (gap.Candidates.Count > 0)
                    repairable++;
            }
            track.RepairableGapCount = repairable;
            track.RepairablePesRegionCount = track.PesRegions.Count(region => region.Candidates.Count > 0);
        }
        ValidateCorrelatedVideoCandidates(result.Tracks);
        var timedTracks = result.Tracks
            .Where(track => track.FirstPts90k != long.MaxValue && track.LastPts90k != long.MinValue)
            .ToArray();
        if (timedTracks.Length > 0)
        {
            result.TimelineStartPts90k = timedTracks.Min(track => track.FirstPts90k);
            result.TimelineEndPts90k = timedTracks.Max(track => track.LastPts90k);
        }
        return result;
    }

    public async Task MatchLargeGapsAsync(
        TsMultiSourceAnalysisResult analysis,
        IProgress<TsMultiSourceProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        foreach (var gap in analysis.LargeGaps)
            gap.Candidates.Clear();
        if (analysis.LargeGaps.Count == 0)
            return;

        var donors = analysis.Sources.Where(item => !item.IsReference).ToArray();
        for (var donorIndex = 0; donorIndex < donors.Length; donorIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var donor = donors[donorIndex];
            var matchers = new List<LargeGapAnchorMatcher>();
            foreach (var gap in analysis.LargeGaps)
            {
                var anchorTrack = analysis.Tracks.FirstOrDefault(item =>
                    item.ReferencePid == gap.AnchorReferencePid);
                var match = anchorTrack?.Matches
                    .Where(item => PathsEqual(item.SourcePath, donor.FilePath))
                    .OrderByDescending(item => item.Confidence)
                    .ThenByDescending(item => item.FingerprintMatches)
                    .FirstOrDefault();
                if (match is null)
                    continue;
                matchers.Add(new LargeGapAnchorMatcher(gap, match));
            }
            if (matchers.Count == 0)
                continue;

            await ScanLargeGapAnchorsAsync(
                    donor, matchers, donorIndex, donors.Length, progress, cancellationToken)
                .ConfigureAwait(false);
            foreach (var matcher in matchers)
            {
                foreach (var range in matcher.Results)
                {
                    var candidate = await InspectLargeGapRangeAsync(
                            analysis, donor, range, cancellationToken)
                        .ConfigureAwait(false);
                    if (candidate.Tracks.Count == 0 || matcher.Gap.Candidates.Any(item =>
                            PathsEqual(item.SourcePath, candidate.SourcePath) &&
                            item.SourceStartOffset == candidate.SourceStartOffset &&
                            item.SourceEndOffset == candidate.SourceEndOffset))
                    {
                        continue;
                    }
                    matcher.Gap.Candidates.Add(candidate);
                }
            }
        }
    }

    private static async Task ScanLargeGapAnchorsAsync(
        TsRepairSourceAnalysis donor,
        IReadOnlyList<LargeGapAnchorMatcher> matchers,
        int sourceIndex,
        int sourceCount,
        IProgress<TsMultiSourceProgress>? progress,
        CancellationToken cancellationToken)
    {
        var states = matchers
            .GroupBy(item => item.Match.SourcePid)
            .ToDictionary(group => group.Key, group => new LargeGapDonorPidState(group, null));
        await ScanPacketsAsync(
            donor.FilePath, donor.Catalog.SyncOffset, sourceIndex, sourceCount, progress,
            TsMultiSourceProgressPhase.LargeGapMatching,
            (packet, fileOffset) =>
            {
                if (!TryParsePacket(packet, out var info) ||
                    !states.TryGetValue(info.Pid, out var state))
                {
                    return;
                }
                if (info.TransportError || info.Discontinuity)
                {
                    state.ResetForDiscontinuity();
                    return;
                }
                if (info.HasPayload && state.HasContinuity)
                {
                    var expected = (state.LastContinuityCounter + 1) & 0x0F;
                    if (info.ContinuityCounter == state.LastContinuityCounter)
                        return;
                    if (info.ContinuityCounter != expected)
                        state.ResetForDiscontinuity();
                }
                if (info.HasPayload)
                {
                    state.HasContinuity = true;
                    state.LastContinuityCounter = info.ContinuityCounter;
                }
                var elementaryPayload = info.HasPayload
                    ? GetElementaryPayload(packet, info, out _)
                    : default;
                state.ProcessPes(packet, info, fileOffset, elementaryPayload, donor.FilePath);
            }, cancellationToken).ConfigureAwait(false);
        foreach (var state in states.Values)
            state.Complete(donor.Catalog.FileSize, donor.FilePath);
    }

    private static async Task<TsRepairLargeGapCandidate> InspectLargeGapRangeAsync(
        TsMultiSourceAnalysisResult analysis,
        TsRepairSourceAnalysis donor,
        LargeGapRangeMatch range,
        CancellationToken cancellationToken)
    {
        var inspections = new Dictionary<int, LargeGapRangeTrackInspection>();
        var usedSourcePids = new HashSet<int>();
        foreach (var boundary in range.Gap.Tracks)
        {
            var referenceTrack = analysis.Tracks.First(item =>
                item.ReferencePid == boundary.ReferencePid);
            var match = referenceTrack.Matches
                .Where(item => PathsEqual(item.SourcePath, donor.FilePath) &&
                               !usedSourcePids.Contains(item.SourcePid))
                .OrderByDescending(item => item.SourcePid == boundary.ReferencePid)
                .ThenByDescending(item => item.Confidence)
                .ThenByDescending(item => item.FingerprintMatches)
                .FirstOrDefault();
            if (match is null)
                continue;
            if (boundary.ReferencePid == range.Gap.AnchorReferencePid &&
                match.SourcePid != range.SourcePid)
            {
                continue;
            }
            usedSourcePids.Add(match.SourcePid);
            // 大段缺失必须使用原始 PTS 差值；虚拟时间轴校正可能正好把真实缺片
            // 压缩成连续时间，若混用会把需要补回的数秒内容误判为不存在。
            var timestampOffset90k = range.TimestampOffset90k;
            inspections[match.SourcePid] = new LargeGapRangeTrackInspection(
                boundary, match.SourcePid, timestampOffset90k, null);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize);
        try
        {
            await using var stream = new FileStream(
                donor.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                ReadBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            // 视频前后锚点可能早于或晚于同时间的音频 PES。只扩展一个固定的小窗口，
            // 让各轨道分别落到自己的 PES 边界，同时仍避免重新扫描整份辅助文件。
            var scanStartOffset = Math.Max(
                donor.Catalog.SyncOffset,
                range.SourceStartOffset - LargeGapRangeInspectionPaddingBytes);
            scanStartOffset -= (scanStartOffset - donor.Catalog.SyncOffset) % PacketSize;
            var scanEndOffset = Math.Min(
                donor.Catalog.FileSize,
                range.SourceEndOffset + LargeGapRangeInspectionPaddingBytes);
            scanEndOffset -= (scanEndOffset - donor.Catalog.SyncOffset) % PacketSize;
            stream.Position = scanStartOffset;
            var remaining = scanEndOffset - scanStartOffset;
            var fileOffset = scanStartOffset;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = (int)Math.Min(buffer.Length, remaining);
                requested -= requested % PacketSize;
                if (requested <= 0)
                    throw new TsRepairException(TsRepairErrorCode.SourceChanged);
                await stream.ReadExactlyAsync(buffer.AsMemory(0, requested), cancellationToken)
                    .ConfigureAwait(false);
                for (var offset = 0; offset < requested; offset += PacketSize)
                {
                    var packet = buffer.AsSpan(offset, PacketSize);
                    if (!TryParsePacket(packet, out var info))
                        throw new TsRepairException(TsRepairErrorCode.SourceChanged);
                    if (inspections.TryGetValue(info.Pid, out var inspection))
                        inspection.Process(packet, info, fileOffset + offset);
                }
                fileOffset += requested;
                remaining -= requested;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var usableInspections = inspections.Values
            .Where(item => item.IsUsable())
            .OrderBy(item => item.Boundary.ReferencePid)
            .ToArray();
        var candidate = new TsRepairLargeGapCandidate
        {
            SourcePath = donor.FilePath,
            SourceStartOffset = usableInspections.Length > 0
                ? usableInspections.Min(item => item.SourceStartOffset)
                : range.SourceStartOffset,
            SourceEndOffset = usableInspections.Length > 0
                ? usableInspections.Max(item => item.SourceEndOffset)
                : range.SourceEndOffset,
            TimestampOffset90k = range.TimestampOffset90k
        };
        foreach (var inspection in usableInspections)
        {
            candidate.Tracks.Add(new TsRepairLargeGapTrackCandidate
            {
                ReferencePid = inspection.Boundary.ReferencePid,
                SourcePid = inspection.SourcePid,
                SourcePacketCount = inspection.PacketCount,
                SourcePayloadPacketCount = inspection.PayloadPacketCount,
                TimestampOffset90k = inspection.TimestampOffset90k,
                SourceStartOffset = inspection.SourceStartOffset,
                SourceEndOffset = inspection.SourceEndOffset
            });
        }
        return candidate;
    }

    private static void ReportSourceCompleted(
        TsRepairSourceAnalysis source,
        IProgress<TsRepairSourceCompleted>? progress) =>
        progress?.Report(new TsRepairSourceCompleted(
            source.FilePath,
            source.IsReference,
            source.Catalog.Programs.Count > 0,
            source.ContinuityErrors,
            source.TransportErrors,
            source.PesSizeErrors));

    public TsRepairOutputPlan BuildOutputPlan(
        TsMultiSourceAnalysisResult analysis,
        IReadOnlySet<int> selectedPids,
        bool includeServiceInformation,
        IReadOnlySet<long>? selectedLargeGapOffsets = null)
    {
        var plan = new TsRepairOutputPlan
        {
            Analysis = analysis,
            IncludeServiceInformation = includeServiceInformation
        };
        plan.SelectedPids.UnionWith(selectedPids);
        if (selectedLargeGapOffsets is not null)
            plan.SelectedLargeGapOffsets.UnionWith(selectedLargeGapOffsets);

        var sourceRank = new Dictionary<string, (int Errors, int Order)>(StringComparer.Ordinal);
        for (var index = 0; index < analysis.Sources.Count; index++)
        {
            var source = analysis.Sources[index];
            sourceRank[source.FilePath] = (
                source.ContinuityErrors + source.TransportErrors + source.PesSizeErrors, index);
        }

        var largeGapCoveredBoundaries = new HashSet<(int Pid, long Offset)>();
        var largeGapCoveredRanges = new List<(int Pid, long StartOffset, long EndOffset)>();
        if (selectedLargeGapOffsets is not null)
        {
            foreach (var gap in analysis.LargeGaps.Where(item =>
                         selectedLargeGapOffsets.Contains(item.ReferenceInsertOffset)))
            {
                var candidate = gap.Candidates
                    .Select(item => new
                    {
                        Candidate = item,
                        SelectedTrackCount = item.Tracks.Count(track =>
                            selectedPids.Contains(track.ReferencePid))
                    })
                    .Where(item => item.SelectedTrackCount > 0)
                    .OrderByDescending(item => item.SelectedTrackCount)
                    .ThenBy(item => sourceRank.GetValueOrDefault(item.Candidate.SourcePath).Errors)
                    .ThenBy(item => sourceRank.GetValueOrDefault(item.Candidate.SourcePath).Order)
                    .Select(item => item.Candidate)
                    .FirstOrDefault();
                if (candidate is null)
                    continue;

                var insertion = new TsLargeGapInsertion
                {
                    SourcePath = candidate.SourcePath,
                    SourceStartOffset = candidate.SourceStartOffset,
                    SourceEndOffset = candidate.SourceEndOffset,
                    Duration90k = gap.MissingDuration90k
                };
                foreach (var candidateTrack in candidate.Tracks.Where(item =>
                             selectedPids.Contains(item.ReferencePid)))
                {
                    var boundary = gap.Tracks.First(item =>
                        item.ReferencePid == candidateTrack.ReferencePid);
                    var startContinuityCounter =
                        (boundary.ReferenceAfterContinuityCounter -
                         (candidateTrack.SourcePayloadPacketCount & 0x0F) + 16) & 0x0F;
                    insertion.Tracks.Add(new TsLargeGapTrackInsertion
                    {
                        SourcePid = candidateTrack.SourcePid,
                        TargetPid = candidateTrack.ReferencePid,
                        StartContinuityCounter = startContinuityCounter,
                        SourcePacketCount = candidateTrack.SourcePacketCount,
                        SourcePayloadPacketCount = candidateTrack.SourcePayloadPacketCount,
                        TimestampOffset90k = candidateTrack.TimestampOffset90k,
                        SourceStartOffset = candidateTrack.SourceStartOffset,
                        SourceEndOffset = candidateTrack.SourceEndOffset,
                        ReferenceDiscardStartOffset = gap.ReferenceInsertOffset,
                        ReferenceDiscardEndOffset = boundary.ReferenceAfterOffset
                    });
                    largeGapCoveredBoundaries.Add((
                        candidateTrack.ReferencePid, boundary.ReferenceAfterOffset));
                    largeGapCoveredRanges.Add((
                        candidateTrack.ReferencePid, gap.ReferenceInsertOffset,
                        boundary.ReferenceAfterOffset));
                    plan.RepairedPacketCount += candidateTrack.SourcePacketCount;
                }
                if (insertion.Tracks.Count == 0)
                    continue;
                if (!plan.LargeGapInsertions.TryGetValue(
                        gap.ReferenceInsertOffset, out var insertions))
                {
                    insertions = [];
                    plan.LargeGapInsertions[gap.ReferenceInsertOffset] = insertions;
                }
                insertions.Add(insertion);
                plan.RepairedLargeGapCount++;
                plan.RepairedLargeGapDuration90k += gap.MissingDuration90k;
            }
        }

        foreach (var track in analysis.Tracks)
        {
            if (!selectedPids.Contains(track.ReferencePid))
                continue;
            var selectedInsertionOffsets = new List<long>();
            foreach (var gap in track.Gaps)
            {
                if (largeGapCoveredBoundaries.Contains((
                        track.ReferencePid, gap.ReferenceInsertOffset)))
                {
                    continue;
                }
                if (largeGapCoveredRanges.Any(range =>
                        range.Pid == track.ReferencePid &&
                        gap.ReferenceInsertOffset >= range.StartOffset &&
                        gap.ReferenceInsertOffset < range.EndOffset))
                {
                    continue;
                }
                if (gap.Candidates.Count == 0)
                    continue;
                var candidate = gap.Candidates
                    .OrderBy(item => sourceRank.GetValueOrDefault(item.SourcePath).Errors)
                    .ThenBy(item => sourceRank.GetValueOrDefault(item.SourcePath).Order)
                    .First();
                var timestampOffset90k = track.Matches.FirstOrDefault(item =>
                    item.SourcePid == candidate.SourcePid &&
                    string.Equals(item.SourcePath, candidate.SourcePath, StringComparison.Ordinal))
                    ?.TimestampOffset90k ?? 0;
                if (candidate.SourcePacketOffsets.Length > 0)
                {
                    timestampOffset90k = ConvertNormalizedOffsetToSourceTimeline(
                        analysis, timestampOffset90k,
                        candidate.SourcePath, candidate.SourcePid, candidate.SourcePacketOffsets[0],
                        analysis.ReferenceSource.FilePath, track.ReferencePid, gap.ReferenceInsertOffset);
                }
                var insertion = new TsPacketInsertion
                {
                    SourcePath = candidate.SourcePath,
                    SourcePid = candidate.SourcePid,
                    TargetPid = track.ReferencePid,
                    StartContinuityCounter = gap.ExpectedContinuityCounter,
                    SourcePacketOffsets = candidate.SourcePacketOffsets,
                    ElementaryPayload = candidate.ElementaryPayload,
                    PesBoundaries = candidate.PesBoundaries,
                    SynthesizedPacketCount = candidate.SynthesizedPacketCount,
                    TimestampOffset90k = timestampOffset90k
                };
                if (!plan.Insertions.TryGetValue(gap.ReferenceInsertOffset, out var insertions))
                {
                    insertions = [];
                    plan.Insertions[gap.ReferenceInsertOffset] = insertions;
                }
                insertions.Add(insertion);
                selectedInsertionOffsets.Add(gap.ReferenceInsertOffset);
                plan.DiscardPacketOffsets.UnionWith(gap.ReferenceDiscardOffsets);
                plan.RepairedGapCount++;
                plan.RepairedPacketCount += candidate.SynthesizedPacketCount > 0
                    ? candidate.SynthesizedPacketCount
                    : candidate.SourcePacketOffsets.Length;
            }
            foreach (var region in track.PesRegions)
            {
                if (largeGapCoveredRanges.Any(range =>
                        range.Pid == track.ReferencePid &&
                        region.ReferenceStartOffset < range.EndOffset &&
                        region.ReferenceEndOffset > range.StartOffset))
                {
                    continue;
                }
                if (region.Candidates.Count == 0)
                    continue;
                var candidate = region.Candidates
                    .OrderByDescending(item => item.FingerprintMatches)
                    .ThenBy(item => sourceRank.GetValueOrDefault(item.SourcePath).Errors)
                    .ThenBy(item => sourceRank.GetValueOrDefault(item.SourcePath).Order)
                    .First();
                var referenceStartOffset = candidate.ReferenceStartOffset ?? region.ReferenceStartOffset;
                var referenceEndOffset = candidate.ReferenceEndOffset ?? region.ReferenceEndOffset;
                // 连续性缺口会同时让所在 PES 呈现长度不符。若该缺口已能按包或 ES 精确补齐，
                // 不再整段替换同一范围，避免补包与区域替换重复写入同一份媒体内容。
                if (selectedInsertionOffsets.Any(offset =>
                        offset >= referenceStartOffset && offset < referenceEndOffset))
                {
                    continue;
                }
                plan.Replacements.Add(new TsPacketReplacement
                {
                    SourcePath = candidate.SourcePath,
                    SourcePid = candidate.SourcePid,
                    TargetPid = track.ReferencePid,
                    ReferenceStartOffset = referenceStartOffset,
                    ReferenceEndOffset = referenceEndOffset,
                    StartContinuityCounter = candidate.ReferenceStartContinuityCounter ??
                                             region.ReferenceStartContinuityCounter,
                    ReferencePacketCount = candidate.ReferencePacketCount ?? region.ReferencePacketCount,
                    SourceStartOffset = candidate.SourceStartOffset,
                    SourceEndOffset = candidate.SourceEndOffset,
                    PacketCount = candidate.PacketCount,
                    TimestampOffset90k = ConvertNormalizedOffsetToSourceTimeline(
                        analysis, candidate.TimestampOffset90k,
                        candidate.SourcePath, candidate.SourcePid, candidate.SourceStartOffset,
                        analysis.ReferenceSource.FilePath, track.ReferencePid, referenceStartOffset),
                    ElementaryPayloadOnly = region.Reason ==
                        TsRepairPesRegionReason.CorrelatedVideoElementaryMismatch &&
                        candidate.ElementaryLength == GetReferenceElementaryLength(region, candidate),
                    ElementaryLength = candidate.ElementaryLength
                });
                plan.RepairedPesRegionCount++;
                plan.RepairedPacketCount += candidate.PacketCount;
            }
        }
        return plan;
    }

    private static long ConvertNormalizedOffsetToSourceTimeline(
        TsMultiSourceAnalysisResult analysis,
        long normalizedOffset90k,
        string donorPath,
        int donorPid,
        long donorFileOffset,
        string referencePath,
        int referencePid,
        long referenceFileOffset)
    {
        var donor = analysis.Sources.First(item => PathsEqual(item.FilePath, donorPath));
        var reference = analysis.Sources.First(item => PathsEqual(item.FilePath, referencePath));
        var donorCorrection = GetTimelineCorrection90k(donor, donorPid, donorFileOffset);
        var referenceCorrection = GetTimelineCorrection90k(reference, referencePid, referenceFileOffset);
        // 匹配阶段比较的是归一化 PTS；写回原始参考流时需还原两端各自的虚拟校正量，
        // 否则来自异常时间段的辅助包会携带归一化时间而无法贴合参考流的原始时钟。
        return normalizedOffset90k + donorCorrection - referenceCorrection;
    }

    private static long GetTimelineCorrection90k(
        TsRepairSourceAnalysis source,
        int streamPid,
        long fileOffset)
    {
        if (source.TimelineAnalysis is not { } timelineAnalysis)
            return 0;
        var pcrPid = FindPcrPid(source.Catalog, streamPid);
        if (pcrPid < 0)
            return 0;
        var packetIndex = Math.Max(0, (fileOffset - source.Catalog.SyncOffset) / PacketSize);
        return TsTimelineRepairService.GetVirtualTimestampCorrection90k(
            timelineAnalysis, pcrPid, packetIndex);
    }

    public async Task<TsRepairOutputResult> OutputAsync(
        TsRepairOutputPlan plan,
        string outputPath,
        IProgress<TsFilterProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedOutputPath = Path.GetFullPath(outputPath);
        if (plan.Analysis.Sources.Any(source => PathsEqual(source.FilePath, normalizedOutputPath)))
            throw new TsRepairException(TsRepairErrorCode.OutputIsSource);

        var filter = new TsStreamFilterService();
        var filterPlan = filter.BuildPlan(
            plan.Analysis.ReferenceSource.Catalog,
            plan.SelectedPids,
            plan.IncludeServiceInformation);
        var outputValidator = new TsRepairOutputValidator(plan.SelectedPids);
        var filterResult = await filter.FilterWithInsertionsAsync(
            plan.Analysis.ReferenceSource.FilePath,
            outputPath,
            plan.Analysis.ReferenceSource.Catalog,
            filterPlan,
            plan.Insertions,
            plan.LargeGapInsertions,
            plan.Replacements,
            plan.DiscardPacketOffsets,
            outputValidator,
            progress,
            cancellationToken).ConfigureAwait(false);
        // 输出缓冲在写盘时已经同步经过轻量结构复检，不需要重新打开并顺序读取完整文件。
        // 允许保留无法从辅助源找到候选的少量异常，但错误总数必须确有下降。
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var verificationErrors = outputValidator.TotalErrors;
            var referenceErrors = plan.Analysis.Tracks
                .Where(track => plan.SelectedPids.Contains(track.ReferencePid))
                .Sum(track => track.ContinuityErrorCount + track.TransportErrorCount + track.PesSizeErrorCount);
            if (referenceErrors == 0 ? verificationErrors > 0 : verificationErrors >= referenceErrors)
            {
                throw new TsRepairException(
                    TsRepairErrorCode.NoImprovement, referenceErrors, verificationErrors);
            }
            return new TsRepairOutputResult
            {
                FilterResult = filterResult,
                RepairedGapCount = plan.RepairedGapCount,
                RepairedPacketCount = plan.RepairedPacketCount,
                RepairedPesRegionCount = plan.RepairedPesRegionCount,
                RepairedLargeGapCount = plan.RepairedLargeGapCount,
                RepairedLargeGapDuration90k = plan.RepairedLargeGapDuration90k,
                ReferenceErrorCount = referenceErrors,
                RemainingErrorCount = verificationErrors,
                Plan = plan
            };
        }
        catch
        {
            try
            {
                File.Delete(outputPath);
            }
            catch
            {
                // 清理失败不覆盖原始的验证或取消异常。
            }
            throw;
        }
    }

    private static Dictionary<int, ReferenceTrackState> BuildReferenceTracks(
        TsRepairSourceAnalysis source,
        ICollection<TsRepairTrackAnalysis> output)
    {
        var result = new Dictionary<int, ReferenceTrackState>();
        var catalog = source.Catalog;
        foreach (var program in catalog.Programs.Values.OrderBy(item => item.ProgramNumber))
        {
            foreach (var stream in program.Streams.OrderBy(item => item.Key))
            {
                catalog.Pids.TryGetValue(stream.Key, out var summary);
                var track = new TsRepairTrackAnalysis
                {
                    ReferencePid = stream.Key,
                    ProgramNumber = program.ProgramNumber,
                    StreamType = stream.Value,
                    MpegAudioLayer = summary?.MpegAudioLayer,
                    SupplementaryStreamType = summary?.SupplementaryStreamType,
                    Language = summary?.Language
                };
                output.Add(track);
                result[track.ReferencePid] = new ReferenceTrackState(
                    track, program.PcrPid, CreateTimelineNormalizer(source, program.PcrPid));
            }
        }
        return result;
    }

    private static void ApplyReferenceTimelineNormalization(
        TsRepairSourceAnalysis reference,
        IEnumerable<ReferenceTrackState> states,
        List<TsBroadcastTimeAnchor> broadcastTimes)
    {
        if (reference.TimelineAnalysis is not { Issues.Count: > 0 } analysis)
            return;

        var stateList = states.ToArray();
        foreach (var state in stateList)
            state.ApplyTimelineNormalization(analysis, reference.Catalog.SyncOffset);

        // 广播时间锚点在参考源主扫描时记录的是原始节目 PTS；按各节目 PCR PID
        // 回写到同一虚拟时间轴，修复地图的绝对时间换算才不会在异常边界跳变。
        var pcrPids = reference.Catalog.Programs.Values.ToDictionary(
            item => item.ProgramNumber, item => item.PcrPid);
        for (var index = 0; index < broadcastTimes.Count; index++)
        {
            var anchor = broadcastTimes[index];
            if (!pcrPids.TryGetValue(anchor.ProgramNumber, out var pcrPid) || pcrPid < 0)
                continue;
            var correction = TsTimelineRepairService.GetVirtualTimestampCorrection90k(
                analysis, pcrPid, anchor.PacketIndex);
            if (correction != 0)
                broadcastTimes[index] = anchor with { Clock90k = anchor.Clock90k + correction };
        }
    }

    private static List<TrackMapping> BuildTrackMappings(
        TsRepairSourceAnalysis donor,
        IReadOnlyDictionary<int, ReferenceTrackState> referenceStates)
    {
        var result = new List<TrackMapping>();
        foreach (var referenceState in referenceStates.Values.OrderBy(item => item.Track.ReferencePid))
        {
            var track = referenceState.Track;
            var candidates = donor.Catalog.Pids.Values
                .Where(item => item.ProgramNumber is not null && item.StreamType == track.StreamType &&
                               item.SupplementaryStreamType == track.SupplementaryStreamType &&
                               LanguagesCompatible(track.Language, item.Language))
                .OrderByDescending(item => item.ProgramNumber == track.ProgramNumber)
                .ThenByDescending(item => item.Pid == track.ReferencePid)
                .ThenBy(item => item.Pid)
                .ToArray();
            if (candidates.Length == 0)
                continue;

            // 同一节目、同一 PID 是复用器跨录制通常不会改变的稳定标识。
            // 当它唯一存在时直接建立一对一映射，避免两路同编码音轨被迫做无意义的
            // 全量 ES 歧义扫描；PID 发生变化时仍保留下面的指纹匹配回退路径。
            var directPidCandidates = candidates.Where(item =>
                item.Pid == track.ReferencePid && item.ProgramNumber == track.ProgramNumber).ToArray();
            if (directPidCandidates.Length == 1)
                candidates = directPidCandidates;

            // 语言描述缺失时，同类型多音轨仅凭排列顺序无法可靠一一对应。这里保留全部合理候选，
            // 后续以负载指纹确认；只有双向唯一的元数据候选才允许单独作为“元数据匹配”展示。
            foreach (var candidate in candidates)
            {
                var match = new TsRepairTrackMatch
                {
                    SourcePath = donor.FilePath,
                    SourcePid = candidate.Pid,
                    Confidence = TsRepairMatchConfidence.Metadata
                };
                track.Matches.Add(match);
                result.Add(new TrackMapping(
                    referenceState, candidate.Pid, match,
                    CreateTimelineNormalizer(donor, FindPcrPid(donor.Catalog, candidate.Pid))));
            }
        }

        foreach (var group in result.GroupBy(item => item.ReferenceState.Track.ReferencePid))
        {
            var referenceCandidateCount = group.Count();
            foreach (var mapping in group)
            {
                var donorCandidateCount = result.Count(item => item.SourcePid == mapping.SourcePid);
                mapping.MetadataIsAmbiguous = referenceCandidateCount != 1 || donorCandidateCount != 1;
            }
        }
        return result;
    }

    private static bool LanguagesCompatible(string? left, string? right) =>
        string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right) ||
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static int FindPcrPid(TsCheckResult catalog, int streamPid)
    {
        foreach (var program in catalog.Programs.Values)
        {
            if (program.Streams.ContainsKey(streamPid))
                return program.PcrPid;
        }
        return -1;
    }

    private static TimelineTimestampNormalizer? CreateTimelineNormalizer(
        TsRepairSourceAnalysis source,
        int pcrPid) =>
        source.TimelineAnalysis is { Issues.Count: > 0 } analysis && pcrPid >= 0
            ? new TimelineTimestampNormalizer(analysis, pcrPid, source.Catalog.SyncOffset)
            : null;

    private static TsRepairElementaryKind GetElementaryKind(TsRepairTrackAnalysis track)
    {
        var kind = track.StreamType switch
        {
            TsStreamTypes.Mpeg1Audio or TsStreamTypes.Mpeg2Audio => TsRepairElementaryKind.MpegAudio,
            TsStreamTypes.Aac => TsRepairElementaryKind.AacAdts,
            TsStreamTypes.AacLatm => TsRepairElementaryKind.AacLatm,
            TsStreamTypes.Av3a => TsRepairElementaryKind.Av3a,
            TsStreamTypes.Mpeg1Video or TsStreamTypes.Mpeg2Video => TsRepairElementaryKind.MpegVideo,
            TsStreamTypes.Cavs => TsRepairElementaryKind.Cavs,
            TsStreamTypes.Avs2 => TsRepairElementaryKind.Avs2,
            TsStreamTypes.Avs3 => TsRepairElementaryKind.Avs3,
            TsStreamTypes.H264 => TsRepairElementaryKind.H264,
            TsStreamTypes.Hevc => TsRepairElementaryKind.H265,
            _ => TsRepairElementaryKind.None
        };
        return kind != TsRepairElementaryKind.None
            ? kind
            : TsStreamTypes.IsAudio(track.StreamType, track.SupplementaryStreamType)
                ? TsRepairElementaryKind.Audio
                : TsRepairElementaryKind.None;
    }

    private static bool IsAudioElementaryKind(TsRepairElementaryKind kind) => kind is
        TsRepairElementaryKind.Audio or TsRepairElementaryKind.MpegAudio or
        TsRepairElementaryKind.AacAdts or TsRepairElementaryKind.AacLatm or TsRepairElementaryKind.Av3a;

    private static bool IsFramedAudioElementaryKind(TsRepairElementaryKind kind) => kind is
        TsRepairElementaryKind.MpegAudio or TsRepairElementaryKind.AacAdts or
        TsRepairElementaryKind.AacLatm or TsRepairElementaryKind.Av3a;

    private static AudioFrameTracker? CreateAudioFrameTracker(TsRepairTrackAnalysis track)
    {
        var kind = GetElementaryKind(track);
        return IsFramedAudioElementaryKind(kind) ? new AudioFrameTracker(kind) : null;
    }

    private static void CreateLargeGaps(
        IEnumerable<ReferenceTrackState> states,
        ICollection<TsRepairLargeGap> output)
    {
        foreach (var program in states.GroupBy(item => item.Track.ProgramNumber))
        {
            var programStates = program.ToArray();
            foreach (var anchorState in programStates.Where(item =>
                         TsStreamTypes.IsVideo(item.Track.StreamType)))
            {
                foreach (var boundary in anchorState.LargeGapBoundaries)
                {
                    if (output.Any(item => item.ProgramNumber == anchorState.Track.ProgramNumber &&
                                           Math.Abs(item.ReferenceAfterPts90k -
                                               boundary.ReferenceAfterPts90k) <=
                                           LargeGapTrackCorrelationTolerance90k))
                    {
                        continue;
                    }

                    if (boundary.BeforeAnchor.Length != LargeGapAnchorCount ||
                        boundary.AfterAnchor.Count != LargeGapAnchorCount)
                    {
                        continue;
                    }

                    var correlatedBoundaries = programStates
                        .SelectMany(state => state.LargeGapBoundaries.Select(item =>
                            (State: state, Boundary: item)))
                        .Where(item =>
                            Math.Abs(item.Boundary.ReferenceAfterPts90k -
                                boundary.ReferenceAfterPts90k) <=
                            LargeGapTrackCorrelationTolerance90k &&
                            Math.Abs(item.Boundary.ReferenceMissingStartPts90k -
                                boundary.ReferenceMissingStartPts90k) <=
                            LargeGapTrackCorrelationTolerance90k)
                        .GroupBy(item => item.State.Track.ReferencePid)
                        .Select(group => group.OrderBy(item => Math.Abs(
                            item.Boundary.ReferenceAfterPts90k - boundary.ReferenceAfterPts90k)).First())
                        .ToArray();
                    if (correlatedBoundaries.Length == 0)
                        continue;

                    var originalMissingStartPts90k = boundary.ReferenceMissingStartPts90k;
                    var originalInsertOffset = correlatedBoundaries.Min(item =>
                        item.Boundary.ReferenceAfterOffset);
                    // 硬盘或网络写入故障常先产生一串 CC/TEI/PES 异常，随后才完全丢失
                    // 一段媒体数据。若这些异常紧邻大段 PTS 跳变，则将其视为同一故障簇；
                    // 匹配阶段仍需辅助源两端锚点通过，不能仅凭时间距离直接替换。
                    var incidentIssueCandidates = correlatedBoundaries
                        .SelectMany(item => item.State.Track.Gaps
                            .Where(item => item.ReferencePts90k != long.MinValue)
                            .Select(gapItem => new LargeGapIncidentIssue(
                                gapItem.ReferencePts90k + Math.Max(0,
                                    item.Boundary.ReferenceMissingStartPts90k -
                                    item.Boundary.ReferenceBeforePts90k),
                                gapItem.ReferenceInsertOffset,
                                item.State.Track.ReferencePid)))
                        .Concat(programStates.SelectMany(state => state.Track.PesRegions
                            .Where(item => item.ReferenceFirstPts90k != long.MinValue)
                            .Select(item => new LargeGapIncidentIssue(
                                item.ReferenceFirstPts90k, item.ReferenceStartOffset,
                                state.Track.ReferencePid))))
                        .Where(item => item.FileOffset < originalInsertOffset &&
                                       item.Pts90k < originalMissingStartPts90k &&
                                       originalMissingStartPts90k - item.Pts90k <=
                                       LargeGapIncidentHistoryWindow90k)
                        .ToArray();
                    // 单轨异常只按短距离连续簇合并；若同一节目至少两个 PID 在 1 秒内
                    // 同时异常，则视为强 I/O 故障信号，允许在 60 秒历史内整体回溯。
                    var precedingIssues = FindAdjacentLargeGapIncidentIssues(
                        incidentIssueCandidates, originalMissingStartPts90k);
                    var proposedRepairStartPts90k = precedingIssues.Length > 0
                        ? precedingIssues.Min(item => item.Pts90k)
                        : originalMissingStartPts90k;
                    var incidentTrackStartOffsets = correlatedBoundaries
                        .Select(item =>
                        {
                            var issueOffset = precedingIssues
                                .Where(issue => issue.Pid == item.State.Track.ReferencePid &&
                                                Math.Abs(issue.Pts90k - proposedRepairStartPts90k) <=
                                                LargeGapPesBoundaryTolerance90k)
                                .Select(issue => (long?)issue.FileOffset)
                                .Min();
                            if (issueOffset is not null)
                                return issueOffset;
                            return item.Boundary.IncidentHistory
                                .Where(pes => pes.Pts90k >= proposedRepairStartPts90k -
                                              LargeGapPesBoundaryTolerance90k &&
                                              pes.Pts90k <= proposedRepairStartPts90k +
                                              MinimumLargeGap90k)
                                .Select(pes => (long?)pes.StartOffset)
                                .FirstOrDefault();
                        })
                        .ToArray();
                    var includesPrecedingDamage = precedingIssues.Length > 0 &&
                                                  incidentTrackStartOffsets.All(item => item is not null);
                    var repairStartPts90k = includesPrecedingDamage
                        ? proposedRepairStartPts90k
                        : originalMissingStartPts90k;
                    var repairInsertOffset = includesPrecedingDamage
                        ? incidentTrackStartOffsets.Min(item => item!.Value)
                        : originalInsertOffset;

                    var gap = new TsRepairLargeGap
                    {
                        ProgramNumber = anchorState.Track.ProgramNumber,
                        AnchorReferencePid = anchorState.Track.ReferencePid,
                        ReferenceInsertOffset = repairInsertOffset,
                        ReferenceBeforePts90k = boundary.ReferenceBeforePts90k,
                        ReferenceOriginalMissingStartPts90k = originalMissingStartPts90k,
                        ReferenceMissingStartPts90k = repairStartPts90k,
                        ReferenceAfterPts90k = boundary.ReferenceAfterPts90k,
                        BeforeAnchor = boundary.BeforeAnchor,
                        AfterAnchor = boundary.AfterAnchor.ToArray()
                    };
                    foreach (var item in correlatedBoundaries)
                    {
                        var packetGap = item.State.Track.Gaps.FirstOrDefault(value =>
                            value.ReferenceInsertOffset == item.Boundary.ReferenceAfterOffset);
                        gap.Tracks.Add(new TsRepairLargeGapTrackBoundary
                        {
                            ReferencePid = item.State.Track.ReferencePid,
                            ReferenceMissingStartPts90k = repairStartPts90k,
                            ReferenceAfterPts90k = item.Boundary.ReferenceAfterPts90k,
                            ReferenceAfterOffset = item.Boundary.ReferenceAfterOffset,
                            ReferenceAfterContinuityCounter =
                                item.Boundary.ReferenceAfterContinuityCounter,
                            // 扩展区间必须按 PTS 找到统一的故障簇起点；原小缺口包锚点只对应
                            // 真正缺失部分，继续使用会把前面的损坏数据留在参考文件中。
                            BeforePacketAnchor = includesPrecedingDamage
                                ? []
                                : packetGap?.BeforeAnchor ?? [],
                            AfterPacketAnchor = includesPrecedingDamage
                                ? []
                                : packetGap?.AfterAnchor ?? []
                        });
                    }
                    output.Add(gap);
                }
            }
        }
    }

    private static LargeGapIncidentIssue[] FindAdjacentLargeGapIncidentIssues(
        IEnumerable<LargeGapIncidentIssue> candidates,
        long missingStartPts90k)
    {
        var ordered = candidates.OrderBy(item => item.Pts90k)
            .ThenBy(item => item.FileOffset)
            .ToArray();
        if (ordered.Length == 0)
            return [];

        long? earliestPts90k = null;
        var frontierPts90k = missingStartPts90k;
        foreach (var issue in ordered
                     .Where(item => missingStartPts90k - item.Pts90k <=
                                    LargeGapIncidentWeakMaximumLookback90k)
                     .OrderByDescending(item => item.Pts90k)
                     .ThenByDescending(item => item.FileOffset))
        {
            if (frontierPts90k - issue.Pts90k > LargeGapIncidentMaximumHealthyInterval90k)
                break;
            earliestPts90k = issue.Pts90k;
            frontierPts90k = Math.Min(frontierPts90k, issue.Pts90k);
        }

        // 滑动窗口按不同 PID 计数，避免异常较密集时做 O(n^2) 两两比较。
        var pidCounts = new Dictionary<int, int>();
        var left = 0;
        for (var right = 0; right < ordered.Length; right++)
        {
            var rightIssue = ordered[right];
            pidCounts[rightIssue.Pid] = pidCounts.GetValueOrDefault(rightIssue.Pid) + 1;
            while (rightIssue.Pts90k - ordered[left].Pts90k >
                   LargeGapIncidentCorrelatedTrackWindow90k)
            {
                var leftPid = ordered[left++].Pid;
                if (--pidCounts[leftPid] == 0)
                    pidCounts.Remove(leftPid);
            }
            if (pidCounts.Count >= 2)
            {
                earliestPts90k = earliestPts90k is { } current
                    ? Math.Min(current, ordered[left].Pts90k)
                    : ordered[left].Pts90k;
            }
        }

        return earliestPts90k is { } startPts90k
            ? ordered.Where(item => item.Pts90k >= startPts90k).ToArray()
            : [];
    }

    private readonly record struct LargeGapIncidentIssue(long Pts90k, long FileOffset, int Pid);

    private static void CreateCorrelatedVideoRegions(IEnumerable<ReferenceTrackState> states)
    {
        foreach (var program in states.GroupBy(item => item.Track.ProgramNumber))
        {
            var issueWindows = program
                .Where(item => IsAudioElementaryKind(item.ElementaryKind))
                .SelectMany(item => item.Track.PesRegions)
                .Where(item => item.Reason == TsRepairPesRegionReason.PesSizeMismatch &&
                               item.ReferenceFirstPts90k != long.MinValue &&
                               item.ReferenceLastPts90k != long.MinValue)
                .Select(item => (Start: item.ReferenceFirstPts90k - CorrelatedVideoPadding90k,
                    End: item.ReferenceLastPts90k + CorrelatedVideoPadding90k))
                .OrderBy(item => item.Start)
                .ToArray();
            if (issueWindows.Length == 0)
                continue;

            var mergedWindows = new List<(long Start, long End)>();
            foreach (var window in issueWindows)
            {
                if (mergedWindows.Count == 0 || window.Start > mergedWindows[^1].End)
                {
                    mergedWindows.Add(window);
                    continue;
                }
                var previous = mergedWindows[^1];
                mergedWindows[^1] = (previous.Start, Math.Max(previous.End, window.End));
            }

            foreach (var video in program.Where(item => item.ElementaryKind is
                         TsRepairElementaryKind.H264 or TsRepairElementaryKind.H265))
            {
                foreach (var window in mergedWindows)
                    video.TryAddCorrelatedVideoRegion(window.Start, window.End);
            }
        }
    }

    private static void ValidateCorrelatedVideoCandidates(IEnumerable<TsRepairTrackAnalysis> tracks)
    {
        var values = tracks.ToArray();
        foreach (var track in values.Where(track => GetElementaryKind(track) is
                     TsRepairElementaryKind.H264 or TsRepairElementaryKind.H265))
        {
            foreach (var region in track.PesRegions.Where(region => region.Reason ==
                         TsRepairPesRegionReason.CorrelatedVideoElementaryMismatch))
            {
                region.Candidates.RemoveAll(candidate =>
                {
                    var match = track.Matches.FirstOrDefault(item =>
                        item.SourcePid == candidate.SourcePid &&
                        string.Equals(item.SourcePath, candidate.SourcePath, StringComparison.Ordinal));
                    return match?.TimestampOffset90k is not { } expectedOffset ||
                           Math.Abs(candidate.TimestampOffset90k - expectedOffset) > 180_000;
                });
            }
        }

        foreach (var track in values)
            track.RepairablePesRegionCount = track.PesRegions.Count(region => region.Candidates.Count > 0);
    }

    private static async Task ScanReferenceAsync(
        TsRepairSourceAnalysis source,
        IReadOnlyDictionary<int, ReferenceTrackState> states,
        List<TsBroadcastTimeAnchor> broadcastTimes,
        ReferencePcrCollector? pcrCollector,
        int sourceIndex,
        int sourceCount,
        IProgress<TsMultiSourceProgress>? progress,
        CancellationToken cancellationToken)
    {
        var broadcastClock = new ReferenceBroadcastClockCollector(broadcastTimes, source.Catalog.SyncOffset);
        await ScanPacketsAsync(source.FilePath, source.Catalog.SyncOffset, sourceIndex, sourceCount, progress,
            TsMultiSourceProgressPhase.ReferenceScan,
            (packet, fileOffset) =>
            {
                if (!TryParsePacket(packet, out var info))
                    return;
                pcrCollector?.Process(packet, info, fileOffset);
                if (info.Pid == TsDvbTimeTableParser.Pid)
                {
                    broadcastClock.ProcessPacket(packet, info, fileOffset, states.Values);
                    return;
                }
                if (!states.TryGetValue(info.Pid, out var state))
                    return;
                if (info.TransportError)
                {
                    source.TransportErrors++;
                    state.Track.TransportErrorCount++;
                    state.AddTransportError(fileOffset, info.ContinuityCounter, info.HasPayload);
                    state.DiscardPes();
                    return;
                }
                if (info.Discontinuity)
                {
                    state.ResetContinuity();
                    state.DiscardPes();
                    state.ResetAnchors();
                }
                if (!info.HasPayload)
                {
                    // 仅含适配字段的同 PID 包仍属于当前 PES 区间，需计入区域封包数量。
                    state.ProcessPes(packet, info, fileOffset, default);
                    return;
                }

                if (state.TryCompleteTransportError(info.ContinuityCounter))
                    state.HasContinuity = false;

                if (state.HasContinuity)
                {
                    var expected = (state.LastContinuityCounter + 1) & 0x0F;
                    if (info.ContinuityCounter == state.LastContinuityCounter)
                        return;
                    if (info.ContinuityCounter != expected)
                    {
                        source.ContinuityErrors++;
                        state.Track.ContinuityErrorCount++;
                        if (state.RecentHashes.Count == AnchorLength &&
                            state.Track.Gaps.Count + state.PendingGaps.Count < MaxReferenceGaps)
                        {
                            var missingModulo = (info.ContinuityCounter - expected + 16) & 0x0F;
                            var elementaryBytesUntilPesEnd = state.HasPesLength
                                ? state.PesBytesRemaining
                                : (int?)null;
                            var elementaryBytesUntilFrameEnd = state.ElementaryBytesUntilFrameEnd;
                            var canUseElementaryFallback = state.ElementaryKind switch
                            {
                                TsRepairElementaryKind.Audio =>
                                    elementaryBytesUntilPesEnd > 0,
                                _ when IsFramedAudioElementaryKind(state.ElementaryKind) =>
                                    elementaryBytesUntilPesEnd > 0 && elementaryBytesUntilFrameEnd > 0,
                                TsRepairElementaryKind.MpegVideo or TsRepairElementaryKind.Cavs or
                                    TsRepairElementaryKind.Avs2 or TsRepairElementaryKind.Avs3 or
                                    TsRepairElementaryKind.H264 or TsRepairElementaryKind.H265 => true,
                                _ => false
                            };
                            canUseElementaryFallback &=
                                state.Track.Gaps.Count + state.PendingGaps.Count <
                                MaxElementaryRepairGapsPerMapping;
                            TryCreateBeforeElementaryAnchor(
                                state.RecentElementaryBytes,
                                out var beforeElementaryAnchor,
                                out var beforeElementarySuffix,
                                out var beforeElementaryRepeatedSuffixLength,
                                out var beforeElementaryRepeatedSuffixByte);
                            state.PendingGaps.Add(new PendingGap(
                                info.Pid, fileOffset, expected, Math.Max(1, missingModulo),
                                state.CurrentPesPts90k,
                                state.RecentHashes.ToArray(),
                                canUseElementaryFallback &&
                                beforeElementaryAnchor is not null
                                    ? beforeElementaryAnchor
                                    : null,
                                canUseElementaryFallback ? beforeElementarySuffix : null,
                                canUseElementaryFallback
                                    ? beforeElementaryRepeatedSuffixLength
                                    : 0,
                                beforeElementaryRepeatedSuffixByte,
                                elementaryBytesUntilPesEnd,
                                elementaryBytesUntilFrameEnd,
                                state.ElementaryKind));
                            // 已丢失包的实际负载长度未知；直到下一个 PUSI 前不再用 PES 长度证明后续缺口安全。
                            state.InvalidatePesLength();
                            state.ResetElementarySampleWindow();
                        }
                        // 与快速检查保持一致：CC 跳号后不再结算跨缺口的半个 PES，
                        // 否则同一次丢包会额外制造一个假的 PES 长度异常区域。
                        state.DiscardPes();
                        // 丢包后无法继续信任音频帧内偏移；从当前收到的 ES 重新寻找连续两帧同步。
                        state.InvalidateElementaryStructure();
                    }
                }
                state.HasContinuity = true;
                state.LastContinuityCounter = info.ContinuityCounter;

                var hash = ComputePayloadHash(packet[info.PayloadOffset..]);
                var elementaryPayload = GetElementaryPayload(packet, info, out var startsNewPes);
                // 重复包已经在上方返回，只有连续性可接受的当前包才进入 PES 长度和 ES 指纹状态。
                state.ProcessPes(packet, info, fileOffset, elementaryPayload);
                if (broadcastClock.HasPending && state.CurrentPesPts90k != long.MinValue)
                    broadcastClock.TryResolvePending(states.Values);
                state.Track.PayloadPacketCount++;
                for (var index = state.PendingGaps.Count - 1; index >= 0; index--)
                {
                    var pending = state.PendingGaps[index];
                    if (pending.AfterHashes.Count < AnchorLength)
                        pending.AfterHashes.Add(hash);
                    pending.AddElementaryPayload(elementaryPayload, startsNewPes);
                    if (!pending.IsComplete)
                        continue;
                    state.Track.Gaps.Add(pending.Build());
                    state.PendingGaps.RemoveAt(index);
                }

                state.PayloadPacketCount++;
                if (state.SampleHashes.Count < MaxFingerprintSamples &&
                    (state.PayloadPacketCount == 1 ||
                     state.PayloadPacketCount % FingerprintSampleInterval == 0))
                {
                    state.SampleHashes.Add(hash);
                    if (state.CurrentPesPts90k != long.MinValue)
                        state.AddSamplePts(hash, fileOffset);
                }
                EnqueueHash(state.RecentHashes, hash);
                if (startsNewPes)
                    state.RecentElementaryBytes.Clear();
                EnqueueBytes(
                    state.RecentElementaryBytes, elementaryPayload, ElementaryAnchorHistoryLength);
                state.ProcessElementarySamples(elementaryPayload);
                state.ProcessElementaryStructure(elementaryPayload);
                state.UpdatePesLength(packet, info);
            }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ScanDonorAsync(
        TsRepairSourceAnalysis source,
        IReadOnlyList<TrackMapping> mappings,
        int sourceIndex,
        int sourceCount,
        IProgress<TsMultiSourceProgress>? progress,
        CancellationToken cancellationToken)
    {
        var states = mappings
            .GroupBy(item => item.SourcePid)
            .ToDictionary(item => item.Key, item => new DonorPidState(item, item.First().TimelineNormalizer));
        var phase = source.TimelineAnalysis is null
            ? TsMultiSourceProgressPhase.DonorScan
            : TsMultiSourceProgressPhase.DonorMatchingScan;
        await ScanPacketsAsync(source.FilePath, source.Catalog.SyncOffset, sourceIndex, sourceCount, progress,
            phase,
            (packet, fileOffset) =>
            {
                if (!TryParsePacket(packet, out var info) || !states.TryGetValue(info.Pid, out var state))
                    return;
                if (info.TransportError)
                {
                    source.TransportErrors++;
                    state.DiscardPes();
                    state.ResetForDiscontinuity();
                    return;
                }
                if (info.Discontinuity)
                {
                    state.ResetContinuity();
                    state.DiscardPes();
                    state.ResetForDiscontinuity();
                }
                var startsNewPes = false;
                var pesHeaderLength = 0;
                var elementaryPayload = info.HasPayload
                    ? GetElementaryPayload(packet, info, out startsNewPes, out pesHeaderLength)
                    : default;
                if (!info.HasPayload)
                {
                    state.ProcessPes(packet, info, fileOffset, elementaryPayload, source.FilePath);
                    return;
                }

                if (state.HasContinuity)
                {
                    var expected = (state.LastContinuityCounter + 1) & 0x0F;
                    if (info.ContinuityCounter == state.LastContinuityCounter)
                        return;
                    if (info.ContinuityCounter != expected)
                    {
                        source.ContinuityErrors++;
                        state.DiscardPes();
                        // 辅助源自身存在缺口时，不能让候选区间跨过该缺口，否则可能把不完整数据当成修复包。
                        state.ResetForDiscontinuity();
                    }
                }
                state.HasContinuity = true;
                state.LastContinuityCounter = info.ContinuityCounter;

                state.ProcessPes(packet, info, fileOffset, elementaryPayload, source.FilePath);
                var hash = ComputePayloadHash(packet[info.PayloadOffset..]);
                state.ProcessPacket(hash, fileOffset, source.FilePath);
                var pesHeader = startsNewPes
                    ? packet.Slice(info.PayloadOffset, pesHeaderLength)
                    : default;
                state.ProcessElementaryPayload(
                    elementaryPayload, source.FilePath, startsNewPes, pesHeader);
            }, cancellationToken).ConfigureAwait(false);

        source.PesSizeErrors = states.Values.Sum(item => item.PesSizeErrorCount);
        var validTrackStates = new List<DonorTrackState>();
        foreach (var state in states.Values.SelectMany(item => item.TrackStates))
        {
            if (state.UsedElementaryFallback && !state.HasConfirmedElementaryMatch)
                state.RemoveUnconfirmedElementaryCandidates(source.FilePath);

            if (state.Mapping.MetadataIsAmbiguous &&
                state.Mapping.ReferenceState.SupportsElementaryFallback &&
                state.Mapping.ReferenceState.ElementarySampleHashes.Count > 0 &&
                !state.HasConfirmedElementaryMatch)
            {
                state.RemoveAllCandidates(source.FilePath);
                state.Mapping.ReferenceState.Track.Matches.Remove(state.Mapping.Match);
                continue;
            }

            state.Mapping.Match.FingerprintMatches =
                state.MatchedSamples.Count + state.MatchedElementarySamples.Count;
            state.CompleteTimestampOffset();
            validTrackStates.Add(state);
        }

        await ScanTimedElementaryGapsAsync(
            source, states, validTrackStates, sourceIndex, sourceCount, progress, cancellationToken)
            .ConfigureAwait(false);

        foreach (var state in validTrackStates)
        {
            if (state.UsedElementaryFallback && state.HasConfirmedElementaryMatch)
                state.Mapping.Match.Confidence = TsRepairMatchConfidence.ElementaryStreamFingerprint;
            else if (state.MatchedSamples.Count > 0 || state.Mapping.Match.RepairedGapCount > 0)
                state.Mapping.Match.Confidence = TsRepairMatchConfidence.PacketFingerprint;
            else if (state.HasConfirmedElementaryMatch)
                state.Mapping.Match.Confidence = TsRepairMatchConfidence.ElementaryStreamFingerprint;
            else if (state.Mapping.MetadataIsAmbiguous)
                state.Mapping.ReferenceState.Track.Matches.Remove(state.Mapping.Match);
        }
    }

    private static async Task ScanTimedElementaryGapsAsync(
        TsRepairSourceAnalysis source,
        IReadOnlyDictionary<int, DonorPidState> pidStates,
        IReadOnlyList<DonorTrackState> trackStates,
        int sourceIndex,
        int sourceCount,
        IProgress<TsMultiSourceProgress>? progress,
        CancellationToken cancellationToken)
    {
        // 主扫描只建立稀疏的 PTS→文件偏移索引；真正高开销的逐字节 ES 锚点匹配
        // 被拆成相互独立的小窗口，从而可以并行读取，又不破坏 TS/PES 顺序状态。
        var jobs = new List<ElementaryGapSearchJob>();
        foreach (var state in trackStates)
        {
            if (!pidStates.TryGetValue(state.Mapping.SourcePid, out var pidState))
                continue;
            jobs.AddRange(state.CreateTimedElementarySearchJobs(
                pidState.TimestampIndex, source.FilePath, source.Catalog.FileSize));
        }
        if (jobs.Count == 0)
            return;

        var completed = 0;
        var progressSync = new object();
        // 在首个窗口完成前就通知界面，避免大窗口计算期间仍停留在“扫描 100%”。
        progress?.Report(new TsMultiSourceProgress(
            sourceIndex, sourceCount, source.FilePath,
            source.Catalog.FileSize, source.Catalog.FileSize, 0, TimeSpan.Zero,
            IsIntensiveAnalysis: true,
            IntensiveTaskCompleted: 0,
            IntensiveTaskCount: jobs.Count,
            Phase: TsMultiSourceProgressPhase.DonorMatchingScan));
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Min(
                MaxParallelElementarySearchTasks,
                Math.Max(1, Environment.ProcessorCount - 1))
        };
        await Parallel.ForEachAsync(jobs, options, async (job, token) =>
        {
            job.Results = await SearchElementaryGapWindowAsync(job, token)
                .ConfigureAwait(false);
            // IProgress<T> 不保证实现本身可并发调用；完成通知很稀疏，用短锁串行化即可。
            lock (progressSync)
            {
                completed++;
                progress?.Report(new TsMultiSourceProgress(
                    sourceIndex, sourceCount, source.FilePath,
                    source.Catalog.FileSize, source.Catalog.FileSize, 0, TimeSpan.Zero,
                    IsIntensiveAnalysis: true,
                    IntensiveTaskCompleted: completed,
                    IntensiveTaskCount: jobs.Count,
                    Phase: TsMultiSourceProgressPhase.DonorMatchingScan));
            }
        }).ConfigureAwait(false);

        // 工作线程不写入最终候选集合；按规划顺序在当前线程合并，确保多次运行
        // 即使任务完成顺序不同，也会得到完全一致的候选和计数。
        var addedByOwner = new Dictionary<DonorTrackState, int>();
        foreach (var job in jobs)
        {
            foreach (var result in job.Results)
            {
                var gap = result.State.Gap;
                if (gap.Candidates.Any(item => item.SourcePid == job.SourcePid &&
                        string.Equals(item.SourcePath, job.SourcePath, StringComparison.Ordinal)))
                {
                    continue;
                }
                gap.Candidates.Add(result.Candidate);
                result.State.Completed = true;
                addedByOwner[job.Owner] = addedByOwner.GetValueOrDefault(job.Owner) + 1;
            }
        }
        foreach (var item in addedByOwner)
            item.Key.ApplyParallelElementaryResults(item.Value);
    }

    private static async Task<List<ElementaryGapCandidateResult>> SearchElementaryGapWindowAsync(
        ElementaryGapSearchJob job,
        CancellationToken cancellationToken)
    {
        // 每个窗口拥有独立的匹配器和有界缓冲，最大并行度固定，因此内存不会随
        // 异常数量或文件大小线性增长。
        var matcher = new ElementaryGapWindowMatcher(job.GapStates, job.SourcePath, job.SourcePid);
        var buffer = ArrayPool<byte>.Shared.Rent(ParallelReadBufferSize + PacketSize);
        var buffered = 0;
        var position = job.StartOffset;
        var hasContinuity = false;
        var lastContinuityCounter = 0;
        var currentPesPts90k = long.MinValue;
        var lastRawPts90k = long.MinValue;
        var ptsWrapOffset90k = 0L;
        try
        {
            await using var stream = new FileStream(
                job.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                ParallelReadBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            stream.Position = job.StartOffset;
            while (position < job.EndOffset)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = job.EndOffset - position - buffered;
                if (remaining <= 0)
                    break;
                var readLength = (int)Math.Min(ParallelReadBufferSize, remaining);
                var read = await stream.ReadAsync(
                    buffer.AsMemory(buffered, readLength), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                buffered += read;
                var completeLength = buffered / PacketSize * PacketSize;
                for (var offset = 0; offset < completeLength; offset += PacketSize)
                {
                    if ((offset / PacketSize & 0x0FFF) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    var packet = buffer.AsSpan(offset, PacketSize);
                    if (!TryParsePacket(packet, out var info) || info.Pid != job.SourcePid)
                        continue;
                    if (info.TransportError || info.Discontinuity)
                    {
                        matcher.ResetForDiscontinuity();
                        hasContinuity = false;
                        currentPesPts90k = long.MinValue;
                        if (info.TransportError)
                            continue;
                    }
                    if (!info.HasPayload)
                        continue;
                    if (hasContinuity)
                    {
                        var expected = (lastContinuityCounter + 1) & 0x0F;
                        if (info.ContinuityCounter == lastContinuityCounter)
                            continue;
                        if (info.ContinuityCounter != expected)
                            matcher.ResetForDiscontinuity();
                    }
                    hasContinuity = true;
                    lastContinuityCounter = info.ContinuityCounter;
                    var elementaryPayload = GetElementaryPayload(
                        packet, info, out var startsNewPes, out var pesHeaderLength);
                    if (startsNewPes && TryReadPesStart(packet, info, out _, out var rawPts90k) &&
                        rawPts90k != long.MinValue)
                    {
                        currentPesPts90k = UnwrapTimestamp(
                            rawPts90k, ref lastRawPts90k, ref ptsWrapOffset90k);
                        currentPesPts90k = job.TimelineNormalizer?.Normalize(
                            currentPesPts90k, position + offset) ?? currentPesPts90k;
                    }
                    var pesHeader = startsNewPes
                        ? packet.Slice(info.PayloadOffset, pesHeaderLength)
                        : default;
                    matcher.Process(
                        elementaryPayload, startsNewPes, pesHeader, currentPesPts90k);
                }
                position += completeLength;
                buffered -= completeLength;
                if (buffered > 0)
                    buffer.AsSpan(completeLength, buffered).CopyTo(buffer);
            }
            matcher.Finish();
            return matcher.Results;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task ScanPacketsAsync(
        string path,
        long syncOffset,
        int sourceIndex,
        int sourceCount,
        IProgress<TsMultiSourceProgress>? progress,
        TsMultiSourceProgressPhase phase,
        PacketHandler packetHandler,
        CancellationToken cancellationToken)
    {
        var fileSize = new FileInfo(path).Length;
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize + PacketSize);
        var stopwatch = Stopwatch.StartNew();
        var lastProgressTicks = 0L;
        var buffered = 0;
        var bytesProcessed = Math.Max(0, syncOffset);
        try
        {
            await using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read,
                ReadBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            stream.Position = bytesProcessed;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer.AsMemory(buffered, ReadBufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;
                buffered += read;
                var completeLength = buffered / PacketSize * PacketSize;
                var heartbeatCountdown = 1_024;
                for (var offset = 0; offset < completeLength; offset += PacketSize)
                {
                    if (--heartbeatCountdown == 0)
                    {
                        heartbeatCountdown = 1_024;
                        cancellationToken.ThrowIfCancellationRequested();
                        var heartbeatTicks = stopwatch.ElapsedTicks;
                        if (progress is not null &&
                            heartbeatTicks - lastProgressTicks >= Stopwatch.Frequency / 2)
                        {
                            lastProgressTicks = heartbeatTicks;
                            var heartbeatBytes = Math.Min(bytesProcessed + offset, fileSize);
                            progress.Report(new TsMultiSourceProgress(
                                sourceIndex, sourceCount, path, heartbeatBytes, fileSize,
                                Math.Max(0, heartbeatBytes - syncOffset) /
                                Math.Max(0.001, stopwatch.Elapsed.TotalSeconds),
                                stopwatch.Elapsed,
                                IsIntensiveAnalysis: true,
                                Phase: phase));
                        }
                    }
                    if (buffer[offset] != 0x47)
                        continue;
                    packetHandler(buffer.AsSpan(offset, PacketSize), bytesProcessed + offset);
                }
                bytesProcessed += completeLength;
                buffered -= completeLength;
                if (buffered > 0)
                    buffer.AsSpan(completeLength, buffered).CopyTo(buffer);

                var elapsedTicks = stopwatch.ElapsedTicks;
                if (progress is not null && elapsedTicks - lastProgressTicks >= Stopwatch.Frequency / 10)
                {
                    lastProgressTicks = elapsedTicks;
                    progress.Report(new TsMultiSourceProgress(
                        sourceIndex, sourceCount, path, Math.Min(bytesProcessed, fileSize), fileSize,
                        Math.Max(0, bytesProcessed - syncOffset) / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds),
                        stopwatch.Elapsed,
                        IsIntensiveAnalysis: false,
                        Phase: phase));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        progress?.Report(new TsMultiSourceProgress(
            sourceIndex, sourceCount, path, Math.Min(bytesProcessed, fileSize), fileSize,
            Math.Max(0, bytesProcessed - syncOffset) / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds),
            stopwatch.Elapsed,
            Phase: phase));
    }

    private static bool TryParsePacket(ReadOnlySpan<byte> packet, out PacketInfo info)
    {
        info = default;
        if (packet.Length != PacketSize || packet[0] != 0x47)
            return false;
        var adaptationControl = (packet[3] >> 4) & 0x03;
        if (adaptationControl == 0)
            return false;
        var payloadOffset = 4;
        var discontinuity = false;
        if ((adaptationControl & 0x02) != 0)
        {
            var adaptationLength = packet[4];
            if (adaptationLength > 183)
                return false;
            payloadOffset += adaptationLength + 1;
            if (adaptationLength > 0)
                discontinuity = (packet[5] & 0x80) != 0;
        }
        var hasPayload = (adaptationControl & 0x01) != 0 && payloadOffset < PacketSize;
        info = new PacketInfo(
            ((packet[1] & 0x1F) << 8) | packet[2],
            packet[3] & 0x0F,
            payloadOffset,
            hasPayload,
            (packet[1] & 0x40) != 0,
            discontinuity,
            (packet[1] & 0x80) != 0);
        return true;
    }

    private static ReadOnlySpan<byte> GetElementaryPayload(
        ReadOnlySpan<byte> packet,
        PacketInfo info,
        out bool startsNewPes)
    {
        return GetElementaryPayload(packet, info, out startsNewPes, out _);
    }

    private static ReadOnlySpan<byte> GetElementaryPayload(
        ReadOnlySpan<byte> packet,
        PacketInfo info,
        out bool startsNewPes,
        out int pesHeaderLength)
    {
        startsNewPes = false;
        pesHeaderLength = 0;
        if (!info.HasPayload)
            return default;

        var payload = packet[info.PayloadOffset..];
        if (!info.PayloadStart)
            return payload;

        // 仅接受完整位于当前 TS 包内的 MPEG-2 PES 头。无法确认头部边界时放弃该包的
        // ES 回退数据，避免把 PES 元数据误当成音频内容参与匹配。
        if (payload.Length < 9 || payload[0] != 0 || payload[1] != 0 || payload[2] != 1)
            return default;
        var headerLength = 9 + payload[8];
        if (headerLength > payload.Length)
            return default;
        startsNewPes = true;
        pesHeaderLength = headerLength;
        return payload[headerLength..];
    }

    private static bool TryReadPesStart(
        ReadOnlySpan<byte> packet, PacketInfo info, out int expectedTotalLength, out long pts90k)
    {
        expectedTotalLength = 0;
        pts90k = long.MinValue;
        if (!info.PayloadStart || !info.HasPayload)
            return false;
        var payload = packet[info.PayloadOffset..];
        if (payload.Length < 9 || payload[0] != 0 || payload[1] != 0 || payload[2] != 1)
            return false;
        var declaredLength = (payload[4] << 8) | payload[5];
        // 视频 PES 允许 packet_length 为 0，表示长度延伸到下一个 PES 起点；仍需记录
        // PTS 和 ES 指纹，才能在不解码的情况下建立同源视频区间映射。
        expectedTotalLength = declaredLength == 0 ? 0 : declaredLength + 6;
        if ((payload[7] & 0x80) != 0 && payload.Length >= 14)
            pts90k = ReadPesTimestamp(payload[9..14]);
        return true;
    }

    private static long ReadPesTimestamp(ReadOnlySpan<byte> value) =>
        ((long)(value[0] & 0x0E) << 29) |
        ((long)value[1] << 22) |
        ((long)(value[2] & 0xFE) << 14) |
        ((long)value[3] << 7) |
        ((long)value[4] >> 1);

    private static long UnwrapTimestamp(long rawTimestamp, ref long lastRawTimestamp, ref long wrapOffset)
    {
        if (lastRawTimestamp != long.MinValue)
        {
            if (lastRawTimestamp - rawTimestamp > TimestampWrap / 2)
                wrapOffset += TimestampWrap;
            else if (rawTimestamp - lastRawTimestamp > TimestampWrap / 2)
                wrapOffset -= TimestampWrap;
        }
        lastRawTimestamp = rawTimestamp;
        return rawTimestamp + wrapOffset;
    }

    private static TsRepairPesSignature CreatePesSignature(ulong hash, int elementaryLength) =>
        new(hash, elementaryLength);

    private static ulong ComputePesHash(ulong hash, ReadOnlySpan<byte> elementaryPayload)
    {
        foreach (var value in elementaryPayload)
            hash = (hash ^ value) * 1099511628211UL;
        return hash;
    }

    private static bool PesAnchorsEqual(
        IReadOnlyList<TsRepairPesSignature> left, IReadOnlyList<TsRepairPesSignature> right)
    {
        if (left.Count != right.Count)
            return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index])
                return false;
        }
        return true;
    }

    private static int GetMaxRegionPackets(TsRepairPesRegion region) =>
        region.Reason == TsRepairPesRegionReason.CorrelatedVideoElementaryMismatch
            ? MaxVideoPesRegionPackets
            : MaxPesRegionPackets;

    private static int GetReferenceElementaryLength(
        TsRepairPesRegion region, TsRepairPesRegionCandidate candidate)
    {
        if (candidate.ReferenceStartOffset is null || region.ReferencePesStartOffsets.Length == 0)
            return region.ReferenceSignatures.Sum(item => item.ElementaryLength);
        var first = Array.IndexOf(region.ReferencePesStartOffsets, candidate.ReferenceStartOffset.Value);
        var end = Array.IndexOf(region.ReferencePesEndOffsets, candidate.ReferenceEndOffset!.Value);
        if (first < 0 || end < first)
            return -1;
        var length = 0;
        for (var index = first; index <= end; index++)
            length += region.ReferenceSignatures[index].ElementaryLength;
        return length;
    }

    private static ulong ComputePayloadHash(ReadOnlySpan<byte> payload)
    {
        // 64 位混合哈希按 8 字节处理负载；连续 4 包共同作为锚点，避免单包重复内容误匹配。
        var hash = 0x9E3779B185EBCA87UL ^ (uint)payload.Length;
        while (payload.Length >= 8)
        {
            hash ^= BinaryPrimitives.ReadUInt64LittleEndian(payload);
            hash = BitOperations.RotateLeft(hash * 0xC2B2AE3D27D4EB4FUL, 31);
            payload = payload[8..];
        }
        ulong tail = 0;
        for (var index = 0; index < payload.Length; index++)
            tail |= (ulong)payload[index] << (index * 8);
        hash ^= tail;
        hash *= 0x165667B19E3779F9UL;
        hash ^= hash >> 29;
        return hash;
    }

    private static void EnqueueHash(Queue<ulong> queue, ulong hash)
    {
        if (queue.Count == AnchorLength)
            queue.Dequeue();
        queue.Enqueue(hash);
    }

    private static void EnqueueBytes(Queue<byte> queue, ReadOnlySpan<byte> bytes, int capacity)
    {
        foreach (var value in bytes)
        {
            if (queue.Count == capacity)
                queue.Dequeue();
            queue.Enqueue(value);
        }
    }

    private static void TryCreateBeforeElementaryAnchor(
        Queue<byte> history,
        out byte[]? anchor,
        out byte[]? suffix,
        out int repeatedSuffixLength,
        out byte repeatedSuffixByte)
    {
        anchor = null;
        suffix = null;
        repeatedSuffixLength = 0;
        repeatedSuffixByte = 0;
        if (history.Count < ElementaryAnchorLength)
            return;

        var values = history.ToArray();
        var maximumSuffix = Math.Min(
            MaxElementaryAnchorSuffixLength, values.Length - ElementaryAnchorLength);
        var trailingRunLength = 1;
        while (trailingRunLength < values.Length &&
               values[^(trailingRunLength + 1)] == values[^1])
        {
            trailingRunLength++;
        }
        if (trailingRunLength <= MaxElementaryRepeatedSuffixLength &&
            values.Length >= trailingRunLength + ElementaryAnchorLength)
        {
            var anchorStart = values.Length - trailingRunLength - ElementaryAnchorLength;
            var candidate = values.AsSpan(anchorStart, ElementaryAnchorLength);
            if (IsDiscriminativeElementaryAnchor(candidate))
            {
                anchor = candidate.ToArray();
                suffix = [];
                repeatedSuffixLength = trailingRunLength;
                repeatedSuffixByte = values[^1];
                return;
            }
        }
        // 若尾部是长 stuffing run，完全位于 run 内的窗口必然不具区分度，直接跳过，
        // 避免对数千个等价的 0xFF/0x00 窗口重复计算直方图。
        var minimumSuffix = Math.Min(
            maximumSuffix, Math.Max(0, trailingRunLength - ElementaryAnchorLength + 1));
        for (var suffixLength = minimumSuffix;
             suffixLength <= maximumSuffix;
             suffixLength++)
        {
            var anchorStart = values.Length - ElementaryAnchorLength - suffixLength;
            var candidate = values.AsSpan(anchorStart, ElementaryAnchorLength);
            if (!IsDiscriminativeElementaryAnchor(candidate))
                continue;
            anchor = candidate.ToArray();
            suffix = suffixLength == 0
                ? []
                : values.AsSpan(anchorStart + ElementaryAnchorLength, suffixLength).ToArray();
            return;
        }
    }

    private static bool IsDiscriminativeElementaryAnchor(ReadOnlySpan<byte> value)
    {
        Span<int> counts = stackalloc int[256];
        var distinct = 0;
        var maximumCount = 0;
        foreach (var item in value)
        {
            if (counts[item]++ == 0)
                distinct++;
            maximumCount = Math.Max(maximumCount, counts[item]);
        }
        // 编码流边缘可能存在长串 0x00/0xFF stuffing；这种窗口即使扩大到 128 字节
        // 仍会在同一 PES 内反复命中。退回到更早的高熵窗口，并把中间已知字节作为
        // suffix 验证但不重复写入，避免“连续性修好、画面仍花”的假修复。
        return distinct >= 8 && maximumCount <= value.Length * 7 / 8;
    }

    private static ulong ComputeByteHash(ReadOnlySpan<byte> bytes)
    {
        ulong hash = 0;
        foreach (var value in bytes)
            hash = hash * ElementaryHashBase + value + 1UL;
        return hash;
    }

    private static ulong ComputePower(ulong value, int exponent)
    {
        ulong result = 1;
        for (var index = 0; index < exponent; index++)
            result *= value;
        return result;
    }

    private static void RemoveAtSwapBack<T>(List<T> values, int index)
    {
        var last = values.Count - 1;
        if (index != last)
            values[index] = values[last];
        values.RemoveAt(last);
    }

    private readonly record struct PacketInfo(
        int Pid,
        int ContinuityCounter,
        int PayloadOffset,
        bool HasPayload,
        bool PayloadStart,
        bool Discontinuity,
        bool TransportError);

    private delegate void PacketHandler(ReadOnlySpan<byte> packet, long fileOffset);

    private sealed class ReferencePcrCollector(long syncOffset)
    {
        private readonly Dictionary<int, PcrState> _states = [];
        public Dictionary<int, List<TsTimelineRepairService.PcrSample>> Samples { get; } = [];

        public void Process(ReadOnlySpan<byte> packet, PacketInfo info, long fileOffset)
        {
            if (info.TransportError || (packet[3] & 0x20) == 0)
                return;
            var adaptationLength = packet[4];
            if (adaptationLength < 7 || packet.Length < 12 || (packet[5] & 0x10) == 0)
                return;

            var raw = ((long)packet[6] << 25) |
                      ((long)packet[7] << 17) |
                      ((long)packet[8] << 9) |
                      ((long)packet[9] << 1) |
                      ((long)packet[10] >> 7);
            if (!_states.TryGetValue(info.Pid, out var state))
            {
                state = new PcrState();
                _states[info.Pid] = state;
                Samples[info.Pid] = [];
            }
            var unwrapped = UnwrapPcr(raw, state.LastRaw, state.WrapOffset);
            state.LastRaw = raw;
            state.WrapOffset = unwrapped - raw;
            var packetIndex = Math.Max(0, (fileOffset - syncOffset) / PacketSize);
            Samples[info.Pid].Add(new TsTimelineRepairService.PcrSample(
                packetIndex, unwrapped, info.Discontinuity));
        }

        private static long UnwrapPcr(long raw, long lastRaw, long wrapOffset)
        {
            if (lastRaw != long.MinValue)
            {
                if (lastRaw - raw > TimestampWrap / 2)
                    wrapOffset += TimestampWrap;
                else if (raw - lastRaw > TimestampWrap / 2)
                    wrapOffset -= TimestampWrap;
            }
            return raw + wrapOffset;
        }

        private sealed class PcrState
        {
            public long LastRaw = long.MinValue;
            public long WrapOffset;
        }
    }

    private sealed class ReferenceBroadcastClockCollector(
        List<TsBroadcastTimeAnchor> output,
        long syncOffset)
    {
        private const int MaxAnchors = 8_192;
        private readonly TsPsiSectionAssembler _assembler = new();
        private readonly Dictionary<int, DateTimeOffset> _lastAnchorTimes = [];
        private PendingBroadcastTime? _pending;
        private double _minimumIntervalSeconds = 0.5;
        private bool _hasContinuity;
        private int _lastContinuityCounter;

        public bool HasPending => _pending is not null;

        public void ProcessPacket(
            ReadOnlySpan<byte> packet,
            PacketInfo info,
            long fileOffset,
            IEnumerable<ReferenceTrackState> states)
        {
            if (info.TransportError)
            {
                _assembler.DiscardUntilPayloadStart();
                _hasContinuity = false;
                return;
            }
            var continuityBroken = info.Discontinuity;
            if (continuityBroken)
            {
                _assembler.DiscardUntilPayloadStart();
                _hasContinuity = false;
            }
            if (!info.HasPayload)
                return;

            if (_hasContinuity)
            {
                var expected = (_lastContinuityCounter + 1) & 0x0F;
                if (info.ContinuityCounter == _lastContinuityCounter)
                    return;
                if (info.ContinuityCounter != expected)
                {
                    _assembler.DiscardUntilPayloadStart();
                    continuityBroken = true;
                }
            }
            _hasContinuity = true;
            _lastContinuityCounter = info.ContinuityCounter;
            if (continuityBroken && !info.PayloadStart)
                return;

            _assembler.Push(packet[info.PayloadOffset..], info.PayloadStart, section =>
            {
                if (!TsDvbTimeTableParser.TryParseUtc(section, out var utcTime))
                    return;
                var packetIndex = Math.Max(0, (fileOffset - syncOffset) / PacketSize);
                if (AddAvailableProgramAnchors(utcTime, packetIndex, fileOffset, states) == 0)
                {
                    // 表可能先于首个音视频 PES 出现，延迟到取得节目 PTS 后再建立锚点。
                    _pending = new PendingBroadcastTime(utcTime, packetIndex, fileOffset);
                    return;
                }
                _pending = null;
            });
        }

        public void TryResolvePending(IEnumerable<ReferenceTrackState> states)
        {
            if (_pending is not { } pending)
                return;
            if (AddAvailableProgramAnchors(
                    pending.UtcTime, pending.PacketIndex, pending.FileOffset, states) > 0)
                _pending = null;
        }

        private int AddAvailableProgramAnchors(
            DateTimeOffset utcTime,
            long packetIndex,
            long fileOffset,
            IEnumerable<ReferenceTrackState> states)
        {
            var clocks = new Dictionary<int, (long Clock90k, long FileOffset, bool IsVideo)>();
            // 每个 program 都有独立的 STC/PTS 时钟域，不能用某个节目的 PTS 推算其他节目。
            foreach (var state in states)
            {
                var clock90k = state.CurrentPesPts90k;
                var clockOffset = state.CurrentPesPtsFileOffset;
                if (clock90k == long.MinValue || clockOffset < 0)
                    continue;
                var isVideo = TsStreamTypes.IsVideo(state.Track.StreamType);
                if (!clocks.TryGetValue(state.Track.ProgramNumber, out var current) ||
                    clockOffset > current.FileOffset ||
                    (clockOffset == current.FileOffset && isVideo && !current.IsVideo))
                {
                    clocks[state.Track.ProgramNumber] = (clock90k, clockOffset, isVideo);
                }
            }
            foreach (var pair in clocks)
                Add(utcTime, pair.Value.Clock90k, packetIndex, fileOffset, pair.Key);
            return clocks.Count;
        }

        private void Add(
            DateTimeOffset utcTime,
            long clock90k,
            long packetIndex,
            long fileOffset,
            int programNumber)
        {
            if (_lastAnchorTimes.TryGetValue(programNumber, out var lastTime) &&
                Math.Abs((utcTime - lastTime).TotalSeconds) < _minimumIntervalSeconds)
                return;
            _lastAnchorTimes[programNumber] = utcTime;
            if (output.Count >= MaxAnchors)
                Compact();
            output.Add(new TsBroadcastTimeAnchor(
                utcTime, clock90k, packetIndex, fileOffset, programNumber));
        }

        private void Compact()
        {
            // 超长录制才会触发；各节目分别保留首尾并隔点降采样，避免交错顺序偏向某个节目。
            var compacted = new List<TsBroadcastTimeAnchor>(output.Count / 2 + 32);
            foreach (var group in output.GroupBy(item => item.ProgramNumber))
            {
                var values = group.OrderBy(item => item.FileOffset).ToArray();
                if (values.Length <= 2)
                {
                    compacted.AddRange(values);
                    continue;
                }
                compacted.Add(values[0]);
                for (var index = 2; index < values.Length - 1; index += 2)
                    compacted.Add(values[index]);
                compacted.Add(values[^1]);
            }
            compacted.Sort((left, right) => left.FileOffset.CompareTo(right.FileOffset));
            output.Clear();
            output.AddRange(compacted);
            _minimumIntervalSeconds *= 2;
        }

        private readonly record struct PendingBroadcastTime(
            DateTimeOffset UtcTime,
            long PacketIndex,
            long FileOffset);
    }

    private sealed class ReferenceTrackState(
        TsRepairTrackAnalysis track,
        int pcrPid,
        TimelineTimestampNormalizer? timelineNormalizer)
    {
        private TimelineTimestampNormalizer? _timelineNormalizer = timelineNormalizer;
        public TsRepairTrackAnalysis Track { get; } = track;
        public int PcrPid { get; } = pcrPid;
        public Queue<ulong> RecentHashes { get; } = new(AnchorLength);
        public Queue<byte> RecentElementaryBytes { get; } = new(ElementaryAnchorLength);
        private readonly byte[] _elementarySample = new byte[ElementarySampleLength];
        public List<PendingGap> PendingGaps { get; } = [];
        public HashSet<ulong> SampleHashes { get; } = [];
        public Dictionary<ulong, long> SamplePts90k { get; } = [];
        private Dictionary<ulong, long> SamplePtsFileOffsets { get; } = [];
        public HashSet<ulong> ElementarySampleHashes { get; } = [];
        public long PayloadPacketCount;
        public bool HasContinuity;
        public int LastContinuityCounter;
        public TsRepairElementaryKind ElementaryKind { get; } = GetElementaryKind(track);
        public bool SupportsElementaryFallback => ElementaryKind != TsRepairElementaryKind.None;
        private AudioFrameTracker? ElementaryStructureTracker { get; } = CreateAudioFrameTracker(track);
        public int? ElementaryBytesUntilFrameEnd =>
            ElementaryStructureTracker?.BytesRemainingInConfirmedFrame;
        public bool HasPesLength { get; private set; }
        public int PesBytesRemaining { get; private set; }
        private int ElementaryBytesUntilSample { get; set; } = ElementarySampleLength;
        private readonly Queue<ReferencePesInfo> _recentGoodPes = new(PesRegionAnchorCount);
        private readonly List<ReferencePesInfo> _pendingRegionAfter = new(PesRegionClosingGoodPesCount);
        private ReferencePesInfo? _activePes;
        private ReferencePesRegionBuilder? _activePesRegion;
        private readonly List<ReferencePesInfo> _indexedVideoPes = [];
        private readonly Queue<ReferencePesInfo> _recentLargeGapPes = new(LargeGapAnchorCount);
        private readonly Queue<ReferencePesInfo> _recentIncidentPes = [];
        private readonly Queue<long> _recentPtsDeltas = new(LargeGapRecentDeltaCount);
        private ReferencePesInfo? _previousCompletedPes;
        private readonly List<long> _pendingTransportErrorOffsets = [];
        private int _pendingTransportErrorExpectedCounter;
        private long _pendingTransportErrorPts90k = long.MinValue;
        private bool _hasPendingTransportError;
        private long _lastRawPts90k = long.MinValue;
        private long _ptsWrapOffset90k;
        private long _lastKnownPts90k = long.MinValue;
        private long _lastKnownPtsFileOffset = -1;
        private long _firstPtsFileOffset = -1;
        private long _lastPtsFileOffset = -1;

        public void ResetContinuity() => HasContinuity = false;
        public long CurrentPesPts90k => _activePes?.Pts90k ?? _lastKnownPts90k;
        public long CurrentPesPtsFileOffset => _activePes?.StartOffset ?? _lastKnownPtsFileOffset;
        public IReadOnlyList<ReferencePesInfo> IndexedVideoPes => _indexedVideoPes;
        public List<DetectedTrackLargeGap> LargeGapBoundaries { get; } = [];

        public void AddSamplePts(ulong hash, long fileOffset)
        {
            if (CurrentPesPts90k == long.MinValue || !SamplePts90k.TryAdd(hash, CurrentPesPts90k))
                return;
            SamplePtsFileOffsets[hash] = fileOffset;
        }

        public void ApplyTimelineNormalization(TsTimelineRepairAnalysis analysis, long syncOffset)
        {
            _timelineNormalizer = new TimelineTimestampNormalizer(analysis, PcrPid, syncOffset);
            if (_firstPtsFileOffset >= 0 && Track.FirstPts90k != long.MinValue)
                Track.FirstPts90k = _timelineNormalizer.Normalize(Track.FirstPts90k, _firstPtsFileOffset);
            if (_lastPtsFileOffset >= 0 && Track.LastPts90k != long.MinValue)
                Track.LastPts90k = _timelineNormalizer.Normalize(Track.LastPts90k, _lastPtsFileOffset);

            foreach (var hash in SamplePts90k.Keys.ToArray())
            {
                if (SamplePtsFileOffsets.TryGetValue(hash, out var fileOffset))
                    SamplePts90k[hash] = _timelineNormalizer.Normalize(SamplePts90k[hash], fileOffset);
            }
            foreach (var gap in Track.Gaps)
            {
                if (gap.ReferencePts90k != long.MinValue)
                    gap.ReferencePts90k = _timelineNormalizer.Normalize(
                        gap.ReferencePts90k, gap.ReferenceInsertOffset);
            }
            foreach (var region in Track.PesRegions)
            {
                var offsets = region.ReferencePtsFileOffsets;
                for (var index = 0; index < region.ReferencePts90k.Length; index++)
                {
                    if (region.ReferencePts90k[index] == long.MinValue || index >= offsets.Length)
                        continue;
                    region.ReferencePts90k[index] = _timelineNormalizer.Normalize(
                        region.ReferencePts90k[index], offsets[index]);
                }
                if (region.ReferencePts90k.Length > 0)
                {
                    region.ReferenceFirstPts90k = region.ReferencePts90k[0];
                    region.ReferenceLastPts90k = region.ReferencePts90k[^1];
                }
            }
            foreach (var pes in _indexedVideoPes)
            {
                if (pes.Pts90k != long.MinValue)
                    pes.Pts90k = _timelineNormalizer.Normalize(pes.Pts90k, pes.StartOffset);
            }
        }

        public void AddTransportError(long fileOffset, int continuityCounter, bool hasPayload)
        {
            if (!hasPayload)
                return;
            if (!_hasPendingTransportError)
            {
                if (RecentHashes.Count != AnchorLength ||
                    Track.Gaps.Count + PendingGaps.Count >= MaxReferenceGaps)
                {
                    ResetAnchors();
                    return;
                }
                _pendingTransportErrorExpectedCounter = HasContinuity
                    ? (LastContinuityCounter + 1) & 0x0F
                    : continuityCounter;
                _pendingTransportErrorPts90k = CurrentPesPts90k;
                _hasPendingTransportError = true;
            }
            _pendingTransportErrorOffsets.Add(fileOffset);
            InvalidatePesLength();
            ResetElementarySampleWindow();
            InvalidateElementaryStructure();
        }

        public bool TryCompleteTransportError(int nextContinuityCounter)
        {
            if (!_hasPendingTransportError)
                return false;

            var replacementPacketModulo =
                (nextContinuityCounter - _pendingTransportErrorExpectedCounter + 16) & 0x0F;
            if (replacementPacketModulo != 0 || _pendingTransportErrorOffsets.Count >= 16)
            {
                byte[]? beforeElementaryAnchor = null;
                byte[]? beforeElementarySuffix = null;
                var beforeElementaryRepeatedSuffixLength = 0;
                byte beforeElementaryRepeatedSuffixByte = 0;
                if (Track.Gaps.Count + PendingGaps.Count < MaxElementaryRepairGapsPerMapping)
                {
                    TryCreateBeforeElementaryAnchor(
                        RecentElementaryBytes,
                        out beforeElementaryAnchor,
                        out beforeElementarySuffix,
                        out beforeElementaryRepeatedSuffixLength,
                        out beforeElementaryRepeatedSuffixByte);
                }
                PendingGaps.Add(new PendingGap(
                    Track.ReferencePid,
                    _pendingTransportErrorOffsets[0],
                    _pendingTransportErrorExpectedCounter,
                    replacementPacketModulo,
                    _pendingTransportErrorPts90k,
                    RecentHashes.ToArray(),
                    beforeElementaryAnchor,
                    beforeElementarySuffix,
                    beforeElementaryRepeatedSuffixLength,
                    beforeElementaryRepeatedSuffixByte,
                    null,
                    null,
                    ElementaryKind,
                    _pendingTransportErrorOffsets.ToArray()));
            }
            _pendingTransportErrorOffsets.Clear();
            _pendingTransportErrorPts90k = long.MinValue;
            _hasPendingTransportError = false;
            return true;
        }

        public void ProcessPes(
            ReadOnlySpan<byte> packet, PacketInfo info, long fileOffset, ReadOnlySpan<byte> elementaryPayload)
        {
            if (info.PayloadStart)
            {
                FinishPes(fileOffset);
                if (!TryReadPesStart(packet, info, out var expectedLength, out var rawPts90k))
                    return;
                var pts90k = UnwrapTimestamp(rawPts90k, ref _lastRawPts90k, ref _ptsWrapOffset90k);
                pts90k = _timelineNormalizer?.Normalize(pts90k, fileOffset) ?? pts90k;
                _lastKnownPts90k = pts90k;
                _lastKnownPtsFileOffset = fileOffset;
                if (pts90k < Track.FirstPts90k)
                {
                    Track.FirstPts90k = pts90k;
                    _firstPtsFileOffset = fileOffset;
                }
                if (pts90k > Track.LastPts90k)
                {
                    Track.LastPts90k = pts90k;
                    _lastPtsFileOffset = fileOffset;
                }
                _activePes = new ReferencePesInfo(
                    fileOffset, info.ContinuityCounter, expectedLength, pts90k);
            }
            if (_activePes is null)
                return;
            _activePes.PacketCount++;
            if (!info.HasPayload)
                return;
            _activePes.ActualLength += PacketSize - info.PayloadOffset;
            _activePes.ElementaryLength += elementaryPayload.Length;
            _activePes.Hash = ComputePesHash(_activePes.Hash, elementaryPayload);
        }

        public void DiscardPes() => _activePes = null;

        public void CompletePesRegions()
        {
            FinishPes(long.MaxValue);
            // 文件尾没有完整的后锚点，不生成替换窗口，避免把自然截断当成可修复区域。
            _activePesRegion = null;
            _pendingRegionAfter.Clear();
        }

        private void FinishPes(long endOffset)
        {
            var pes = _activePes;
            _activePes = null;
            if (pes is null)
                return;
            pes.EndOffset = endOffset;
            pes.Signature = CreatePesSignature(pes.Hash, pes.ElementaryLength);
            pes.IsMismatch = pes.ExpectedLength > 0 && pes.ActualLength != pes.ExpectedLength;
            DetectLargeGap(pes);

            // H.264/H.265 广播流通常把 PES_packet_length 写成 0，无法靠 PES 长度判断损坏。
            // 多源修复模式下仅保存紧凑的 PES/ES 指纹索引，供同节目音频异常时间窗关联；
            // 普通快速检查不会建立这份索引，也不会承担额外内存成本。
            if (ElementaryKind is TsRepairElementaryKind.H264 or TsRepairElementaryKind.H265 &&
                _indexedVideoPes.Count < MaxIndexedVideoPes && pes.Pts90k != long.MinValue)
            {
                _indexedVideoPes.Add(pes);
            }

            if (pes.IsMismatch)
            {
                if (_activePesRegion is null && _recentGoodPes.Count == PesRegionAnchorCount &&
                    Track.PesRegions.Count < MaxPesRegionsPerTrack)
                {
                    _activePesRegion = new ReferencePesRegionBuilder(
                        Track.ReferencePid, _recentGoodPes.ToArray());
                }
                _activePesRegion?.AddMismatch(pes);
                _pendingRegionAfter.Clear();
                return;
            }

            if (_activePesRegion is not null)
            {
                _pendingRegionAfter.Add(pes);
                if (_pendingRegionAfter.Count == PesRegionClosingGoodPesCount)
                {
                    Track.PesRegions.Add(_activePesRegion.Build(_pendingRegionAfter));
                    _activePesRegion = null;
                    _recentGoodPes.Clear();
                    foreach (var item in _pendingRegionAfter.TakeLast(PesRegionAnchorCount))
                        _recentGoodPes.Enqueue(item);
                    _pendingRegionAfter.Clear();
                }
                return;
            }

            if (_recentGoodPes.Count == PesRegionAnchorCount)
                _recentGoodPes.Dequeue();
            _recentGoodPes.Enqueue(pes);
        }

        private void DetectLargeGap(ReferencePesInfo pes)
        {
            // 已发现的缺口只继续收集少量后锚点，不为整份文件建立新的 PES 索引。
            foreach (var boundary in LargeGapBoundaries)
            {
                if (boundary.AfterAnchor.Count < LargeGapAnchorCount)
                    boundary.AfterAnchor.Add(pes.Signature);
            }

            var previous = _previousCompletedPes;
            _previousCompletedPes = pes;
            if (previous is null || previous.Pts90k == long.MinValue ||
                pes.Pts90k == long.MinValue)
            {
                AddRecentLargeGapPes(pes);
                AddRecentIncidentPes(pes);
                return;
            }

            var delta90k = pes.Pts90k - previous.Pts90k;
            if (delta90k <= 0 || delta90k > MaximumLargeGap90k)
            {
                AddRecentLargeGapPes(pes);
                AddRecentIncidentPes(pes);
                return;
            }

            // 正常音视频 PES 间隔远小于一秒。先做常量时间快速判断，只有真正可疑的
            // 跳变才计算滚动中位数，避免默认扫描路径反复排序和产生临时数组。
            if (delta90k > MinimumLargeGap90k &&
                _recentPtsDeltas.Count >= 4 &&
                _recentLargeGapPes.Count == LargeGapAnchorCount &&
                LargeGapBoundaries.Count < MaxLargeGapsPerTrack)
            {
                var ordered = _recentPtsDeltas.Order().ToArray();
                var nominalDelta90k = ordered[ordered.Length / 2];
                var threshold90k = Math.Max(MinimumLargeGap90k, nominalDelta90k * 4);
                if (delta90k > threshold90k)
                {
                    // 大段内容缺失只保存前后 PTS 和一个参考包边界；即使默认不开启匹配，
                    // 长文件也不会因此建立逐包索引或显著增加常驻内存。
                    LargeGapBoundaries.Add(new DetectedTrackLargeGap
                    {
                        ReferenceBeforePts90k = previous.Pts90k,
                        ReferenceBeforeOffset = previous.StartOffset,
                        ReferenceMissingStartPts90k = previous.Pts90k + nominalDelta90k,
                        ReferenceAfterPts90k = pes.Pts90k,
                        ReferenceAfterOffset = pes.StartOffset,
                        ReferenceAfterContinuityCounter = pes.StartContinuityCounter,
                        BeforeAnchor = _recentLargeGapPes
                            .Select(item => item.Signature).ToArray(),
                        AfterAnchor = [pes.Signature],
                        IncidentHistory = LargeGapBoundaries.Count <
                                          MaxLargeGapIncidentSnapshotsPerTrack
                            ? _recentIncidentPes.ToArray()
                            : []
                    });
                    AddRecentLargeGapPes(pes);
                    AddRecentIncidentPes(pes);
                    return;
                }
            }

            if (delta90k <= MinimumLargeGap90k)
            {
                if (_recentPtsDeltas.Count == LargeGapRecentDeltaCount)
                    _recentPtsDeltas.Dequeue();
                _recentPtsDeltas.Enqueue(delta90k);
            }
            AddRecentLargeGapPes(pes);
            AddRecentIncidentPes(pes);
        }

        private void AddRecentLargeGapPes(ReferencePesInfo pes)
        {
            if (_recentLargeGapPes.Count == LargeGapAnchorCount)
                _recentLargeGapPes.Dequeue();
            _recentLargeGapPes.Enqueue(pes);
        }

        private void AddRecentIncidentPes(ReferencePesInfo pes)
        {
            if (pes.Pts90k == long.MinValue)
                return;
            // 时间戳重置时，旧时间轴上的 PES 已不能作为当前缺口起点；同时用数量
            // 上限兜底，避免异常时间戳令 60 秒淘汰条件长期失效并持续占用内存。
            if (_recentIncidentPes.Count > 0 &&
                pes.Pts90k + LargeGapIncidentHistoryWindow90k < _recentIncidentPes.Peek().Pts90k)
            {
                _recentIncidentPes.Clear();
            }
            _recentIncidentPes.Enqueue(pes);
            while (_recentIncidentPes.Count > MaxLargeGapIncidentHistoryPesPerTrack ||
                   (_recentIncidentPes.Count > 0 &&
                    pes.Pts90k - _recentIncidentPes.Peek().Pts90k >
                    LargeGapIncidentHistoryWindow90k))
            {
                _recentIncidentPes.Dequeue();
            }
        }

        public void TryAddCorrelatedVideoRegion(long startPts90k, long endPts90k)
        {
            if (_indexedVideoPes.Count < VideoPesRegionAnchorCount * 2 + 1 ||
                Track.PesRegions.Count >= MaxPesRegionsPerTrack)
            {
                return;
            }

            var first = _indexedVideoPes.FindIndex(item => item.Pts90k >= startPts90k);
            if (first < VideoPesRegionAnchorCount)
                return;
            var last = _indexedVideoPes.FindLastIndex(item => item.Pts90k <= endPts90k);
            if (last < first || last + VideoPesRegionAnchorCount >= _indexedVideoPes.Count)
                return;

            // 相邻音轨往往同时报告同一录制故障。窗口合并后若仍与既有视频区域重叠，
            // 扩大既有区域会破坏已生成锚点，因此直接保留先生成的完整区域。
            if (Track.PesRegions.Any(item =>
                    item.ReferenceStartOffset < _indexedVideoPes[last + 1].StartOffset &&
                    item.ReferenceEndOffset > _indexedVideoPes[first].StartOffset))
            {
                return;
            }

            var values = _indexedVideoPes.GetRange(first, last - first + 1);
            var packetCount = values.Sum(item => item.PacketCount);
            if (packetCount <= 0 || packetCount > MaxVideoPesRegionPackets)
                return;
            Track.PesRegions.Add(new TsRepairPesRegion
            {
                ReferencePid = Track.ReferencePid,
                ReferenceStartOffset = values[0].StartOffset,
                ReferenceEndOffset = _indexedVideoPes[last + 1].StartOffset,
                ReferenceStartContinuityCounter = values[0].StartContinuityCounter,
                ReferencePacketCount = packetCount,
                ReferenceFirstPts90k = values[0].Pts90k,
                ReferenceLastPts90k = values[^1].Pts90k,
                ReferencePts90k = values.Select(item => item.Pts90k).ToArray(),
                ReferencePtsFileOffsets = values.Select(item => item.StartOffset).ToArray(),
                MismatchCount = 0,
                Reason = TsRepairPesRegionReason.CorrelatedVideoElementaryMismatch,
                BeforeAnchor = _indexedVideoPes.GetRange(
                        first - VideoPesRegionAnchorCount, VideoPesRegionAnchorCount)
                    .Select(item => item.Signature).ToArray(),
                ReferenceSignatures = values.Select(item => item.Signature).ToArray(),
                AfterAnchor = _indexedVideoPes.GetRange(last + 1, VideoPesRegionAnchorCount)
                    .Select(item => item.Signature).ToArray(),
                ReferencePesStartOffsets = values.Select(item => item.StartOffset).ToArray(),
                ReferencePesEndOffsets = values.Select(item => item.EndOffset).ToArray(),
                ReferencePesStartContinuityCounters = values.Select(item => item.StartContinuityCounter).ToArray(),
                ReferencePesPacketCounts = values.Select(item => item.PacketCount).ToArray()
            });
        }

        public void UpdatePesLength(ReadOnlySpan<byte> packet, PacketInfo info)
        {
            var payloadLength = PacketSize - info.PayloadOffset;
            if (info.PayloadStart)
            {
                var payload = packet[info.PayloadOffset..];
                if (payload.Length < 6 || payload[0] != 0 || payload[1] != 0 || payload[2] != 1)
                {
                    InvalidatePesLength();
                    return;
                }
                var pesPacketLength = (payload[4] << 8) | payload[5];
                if (pesPacketLength == 0)
                {
                    InvalidatePesLength();
                    return;
                }
                PesBytesRemaining = 6 + pesPacketLength - payloadLength;
                HasPesLength = PesBytesRemaining > 0;
                return;
            }
            if (!HasPesLength)
                return;
            PesBytesRemaining -= payloadLength;
            if (PesBytesRemaining <= 0)
                InvalidatePesLength();
        }

        public void InvalidatePesLength()
        {
            HasPesLength = false;
            PesBytesRemaining = 0;
        }

        public void ProcessElementarySamples(ReadOnlySpan<byte> payload)
        {
            if (!SupportsElementaryFallback || ElementarySampleHashes.Count >= MaxElementarySamples)
                return;
            while (!payload.IsEmpty && ElementarySampleHashes.Count < MaxElementarySamples)
            {
                if (ElementaryBytesUntilSample > ElementarySampleLength)
                {
                    var skip = Math.Min(
                        payload.Length, ElementaryBytesUntilSample - ElementarySampleLength);
                    ElementaryBytesUntilSample -= skip;
                    payload = payload[skip..];
                    continue;
                }

                var sampleOffset = ElementarySampleLength - ElementaryBytesUntilSample;
                var copyLength = Math.Min(payload.Length, ElementaryBytesUntilSample);
                payload[..copyLength].CopyTo(_elementarySample.AsSpan(sampleOffset));
                payload = payload[copyLength..];
                ElementaryBytesUntilSample -= copyLength;
                if (ElementaryBytesUntilSample > 0)
                    continue;

                ElementarySampleHashes.Add(ComputeByteHash(_elementarySample));
                ElementaryBytesUntilSample = IsAudioElementaryKind(ElementaryKind)
                    ? AudioElementarySampleInterval
                    : VideoElementarySampleInterval;
            }
        }

        public void ResetElementarySampleWindow()
        {
            ElementaryBytesUntilSample = ElementarySampleLength;
        }

        public void ProcessElementaryStructure(ReadOnlySpan<byte> payload) =>
            ElementaryStructureTracker?.Process(payload);

        public void InvalidateElementaryStructure() => ElementaryStructureTracker?.Reset();

        public void ResetAnchors()
        {
            RecentHashes.Clear();
            RecentElementaryBytes.Clear();
            PendingGaps.Clear();
            InvalidatePesLength();
            ResetElementarySampleWindow();
            InvalidateElementaryStructure();
            DiscardPes();
            _recentGoodPes.Clear();
            _pendingRegionAfter.Clear();
            _activePesRegion = null;
            _pendingTransportErrorOffsets.Clear();
            _pendingTransportErrorPts90k = long.MinValue;
            _hasPendingTransportError = false;
            _lastRawPts90k = long.MinValue;
            _ptsWrapOffset90k = 0;
            _lastKnownPts90k = long.MinValue;
            _lastKnownPtsFileOffset = -1;
            _previousCompletedPes = null;
            _recentLargeGapPes.Clear();
            _recentIncidentPes.Clear();
            _recentPtsDeltas.Clear();
        }
    }

    private sealed class DetectedTrackLargeGap
    {
        public required long ReferenceBeforePts90k { get; set; }
        public required long ReferenceBeforeOffset { get; init; }
        public required long ReferenceMissingStartPts90k { get; set; }
        public required long ReferenceAfterPts90k { get; set; }
        public required long ReferenceAfterOffset { get; init; }
        public required int ReferenceAfterContinuityCounter { get; init; }
        public required TsRepairPesSignature[] BeforeAnchor { get; init; }
        public required List<TsRepairPesSignature> AfterAnchor { get; init; }
        public required ReferencePesInfo[] IncidentHistory { get; init; }
    }

    private sealed class ReferencePesInfo(
        long startOffset, int startContinuityCounter, int expectedLength, long pts90k)
    {
        public long StartOffset { get; } = startOffset;
        public long EndOffset { get; set; }
        public int StartContinuityCounter { get; } = startContinuityCounter;
        public int ExpectedLength { get; } = expectedLength;
        public int ActualLength { get; set; }
        public int PacketCount { get; set; }
        public long Pts90k { get; set; } = pts90k;
        public ulong Hash { get; set; } = 1469598103934665603UL;
        public int ElementaryLength { get; set; }
        public TsRepairPesSignature Signature { get; set; }
        public bool IsMismatch { get; set; }
    }

    private sealed class ReferencePesRegionBuilder(int pid, ReferencePesInfo[] beforeAnchor)
    {
        private int _mismatchCount;
        private int _mismatchPacketCount;
        private ReferencePesInfo? _firstMismatch;
        private ReferencePesInfo? _lastMismatch;

        public void AddMismatch(ReferencePesInfo pes)
        {
            _firstMismatch ??= pes;
            _lastMismatch = pes;
            _mismatchCount++;
            _mismatchPacketCount += pes.PacketCount;
        }

        public TsRepairPesRegion Build(IReadOnlyList<ReferencePesInfo> afterAnchor)
        {
            var first = _firstMismatch!;
            var anchorStart = afterAnchor.Count - PesRegionAnchorCount;
            var lastReplacement = afterAnchor[anchorStart - 1];
            return new TsRepairPesRegion
            {
                ReferencePid = pid,
                ReferenceStartOffset = first.StartOffset,
                ReferenceEndOffset = afterAnchor[anchorStart].StartOffset,
                ReferenceStartContinuityCounter = first.StartContinuityCounter,
                ReferencePacketCount = _mismatchPacketCount +
                                       afterAnchor.Take(anchorStart).Sum(item => item.PacketCount),
                ReferenceFirstPts90k = first.Pts90k,
                ReferenceLastPts90k = lastReplacement.Pts90k,
                ReferencePts90k = new[] { first }
                    .Concat(afterAnchor.Take(anchorStart))
                    .Select(item => item.Pts90k).ToArray(),
                ReferencePtsFileOffsets = new[] { first }
                    .Concat(afterAnchor.Take(anchorStart))
                    .Select(item => item.StartOffset).ToArray(),
                MismatchCount = _mismatchCount,
                Reason = TsRepairPesRegionReason.PesSizeMismatch,
                BeforeAnchor = beforeAnchor.Select(item => item.Signature).ToArray(),
                ReferenceSignatures = new[] { first }
                    .Concat(afterAnchor.Take(anchorStart))
                    .Select(item => item.Signature).ToArray(),
                AfterAnchor = afterAnchor.Skip(anchorStart).Select(item => item.Signature).ToArray()
            };
        }
    }

    private sealed class PendingGap(
        int pid,
        long insertOffset,
        int expectedCounter,
        int missingModulo,
        long referencePts90k,
        ulong[] beforeHashes,
        byte[]? beforeElementaryAnchor,
        byte[]? beforeElementarySuffix,
        int beforeElementaryRepeatedSuffixLength,
        byte beforeElementaryRepeatedSuffixByte,
        int? elementaryBytesUntilPesEnd,
        int? elementaryBytesUntilFrameEnd,
        TsRepairElementaryKind elementaryKind,
        long[]? referenceDiscardOffsets = null)
    {
        public List<ulong> AfterHashes { get; } = new(AnchorLength);
        private List<byte>? AfterElementaryBytes { get; } =
            beforeElementaryAnchor is null ? null : new List<byte>(ElementaryAnchorLength);
        private bool ElementaryBoundaryCrossed { get; set; }

        public bool IsComplete => AfterHashes.Count == AnchorLength &&
                                  (AfterElementaryBytes is null ||
                                   AfterElementaryBytes.Count == ElementaryAnchorLength ||
                                   ElementaryBoundaryCrossed);

        public void AddElementaryPayload(ReadOnlySpan<byte> payload, bool startsNewPes)
        {
            if (AfterElementaryBytes is null || AfterElementaryBytes.Count == ElementaryAnchorLength)
                return;
            if (startsNewPes)
                ElementaryBoundaryCrossed = true;
            var copyLength = Math.Min(ElementaryAnchorLength - AfterElementaryBytes.Count, payload.Length);
            for (var index = 0; index < copyLength; index++)
                AfterElementaryBytes.Add(payload[index]);
        }

        public TsRepairGap Build() => new()
        {
            ReferencePid = pid,
            ReferenceInsertOffset = insertOffset,
            ExpectedContinuityCounter = expectedCounter,
            MissingPacketModulo = missingModulo,
            ReferencePts90k = referencePts90k,
            BeforeAnchor = beforeHashes,
            AfterAnchor = AfterHashes.ToArray(),
            BeforeElementaryAnchor = !ElementaryBoundaryCrossed ? beforeElementaryAnchor : null,
            BeforeElementarySuffix = !ElementaryBoundaryCrossed ? beforeElementarySuffix : null,
            BeforeElementaryRepeatedSuffixLength = !ElementaryBoundaryCrossed
                ? beforeElementaryRepeatedSuffixLength
                : 0,
            BeforeElementaryRepeatedSuffixByte = beforeElementaryRepeatedSuffixByte,
            AfterElementaryAnchor = !ElementaryBoundaryCrossed &&
                                    AfterElementaryBytes?.Count == ElementaryAnchorLength
                ? AfterElementaryBytes.ToArray()
                : null,
            ElementaryBytesUntilPesEnd = elementaryBytesUntilPesEnd,
            ElementaryBytesUntilFrameEnd = elementaryBytesUntilFrameEnd,
            ElementaryKind = beforeElementaryAnchor is null
                ? TsRepairElementaryKind.None
                : elementaryKind,
            ReferenceDiscardOffsets = referenceDiscardOffsets ?? []
        };
    }

    private sealed class TrackMapping(
        ReferenceTrackState referenceState,
        int sourcePid,
        TsRepairTrackMatch match,
        TimelineTimestampNormalizer? timelineNormalizer)
    {
        public ReferenceTrackState ReferenceState { get; } = referenceState;
        public int SourcePid { get; } = sourcePid;
        public TsRepairTrackMatch Match { get; } = match;
        public TimelineTimestampNormalizer? TimelineNormalizer { get; } = timelineNormalizer;
        public bool MetadataIsAmbiguous { get; set; }
    }

    private sealed class DonorPidState
    {
        private readonly TimelineTimestampNormalizer? _timelineNormalizer;

        public DonorPidState(
            IEnumerable<TrackMapping> mappings,
            TimelineTimestampNormalizer? timelineNormalizer)
        {
            _timelineNormalizer = timelineNormalizer;
            TrackStates = mappings.Select(item => new DonorTrackState(item)).ToArray();
            foreach (var state in TrackStates)
            {
                foreach (var hash in state.Mapping.ReferenceState.ElementarySampleHashes)
                {
                    if (!_elementarySampleTargets.TryGetValue(hash, out var targets))
                    {
                        targets = [];
                        _elementarySampleTargets[hash] = targets;
                    }
                    targets.Add(state);
                }
            }
        }

        public DonorTrackState[] TrackStates { get; }
        public int PesSizeErrorCount { get; private set; }
        public bool HasContinuity;
        public int LastContinuityCounter;
        private DonorPesInfo? _activePes;
        private readonly Queue<DonorPesInfo> _recentPes = new();
        private readonly byte[] _elementarySampleWindow = new byte[ElementarySampleLength];
        private readonly Dictionary<ulong, List<DonorTrackState>> _elementarySampleTargets = [];
        private int _elementarySampleWindowStart;
        private int _elementarySampleWindowCount;
        private ulong _elementarySampleWindowHash;
        private DonorTrackState? _confirmedTrackState;
        private long _lastRawPts90k = long.MinValue;
        private long _ptsWrapOffset90k;
        private long _lastIndexedPts90k = long.MinValue;

        public void ResetContinuity() => HasContinuity = false;
        public long CurrentPesPts90k => _activePes?.Pts90k ?? long.MinValue;
        public List<DonorTimestampIndexEntry> TimestampIndex { get; } = [];

        public void DiscardPes()
        {
            _activePes = null;
            _recentPes.Clear();
            foreach (var state in TrackStates)
                state.ResetPesRegions();
        }

        public void ProcessPes(
            ReadOnlySpan<byte> packet, PacketInfo info, long fileOffset,
            ReadOnlySpan<byte> elementaryPayload, string sourcePath)
        {
            if (info.PayloadStart)
            {
                FinishPes(fileOffset, sourcePath);
                if (!TryReadPesStart(packet, info, out var expectedLength, out var rawPts90k))
                    return;
                var pts90k = UnwrapTimestamp(rawPts90k, ref _lastRawPts90k, ref _ptsWrapOffset90k);
                pts90k = _timelineNormalizer?.Normalize(pts90k, fileOffset) ?? pts90k;
                if (pts90k != long.MinValue &&
                    (_lastIndexedPts90k == long.MinValue ||
                     Math.Abs(pts90k - _lastIndexedPts90k) >= DonorTimestampIndexInterval90k))
                {
                    // 每 0.5 秒保留一个稀疏定位点即可覆盖 ±2 秒搜索窗；无需把每个 PES
                    // 都留在内存中，也避免第二阶段从文件头重新顺序扫描。
                    TimestampIndex.Add(new DonorTimestampIndexEntry(fileOffset, pts90k));
                    _lastIndexedPts90k = pts90k;
                }
                _activePes = new DonorPesInfo(fileOffset, expectedLength, pts90k);
            }
            if (_activePes is null)
                return;
            _activePes.PacketCount++;
            if (!info.HasPayload)
                return;
            _activePes.ActualLength += PacketSize - info.PayloadOffset;
            _activePes.ElementaryLength += elementaryPayload.Length;
            _activePes.Hash = ComputePesHash(_activePes.Hash, elementaryPayload);
        }

        private void FinishPes(long endOffset, string sourcePath)
        {
            var pes = _activePes;
            _activePes = null;
            if (pes is null)
                return;
            pes.EndOffset = endOffset;
            pes.Signature = CreatePesSignature(pes.Hash, pes.ElementaryLength);
            pes.IsValid = pes.ExpectedLength == 0 || pes.ActualLength == pes.ExpectedLength;
            if (!pes.IsValid)
                PesSizeErrorCount++;
            _recentPes.Enqueue(pes);
            // 启动候选只需检查最长的视频前锚点，不需要为每个 PES 保留数千项历史。
            // 原实现随后对这份大队列反复 ToArray，长文件会产生数 GB 短命分配。
            while (_recentPes.Count > VideoPesRegionAnchorCount)
                _recentPes.Dequeue();
            if (_confirmedTrackState is { } confirmed)
            {
                confirmed.ProcessPes(pes, _recentPes, sourcePath);
            }
            else
            {
                foreach (var state in TrackStates)
                    state.ProcessPes(pes, _recentPes, sourcePath);
            }
        }

        public void ResetForDiscontinuity()
        {
            foreach (var state in TrackStates)
                state.ResetForDiscontinuity();
            DiscardPes();
            _lastRawPts90k = long.MinValue;
            _ptsWrapOffset90k = 0;
            _lastIndexedPts90k = long.MinValue;
            _elementarySampleWindowStart = 0;
            _elementarySampleWindowCount = 0;
            _elementarySampleWindowHash = 0;
        }

        public void ProcessPacket(ulong hash, long fileOffset, string sourcePath)
        {
            if (_confirmedTrackState is { } confirmed)
            {
                confirmed.ProcessPacket(hash, fileOffset, sourcePath, CurrentPesPts90k);
                return;
            }
            foreach (var state in TrackStates)
                state.ProcessPacket(hash, fileOffset, sourcePath, CurrentPesPts90k);
        }

        public void ProcessElementaryPayload(
            ReadOnlySpan<byte> payload,
            string sourcePath,
            bool startsNewPes,
            ReadOnlySpan<byte> pesHeader)
        {
            var needsIdentitySamples = false;
            if (_confirmedTrackState is null)
            {
                foreach (var state in TrackStates)
                {
                    if (!state.NeedsElementaryIdentitySamples)
                        continue;
                    needsIdentitySamples = true;
                    break;
                }
            }
            if (needsIdentitySamples && _elementarySampleTargets.Count > 0)
            {
                foreach (var value in payload)
                {
                    AppendElementarySampleByte(value);
                    if (_elementarySampleWindowCount != ElementarySampleLength ||
                        !_elementarySampleTargets.TryGetValue(_elementarySampleWindowHash, out var targets))
                    {
                        continue;
                    }
                    foreach (var state in targets)
                        state.AddElementarySampleHash(_elementarySampleWindowHash);
                }
                TryResolveConfirmedTrackState();
            }
            var donorPts90k = CurrentPesPts90k;
            if (_confirmedTrackState is { } confirmed)
            {
                confirmed.ProcessElementaryPayload(
                    payload, sourcePath, donorPts90k, startsNewPes, pesHeader);
                return;
            }
            foreach (var state in TrackStates)
            {
                state.ProcessElementaryPayload(
                    payload, sourcePath, donorPts90k, startsNewPes, pesHeader);
            }
        }

        private void TryResolveConfirmedTrackState()
        {
            DonorTrackState? confirmed = null;
            foreach (var state in TrackStates)
            {
                if (!state.HasConfirmedElementaryMatch)
                    continue;
                if (confirmed is not null)
                    return;
                confirmed = state;
            }
            if (confirmed is null)
                return;

            // 同一个辅助 PID 在 ES 指纹唯一确认后，只可能对应这一条参考轨道。
            // 立即停止其余 N×M 歧义候选的逐包处理，避免多节目、缺少语言描述的
            // 大型复用流在整段文件上反复执行无效的哈希查找和候选匹配。
            _confirmedTrackState = confirmed;
        }

        private void AppendElementarySampleByte(byte value)
        {
            if (_elementarySampleWindowCount < ElementarySampleLength)
            {
                var index = (_elementarySampleWindowStart + _elementarySampleWindowCount) %
                            ElementarySampleLength;
                _elementarySampleWindow[index] = value;
                _elementarySampleWindowHash = _elementarySampleWindowHash * ElementaryHashBase + value + 1UL;
                _elementarySampleWindowCount++;
                return;
            }

            var oldest = _elementarySampleWindow[_elementarySampleWindowStart];
            _elementarySampleWindowHash -= (oldest + 1UL) * ElementarySampleHashOldestFactor;
            _elementarySampleWindowHash = _elementarySampleWindowHash * ElementaryHashBase + value + 1UL;
            _elementarySampleWindow[_elementarySampleWindowStart] = value;
            _elementarySampleWindowStart = (_elementarySampleWindowStart + 1) % ElementarySampleLength;
        }
    }

    private sealed class DonorPesInfo(long startOffset, int expectedLength, long pts90k)
    {
        public long StartOffset { get; } = startOffset;
        public long EndOffset { get; set; }
        public int ExpectedLength { get; } = expectedLength;
        public int ActualLength { get; set; }
        public int PacketCount { get; set; }
        public long Pts90k { get; } = pts90k;
        public ulong Hash { get; set; } = 1469598103934665603UL;
        public int ElementaryLength { get; set; }
        public TsRepairPesSignature Signature { get; set; }
        public bool IsValid { get; set; }
    }

    private sealed class LargeGapDonorPidState(
        IEnumerable<LargeGapAnchorMatcher> matchers,
        TimelineTimestampNormalizer? timelineNormalizer)
    {
        private readonly LargeGapAnchorMatcher[] _matchers = matchers.ToArray();
        private readonly Queue<DonorPesInfo> _repairHistory = [];
        private DonorPesInfo? _activePes;
        private long _lastRawPts90k = long.MinValue;
        private long _ptsWrapOffset90k;
        public bool HasContinuity { get; set; }
        public int LastContinuityCounter { get; set; }

        public void ProcessPes(
            ReadOnlySpan<byte> packet,
            PacketInfo info,
            long fileOffset,
            ReadOnlySpan<byte> elementaryPayload,
            string sourcePath)
        {
            if (info.PayloadStart)
            {
                FinishPes(fileOffset);
                if (!TryReadPesStart(packet, info, out var expectedLength, out var rawPts90k))
                    return;
                var pts90k = UnwrapTimestamp(rawPts90k, ref _lastRawPts90k, ref _ptsWrapOffset90k);
                pts90k = timelineNormalizer?.Normalize(pts90k, fileOffset) ?? pts90k;
                _activePes = new DonorPesInfo(fileOffset, expectedLength, pts90k);
            }
            if (_activePes is null)
                return;
            _activePes.PacketCount++;
            if (!info.HasPayload)
                return;
            _activePes.ActualLength += PacketSize - info.PayloadOffset;
            _activePes.ElementaryLength += elementaryPayload.Length;
            _activePes.Hash = ComputePesHash(_activePes.Hash, elementaryPayload);
        }

        public void Complete(long fileSize, string sourcePath) => FinishPes(fileSize);

        private void FinishPes(long endOffset)
        {
            var pes = _activePes;
            _activePes = null;
            if (pes is null)
                return;
            pes.EndOffset = endOffset;
            pes.Signature = CreatePesSignature(pes.Hash, pes.ElementaryLength);
            pes.IsValid = pes.ExpectedLength == 0 || pes.ActualLength == pes.ExpectedLength;
            if (!pes.IsValid)
            {
                foreach (var matcher in _matchers)
                    matcher.ResetForDiscontinuity();
                return;
            }
            if (pes.Pts90k != long.MinValue)
            {
                _repairHistory.Enqueue(pes);
                while (_repairHistory.Count > 0 &&
                       pes.Pts90k - _repairHistory.Peek().Pts90k >
                       LargeGapIncidentHistoryWindow90k + MinimumLargeGap90k)
                {
                    _repairHistory.Dequeue();
                }
            }
            foreach (var matcher in _matchers)
                matcher.ProcessPes(pes, _repairHistory);
        }

        public void ResetForDiscontinuity()
        {
            HasContinuity = false;
            _activePes = null;
            _repairHistory.Clear();
            _lastRawPts90k = long.MinValue;
            _ptsWrapOffset90k = 0;
            foreach (var matcher in _matchers)
                matcher.ResetForDiscontinuity();
        }
    }

    private sealed class LargeGapAnchorMatcher(
        TsRepairLargeGap gap,
        TsRepairTrackMatch match)
    {
        private readonly Queue<DonorPesInfo> _recentPes = new(LargeGapAnchorCount);
        private ActiveLargeGapAnchorMatch? _activeMatch;
        public TsRepairLargeGap Gap { get; } = gap;
        public TsRepairTrackMatch Match { get; } = match;
        public List<LargeGapRangeMatch> Results { get; } = [];

        public void ProcessPes(
            DonorPesInfo pes,
            IReadOnlyCollection<DonorPesInfo> repairHistory)
        {
            if (_activeMatch is not null)
            {
                if (_activeMatch.TryAppend(pes, out var result, out var completed))
                {
                    if (result is { } value && !Results.Any(item =>
                            item.SourceStartOffset == value.SourceStartOffset &&
                            item.SourceEndOffset == value.SourceEndOffset))
                    {
                        Results.Add(value);
                    }
                }
                if (completed)
                    _activeMatch = null;
            }

            _recentPes.Enqueue(pes);
            while (_recentPes.Count > LargeGapAnchorCount)
                _recentPes.Dequeue();
            if (_activeMatch is not null ||
                !QueueEndsWithPesAnchors(_recentPes, Gap.BeforeAnchor) ||
                pes.Pts90k == long.MinValue)
            {
                return;
            }

            var timestampOffset90k = Gap.ReferenceBeforePts90k - pes.Pts90k;
            if (Match.TimestampOffset90k is { } expectedOffset &&
                Math.Abs(timestampOffset90k - expectedOffset) > LargeGapTrackCorrelationTolerance90k * 2)
            {
                return;
            }
            var sourceScanStartOffset = _recentPes.Peek().StartOffset;
            if (Gap.IncludesPrecedingDamage)
            {
                var expectedRepairStartPts90k =
                    Gap.ReferenceMissingStartPts90k - timestampOffset90k;
                var repairStart = repairHistory.FirstOrDefault(item =>
                    item.Pts90k >= expectedRepairStartPts90k -
                    LargeGapPesBoundaryTolerance90k);
                if (repairStart is null ||
                    repairStart.Pts90k > expectedRepairStartPts90k + MinimumLargeGap90k)
                {
                    return;
                }
                sourceScanStartOffset = repairStart.StartOffset;
            }
            _activeMatch = new ActiveLargeGapAnchorMatch(
                Gap, Match.SourcePid, sourceScanStartOffset,
                pes.EndOffset, timestampOffset90k);
        }

        public void ResetForDiscontinuity()
        {
            _recentPes.Clear();
            _activeMatch = null;
        }
    }

    private sealed class ActiveLargeGapAnchorMatch(
        TsRepairLargeGap gap,
        int sourcePid,
        long sourceScanStartOffset,
        long sourceContentStartOffset,
        long timestampOffset90k)
    {
        private int _afterAnchorIndex;
        private long _sourceEndOffset = -1;

        public bool TryAppend(
            DonorPesInfo pes,
            out LargeGapRangeMatch? result,
            out bool completed)
        {
            result = null;
            completed = false;
            if (pes.Pts90k != long.MinValue &&
                pes.Pts90k + timestampOffset90k >
                gap.ReferenceAfterPts90k + LargeGapTrackCorrelationTolerance90k)
            {
                completed = true;
                return false;
            }

            if (_afterAnchorIndex == 0)
            {
                if (pes.Signature != gap.AfterAnchor[0] || pes.Pts90k == long.MinValue ||
                    Math.Abs(pes.Pts90k + timestampOffset90k - gap.ReferenceAfterPts90k) >
                    LargeGapMatchTolerance90k)
                {
                    return false;
                }
                _sourceEndOffset = pes.StartOffset;
                _afterAnchorIndex = 1;
            }
            else
            {
                if (_afterAnchorIndex >= gap.AfterAnchor.Length ||
                    pes.Signature != gap.AfterAnchor[_afterAnchorIndex])
                {
                    completed = true;
                    return false;
                }
                _afterAnchorIndex++;
            }

            if (_afterAnchorIndex < gap.AfterAnchor.Length)
                return false;
            completed = true;
            if (_sourceEndOffset <= sourceContentStartOffset)
                return false;
            result = new LargeGapRangeMatch(
                gap, sourcePid, sourceScanStartOffset, pes.EndOffset, timestampOffset90k);
            return true;
        }
    }

    private sealed class LargeGapRangeTrackInspection(
        TsRepairLargeGapTrackBoundary boundary,
        int sourcePid,
        long timestampOffset90k,
        TimelineTimestampNormalizer? timelineNormalizer)
    {
        private readonly Queue<(ulong Hash, long Offset)> _recentPayloadHashes = [];
        private readonly Queue<(long Offset, bool HasPayload)> _recentSourcePackets = [];
        private bool _hasContinuity;
        private int _lastContinuityCounter;
        private long _lastRawPts90k = long.MinValue;
        private long _ptsWrapOffset90k;
        private long _lastPts90k = long.MinValue;
        private int _ptsCount;
        private bool _invalid;
        private bool _waitingForFirstContentPacket;
        private bool _contentStarted;
        private bool _contentEnded;
        public TsRepairLargeGapTrackBoundary Boundary { get; } = boundary;
        public int SourcePid { get; } = sourcePid;
        public long TimestampOffset90k { get; } = timestampOffset90k;
        public long SourceStartOffset { get; private set; } = -1;
        public long SourceEndOffset { get; private set; } = -1;
        public int PacketCount { get; private set; }
        public int PayloadPacketCount { get; private set; }
        public long FirstPts90k { get; private set; } = long.MinValue;
        public long LastPts90k { get; private set; } = long.MinValue;
        public long MaximumPtsGap90k { get; private set; }

        public void Process(ReadOnlySpan<byte> packet, PacketInfo info, long fileOffset)
        {
            var contentWasStarted = _contentStarted;
            var continuityInvalid = false;
            if (info.HasPayload)
            {
                if (_hasContinuity)
                {
                    var expected = (_lastContinuityCounter + 1) & 0x0F;
                    if (info.ContinuityCounter != expected)
                        continuityInvalid = true;
                }
                _hasContinuity = true;
                _lastContinuityCounter = info.ContinuityCounter;
            }

            long? pts90k = null;
            if (info.PayloadStart &&
                TryReadPesStart(packet, info, out _, out var rawPts90k))
            {
                var value = UnwrapTimestamp(rawPts90k, ref _lastRawPts90k, ref _ptsWrapOffset90k);
                pts90k = timelineNormalizer?.Normalize(value, fileOffset) ?? value;
            }

            var hasPacketAnchors = Boundary.BeforePacketAnchor.Length > 0 &&
                                   Boundary.AfterPacketAnchor.Length > 0;
            if (!hasPacketAnchors && !_contentStarted && pts90k is { } startPts90k)
            {
                var referencePts90k = startPts90k + TimestampOffset90k;
                if (referencePts90k >=
                        Boundary.ReferenceMissingStartPts90k - LargeGapPesBoundaryTolerance90k &&
                    referencePts90k < Boundary.ReferenceAfterPts90k - LargeGapPesBoundaryTolerance90k)
                {
                    StartContent(fileOffset);
                }
            }
            if (!hasPacketAnchors && _contentStarted && !_contentEnded && pts90k is { } endPts90k &&
                endPts90k + TimestampOffset90k >=
                Boundary.ReferenceAfterPts90k - LargeGapPesBoundaryTolerance90k)
            {
                SourceEndOffset = fileOffset;
                _contentEnded = true;
                return;
            }

            if (hasPacketAnchors && _waitingForFirstContentPacket && !_contentStarted)
                StartContent(fileOffset);
            if (_contentStarted && !_contentEnded &&
                (info.TransportError || info.Discontinuity ||
                 (contentWasStarted && continuityInvalid)))
            {
                // 定位扫描会在候选前后读取额外 padding。只有实际准备写入的区间必须
                // 完全健康，不能让 padding 内无关的 CC/TEI 淘汰一个有效候选。
                _invalid = true;
            }
            if (_contentStarted && !_contentEnded)
            {
                PacketCount++;
                if (info.HasPayload)
                    PayloadPacketCount++;
                if (hasPacketAnchors)
                    _recentSourcePackets.Enqueue((fileOffset, info.HasPayload));
            }

            if (pts90k is { } contentPts90k && _contentStarted && !_contentEnded)
                AddPts(contentPts90k);
            if (!hasPacketAnchors || !info.HasPayload)
                return;

            var hash = ComputePayloadHash(packet[info.PayloadOffset..]);
            _recentPayloadHashes.Enqueue((hash, fileOffset));
            while (_recentPayloadHashes.Count > Math.Max(
                       Boundary.BeforePacketAnchor.Length, Boundary.AfterPacketAnchor.Length))
            {
                _recentPayloadHashes.Dequeue();
            }
            if (_contentStarted && _recentPayloadHashes.Count > 0)
            {
                var earliestRecentPayloadOffset = _recentPayloadHashes.Peek().Offset;
                while (_recentSourcePackets.Count > 0 &&
                       _recentSourcePackets.Peek().Offset < earliestRecentPayloadOffset)
                {
                    _recentSourcePackets.Dequeue();
                }
            }
            if (!_contentStarted &&
                PacketAnchorsMatch(_recentPayloadHashes, Boundary.BeforePacketAnchor))
            {
                _waitingForFirstContentPacket = true;
                return;
            }
            if (!_contentStarted ||
                !PacketAnchorsMatch(_recentPayloadHashes, Boundary.AfterPacketAnchor))
            {
                return;
            }

            SourceEndOffset = _recentPayloadHashes
                .Skip(_recentPayloadHashes.Count - Boundary.AfterPacketAnchor.Length)
                .First().Offset;
            while (_recentSourcePackets.Count > 0 &&
                   _recentSourcePackets.Peek().Offset < SourceEndOffset)
            {
                _recentSourcePackets.Dequeue();
            }
            foreach (var item in _recentSourcePackets)
            {
                PacketCount--;
                if (item.HasPayload)
                    PayloadPacketCount--;
            }
            _contentEnded = true;
        }

        private void StartContent(long fileOffset)
        {
            SourceStartOffset = fileOffset;
            _contentStarted = true;
            _waitingForFirstContentPacket = false;
            _recentSourcePackets.Clear();
        }

        private void AddPts(long pts90k)
        {
            if (_lastPts90k != long.MinValue)
            {
                var delta90k = pts90k - _lastPts90k;
                // 带 B 帧的视频 PES 按解码顺序复用时，PTS 会在一个很小的窗口内回摆；
                // 这不是缺片。这里只用明显的正向大跳变否决辅助区间。
                if (delta90k > 0)
                    MaximumPtsGap90k = Math.Max(MaximumPtsGap90k, delta90k);
            }
            FirstPts90k = FirstPts90k == long.MinValue ? pts90k : Math.Min(FirstPts90k, pts90k);
            LastPts90k = Math.Max(LastPts90k, pts90k);
            _lastPts90k = pts90k;
            _ptsCount++;
        }

        private static bool PacketAnchorsMatch(
            Queue<(ulong Hash, long Offset)> values,
            IReadOnlyList<ulong> anchors)
        {
            if (anchors.Count == 0 || values.Count < anchors.Count)
                return false;
            var skip = values.Count - anchors.Count;
            var index = 0;
            foreach (var item in values)
            {
                if (skip > 0)
                {
                    skip--;
                    continue;
                }
                if (item.Hash != anchors[index++])
                    return false;
            }
            return index == anchors.Count;
        }

        public bool IsUsable()
        {
            if (_invalid || !_contentEnded || SourceEndOffset <= SourceStartOffset ||
                PacketCount == 0 || PayloadPacketCount == 0)
                return false;
            if (Boundary.BeforePacketAnchor.Length > 0 && Boundary.AfterPacketAnchor.Length > 0)
                return true;
            if (_ptsCount < 2)
                return false;
            var expectedStartPts90k = Boundary.ReferenceMissingStartPts90k - TimestampOffset90k;
            var expectedEndPts90k = Boundary.ReferenceAfterPts90k - TimestampOffset90k;
            return FirstPts90k <= expectedStartPts90k + MinimumLargeGap90k &&
                   LastPts90k >= expectedEndPts90k - MinimumLargeGap90k &&
                   MaximumPtsGap90k <= MinimumLargeGap90k;
        }
    }

    private readonly record struct LargeGapRangeMatch(
        TsRepairLargeGap Gap,
        int SourcePid,
        long SourceStartOffset,
        long SourceEndOffset,
        long TimestampOffset90k);

    private readonly record struct DonorTimestampIndexEntry(long FileOffset, long Pts90k);

    private sealed class DonorTrackState
    {
        private readonly Dictionary<ulong, List<GapSearchState>> _gapStarts = [];
        private readonly Queue<ulong> _recentHashes = new(AnchorLength);
        private readonly List<ActiveGapCandidate> _activeCandidates = [];
        private readonly Dictionary<ulong, List<GapSearchState>> _elementaryGapStarts = [];
        private readonly List<GapSearchState> _elementaryGapStates = [];
        private readonly byte[] _elementaryWindow = new byte[ElementaryAnchorLength];
        private readonly List<ActiveElementaryGapCandidate> _activeElementaryCandidates = [];
        private readonly Dictionary<TsRepairPesSignature, List<TsRepairPesRegion>> _pesRegionStarts = [];
        private int _elementaryWindowStart;
        private int _elementaryWindowCount;
        private ulong _elementaryWindowHash;
        private readonly HashSet<TsRepairPesRegion> _completedPesRegions = [];
        private readonly List<ActivePesRegionCandidate> _activePesRegions = [];
        private readonly HashSet<TsRepairPesRegion> _activePesRegionSet = [];
        private readonly List<long> _timestampOffsets = new(MaxFingerprintSamples);
        private readonly HashSet<ulong> _timestampOffsetHashes = [];
        private readonly int _untimedElementaryGapCount;
        private int _completedElementaryGapCount;
        private bool _wasScanningElementaryGaps;

        public DonorTrackState(TrackMapping mapping)
        {
            Mapping = mapping;
            var elementaryGapCount = 0;
            var indexedElementaryGaps = new List<TsRepairGap>();
            foreach (var gap in mapping.ReferenceState.Track.Gaps)
            {
                var gapState = new GapSearchState(gap);
                if (!_gapStarts.TryGetValue(gap.BeforeAnchor[0], out var gaps))
                {
                    gaps = [];
                    _gapStarts[gap.BeforeAnchor[0]] = gaps;
                }
                gaps.Add(gapState);

                if (gap.BeforeElementaryAnchor is not { Length: ElementaryAnchorLength } elementaryAnchor ||
                    gap.AfterElementaryAnchor is not { Length: ElementaryAnchorLength } ||
                    elementaryGapCount >= MaxElementaryRepairGapsPerMapping)
                {
                    continue;
                }
                elementaryGapCount++;
                gapState.ElementaryEligible = true;
                _elementaryGapStates.Add(gapState);
                indexedElementaryGaps.Add(gap);
                var elementaryHash = ComputeByteHash(elementaryAnchor);
                if (!_elementaryGapStarts.TryGetValue(elementaryHash, out var elementaryGaps))
                {
                    elementaryGaps = [];
                    _elementaryGapStarts[elementaryHash] = elementaryGaps;
                }
                elementaryGaps.Add(gapState);
            }
            _untimedElementaryGapCount = indexedElementaryGaps.Count(gap =>
                gap.ReferencePts90k == long.MinValue);
            foreach (var region in mapping.ReferenceState.Track.PesRegions)
            {
                if (region.BeforeAnchor.Length == 0)
                    continue;
                var key = region.BeforeAnchor[^1];
                if (!_pesRegionStarts.TryGetValue(key, out var regions))
                {
                    regions = [];
                    _pesRegionStarts[key] = regions;
                }
                regions.Add(region);
            }
        }

        public TrackMapping Mapping { get; }
        public HashSet<ulong> MatchedSamples { get; } = [];
        public HashSet<ulong> MatchedElementarySamples { get; } = [];
        public bool NeedsElementaryIdentitySamples =>
            Mapping.MetadataIsAmbiguous && !HasConfirmedElementaryMatch;
        public bool UsedElementaryFallback { get; private set; }
        public bool HasConfirmedElementaryMatch
        {
            get
            {
                var availableSamples = Mapping.ReferenceState.ElementarySampleHashes.Count;
                // 多音轨可能共享静音帧，因此要求多个分散样本吻合，防止同编码、同时长
                // 但内容不同的轨道仅凭公共静音片段被误认。
                // 两份录制可能并非同一时刻起录；参考源开头的一部分 ES 样本可能根本不在
                // 辅助源中，因此不能要求固定 80% 全部样本。16 个独立 64 字节窗口已经
                // 足以区分同编码音轨，且仍能覆盖短重叠区；同 PID 的轨道则不经过此回退。
                if (availableSamples < MinimumElementarySampleMatches)
                    return false;
                var requiredMatches = Math.Min(16, availableSamples);
                return requiredMatches > 0 && MatchedElementarySamples.Count >= requiredMatches;
            }
        }

        public void AddElementarySampleHash(ulong hash)
        {
            if (NeedsElementaryIdentitySamples)
                MatchedElementarySamples.Add(hash);
        }
        public void ResetForDiscontinuity()
        {
            _recentHashes.Clear();
            foreach (var candidate in _activeCandidates)
                candidate.State.PacketActive = false;
            _activeCandidates.Clear();
            ResetElementaryState();
            ResetPesRegions();
        }

        public void ResetPesRegions()
        {
            _completedPesRegions.Clear();
            _activePesRegions.Clear();
            _activePesRegionSet.Clear();
        }

        public void ProcessPes(
            DonorPesInfo current, Queue<DonorPesInfo> recent, string sourcePath)
        {
            for (var index = _activePesRegions.Count - 1; index >= 0; index--)
            {
                var candidate = _activePesRegions[index];
                if (!candidate.TryAppend(current, sourcePath, Mapping.SourcePid))
                    continue;
                if (candidate.Completed)
                    _completedPesRegions.Add(candidate.Region);
                _activePesRegionSet.Remove(candidate.Region);
                RemoveAtSwapBack(_activePesRegions, index);
            }

            if (!_pesRegionStarts.TryGetValue(current.Signature, out var matchingRegions))
                return;
            foreach (var region in matchingRegions)
            {
                var isVideoRegion = region.Reason ==
                                    TsRepairPesRegionReason.CorrelatedVideoElementaryMismatch;
                var existingCandidateCount = 0;
                for (var candidateIndex = 0; candidateIndex < region.Candidates.Count; candidateIndex++)
                {
                    var existing = region.Candidates[candidateIndex];
                    if (existing.SourcePid == Mapping.SourcePid &&
                        string.Equals(existing.SourcePath, sourcePath, StringComparison.Ordinal))
                    {
                        existingCandidateCount++;
                    }
                }
                if (_completedPesRegions.Contains(region) || _activePesRegionSet.Contains(region) ||
                    (!isVideoRegion && existingCandidateCount > 0) ||
                    existingCandidateCount >= MaxVideoRegionCandidatesPerSource)
                {
                    continue;
                }

                if (!QueueEndsWithPesAnchors(recent, region.BeforeAnchor))
                {
                    continue;
                }
                _activePesRegions.Add(new ActivePesRegionCandidate(
                    region));
                _activePesRegionSet.Add(region);
            }
        }

        public void ProcessPacket(ulong hash, long fileOffset, string sourcePath, long donorPts90k)
        {
            if (Mapping.ReferenceState.SampleHashes.Contains(hash))
            {
                MatchedSamples.Add(hash);
                if (donorPts90k != long.MinValue && _timestampOffsetHashes.Add(hash) &&
                    Mapping.ReferenceState.SamplePts90k.TryGetValue(hash, out var referencePts90k))
                {
                    _timestampOffsets.Add(referencePts90k - donorPts90k);
                }
            }

            for (var index = _activeCandidates.Count - 1; index >= 0; index--)
            {
                var candidate = _activeCandidates[index];
                candidate.Hashes.Add(hash);
                candidate.Offsets.Add(fileOffset);
                if (candidate.TryComplete(sourcePath, Mapping.SourcePid))
                {
                    Mapping.Match.RepairedGapCount++;
                    MarkGapCompleted(candidate.State);
                    RemoveAtSwapBack(_activeCandidates, index);
                    candidate.State.PacketActive = false;
                }
                else if (candidate.Hashes.Count >= MaxRepairPacketsPerGap + AnchorLength)
                {
                    RemoveAtSwapBack(_activeCandidates, index);
                    candidate.State.PacketActive = false;
                }
            }

            EnqueueHash(_recentHashes, hash);
            if (_recentHashes.Count != AnchorLength ||
                !_gapStarts.TryGetValue(_recentHashes.Peek(), out var gaps))
            {
                return;
            }
            var recent = _recentHashes.ToArray();
            foreach (var gapState in gaps)
            {
                var gap = gapState.Gap;
                var hasCandidate = false;
                for (var candidateIndex = 0; candidateIndex < gap.Candidates.Count; candidateIndex++)
                {
                    var existing = gap.Candidates[candidateIndex];
                    if (existing.SourcePid == Mapping.SourcePid &&
                        string.Equals(existing.SourcePath, sourcePath, StringComparison.Ordinal))
                    {
                        hasCandidate = true;
                        break;
                    }
                }
                if (_activeCandidates.Count >= 64 || gapState.Completed || gapState.PacketActive ||
                    hasCandidate ||
                    !recent.AsSpan().SequenceEqual(gap.BeforeAnchor))
                {
                    continue;
                }
                _activeCandidates.Add(new ActiveGapCandidate(gapState));
                gapState.PacketActive = true;
            }
        }

        public void ProcessElementaryPayload(
            ReadOnlySpan<byte> payload,
            string sourcePath,
            long donorPts90k,
            bool startsNewPes,
            ReadOnlySpan<byte> pesHeader)
        {
            if (!Mapping.ReferenceState.SupportsElementaryFallback || payload.IsEmpty)
                return;

            // 带 PTS 的缺口在主扫描完成后按时间窗口并行匹配；这里只保留
            // 无法按时间定位的少量缺口，避免再次退化为整条 ES 的串行热点。
            var needsGapSearch = _completedElementaryGapCount < _untimedElementaryGapCount;
            if (!needsGapSearch)
            {
                if (_wasScanningElementaryGaps)
                    ResetElementaryGapSearchState();
                _wasScanningElementaryGaps = false;
            }
            else
            {
                _wasScanningElementaryGaps = true;
            }
            if (!needsGapSearch)
                return;

            if (startsNewPes)
            {
                foreach (var candidate in _activeElementaryCandidates)
                    candidate.BeginPes(pesHeader);
            }
            foreach (var candidate in _activeElementaryCandidates)
                candidate.BeginPacket();

            // PES 头的组织方式和边界可能因复用器而异，因此辅助源侧把 ES 视为连续字节流；
            // 是否会跨越参考源 PES 边界，已由参考源记录的 PES 剩余长度单独约束。

            foreach (var value in payload)
            {
                if (_elementaryGapStarts.Count == 0)
                    continue;
                for (var index = _activeElementaryCandidates.Count - 1; index >= 0; index--)
                {
                    var candidate = _activeElementaryCandidates[index];
                    if (candidate.State.Completed)
                    {
                        candidate.State.ElementaryActive = false;
                        RemoveAtSwapBack(_activeElementaryCandidates, index);
                        continue;
                    }
                    candidate.Append(value);
                    if (candidate.TryComplete(sourcePath, Mapping.SourcePid, out var added))
                    {
                        if (added)
                        {
                            Mapping.Match.RepairedGapCount++;
                            UsedElementaryFallback = true;
                            MarkGapCompleted(candidate.State);
                        }
                        RemoveAtSwapBack(_activeElementaryCandidates, index);
                        candidate.State.ElementaryActive = false;
                    }
                    else if (candidate.Bytes.Count >= candidate.MaximumByteCount)
                    {
                        RemoveAtSwapBack(_activeElementaryCandidates, index);
                        candidate.State.ElementaryActive = false;
                    }
                }

                AppendElementaryByte(value);
                if (_elementaryWindowCount != ElementaryAnchorLength ||
                    !_elementaryGapStarts.TryGetValue(_elementaryWindowHash, out var gaps))
                {
                    continue;
                }
                foreach (var gapState in gaps)
                {
                    if (gapState.Completed)
                        continue;
                    var gap = gapState.Gap;
                    if (gap.ReferencePts90k != long.MinValue)
                        continue;
                    var hasCandidate = false;
                    for (var candidateIndex = 0; candidateIndex < gap.Candidates.Count; candidateIndex++)
                    {
                        var existing = gap.Candidates[candidateIndex];
                        if (existing.SourcePid == Mapping.SourcePid &&
                            string.Equals(existing.SourcePath, sourcePath, StringComparison.Ordinal))
                        {
                            hasCandidate = true;
                            break;
                        }
                    }
                    if (_activeElementaryCandidates.Count >= 64 || gapState.ElementaryActive ||
                        hasCandidate ||
                        !ElementaryWindowEquals(gap.BeforeElementaryAnchor!))
                    {
                        continue;
                    }
                    _activeElementaryCandidates.Add(new ActiveElementaryGapCandidate(gapState));
                    gapState.ElementaryActive = true;
                }
            }
        }

        private void MarkGapCompleted(GapSearchState state)
        {
            if (state.Completed)
                return;
            state.Completed = true;
            if (state.ElementaryEligible && state.Gap.ReferencePts90k == long.MinValue)
                _completedElementaryGapCount++;
        }

        private void ResetElementaryGapSearchState()
        {
            _elementaryWindowStart = 0;
            _elementaryWindowCount = 0;
            _elementaryWindowHash = 0;
            foreach (var candidate in _activeElementaryCandidates)
                candidate.State.ElementaryActive = false;
            _activeElementaryCandidates.Clear();
        }

        public void RemoveUnconfirmedElementaryCandidates(string sourcePath)
        {
            var removed = 0;
            foreach (var gap in Mapping.ReferenceState.Track.Gaps)
            {
                removed += gap.Candidates.RemoveAll(item =>
                    item.ElementaryPayload is not null && item.SourcePid == Mapping.SourcePid &&
                    string.Equals(item.SourcePath, sourcePath, StringComparison.Ordinal));
            }
            Mapping.Match.RepairedGapCount = Math.Max(0, Mapping.Match.RepairedGapCount - removed);
            UsedElementaryFallback = false;
        }

        public void RemoveAllCandidates(string sourcePath)
        {
            var removed = 0;
            foreach (var gap in Mapping.ReferenceState.Track.Gaps)
            {
                removed += gap.Candidates.RemoveAll(item =>
                    item.SourcePid == Mapping.SourcePid &&
                    string.Equals(item.SourcePath, sourcePath, StringComparison.Ordinal));
            }
            Mapping.Match.RepairedGapCount = Math.Max(0, Mapping.Match.RepairedGapCount - removed);
            UsedElementaryFallback = false;
        }

        public void CompleteTimestampOffset()
        {
            if (_timestampOffsets.Count < MinimumElementarySampleMatches)
                return;
            // 压缩视频中仍可能出现少量重复负载。先按 0.1 秒偏移聚类，再取最大簇
            // 的中位数，避免单个重复镜头把后续目标 ES 窗口带到错误时刻。
            var cluster = _timestampOffsets
                .GroupBy(value => (long)Math.Round(value / 9_000.0))
                .OrderByDescending(group => group.Count())
                .First();
            if (cluster.Count() < MinimumElementarySampleMatches)
                return;
            var values = cluster.Order().ToArray();
            Mapping.Match.TimestampOffset90k = values[values.Length / 2];
        }

        public List<ElementaryGapSearchJob> CreateTimedElementarySearchJobs(
            IReadOnlyList<DonorTimestampIndexEntry> timestampIndex,
            string sourcePath,
            long fileSize)
        {
            if (timestampIndex.Count == 0)
                return [];

            // 参考时间减去两份录制的稳健时间戳偏移，得到辅助源中的目标时间。
            // 相邻 ±2 秒区间先合并，避免异常密集处对同一批文件数据反复读取。
            var timestampOffset90k = Mapping.Match.TimestampOffset90k ?? 0;
            var pending = _elementaryGapStates
                .Where(state => !state.Completed && state.Gap.ReferencePts90k != long.MinValue)
                .Select(state => (State: state,
                    DonorPts90k: state.Gap.ReferencePts90k - timestampOffset90k))
                .OrderBy(item => item.DonorPts90k)
                .ToArray();
            if (pending.Length == 0)
                return [];
            foreach (var item in pending)
                item.State.SearchTargetPts90k = item.DonorPts90k;

            var windows = new List<ElementaryGapSearchWindow>();
            foreach (var item in pending)
            {
                var startPts90k = item.DonorPts90k - ElementaryGapSearchPadding90k;
                var endPts90k = item.DonorPts90k + ElementaryGapSearchPadding90k;
                var startsNewWindow = windows.Count == 0;
                if (!startsNewWindow)
                {
                    var current = windows[^1];
                    // 密集异常若无限串联成一个大窗口，第二阶段仍会退化为单核长任务。
                    // 限制单任务跨度和目标数；新旧窗口可以重叠，但每个缺口只归属一个任务。
                    startsNewWindow = startPts90k > current.EndPts90k ||
                                      current.GapStates.Count >= MaxElementaryGapsPerSearchTask ||
                                      Math.Max(current.EndPts90k, endPts90k) - current.StartPts90k >
                                      MaxElementarySearchWindowDuration90k;
                }
                if (startsNewWindow)
                {
                    windows.Add(new ElementaryGapSearchWindow(startPts90k, endPts90k));
                }
                else
                {
                    windows[^1].EndPts90k = Math.Max(windows[^1].EndPts90k, endPts90k);
                }
                windows[^1].GapStates.Add(item.State);
            }

            var jobs = new List<ElementaryGapSearchJob>(windows.Count);
            foreach (var window in windows)
            {
                var centerPts90k = window.StartPts90k + (window.EndPts90k - window.StartPts90k) / 2;
                var closestIndex = FindClosestTimestampIndex(timestampIndex, centerPts90k);
                if (closestIndex < 0 || Math.Abs(timestampIndex[closestIndex].Pts90k - centerPts90k) >
                    ElementaryGapSearchPadding90k * 2)
                {
                    continue;
                }

                var startIndex = closestIndex;
                while (startIndex > 0 &&
                       timestampIndex[startIndex - 1].Pts90k <= timestampIndex[startIndex].Pts90k &&
                       timestampIndex[startIndex - 1].Pts90k >=
                       window.StartPts90k - DonorTimestampIndexInterval90k)
                {
                    startIndex--;
                }
                if (startIndex > 0)
                    startIndex--;

                var endIndex = closestIndex;
                while (endIndex + 1 < timestampIndex.Count &&
                       timestampIndex[endIndex + 1].Pts90k >= timestampIndex[endIndex].Pts90k &&
                       timestampIndex[endIndex + 1].Pts90k <=
                       window.EndPts90k + DonorTimestampIndexInterval90k)
                {
                    endIndex++;
                }
                if (endIndex + 1 < timestampIndex.Count)
                    endIndex++;

                var startOffset = timestampIndex[startIndex].FileOffset;
                var endOffset = endIndex + 1 < timestampIndex.Count
                    ? timestampIndex[endIndex + 1].FileOffset
                    : fileSize;
                if (endOffset <= startOffset)
                    continue;
                jobs.Add(new ElementaryGapSearchJob(
                    this, sourcePath, Mapping.SourcePid, startOffset, Math.Min(endOffset, fileSize),
                    window.GapStates.ToArray(), Mapping.TimelineNormalizer));
            }
            return jobs;
        }

        public void ApplyParallelElementaryResults(int addedCandidateCount)
        {
            if (addedCandidateCount <= 0)
                return;
            Mapping.Match.RepairedGapCount += addedCandidateCount;
            UsedElementaryFallback = true;
        }

        private static int FindClosestTimestampIndex(
            IReadOnlyList<DonorTimestampIndexEntry> values,
            long targetPts90k)
        {
            var closestIndex = -1;
            var closestDistance = long.MaxValue;
            for (var index = 0; index < values.Count; index++)
            {
                var distance = Math.Abs(values[index].Pts90k - targetPts90k);
                if (distance >= closestDistance)
                    continue;
                closestDistance = distance;
                closestIndex = index;
            }
            return closestIndex;
        }

        private void AppendElementaryByte(byte value)
        {
            if (_elementaryWindowCount < ElementaryAnchorLength)
            {
                var index = (_elementaryWindowStart + _elementaryWindowCount) % ElementaryAnchorLength;
                _elementaryWindow[index] = value;
                _elementaryWindowHash = _elementaryWindowHash * ElementaryHashBase + value + 1UL;
                _elementaryWindowCount++;
                return;
            }

            var oldest = _elementaryWindow[_elementaryWindowStart];
            _elementaryWindowHash -= (oldest + 1UL) * ElementaryHashOldestFactor;
            _elementaryWindowHash = _elementaryWindowHash * ElementaryHashBase + value + 1UL;
            _elementaryWindow[_elementaryWindowStart] = value;
            _elementaryWindowStart = (_elementaryWindowStart + 1) % ElementaryAnchorLength;
        }

        private bool ElementaryWindowEquals(ReadOnlySpan<byte> expected)
        {
            for (var index = 0; index < ElementaryAnchorLength; index++)
            {
                if (_elementaryWindow[(_elementaryWindowStart + index) % ElementaryAnchorLength] != expected[index])
                    return false;
            }
            return true;
        }

        private void ResetElementaryWindowAndCandidates()
        {
            _elementaryWindowStart = 0;
            _elementaryWindowCount = 0;
            _elementaryWindowHash = 0;
            foreach (var candidate in _activeElementaryCandidates)
                candidate.State.ElementaryActive = false;
            _activeElementaryCandidates.Clear();
        }

        private void ResetElementaryState() => ResetElementaryWindowAndCandidates();
    }

    private static bool QueueEndsWithPesAnchors(
        Queue<DonorPesInfo> values, IReadOnlyList<TsRepairPesSignature> suffix)
    {
        if (values.Count < suffix.Count)
            return false;
        var skip = values.Count - suffix.Count;
        var index = 0;
        foreach (var value in values)
        {
            if (skip > 0)
            {
                skip--;
                continue;
            }
            if (value.Signature != suffix[index++])
                return false;
        }
        return index == suffix.Count;
    }

    private sealed class AudioFrameTracker(TsRepairElementaryKind kind)
    {
        private const int RequiredConfirmedFrames = 2;
        private const int Av3aFrameSamples = 1_024;
        private static readonly int[] Mpeg1Layer1Bitrates =
            [32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448];
        private static readonly int[] Mpeg1Layer2Bitrates =
            [32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
        private static readonly int[] Mpeg1Layer3Bitrates =
            [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
        private static readonly int[] Mpeg2Layer1Bitrates =
            [32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256];
        private static readonly int[] Mpeg2Layer2Or3Bitrates =
            [8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
        private static readonly int[] MpegSampleRates = [44_100, 48_000, 32_000];
        private static readonly int[] Av3aSampleRates =
            [192_000, 96_000, 48_000, 44_100, 32_000, 24_000, 22_050, 16_000, 8_000];
        private static readonly int[][] Av3aBitrates =
        [
            [16_000, 32_000, 44_000, 56_000, 64_000, 72_000, 80_000, 96_000, 128_000, 144_000, 164_000, 192_000],
            [24_000, 32_000, 48_000, 64_000, 80_000, 96_000, 128_000, 144_000, 192_000, 256_000, 320_000],
            [192_000, 256_000, 320_000, 384_000, 448_000, 512_000, 640_000, 720_000, 144_000, 96_000, 128_000, 160_000],
            [192_000, 480_000, 256_000, 384_000, 576_000, 640_000, 128_000, 160_000],
            [],
            [],
            [48_000, 96_000, 128_000, 192_000, 256_000],
            [152_000, 320_000, 480_000, 576_000],
            [176_000, 384_000, 576_000, 704_000, 256_000, 448_000],
            [216_000, 480_000, 576_000, 384_000, 768_000],
            [240_000, 608_000, 384_000, 512_000, 832_000],
            [48_000, 96_000, 128_000, 192_000, 256_000],
            [192_000, 256_000, 320_000, 384_000, 480_000, 512_000, 640_000],
            [256_000, 320_000, 384_000, 512_000, 640_000, 896_000]
        ];

        private readonly byte[] _header = new byte[GetHeaderLength(kind)];
        private int _headerCount;
        private int _bytesRemaining;
        private int _confirmedFrames;
        private bool _expectingAlignedHeader;

        public int? BytesRemainingInConfirmedFrame =>
            _confirmedFrames >= RequiredConfirmedFrames && _bytesRemaining > 0
                ? _bytesRemaining
                : null;

        public void Process(ReadOnlySpan<byte> payload)
        {
            while (!payload.IsEmpty)
            {
                if (_bytesRemaining == 0)
                {
                    ProcessByte(payload[0]);
                    payload = payload[1..];
                    continue;
                }

                // 已确认帧内的数据无需逐字节检查，整段跳过可把热路径降为每个 TS
                // 负载一次减法；只有最多 9 字节的下一帧头需要逐字节解析。
                var consumed = Math.Min(_bytesRemaining, payload.Length);
                _bytesRemaining -= consumed;
                payload = payload[consumed..];
                if (_bytesRemaining == 0)
                {
                    _headerCount = 0;
                    _expectingAlignedHeader = true;
                }
            }
        }

        public void Reset()
        {
            _headerCount = 0;
            _bytesRemaining = 0;
            _confirmedFrames = 0;
            _expectingAlignedHeader = false;
        }

        private void ProcessByte(byte value)
        {
            if (_bytesRemaining > 0)
            {
                _bytesRemaining--;
                if (_bytesRemaining == 0)
                {
                    _headerCount = 0;
                    _expectingAlignedHeader = true;
                }
                return;
            }

            if (_headerCount < _header.Length)
            {
                _header[_headerCount++] = value;
            }
            else
            {
                _header.AsSpan(1).CopyTo(_header);
                _header[^1] = value;
            }

            if (_headerCount < _header.Length)
                return;

            if (TryGetFrameLength(_header, out var frameLength))
            {
                // 第一次同步可能只是压缩负载中的偶然字节；只有下一帧也恰好落在计算出的
                // 边界上才认为帧结构可信，避免用单个短同步字放宽修复条件。
                _confirmedFrames = _expectingAlignedHeader
                    ? Math.Min(RequiredConfirmedFrames, _confirmedFrames + 1)
                    : 1;
                _bytesRemaining = frameLength - _header.Length;
                _headerCount = 0;
                _expectingAlignedHeader = _bytesRemaining == 0;
                return;
            }

            if (_expectingAlignedHeader)
            {
                _confirmedFrames = 0;
                _expectingAlignedHeader = false;
            }
        }

        private bool TryGetFrameLength(ReadOnlySpan<byte> header, out int frameLength) => kind switch
        {
            TsRepairElementaryKind.MpegAudio => TryGetMpegAudioFrameLength(header, out frameLength),
            TsRepairElementaryKind.AacAdts => TryGetAdtsFrameLength(header, out frameLength),
            TsRepairElementaryKind.AacLatm => TryGetLatmFrameLength(header, out frameLength),
            TsRepairElementaryKind.Av3a => TryGetAv3aFrameLength(header, out frameLength),
            _ => Fail(out frameLength)
        };

        private static int GetHeaderLength(TsRepairElementaryKind elementaryKind) => elementaryKind switch
        {
            TsRepairElementaryKind.MpegAudio => 4,
            TsRepairElementaryKind.AacAdts => 7,
            TsRepairElementaryKind.AacLatm => 3,
            TsRepairElementaryKind.Av3a => 9,
            _ => throw new ArgumentOutOfRangeException(nameof(elementaryKind))
        };

        private static bool TryGetMpegAudioFrameLength(ReadOnlySpan<byte> header, out int frameLength)
        {
            frameLength = 0;
            var value = BinaryPrimitives.ReadUInt32BigEndian(header);
            if ((value & 0xFFE00000u) != 0xFFE00000u)
                return false;

            var version = (int)((value >> 19) & 0x03);
            var layer = (int)((value >> 17) & 0x03);
            var bitrateIndex = (int)((value >> 12) & 0x0F);
            var sampleRateIndex = (int)((value >> 10) & 0x03);
            if (version == 1 || layer == 0 || bitrateIndex is 0 or 15 || sampleRateIndex == 3)
                return false;

            var bitrateTable = version == 3
                ? layer switch
                {
                    3 => Mpeg1Layer1Bitrates,
                    2 => Mpeg1Layer2Bitrates,
                    _ => Mpeg1Layer3Bitrates
                }
                : layer == 3
                    ? Mpeg2Layer1Bitrates
                    : Mpeg2Layer2Or3Bitrates;
            var bitrate = bitrateTable[bitrateIndex - 1] * 1_000;
            var sampleRate = MpegSampleRates[sampleRateIndex] / (version == 3 ? 1 : version == 2 ? 2 : 4);
            var padding = (int)((value >> 9) & 1);
            frameLength = layer switch
            {
                3 => ((12 * bitrate / sampleRate) + padding) * 4,
                2 => 144 * bitrate / sampleRate + padding,
                _ => (version == 3 ? 144 : 72) * bitrate / sampleRate + padding
            };
            return frameLength >= header.Length;
        }

        private static bool TryGetAdtsFrameLength(ReadOnlySpan<byte> header, out int frameLength)
        {
            frameLength = 0;
            if (header[0] != 0xFF || (header[1] & 0xF6) != 0xF0 || (header[2] & 0x3C) == 0x3C)
                return false;
            frameLength = ((header[3] & 0x03) << 11) | (header[4] << 3) | (header[5] >> 5);
            var actualHeaderLength = (header[1] & 1) != 0 ? 7 : 9;
            return frameLength >= actualHeaderLength;
        }

        private static bool TryGetLatmFrameLength(ReadOnlySpan<byte> header, out int frameLength)
        {
            var value = (header[0] << 16) | (header[1] << 8) | header[2];
            frameLength = 0;
            if ((value & 0xFFE000) != 0x56E000)
                return false;
            frameLength = 3 + (value & 0x1FFF);
            return frameLength >= 7;
        }

        private static bool TryGetAv3aFrameLength(ReadOnlySpan<byte> header, out int frameLength)
        {
            frameLength = 0;
            var bitOffset = 0;
            if (ReadBits(header, ref bitOffset, 12) != 0xFFF ||
                ReadBits(header, ref bitOffset, 4) != 2)
            {
                return false;
            }

            bitOffset += 4;
            var codingProfile = ReadBits(header, ref bitOffset, 3);
            var sampleRateIndex = ReadBits(header, ref bitOffset, 4);
            if ((uint)sampleRateIndex >= Av3aSampleRates.Length)
                return false;
            var sampleRate = Av3aSampleRates[sampleRateIndex];
            bitOffset += 8;

            var channelIndex = -1;
            long totalBitrate;
            if (codingProfile == 0)
            {
                channelIndex = ReadBits(header, ref bitOffset, 7);
                if (!IsValidAv3aChannelIndex(channelIndex, allowMono: true))
                    return false;
                totalBitrate = 0;
            }
            else if (codingProfile == 1)
            {
                var soundbedType = ReadBits(header, ref bitOffset, 2);
                if (soundbedType == 0)
                {
                    var objects = ReadBits(header, ref bitOffset, 7) + 1;
                    var objectBitrateIndex = ReadBits(header, ref bitOffset, 4);
                    if (!TryGetAv3aBitrate(0, objectBitrateIndex, out var objectBitrate))
                        return false;
                    totalBitrate = (long)objectBitrate * objects;
                }
                else if (soundbedType == 1)
                {
                    channelIndex = ReadBits(header, ref bitOffset, 7);
                    var soundbedBitrateIndex = ReadBits(header, ref bitOffset, 4);
                    var objects = ReadBits(header, ref bitOffset, 7) + 1;
                    var objectBitrateIndex = ReadBits(header, ref bitOffset, 4);
                    if (!IsValidAv3aChannelIndex(channelIndex, allowMono: false) ||
                        !TryGetAv3aBitrate(channelIndex, soundbedBitrateIndex, out var soundbedBitrate) ||
                        !TryGetAv3aBitrate(0, objectBitrateIndex, out var objectBitrate))
                    {
                        return false;
                    }
                    totalBitrate = soundbedBitrate + (long)objectBitrate * objects;
                }
                else
                {
                    return false;
                }
            }
            else if (codingProfile == 2)
            {
                channelIndex = ReadBits(header, ref bitOffset, 4) switch
                {
                    0 => 11,
                    1 => 12,
                    2 => 13,
                    _ => -1
                };
                if (channelIndex < 0)
                    return false;
                totalBitrate = 0;
            }
            else
            {
                return false;
            }

            bitOffset += 2;
            if (codingProfile != 1)
            {
                var bitrateIndex = ReadBits(header, ref bitOffset, 4);
                if (!TryGetAv3aBitrate(channelIndex, bitrateIndex, out var bitrate))
                    return false;
                totalBitrate = bitrate;
            }

            if (totalBitrate <= 0)
                return false;
            var numerator = totalBitrate * Av3aFrameSamples;
            frameLength = sampleRate == 44_100
                ? (int)(((numerator / sampleRate) + 7) / 8)
                : (int)((numerator + sampleRate * 8L - 1) / (sampleRate * 8L));
            return frameLength >= header.Length;
        }

        private static bool IsValidAv3aChannelIndex(int channelIndex, bool allowMono) =>
            channelIndex is >= 0 and <= 10 && channelIndex is not (4 or 5) &&
            (allowMono || channelIndex != 0);

        private static bool TryGetAv3aBitrate(int channelIndex, int bitrateIndex, out int bitrate)
        {
            bitrate = 0;
            if ((uint)channelIndex >= Av3aBitrates.Length)
                return false;
            var table = Av3aBitrates[channelIndex];
            if ((uint)bitrateIndex >= table.Length)
                return false;
            bitrate = table[bitrateIndex];
            return bitrate > 0;
        }

        private static int ReadBits(ReadOnlySpan<byte> data, ref int bitOffset, int count)
        {
            var result = 0;
            for (var index = 0; index < count; index++, bitOffset++)
                result = (result << 1) | ((data[bitOffset >> 3] >> (7 - (bitOffset & 7))) & 1);
            return result;
        }

        private static bool Fail(out int value)
        {
            value = 0;
            return false;
        }
    }

    private sealed class GapSearchState(TsRepairGap gap)
    {
        public TsRepairGap Gap { get; } = gap;
        public bool ElementaryEligible { get; set; }
        public bool Completed { get; set; }
        public bool PacketActive { get; set; }
        public bool ElementaryActive { get; set; }
        public long SearchTargetPts90k { get; set; } = long.MinValue;
    }

    private sealed class ElementaryGapSearchWindow(long startPts90k, long endPts90k)
    {
        public long StartPts90k { get; } = startPts90k;
        public long EndPts90k { get; set; } = endPts90k;
        public List<GapSearchState> GapStates { get; } = [];
    }

    private sealed class ElementaryGapSearchJob(
        DonorTrackState owner,
        string sourcePath,
        int sourcePid,
        long startOffset,
        long endOffset,
        GapSearchState[] gapStates,
        TimelineTimestampNormalizer? timelineNormalizer)
    {
        public DonorTrackState Owner { get; } = owner;
        public string SourcePath { get; } = sourcePath;
        public int SourcePid { get; } = sourcePid;
        public long StartOffset { get; } = startOffset;
        public long EndOffset { get; } = endOffset;
        public GapSearchState[] GapStates { get; } = gapStates;
        public TimelineTimestampNormalizer? TimelineNormalizer { get; } = timelineNormalizer;
        public List<ElementaryGapCandidateResult> Results { get; set; } = [];
    }

    private sealed class TimelineTimestampNormalizer(
        TsTimelineRepairAnalysis analysis,
        int pcrPid,
        long syncOffset)
    {
        public long Normalize(long timestamp90k, long fileOffset)
        {
            var packetIndex = Math.Max(0, (fileOffset - syncOffset) / PacketSize);
            return timestamp90k + TsTimelineRepairService.GetVirtualTimestampCorrection90k(
                analysis, pcrPid, packetIndex);
        }
    }

    private readonly record struct ElementaryGapCandidateResult(
        GapSearchState State,
        TsRepairGapCandidate Candidate);

    private sealed class ElementaryGapWindowMatcher(
        IReadOnlyList<GapSearchState> gapStates,
        string sourcePath,
        int sourcePid)
    {
        private readonly Dictionary<ulong, List<GapSearchState>> _gapStarts = BuildGapStarts(gapStates);
        private readonly byte[] _window = new byte[ElementaryAnchorLength];
        private readonly List<ActiveElementaryGapCandidate> _activeCandidates = [];
        private readonly Dictionary<GapSearchState,
            (TsRepairGapCandidate Candidate, long Distance90k, int PacketDistance)>
            _bestResults = [];
        private int _windowStart;
        private int _windowCount;
        private ulong _windowHash;

        public List<ElementaryGapCandidateResult> Results => _bestResults
            .OrderBy(item => item.Key.Gap.ReferenceInsertOffset)
            .Select(item => new ElementaryGapCandidateResult(item.Key, item.Value.Candidate))
            .ToList();

        public void Process(
            ReadOnlySpan<byte> payload,
            bool startsNewPes,
            ReadOnlySpan<byte> pesHeader,
            long donorPts90k)
        {
            if (startsNewPes)
            {
                foreach (var candidate in _activeCandidates)
                    candidate.BeginPes(pesHeader);
            }
            foreach (var candidate in _activeCandidates)
                candidate.BeginPacket();
            foreach (var value in payload)
            {
                for (var index = _activeCandidates.Count - 1; index >= 0; index--)
                {
                    var candidate = _activeCandidates[index];
                    candidate.Append(value);
                    if (candidate.TryCreateCandidate(sourcePath, sourcePid, out var result))
                    {
                        if (result is not null)
                        {
                            var targetPts90k = candidate.State.SearchTargetPts90k;
                            var distance90k = candidate.StartPts90k == long.MinValue ||
                                              targetPts90k == long.MinValue
                                ? long.MaxValue
                                : Math.Abs(candidate.StartPts90k - targetPts90k);
                            var packetDistance = Math.Abs(
                                candidate.SourcePayloadPacketCount -
                                candidate.State.Gap.MissingPacketModulo);
                            if (!_bestResults.TryGetValue(candidate.State, out var existing) ||
                                distance90k < existing.Distance90k ||
                                distance90k == existing.Distance90k &&
                                packetDistance < existing.PacketDistance)
                            {
                                _bestResults[candidate.State] =
                                    (result, distance90k, packetDistance);
                            }
                        }
                        candidate.State.ElementaryActive = false;
                        RemoveAtSwapBack(_activeCandidates, index);
                    }
                    else if (candidate.Bytes.Count >= candidate.MaximumByteCount)
                    {
                        candidate.State.ElementaryActive = false;
                        RemoveAtSwapBack(_activeCandidates, index);
                    }
                }

                Append(value);
                if (_windowCount != ElementaryAnchorLength ||
                    !_gapStarts.TryGetValue(_windowHash, out var starts))
                {
                    continue;
                }
                foreach (var state in starts)
                {
                    if (state.Completed || state.ElementaryActive ||
                        _activeCandidates.Count >= 64 ||
                        !WindowEquals(state.Gap.BeforeElementaryAnchor!))
                    {
                        continue;
                    }
                    state.ElementaryActive = true;
                    _activeCandidates.Add(new ActiveElementaryGapCandidate(state, donorPts90k));
                }
            }
        }

        public void ResetForDiscontinuity()
        {
            foreach (var candidate in _activeCandidates)
                candidate.State.ElementaryActive = false;
            _activeCandidates.Clear();
            _windowStart = 0;
            _windowCount = 0;
            _windowHash = 0;
        }

        public void Finish() => ResetForDiscontinuity();

        private void Append(byte value)
        {
            if (_windowCount < ElementaryAnchorLength)
            {
                var index = (_windowStart + _windowCount) % ElementaryAnchorLength;
                _window[index] = value;
                _windowHash = _windowHash * ElementaryHashBase + value + 1UL;
                _windowCount++;
                return;
            }
            var oldest = _window[_windowStart];
            _windowHash -= (oldest + 1UL) * ElementaryHashOldestFactor;
            _windowHash = _windowHash * ElementaryHashBase + value + 1UL;
            _window[_windowStart] = value;
            _windowStart = (_windowStart + 1) % ElementaryAnchorLength;
        }

        private bool WindowEquals(ReadOnlySpan<byte> expected)
        {
            for (var index = 0; index < ElementaryAnchorLength; index++)
            {
                if (_window[(_windowStart + index) % ElementaryAnchorLength] != expected[index])
                    return false;
            }
            return true;
        }

        private static Dictionary<ulong, List<GapSearchState>> BuildGapStarts(
            IReadOnlyList<GapSearchState> states)
        {
            var result = new Dictionary<ulong, List<GapSearchState>>();
            foreach (var state in states)
            {
                var hash = ComputeByteHash(state.Gap.BeforeElementaryAnchor!);
                if (!result.TryGetValue(hash, out var values))
                {
                    values = [];
                    result[hash] = values;
                }
                values.Add(state);
            }
            return result;
        }
    }

    private sealed class ActiveGapCandidate(GapSearchState state)
    {
        public GapSearchState State { get; } = state;
        public TsRepairGap Gap => State.Gap;
        public List<ulong> Hashes { get; } = new(MaxRepairPacketsPerGap + AnchorLength);
        public List<long> Offsets { get; } = new(MaxRepairPacketsPerGap + AnchorLength);

        public bool TryComplete(string sourcePath, int sourcePid)
        {
            if (Hashes.Count < AnchorLength || !EndsWith(Hashes, Gap.AfterAnchor))
            {
                return false;
            }
            var repairPacketCount = Hashes.Count - AnchorLength;
            if (repairPacketCount <= 0 || repairPacketCount > MaxRepairPacketsPerGap ||
                (repairPacketCount & 0x0F) != Gap.MissingPacketModulo)
            {
                return false;
            }
            Gap.Candidates.Add(new TsRepairGapCandidate
            {
                SourcePath = sourcePath,
                SourcePid = sourcePid,
                SourcePacketOffsets = Offsets.Take(repairPacketCount).ToArray()
            });
            return true;
        }

        private static bool EndsWith(IReadOnlyList<ulong> values, IReadOnlyList<ulong> suffix)
        {
            var start = values.Count - suffix.Count;
            if (start < 0)
                return false;
            for (var index = 0; index < suffix.Count; index++)
            {
                if (values[start + index] != suffix[index])
                    return false;
            }
            return true;
        }
    }

    private sealed class ActivePesRegionCandidate(TsRepairPesRegion region)
    {
        private readonly List<DonorPesInfo> _values = [];
        private int _packetCount;
        public TsRepairPesRegion Region { get; } = region;
        public bool Completed { get; private set; }

        public bool TryAppend(DonorPesInfo pes, string sourcePath, int sourcePid)
        {
            if (!pes.IsValid)
                return true;
            _values.Add(pes);
            _packetCount += pes.PacketCount;
            var afterAnchorCount = Region.AfterAnchor.Length;
            if (_values.Count <= afterAnchorCount || _values[0].Pts90k == long.MinValue)
                return false;

            // MP2 静音或重复内容可能让多个 PES 指纹完全相同；先用异常窗口自身时长约束
            // 候选结束位置，再验证后锚点，避免在第一个重复指纹处过早结束成空替换。
            var expectedDuration90k = Region.ReferenceLastPts90k - Region.ReferenceFirstPts90k;
            var replacementLast = _values[^(afterAnchorCount + 1)];
            if (replacementLast.Pts90k == long.MinValue ||
                replacementLast.Pts90k - _values[0].Pts90k < expectedDuration90k - 9_000)
            {
                return false;
            }

            if (!EndsWithPesAnchors(_values, Region.AfterAnchor))
                return _packetCount > GetMaxRegionPackets(Region);

            var replacementPesCount = _values.Count - afterAnchorCount;
            if (replacementPesCount <= 0)
                return true;
            var replacement = _values.Take(replacementPesCount).ToArray();
            var replacementPacketCount = replacement.Sum(item => item.PacketCount);
            if (replacementPacketCount <= 0 || replacementPacketCount > GetMaxRegionPackets(Region) ||
                replacement.Any(item => !item.IsValid || item.Pts90k == long.MinValue))
            {
                return true;
            }

            var replacementSignatures = replacement.Select(item => item.Signature).ToArray();
            if (Region.Reason == TsRepairPesRegionReason.CorrelatedVideoElementaryMismatch &&
                PesAnchorsEqual(replacementSignatures, Region.ReferenceSignatures))
            {
                // 两端锚点和区间 ES 都完全一致，说明音频故障没有波及这条视频流；
                // 不做无意义替换，避免改变 PCR、封包节奏或引入辅助源自身的问题。
                Completed = true;
                return true;
            }

            var timestampOffset = Region.ReferenceFirstPts90k - replacement[0].Pts90k;
            var expectedLastPts = replacement[^1].Pts90k + timestampOffset;
            if (Region.ReferenceFirstPts90k == long.MinValue || Region.ReferenceLastPts90k == long.MinValue ||
                Math.Abs(expectedLastPts - Region.ReferenceLastPts90k) > 9_000)
            {
                return true;
            }

            var replacementFirst = 0;
            var replacementLastIndex = replacement.Length - 1;
            var referenceFirst = 0;
            var referenceLast = Region.ReferenceSignatures.Length - 1;
            if (Region.Reason == TsRepairPesRegionReason.CorrelatedVideoElementaryMismatch)
            {
                // 把大窗口收缩为真正不同的 PES/ES 岛。录制工具通常只改变 TS 封包，
                // 不会改写广播源 PES；因此两端完全相同的 PES 可以安全保留在参考文件中。
                // 若中间 PES 数量不同，也只剥离两端连续相同部分，不假设一一对应。
                while (replacementFirst <= replacementLastIndex && referenceFirst <= referenceLast &&
                       replacement[replacementFirst].Signature == Region.ReferenceSignatures[referenceFirst])
                {
                    replacementFirst++;
                    referenceFirst++;
                }
                while (replacementLastIndex >= replacementFirst && referenceLast >= referenceFirst &&
                       replacement[replacementLastIndex].Signature == Region.ReferenceSignatures[referenceLast])
                {
                    replacementLastIndex--;
                    referenceLast--;
                }
                if (replacementFirst > replacementLastIndex || referenceFirst > referenceLast)
                    return true;
            }

            var replacementIsland = replacement[replacementFirst..(replacementLastIndex + 1)];
            var referencePacketCount = Region.ReferencePacketCount;
            long? referenceStartOffset = null;
            long? referenceEndOffset = null;
            int? referenceStartContinuityCounter = null;
            if (Region.Reason == TsRepairPesRegionReason.CorrelatedVideoElementaryMismatch)
            {
                referenceStartOffset = Region.ReferencePesStartOffsets[referenceFirst];
                referenceEndOffset = Region.ReferencePesEndOffsets[referenceLast];
                referenceStartContinuityCounter =
                    Region.ReferencePesStartContinuityCounters[referenceFirst];
                referencePacketCount = 0;
                for (var index = referenceFirst; index <= referenceLast; index++)
                    referencePacketCount += Region.ReferencePesPacketCounts[index];
            }

            Region.Candidates.Add(new TsRepairPesRegionCandidate
            {
                SourcePath = sourcePath,
                SourcePid = sourcePid,
                SourceStartOffset = replacementIsland[0].StartOffset,
                SourceEndOffset = replacementIsland[^1].EndOffset,
                PacketCount = replacementIsland.Sum(item => item.PacketCount),
                TimestampOffset90k = timestampOffset,
                ElementaryLength = replacementIsland.Sum(item => item.ElementaryLength),
                FingerprintMatches = Region.Reason ==
                    TsRepairPesRegionReason.CorrelatedVideoElementaryMismatch
                        ? Region.BeforeAnchor.Length + Region.AfterAnchor.Length
                        : 0,
                ReferenceStartOffset = referenceStartOffset,
                ReferenceEndOffset = referenceEndOffset,
                ReferenceStartContinuityCounter = referenceStartContinuityCounter,
                ReferencePacketCount = Region.Reason ==
                    TsRepairPesRegionReason.CorrelatedVideoElementaryMismatch
                        ? referencePacketCount
                        : null,
            });
            // 视频内容可能出现重复镜头，单靠 ES 锚点会命中多个时刻。保留有界的多个候选，
            // 扫描结束后再用视频自身大量分散包指纹形成的多数时间偏移筛选。
            Completed = Region.Reason != TsRepairPesRegionReason.CorrelatedVideoElementaryMismatch;
            return true;
        }

        private static bool EndsWithPesAnchors(
            IReadOnlyList<DonorPesInfo> values,
            IReadOnlyList<TsRepairPesSignature> anchors)
        {
            if (values.Count < anchors.Count)
                return false;
            var start = values.Count - anchors.Count;
            for (var index = 0; index < anchors.Count; index++)
            {
                if (values[start + index].Signature != anchors[index])
                    return false;
            }
            return true;
        }
    }

    private sealed class ActiveElementaryGapCandidate(
        GapSearchState state,
        long startPts90k = long.MinValue)
    {
        private readonly ulong _afterAnchorHash = ComputeByteHash(state.Gap.AfterElementaryAnchor!);
        private ulong _afterWindowHash;
        public GapSearchState State { get; } = state;
        public TsRepairGap Gap => State.Gap;
        public long StartPts90k { get; } = startPts90k;
        public int SourcePayloadPacketCount { get; private set; } = 1;
        public int MaximumByteCount { get; } = GetMaximumByteCount(state.Gap);
        public List<byte> Bytes { get; } = new(GetMaximumByteCount(state.Gap));
        private List<TsRepairPesBoundary> PesBoundaries { get; } = [];

        private static int GetMaximumByteCount(TsRepairGap gap) =>
            (gap.BeforeElementarySuffix?.Length ?? 0) +
            gap.BeforeElementaryRepeatedSuffixLength +
            Math.Min(MaxElementaryRepairPackets, gap.MissingPacketModulo) * 184 +
            ElementaryAnchorLength;

        public void Append(byte value)
        {
            if (Bytes.Count < ElementaryAnchorLength)
            {
                _afterWindowHash = _afterWindowHash * ElementaryHashBase + value + 1UL;
            }
            else
            {
                var oldest = Bytes[Bytes.Count - ElementaryAnchorLength];
                _afterWindowHash -= (oldest + 1UL) * ElementaryHashOldestFactor;
                _afterWindowHash = _afterWindowHash * ElementaryHashBase + value + 1UL;
            }
            Bytes.Add(value);
        }

        public void BeginPes(ReadOnlySpan<byte> pesHeader)
        {
            if (pesHeader.IsEmpty || pesHeader.Length > 184)
                return;
            // 候选缓冲只保存 ES；同时记录 PES 头应插入的 ES 字节位置，输出时才能
            // 在新的 TS 包边界恢复 PUSI、PTS/DTS，而不是把跨 PES 的内容错误地拼平。
            PesBoundaries.Add(new TsRepairPesBoundary(Bytes.Count, pesHeader.ToArray()));
        }

        public void BeginPacket() => SourcePayloadPacketCount++;

        public bool TryComplete(string sourcePath, int sourcePid, out bool added)
        {
            added = false;
            if (!TryCreateCandidate(sourcePath, sourcePid, out var candidate))
                return false;
            if (candidate is null || Gap.Candidates.Any(item => string.Equals(
                    item.SourcePath, sourcePath, StringComparison.Ordinal) &&
                    item.SourcePid == sourcePid))
            {
                return true;
            }
            Gap.Candidates.Add(candidate);
            added = true;
            return true;
        }

        public bool TryCreateCandidate(
            string sourcePath,
            int sourcePid,
            out TsRepairGapCandidate? candidate)
        {
            candidate = null;
            if (Bytes.Count < ElementaryAnchorLength ||
                _afterWindowHash != _afterAnchorHash ||
                !EndsWith(Bytes, Gap.AfterElementaryAnchor!))
            {
                return false;
            }

            var explicitSuffixLength = Gap.BeforeElementarySuffix?.Length ?? 0;
            var repeatedSuffixLength = Gap.BeforeElementaryRepeatedSuffixLength;
            var repairStartOffset = explicitSuffixLength + repeatedSuffixLength;
            var values = CollectionsMarshal.AsSpan(Bytes);
            if (Bytes.Count < repairStartOffset ||
                explicitSuffixLength > 0 &&
                !values[..explicitSuffixLength].SequenceEqual(Gap.BeforeElementarySuffix))
            {
                return false;
            }
            for (var index = explicitSuffixLength; index < repairStartOffset; index++)
            {
                if (values[index] == Gap.BeforeElementaryRepeatedSuffixByte)
                    continue;
                return false;
            }
            var repairByteCount = Bytes.Count - ElementaryAnchorLength - repairStartOffset;
            var packetCount = Gap.MissingPacketModulo;
            var boundaries = PesBoundaries
                .Where(item => item.ElementaryOffset >= repairStartOffset &&
                               item.ElementaryOffset < repairStartOffset + repairByteCount)
                .Select(item => new TsRepairPesBoundary(
                    item.ElementaryOffset - repairStartOffset,
                    NormalizeVideoPesHeader(item.PesHeader, Gap.ElementaryKind)))
                .ToArray();
            if (repairByteCount <= 0 || packetCount <= 0 || packetCount > MaxElementaryRepairPackets ||
                !TsElementaryRepairPacketizer.TryCreateLayout(
                    repairByteCount, boundaries, packetCount, out _) ||
                !IsElementaryRepairSafe(repairStartOffset, repairByteCount, boundaries))
            {
                return false;
            }
            candidate = new TsRepairGapCandidate
            {
                SourcePath = sourcePath,
                SourcePid = sourcePid,
                ElementaryPayload = Bytes.Skip(repairStartOffset).Take(repairByteCount).ToArray(),
                PesBoundaries = boundaries,
                SynthesizedPacketCount = packetCount,
                SourcePayloadPacketCount = SourcePayloadPacketCount
            };
            return true;
        }

        private static byte[] NormalizeVideoPesHeader(
            byte[] pesHeader,
            TsRepairElementaryKind elementaryKind)
        {
            var result = pesHeader.ToArray();
            if (result.Length < 6 || elementaryKind is not (
                    TsRepairElementaryKind.MpegVideo or TsRepairElementaryKind.Cavs or
                    TsRepairElementaryKind.Avs2 or TsRepairElementaryKind.Avs3 or
                    TsRepairElementaryKind.H264 or TsRepairElementaryKind.H265))
            {
                return result;
            }
            // 不同复用器可能在同一 ES 位置采用不同的 PES 分段。视频 PES 允许 packet_length 为 0，
            // 合成时清零可让解复用器以下一个 PUSI 为边界，避免沿用辅助源长度而截断参考源后续负载。
            result[4] = 0;
            result[5] = 0;
            return result;
        }

        private bool IsElementaryRepairSafe(
            int repairStartOffset,
            int repairByteCount,
            IReadOnlyList<TsRepairPesBoundary> pesBoundaries) => Gap.ElementaryKind switch
        {
            TsRepairElementaryKind.Audio =>
                pesBoundaries.Count == 0 &&
                Gap.ElementaryBytesUntilPesEnd is { } bytesUntilPesEnd &&
                repairByteCount < bytesUntilPesEnd,
            _ when IsFramedAudioElementaryKind(Gap.ElementaryKind) =>
                pesBoundaries.Count == 0 &&
                Gap.ElementaryBytesUntilPesEnd is { } framedAudioPesBytes &&
                repairByteCount < framedAudioPesBytes &&
                Gap.ElementaryBytesUntilFrameEnd is { } frameBytes &&
                repairByteCount <= frameBytes,
            TsRepairElementaryKind.MpegVideo or TsRepairElementaryKind.Cavs or
                TsRepairElementaryKind.Avs2 or TsRepairElementaryKind.Avs3 or
                TsRepairElementaryKind.H264 or TsRepairElementaryKind.H265 =>
                !ContainsUnsafeVideoStartCodeAroundRepair(repairStartOffset, repairByteCount) ||
                Gap.ReferencePts90k != long.MinValue &&
                HasAlignedPesVideoBoundaries(
                    repairStartOffset, repairByteCount, pesBoundaries),
            _ => false
        };

        private bool HasAlignedPesVideoBoundaries(
            int repairStartOffset,
            int repairByteCount,
            IReadOnlyList<TsRepairPesBoundary> pesBoundaries)
        {
            if (pesBoundaries.Count == 0)
                return false;
            foreach (var boundary in pesBoundaries)
            {
                var searchEnd = Math.Min(repairByteCount - 4, boundary.ElementaryOffset + 16);
                var foundStartCode = false;
                for (var index = boundary.ElementaryOffset; index <= searchEnd; index++)
                {
                    var sourceIndex = repairStartOffset + index;
                    if (Bytes[sourceIndex] != 0 || Bytes[sourceIndex + 1] != 0 ||
                        Bytes[sourceIndex + 2] != 1)
                        continue;
                    var startCode = Bytes[sourceIndex + 3];
                    foundStartCode = Gap.ElementaryKind switch
                    {
                        TsRepairElementaryKind.MpegVideo => startCode is not (>= 0x01 and <= 0xAF),
                        TsRepairElementaryKind.Cavs or TsRepairElementaryKind.Avs2 or
                            TsRepairElementaryKind.Avs3 => startCode > 0xAF,
                        TsRepairElementaryKind.H264 or TsRepairElementaryKind.H265 => true,
                        _ => false
                    };
                    if (foundStartCode)
                        break;
                }
                if (!foundStartCode)
                    return false;
            }
            return true;
        }

        private bool ContainsUnsafeVideoStartCodeAroundRepair(
            int repairStartOffset,
            int repairByteCount)
        {
            const int edgeLength = 4;
            var before = Gap.BeforeElementaryAnchor!;
            var after = Gap.AfterElementaryAnchor!;
            var totalLength = edgeLength + repairByteCount + edgeLength;

            byte GetByte(int index)
            {
                if (index < edgeLength)
                    return before[before.Length - edgeLength + index];
                index -= edgeLength;
                if (index < repairByteCount)
                    return Bytes[repairStartOffset + index];
                return after[index - repairByteCount];
            }

            // MPEG-1/2 与 AVS 的 slice 起始码很密集，完全禁止跨 slice 会让多数
            // 188 字节包失去修复机会。精确 ES 锚点允许复制同一 picture 内的多个 slice，
            // 但 sequence、picture、extension 等高层边界仍拒绝；H.264/H.265 则继续
            // 禁止跨越任何 Annex-B NAL 边界。
            for (var index = 0; index + 3 < totalLength; index++)
            {
                if (GetByte(index) != 0 || GetByte(index + 1) != 0 || GetByte(index + 2) != 1)
                    continue;
                var startCode = GetByte(index + 3);
                var isSlice = Gap.ElementaryKind switch
                {
                    TsRepairElementaryKind.MpegVideo => startCode is >= 0x01 and <= 0xAF,
                    TsRepairElementaryKind.Cavs or TsRepairElementaryKind.Avs2 or
                        TsRepairElementaryKind.Avs3 => startCode <= 0xAF,
                    _ => false
                };
                if (!isSlice)
                    return true;
            }
            return false;
        }

        private static bool EndsWith(IReadOnlyList<byte> values, IReadOnlyList<byte> suffix)
        {
            var start = values.Count - suffix.Count;
            if (start < 0)
                return false;
            for (var index = 0; index < suffix.Count; index++)
            {
                if (values[start + index] != suffix[index])
                    return false;
            }
            return true;
        }
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
