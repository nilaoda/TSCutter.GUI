using System.Collections.Generic;

namespace TSCutter.GUI.Models;

public enum TsRepairMapViewMode
{
    Expected,
    Actual
}

public enum TsRepairMapDisplayMode
{
    SourceMatrix,
    Timeline
}

public enum TsRepairMapRegionStatus
{
    Muted,
    Success,
    Warning,
    Error
}

public enum TsRepairMapIssueKind
{
    PacketGap,
    MediaRegion
}

public enum TsRepairMapSourceCellStatus
{
    None,
    Available,
    Chosen
}

public sealed class TsRepairMapCandidateView
{
    public required string SourcePath { get; init; }
    public required int SourcePid { get; init; }
    public required string SourceText { get; init; }
    public required string MatchText { get; init; }
    public required string PacketText { get; init; }
    public required bool IsChosen { get; init; }
}

public sealed class TsRepairMapRegionView
{
    public required string Key { get; init; }
    public required int Pid { get; init; }
    public required TsRepairMapIssueKind Kind { get; init; }
    public required TsRepairMapRegionStatus Status { get; init; }
    public required double StartSeconds { get; init; }
    public required double EndSeconds { get; init; }
    public required bool IsEstimatedTime { get; init; }
    public required long StartOffset { get; init; }
    public required long EndOffset { get; init; }
    public required string TrackText { get; init; }
    public required string IssueText { get; init; }
    public required string StatusText { get; set; }
    public required string TimeText { get; init; }
    public required string BroadcastTimeText { get; init; }
    public required string PositionText { get; init; }
    public required string SourceText { get; init; }
    public required string MatchText { get; init; }
    public required string PacketText { get; init; }
    public List<TsRepairMapCandidateView> Candidates { get; } = [];
    public bool HasBroadcastTime => !string.IsNullOrEmpty(BroadcastTimeText);
}

public sealed class TsRepairMapTrackView
{
    public required int Pid { get; init; }
    public required string DisplayText { get; init; }
    public required string StatusText { get; set; }
    public required bool IsSelected { get; init; }
    public bool IsOverview { get; init; }
    public List<TsRepairMapRegionView> Regions { get; } = [];
}

public sealed class TsRepairMapSourceView
{
    public required string SourcePath { get; init; }
    public required string Label { get; init; }
    public required string FileName { get; init; }
    public required string CoverageText { get; set; }
}

public sealed class TsRepairMapSourceCellView
{
    public required TsRepairMapSourceView Source { get; init; }
    public required TsRepairMapSourceCellStatus Status { get; init; }
    public required string DisplayText { get; init; }
    public required string TooltipText { get; init; }
    public TsRepairMapCandidateView? Candidate { get; init; }
}

public sealed class TsRepairMapMatrixRowView
{
    public required TsRepairMapRegionView Region { get; init; }
    public required string TimeText { get; init; }
    public required string TrackText { get; init; }
    public required string IssueText { get; init; }
    public List<TsRepairMapSourceCellView> SourceCells { get; } = [];
    public bool IsSuccess => Region.Status == TsRepairMapRegionStatus.Success;
    public bool IsWarning => Region.Status == TsRepairMapRegionStatus.Warning;
    public bool IsError => Region.Status == TsRepairMapRegionStatus.Error;
}
