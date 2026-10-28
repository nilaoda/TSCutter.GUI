using TSCutter.GUI.Models.TransportStream.Analysis;

namespace TSCutter.GUI.Models.Editing;

/// <summary>
/// 片段上展示的导出期 TS 检测轻量结论；完整结果按记录 Id 从导出记录表读取。
/// </summary>
public sealed class ClipExportCheck
{
    public long RecordId { get; init; }
    public bool CheckEnabled { get; init; }
    public bool CheckApplicable { get; init; }
    public TsExportCheckState CheckState { get; init; }
    public TsCheckVerdict? Verdict { get; init; }
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }
}
