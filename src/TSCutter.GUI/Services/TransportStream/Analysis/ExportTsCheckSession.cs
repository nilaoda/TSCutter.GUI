using System;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.TransportStream.Analysis;

namespace TSCutter.GUI.Services.TransportStream.Analysis;

/// <summary>
/// 单个输出文件的导出期 TS 检测会话。导出服务把最终写出的字节按顺序交给 Append；
/// 检测器异常不会打断导出，只把会话标记为 Failed 并保留原因。
/// 会话只属于一个输出文件，所有方法只能在执行导出的工作线程上顺序调用。
/// 首次 Append 时延迟初始化；导出任务在关闭并替换文件成功后调用 Complete，失败则调用 Abort。
/// </summary>
public sealed class ExportTsCheckSession
{
    private TsStreamAnalyzer? _analyzer;

    public ExportTsCheckSession(string outputPath)
    {
        OutputPath = outputPath;
    }

    public string OutputPath { get; }
    public TsExportCheckState State { get; private set; } = TsExportCheckState.NotStarted;
    public TsExportCheckFailure FailureKind { get; private set; } = TsExportCheckFailure.None;
    public string? FailureDetail { get; private set; }
    /// <summary>Complete/Abort 之后可用的检测结果；Failed 时保留部分结果用于诊断。</summary>
    public TsCheckResult? Result { get; private set; }

    public void Begin()
    {
        if (State != TsExportCheckState.NotStarted)
            return;
        try
        {
            // 导出使用完整检测口径（默认 Features、不限制字节数），不使用 InventoryOnly。
            _analyzer = new TsStreamAnalyzer();
            _analyzer.BeginSession(OutputPath, new TsStreamAnalyzeOptions());
            State = TsExportCheckState.Running;
        }
        catch (Exception exception)
        {
            MarkFailed(TsExportCheckFailure.Exception, exception.Message);
        }
    }

    /// <summary>追加一块实际写出的输出字节；检测器异常在此被吞掉并停止后续检测。</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        if (State == TsExportCheckState.NotStarted)
            Begin();
        if (State != TsExportCheckState.Running)
            return;
        try
        {
            _analyzer!.AppendOutput(data);
        }
        catch (Exception exception)
        {
            MarkFailed(TsExportCheckFailure.Exception, exception.Message);
        }
    }

    /// <summary>导出成功收尾时调用，完成最终结构检查并生成结果。</summary>
    public void Complete()
    {
        if (State == TsExportCheckState.NotStarted)
            Begin();
        if (State != TsExportCheckState.Running)
            return;
        try
        {
            Result = _analyzer!.CompleteSession();
            State = TsExportCheckState.Completed;
        }
        catch (Exception exception)
        {
            MarkFailed(TsExportCheckFailure.Exception, exception.Message);
        }
        finally
        {
            ReleaseAnalyzer();
        }
    }

    /// <summary>导出取消或失败时调用；结果标记为已取消，不会成为完整检测通过。</summary>
    public void Abort()
    {
        if (State == TsExportCheckState.NotStarted)
        {
            State = TsExportCheckState.Aborted;
            return;
        }
        if (State != TsExportCheckState.Running)
            return;
        try
        {
            Result = _analyzer!.AbortSession();
            State = TsExportCheckState.Aborted;
        }
        catch (Exception exception)
        {
            MarkFailed(TsExportCheckFailure.Exception, exception.Message);
        }
        finally
        {
            ReleaseAnalyzer();
        }
    }

    /// <summary>M2TS 封装器出现回写时调用：覆盖写入不能当作追加数据检测。</summary>
    public void MarkSeekDetected()
    {
        if (State == TsExportCheckState.NotStarted)
            Begin();
        if (State != TsExportCheckState.Running)
            return;
        MarkFailed(TsExportCheckFailure.SeekDetected, "The output performed a seek; incremental check stopped.");
    }

    private void MarkFailed(TsExportCheckFailure kind, string detail)
    {
        try
        {
            if (_analyzer is not null)
                Result = _analyzer.AbortSession();
        }
        catch
        {
            // 保留原始失败原因，放弃部分结果。
        }
        State = TsExportCheckState.Failed;
        if (FailureKind == TsExportCheckFailure.None)
        {
            FailureKind = kind;
            FailureDetail = detail;
        }
        ReleaseAnalyzer();
    }

    private void ReleaseAnalyzer()
    {
        _analyzer?.Dispose();
        _analyzer = null;
    }
}
