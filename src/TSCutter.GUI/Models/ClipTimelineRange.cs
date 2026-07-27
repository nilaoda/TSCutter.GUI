namespace TSCutter.GUI.Models;

/// <summary>
/// 主时间轴上的轻量剪辑区间；活动项用于保持当前编辑区间的强调显示。
/// </summary>
public readonly record struct ClipTimelineRange(double Start, double End, bool IsActive);
