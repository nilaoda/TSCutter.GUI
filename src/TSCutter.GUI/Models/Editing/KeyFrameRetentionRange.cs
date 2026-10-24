namespace TSCutter.GUI.Models.Editing;

/// <summary>缩略图对应的实际保留区间；有原始 PTS 时优先按 PTS 判断边界。</summary>
public readonly record struct KeyFrameRetentionRange(
    double Start, double End, long? StartPts, long? EndPts, bool IncludesEnd, bool IsActive, string Color)
{
    public bool Contains(long pts, double time)
    {
        if (!double.IsFinite(time) || End < Start)
            return false;
        var startComparison = StartPts is { } start ? pts.CompareTo(start) : time.CompareTo(Start);
        var endComparison = EndPts is { } end ? pts.CompareTo(end) : time.CompareTo(End);
        return startComparison >= 0 && (IncludesEnd ? endComparison <= 0 : endComparison < 0);
    }
}
