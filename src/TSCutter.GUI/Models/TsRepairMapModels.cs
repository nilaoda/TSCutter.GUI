using System.Collections.Generic;

namespace TSCutter.GUI.Models;

public enum TsRepairMapViewMode
{
    Expected,
    Actual
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
    public required string PositionText { get; init; }
    public required string SourceText { get; init; }
    public required string MatchText { get; init; }
    public required string PacketText { get; init; }
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

public sealed class TsRepairMapZoomItem(int percent)
{
    public int Percent { get; } = percent;
    public string DisplayText => $"{Percent}%";
}
