using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.ViewModels;

public partial class TsRepairMapWindowViewModel : ViewModelBase
{
    private readonly TsCheckTextFormatter _text = new();
    private readonly TsMultiSourceRepairService _repairService = new();
    private TsMultiSourceAnalysisResult _analysis;
    private IReadOnlySet<int> _selectedPids;
    private TsRepairOutputResult? _outputResult;
    private TsBroadcastTimeAnchor[] _broadcastAnchors = [];
    private IReadOnlyDictionary<int, TsBroadcastTimeAnchor[]> _broadcastAnchorsByProgram =
        new Dictionary<int, TsBroadcastTimeAnchor[]>();

    public TsRepairMapWindowViewModel(
        TsMultiSourceAnalysisResult analysis,
        IReadOnlySet<int> selectedPids,
        TsRepairOutputResult? outputResult)
    {
        _analysis = analysis;
        _selectedPids = selectedPids;
        _outputResult = outputResult;
        _viewMode = outputResult is null ? TsRepairMapViewMode.Expected : TsRepairMapViewMode.Actual;
        App.LocalizationService.LanguageChanged += OnLanguageChanged;
        Rebuild();
    }

    public ObservableCollection<TsRepairMapTrackView> Tracks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExpectedView), nameof(IsActualView))]
    private TsRepairMapViewMode _viewMode;

    public bool IsExpectedView
    {
        get => ViewMode == TsRepairMapViewMode.Expected;
        set
        {
            if (value)
                ViewMode = TsRepairMapViewMode.Expected;
        }
    }

    public bool IsActualView
    {
        get => ViewMode == TsRepairMapViewMode.Actual;
        set
        {
            if (value && HasActualResult)
                ViewMode = TsRepairMapViewMode.Actual;
        }
    }

    [ObservableProperty]
    private bool _hasActualResult;

    [ObservableProperty]
    private string _referenceFileName = string.Empty;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private bool _hasBroadcastTime;

    [ObservableProperty]
    private string _broadcastTimeRangeText = string.Empty;

    [ObservableProperty]
    private string _hintText = string.Empty;

    [ObservableProperty]
    private double _durationSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectedSuccess), nameof(IsSelectedWarning), nameof(IsSelectedError))]
    private TsRepairMapRegionView? _selectedRegion;

    public bool IsSelectedSuccess => SelectedRegion?.Status == TsRepairMapRegionStatus.Success;
    public bool IsSelectedWarning => SelectedRegion?.Status == TsRepairMapRegionStatus.Warning;
    public bool IsSelectedError => SelectedRegion?.Status == TsRepairMapRegionStatus.Error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomDisplayText))]
    private double _zoomPercent = 100;

    public string ZoomDisplayText => $"{ZoomPercent:0}%";

    partial void OnViewModeChanged(TsRepairMapViewMode value) => Rebuild();

    public void Refresh(
        TsMultiSourceAnalysisResult analysis,
        IReadOnlySet<int> selectedPids,
        TsRepairOutputResult? outputResult,
        bool preferActual = false)
    {
        _analysis = analysis;
        _selectedPids = selectedPids;
        _outputResult = outputResult;
        if (preferActual && outputResult is not null)
            ViewMode = TsRepairMapViewMode.Actual;
        else
            Rebuild();
    }

    private void Rebuild()
    {
        var previousKey = SelectedRegion?.Key;
        HasActualResult = _outputResult is not null;
        if (ViewMode == TsRepairMapViewMode.Actual && !HasActualResult)
        {
            ViewMode = TsRepairMapViewMode.Expected;
            return;
        }

        ReferenceFileName = Path.GetFileName(_analysis.ReferenceSource.FilePath);
        DurationSeconds = GetTimelineDuration(_analysis);
        _broadcastAnchors = _analysis.ReferenceBroadcastTimes
            .Where(item => item.Clock90k != long.MinValue)
            .OrderBy(item => item.FileOffset)
            .ToArray();
        _broadcastAnchorsByProgram = _broadcastAnchors
            .GroupBy(item => item.ProgramNumber)
            .ToDictionary(
                item => item.Key,
                item => item.OrderBy(anchor => anchor.FileOffset).ToArray());
        HasBroadcastTime = _broadcastAnchors.Length > 0;
        BroadcastTimeRangeText = HasBroadcastTime
            ? _text.FormatBroadcastTime(_broadcastAnchors[0], _broadcastAnchors[^1])
            : string.Empty;
        Tracks.Clear();

        var actual = ViewMode == TsRepairMapViewMode.Actual;
        var activePlan = actual
            ? _outputResult?.Plan
            : _repairService.BuildOutputPlan(_analysis, _selectedPids, includeServiceInformation: true);
        var activeSelectedPids = actual && activePlan is not null
            ? activePlan.SelectedPids
            : _selectedPids;

        var total = 0;
        var repairable = 0;
        foreach (var track in _analysis.Tracks
                     .OrderBy(item => item.ProgramNumber)
                     .ThenBy(item => item.ReferencePid))
        {
            if (track.IssueCount == 0)
                continue;
            var isSelected = activeSelectedPids.Contains(track.ReferencePid);
            var row = new TsRepairMapTrackView
            {
                Pid = track.ReferencePid,
                DisplayText = $"0x{track.ReferencePid:X4}  {FormatTrack(track)}",
                StatusText = string.Empty,
                IsSelected = isSelected
            };
            AddGapRegions(row, track, isSelected, activePlan);
            AddPesRegions(row, track, isSelected, activePlan);
            row.Regions.Sort((left, right) => left.StartSeconds.CompareTo(right.StartSeconds));
            var successful = row.Regions.Count(region => region.Status == TsRepairMapRegionStatus.Success);
            row.StatusText = isSelected
                ? string.Format(_text.Strings.String_TsRepair_Map_TrackStatus, successful, row.Regions.Count)
                : _text.Strings.String_TsRepair_Map_StatusNotSelected;
            total += row.Regions.Count;
            repairable += successful;
            Tracks.Add(row);
        }

        if (Tracks.Count > 0)
        {
            var overview = new TsRepairMapTrackView
            {
                Pid = -1,
                DisplayText = _text.Strings.String_TsRepair_Map_Overview,
                StatusText = string.Format(_text.Strings.String_TsRepair_Map_TrackStatus, repairable, total),
                IsSelected = true,
                IsOverview = true
            };
            foreach (var region in Tracks.SelectMany(item => item.Regions)
                         .OrderBy(item => item.StartSeconds))
            {
                overview.Regions.Add(region);
            }
            Tracks.Insert(0, overview);
        }

        SummaryText = actual
            ? string.Format(_text.Strings.String_TsRepair_Map_SummaryActual,
                total, repairable, _outputResult?.RemainingErrorCount ?? 0)
            : string.Format(_text.Strings.String_TsRepair_Map_SummaryExpected, total, repairable);
        HintText = actual
            ? _text.Strings.String_TsRepair_Map_HintActual
            : _text.Strings.String_TsRepair_Map_HintExpected;
        SelectedRegion = Tracks.SelectMany(item => item.Regions)
            .FirstOrDefault(item => item.Key == previousKey)
            ?? Tracks.SelectMany(item => item.Regions).FirstOrDefault();
    }

    private void AddGapRegions(
        TsRepairMapTrackView row,
        TsRepairTrackAnalysis track,
        bool isSelected,
        TsRepairOutputPlan? plan)
    {
        for (var index = 0; index < track.Gaps.Count; index++)
        {
            var gap = track.Gaps[index];
            var insertion = FindGapInsertion(plan, track.ReferencePid, gap.ReferenceInsertOffset);
            var status = ResolveStatus(isSelected, gap.Candidates.Count > 0, insertion is not null);
            var start = GetTimeSeconds(gap.ReferencePts90k, gap.ReferenceInsertOffset);
            var end = Math.Min(DurationSeconds, start + GetMarkerDuration());
            var candidate = gap.Candidates.FirstOrDefault(item => insertion is not null &&
                PathsEqual(item.SourcePath, insertion.SourcePath) && item.SourcePid == insertion.SourcePid)
                ?? gap.Candidates.FirstOrDefault();
            row.Regions.Add(new TsRepairMapRegionView
            {
                Key = $"G:{track.ReferencePid}:{gap.ReferenceInsertOffset}",
                Pid = track.ReferencePid,
                Kind = TsRepairMapIssueKind.PacketGap,
                Status = status,
                StartSeconds = start,
                EndSeconds = end,
                IsEstimatedTime = gap.ReferencePts90k == long.MinValue,
                StartOffset = gap.ReferenceInsertOffset,
                EndOffset = gap.ReferenceInsertOffset,
                TrackText = row.DisplayText,
                IssueText = _text.Strings.String_TsRepair_Map_IssuePacketGap,
                StatusText = FormatStatus(status),
                TimeText = FormatTimePoint(start, gap.ReferencePts90k == long.MinValue),
                BroadcastTimeText = FormatBroadcastTimePoint(
                    track.ProgramNumber, gap.ReferencePts90k, gap.ReferenceInsertOffset,
                    gap.ReferencePts90k == long.MinValue),
                PositionText = FormatPosition(gap.ReferenceInsertOffset, gap.ReferenceInsertOffset),
                SourceText = FormatCandidateSource(insertion?.SourcePath ?? candidate?.SourcePath,
                    insertion?.SourcePid ?? candidate?.SourcePid),
                MatchText = insertion is null && candidate is null
                    ? _text.Strings.String_TsRepair_Map_NoCandidate
                    : FormatGapMatch(insertion, candidate),
                PacketText = FormatGapPackets(gap, insertion, candidate)
            });
        }
    }

    private void AddPesRegions(
        TsRepairMapTrackView row,
        TsRepairTrackAnalysis track,
        bool isSelected,
        TsRepairOutputPlan? plan)
    {
        for (var index = 0; index < track.PesRegions.Count; index++)
        {
            var region = track.PesRegions[index];
            var replacement = FindRegionReplacement(
                plan, track.ReferencePid, region.ReferenceStartOffset, region.ReferenceEndOffset);
            var coveringInsertion = replacement is null
                ? FindCoveringInsertion(plan, track.ReferencePid, region.ReferenceStartOffset, region.ReferenceEndOffset)
                : null;
            var status = ResolveStatus(
                isSelected, region.Candidates.Count > 0, replacement is not null || coveringInsertion is not null);
            var hasPts = region.ReferenceFirstPts90k != long.MinValue;
            var start = GetTimeSeconds(region.ReferenceFirstPts90k, region.ReferenceStartOffset);
            var end = region.ReferenceLastPts90k != long.MinValue
                ? GetTimeSeconds(region.ReferenceLastPts90k, region.ReferenceEndOffset)
                : GetTimeSeconds(long.MinValue, region.ReferenceEndOffset);
            if (end <= start)
                end = Math.Min(DurationSeconds, start + GetMarkerDuration());
            var sourcePath = replacement?.SourcePath ?? coveringInsertion?.SourcePath;
            var sourcePid = replacement?.SourcePid ?? coveringInsertion?.SourcePid;
            var candidate = region.Candidates.FirstOrDefault(item => sourcePath is not null &&
                                PathsEqual(item.SourcePath, sourcePath) && item.SourcePid == sourcePid)
                            ?? region.Candidates.OrderByDescending(item => item.FingerprintMatches).FirstOrDefault();
            row.Regions.Add(new TsRepairMapRegionView
            {
                Key = $"R:{track.ReferencePid}:{region.ReferenceStartOffset}",
                Pid = track.ReferencePid,
                Kind = TsRepairMapIssueKind.MediaRegion,
                Status = status,
                StartSeconds = start,
                EndSeconds = end,
                IsEstimatedTime = !hasPts,
                StartOffset = region.ReferenceStartOffset,
                EndOffset = region.ReferenceEndOffset,
                TrackText = row.DisplayText,
                IssueText = region.Reason == TsRepairPesRegionReason.CorrelatedVideoElementaryMismatch
                    ? _text.Strings.String_TsRepair_Map_IssueElementaryMismatch
                    : _text.Strings.String_TsRepair_Map_IssuePesMismatch,
                StatusText = FormatStatus(status),
                TimeText = FormatTimeRange(start, end, !hasPts),
                BroadcastTimeText = FormatBroadcastTimeRange(
                    track.ProgramNumber,
                    region.ReferenceFirstPts90k, region.ReferenceLastPts90k,
                    region.ReferenceStartOffset, region.ReferenceEndOffset, !hasPts),
                PositionText = FormatPosition(region.ReferenceStartOffset, region.ReferenceEndOffset),
                SourceText = FormatCandidateSource(sourcePath ?? candidate?.SourcePath, sourcePid ?? candidate?.SourcePid),
                MatchText = coveringInsertion is not null
                    ? _text.Strings.String_TsRepair_Map_MatchCoveredByGap
                    : candidate is null
                    ? _text.Strings.String_TsRepair_Map_NoCandidate
                    : string.Format(_text.Strings.String_TsRepair_Map_FingerprintMatches,
                        candidate.FingerprintMatches),
                PacketText = string.Format(_text.Strings.String_TsRepair_Map_RegionPackets,
                    region.ReferencePacketCount,
                    replacement?.PacketCount ?? (coveringInsertion is not null
                        ? GetInsertionPacketCount(coveringInsertion)
                        : candidate?.PacketCount ?? 0))
            });
        }
    }

    private TsRepairMapRegionStatus ResolveStatus(bool selected, bool hasCandidate, bool repaired)
    {
        if (ViewMode == TsRepairMapViewMode.Expected)
            return !selected ? TsRepairMapRegionStatus.Muted
                : hasCandidate ? TsRepairMapRegionStatus.Success
                : TsRepairMapRegionStatus.Error;
        return !selected ? TsRepairMapRegionStatus.Muted
            : repaired ? TsRepairMapRegionStatus.Success
            : hasCandidate ? TsRepairMapRegionStatus.Warning
            : TsRepairMapRegionStatus.Error;
    }

    private static TsPacketInsertion? FindGapInsertion(TsRepairOutputPlan? plan, int pid, long offset) =>
        plan?.Insertions.TryGetValue(offset, out var values) == true
            ? values.FirstOrDefault(item => item.TargetPid == pid)
            : null;

    private static TsPacketReplacement? FindRegionReplacement(
        TsRepairOutputPlan? plan, int pid, long startOffset, long endOffset) => plan?.Replacements.FirstOrDefault(item =>
        item.TargetPid == pid && item.ReferenceStartOffset < endOffset && item.ReferenceEndOffset > startOffset);

    private static TsPacketInsertion? FindCoveringInsertion(
        TsRepairOutputPlan? plan, int pid, long startOffset, long endOffset)
    {
        if (plan is null)
            return null;
        foreach (var pair in plan.Insertions)
        {
            if (pair.Key < startOffset || pair.Key >= endOffset)
                continue;
            var insertion = pair.Value.FirstOrDefault(item => item.TargetPid == pid);
            if (insertion is not null)
                return insertion;
        }
        return null;
    }

    private double GetTimeSeconds(long pts90k, long fileOffset)
    {
        if (pts90k != long.MinValue && _analysis.TimelineStartPts90k != long.MinValue)
            return Math.Clamp((pts90k - _analysis.TimelineStartPts90k) / 90_000.0, 0, DurationSeconds);
        var size = Math.Max(1, _analysis.ReferenceSource.Catalog.FileSize);
        return Math.Clamp(fileOffset / (double)size * DurationSeconds, 0, DurationSeconds);
    }

    private double GetMarkerDuration() => Math.Max(0.2, DurationSeconds / 700.0);

    private static double GetTimelineDuration(TsMultiSourceAnalysisResult analysis)
    {
        if (analysis.TimelineStartPts90k != long.MinValue && analysis.TimelineEndPts90k > analysis.TimelineStartPts90k)
            return Math.Max(1, (analysis.TimelineEndPts90k - analysis.TimelineStartPts90k) / 90_000.0);
        return Math.Max(1, analysis.ReferenceSource.Catalog.PacketCount / 25_000.0);
    }

    private string FormatTrack(TsRepairTrackAnalysis track) => _text.FormatStreamType(
        track.StreamType, track.MpegAudioLayer, track.SupplementaryStreamType, track.Language);

    private string FormatStatus(TsRepairMapRegionStatus status) => status switch
    {
        TsRepairMapRegionStatus.Success => ViewMode == TsRepairMapViewMode.Actual
            ? _text.Strings.String_TsRepair_Map_StatusRepaired
            : _text.Strings.String_TsRepair_Map_StatusRepairable,
        TsRepairMapRegionStatus.Warning => _text.Strings.String_TsRepair_Map_StatusSkipped,
        TsRepairMapRegionStatus.Error => _text.Strings.String_TsRepair_Map_StatusUnrepairable,
        _ => _text.Strings.String_TsRepair_Map_StatusNotSelected
    };

    private string FormatCandidateSource(string? path, int? pid) => path is null || pid is null
        ? _text.Strings.String_TsRepair_Map_NoCandidate
        : string.Format(_text.Strings.String_TsRepair_Map_Source, Path.GetFileName(path), $"0x{pid:X4}");

    private string FormatGapMatch(TsPacketInsertion? insertion, TsRepairGapCandidate? candidate) =>
        insertion?.ElementaryPayload is not null || candidate?.ElementaryPayload is not null
            ? _text.Strings.String_TsRepair_Map_MatchElementary
            : _text.Strings.String_TsRepair_Map_MatchPayload;

    private string FormatGapPackets(
        TsRepairGap gap,
        TsPacketInsertion? insertion,
        TsRepairGapCandidate? candidate)
    {
        var packetCount = insertion is not null
            ? GetInsertionPacketCount(insertion)
            : candidate is null ? 0 : candidate.SynthesizedPacketCount > 0
            ? candidate.SynthesizedPacketCount
            : candidate.SourcePacketOffsets.Length;
        return string.Format(_text.Strings.String_TsRepair_Map_GapPackets,
            gap.MissingPacketModulo, packetCount);
    }

    private static int GetInsertionPacketCount(TsPacketInsertion insertion) => insertion.SynthesizedPacketCount > 0
        ? insertion.SynthesizedPacketCount
        : insertion.SourcePacketOffsets.Length;

    private string FormatTimeRange(double start, double end, bool estimated)
    {
        var suffix = estimated ? _text.Strings.String_TsRepair_Map_EstimatedSuffix : string.Empty;
        return string.Format(_text.Strings.String_TsRepair_Map_TimeRange,
            TsCheckEvent.FormatTime(start), TsCheckEvent.FormatTime(end), suffix);
    }

    private string FormatTimePoint(double value, bool estimated)
    {
        var suffix = estimated ? _text.Strings.String_TsRepair_Map_EstimatedSuffix : string.Empty;
        return string.Format(_text.Strings.String_TsRepair_Map_TimePoint,
            TsCheckEvent.FormatTime(value), suffix);
    }

    private string FormatBroadcastTimePoint(
        int programNumber,
        long pts90k,
        long fileOffset,
        bool estimated)
    {
        if (!TryGetBroadcastTime(programNumber, pts90k, fileOffset, out var utcTime))
            return string.Empty;
        var suffix = estimated ? _text.Strings.String_TsRepair_Map_EstimatedSuffix : string.Empty;
        return _text.FormatBroadcastTime(utcTime) + suffix;
    }

    private string FormatBroadcastTimeRange(
        int programNumber,
        long startPts90k,
        long endPts90k,
        long startOffset,
        long endOffset,
        bool estimated)
    {
        if (!TryGetBroadcastTime(programNumber, startPts90k, startOffset, out var startUtc) ||
            !TryGetBroadcastTime(programNumber, endPts90k, endOffset, out var endUtc))
        {
            return string.Empty;
        }
        var suffix = estimated ? _text.Strings.String_TsRepair_Map_EstimatedSuffix : string.Empty;
        return _text.FormatBroadcastTime(startUtc, endUtc) + suffix;
    }

    private bool TryGetBroadcastTime(
        int programNumber,
        long pts90k,
        long fileOffset,
        out DateTimeOffset utcTime)
    {
        utcTime = default;
        if (!_broadcastAnchorsByProgram.TryGetValue(programNumber, out var anchors) || anchors.Length == 0)
            return false;

        var insertionIndex = FindBroadcastAnchorInsertionIndex(anchors, fileOffset);
        if (pts90k == long.MinValue)
        {
            if (anchors.Length < 2)
                return false;
            var leftIndex = insertionIndex <= 0 ? 0
                : insertionIndex >= anchors.Length ? anchors.Length - 2
                : insertionIndex - 1;
            var rightIndex = leftIndex + 1;
            var left = anchors[leftIndex];
            var right = anchors[rightIndex];
            var offsetSpan = right.FileOffset - left.FileOffset;
            if (offsetSpan <= 0)
                return false;
            var ratio = (fileOffset - left.FileOffset) / (double)offsetSpan;
            try
            {
                // 缺少 PTS 时只在同一节目相邻 UTC 锚点间按文件位置估算，避免跨节目时钟域。
                utcTime = left.UtcTime.AddSeconds((right.UtcTime - left.UtcTime).TotalSeconds * ratio);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        // 先按文件位置选最近的 UTC 锚点，再用 PTS 差值细化到异常区域；这样 PTS 重置后也不会串到旧时间段。
        var index = insertionIndex;
        if (index >= anchors.Length)
            index = anchors.Length - 1;
        else if (index > 0 &&
                 fileOffset - anchors[index - 1].FileOffset <=
                 anchors[index].FileOffset - fileOffset)
            index--;

        var anchor = anchors[index];
        utcTime = anchor.UtcTime.AddSeconds((pts90k - anchor.Clock90k) / 90_000.0);
        return true;
    }

    private static int FindBroadcastAnchorInsertionIndex(TsBroadcastTimeAnchor[] anchors, long fileOffset)
    {
        var index = Array.BinarySearch(
            anchors,
            new TsBroadcastTimeAnchor(default, 0, 0, fileOffset),
            BroadcastAnchorOffsetComparer.Instance);
        return index < 0 ? ~index : index;
    }

    private string FormatPosition(long start, long end)
    {
        var startPacket = Math.Max(0, (start - _analysis.ReferenceSource.Catalog.SyncOffset) / TsStreamAnalyzer.PacketSize);
        var endPacket = Math.Max(startPacket, (end - _analysis.ReferenceSource.Catalog.SyncOffset) / TsStreamAnalyzer.PacketSize);
        return string.Format(_text.Strings.String_TsRepair_Map_Position,
            startPacket, endPacket, start, end);
    }

    private void OnLanguageChanged() => Rebuild();

    public void OnClosed() => App.LocalizationService.LanguageChanged -= OnLanguageChanged;

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed class BroadcastAnchorOffsetComparer : IComparer<TsBroadcastTimeAnchor>
    {
        public static BroadcastAnchorOffsetComparer Instance { get; } = new();

        public int Compare(TsBroadcastTimeAnchor left, TsBroadcastTimeAnchor right) =>
            left.FileOffset.CompareTo(right.FileOffset);
    }
}
