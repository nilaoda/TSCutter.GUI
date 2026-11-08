namespace TSCutter.GUI.Models.Editing;

/// <summary>
/// 缩略图对应的实际保留区间；有原始 PTS 时优先按 PTS 判断边界。
/// 删除模式（Removed）的范围是要删除的画面：终点是恢复播放的画面，本身不在范围内。
/// </summary>
public readonly record struct KeyFrameRetentionRange(
    double Start, double End, long? StartPts, long? EndPts, bool IsActive, string Color, bool Removed = false)
{
    public bool Contains(long pts, double time)
    {
        if (!double.IsFinite(time) || End < Start)
            return false;
        var startComparison = StartPts is { } start ? pts.CompareTo(start) : time.CompareTo(Start);
        var endComparison = EndPts is { } end ? pts.CompareTo(end) : time.CompareTo(End);
        return startComparison >= 0 && (Removed ? endComparison < 0 : endComparison <= 0);
    }
}
