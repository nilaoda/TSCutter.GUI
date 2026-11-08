namespace TSCutter.GUI.Models.Editing;

/// <summary>
/// 主时间轴上的轻量剪辑区间；活动项用于保持当前编辑区间的强调显示。
/// </summary>
/// <param name="Removed">删除模式：范围是要从视频中删除的部分，时间轴用删除颜色标出。</param>
public readonly record struct ClipTimelineRange(double Start, double End, bool IsActive,
    string Name = "", string Notes = "", string Color = "", bool Removed = false);
