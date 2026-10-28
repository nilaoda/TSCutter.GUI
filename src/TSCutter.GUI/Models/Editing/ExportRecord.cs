using System;
using System.Collections.Generic;
using TSCutter.GUI.Models.TransportStream.Analysis;

namespace TSCutter.GUI.Models.Editing;

public enum ExportKind
{
    TsCopy,
    TsProgramOnly,
    TsProgramTablesSupplement,
    TsMerge,
    M2tsRemux,
    Mp4Remux,
    MkvRemux,
    VobRemux
}

public enum TsExportCheckState
{
    /// <summary>尚未开始喂入（例如导出开始前就被取消）。</summary>
    NotStarted,
    /// <summary>正在随写出过程增量检测。</summary>
    Running,
    /// <summary>全部输出字节检测完成。</summary>
    Completed,
    /// <summary>导出取消或提前结束，检测随之中止，不构成完整结论。</summary>
    Aborted,
    /// <summary>检测器自身异常或输出出现回写，已停止本文件后续检测；原因保留在 FailureKind/FailureDetail。</summary>
    Failed
}

public enum TsExportCheckFailure
{
    None,
    /// <summary>检测器内部异常。</summary>
    Exception,
    /// <summary>输出出现 seek/回写，无法按顺序追加的方式继续检测。</summary>
    SeekDetected
}

/// <summary>一次导出任务的结果索引；RecordId 为空表示本次导出未成功或未登记。</summary>
public sealed record ExportTaskOutcome(long? RecordId, string OutputPath, bool Success);

/// <summary>按检测类型保存的有界分类统计。</summary>
public sealed record ExportCheckCategory(TsCheckEventType Type, long Count);

/// <summary>
/// 一次成功导出的轻量摘要。完整 TsCheckResult 只在最近几条记录上保留，
/// 其余记录的详细结果被释放后由 HasRetainedResult 明确说明，不会无提示地重读大文件。
/// </summary>
public sealed class ExportRecord
{
    public long Id { get; internal set; }
    public DateTime Timestamp { get; init; }
    public string OutputPath { get; init; } = "";
    public ExportKind Kind { get; init; }
    public long FileSize { get; init; }
    public TimeSpan Elapsed { get; init; }
    /// <summary>本次导出包含的片段数量；合并导出为片段数，单段导出为 1。</summary>
    public int ClipCount { get; init; }
    /// <summary>已知的剪辑范围描述（各片段起止时间的拼接，过长时截断）。</summary>
    public string ClipRangeSummary { get; init; } = "";
    public bool CheckEnabled { get; init; }
    /// <summary>该输出类型是否适用 TS 检测（MP4/MKV/VOB 不适用）。</summary>
    public bool CheckApplicable { get; init; }
    public TsExportCheckState CheckState { get; init; } = TsExportCheckState.NotStarted;
    public TsCheckVerdict? CheckVerdict { get; init; }
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }
    /// <summary>有界事件列表的分类摘要，完整结果释放后仍保留；不替代累计错误/警告总数。</summary>
    public IReadOnlyList<ExportCheckCategory> CheckCategories { get; internal set; } = [];
    public TsExportCheckFailure FailureKind { get; init; } = TsExportCheckFailure.None;
    public string? FailureDetail { get; init; }

    /// <summary>登记时输出文件的磁盘状态，用于在打开详情前判断记录是否过期。</summary>
    public long FileLengthAtExport { get; init; }
    public DateTime FileModifiedAtExport { get; init; }

    /// <summary>完整检测结果；超过保留数量后释放为 null，摘要字段仍然可用。</summary>
    public TsCheckResult? RetainedResult { get; internal set; }
    public bool HasRetainedResult => RetainedResult is not null;

    public string FileName => System.IO.Path.GetFileName(OutputPath);
}
