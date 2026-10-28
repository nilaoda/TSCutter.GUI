using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TSCutter.GUI.Models.Editing;

namespace TSCutter.GUI.Services.Editing;

/// <summary>
/// 会话内的导出记录表：覆盖单段、批量、队列和合并导出，不依赖仍存在的队列项或片段。
/// 同一个合并文件只登记一份物理输出结果，可被多个片段引用；重复导出到相同路径会
/// 生成独立的新记录，各自携带自己的检测结果，互不串用。
/// </summary>
public sealed class ExportRecordRegistry
{
    /// <summary>轻量摘要的保留上限；超限后按时间淘汰最旧记录。</summary>
    public const int MaxRecords = 100;
    /// <summary>完整 TsCheckResult 的保留数量；只有最近几条保留详细结果。</summary>
    public const int MaxRetainedResults = 8;

    private readonly object _gate = new();
    private readonly List<ExportRecord> _records = [];
    private long _nextId = 1;

    public event Action? Changed;

    public IReadOnlyList<ExportRecord> Records
    {
        get
        {
            lock (_gate)
                return _records.ToArray();
        }
    }

    public ExportRecord Register(ExportRecord record)
    {
        // 未完成的检测只保留失败原因和轻量计数，不占用完整详情的缓存名额。
        if (record.CheckState != TsExportCheckState.Completed)
            record.RetainedResult = null;
        if (record.RetainedResult is { } result)
        {
            record.CheckCategories = result.Events
                .GroupBy(item => item.Type)
                .Select(group => new ExportCheckCategory(group.Key,
                    group.Sum(item => (long)item.Occurrences)))
                .OrderByDescending(category => category.Count)
                .Take(8)
                .ToArray();
        }
        lock (_gate)
        {
            record.Id = _nextId++;
            _records.Insert(0, record);
            if (_records.Count > MaxRecords)
                _records.RemoveRange(MaxRecords, _records.Count - MaxRecords);
            TrimRetainedResults();
        }
        Changed?.Invoke();
        return record;
    }

    public ExportRecord? Find(long id)
    {
        lock (_gate)
        {
            foreach (var record in _records)
            {
                if (record.Id == id)
                    return record;
            }
        }
        return null;
    }

    /// <summary>输出文件是否还与登记时一致；不存在或已被覆盖的记录不能冒充当前文件的检测结论。</summary>
    public static bool IsOutputUnchanged(ExportRecord record)
    {
        try
        {
            var info = new FileInfo(record.OutputPath);
            return info.Exists && info.Length == record.FileLengthAtExport &&
                   info.LastWriteTimeUtc == record.FileModifiedAtExport;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void TrimRetainedResults()
    {
        var retained = 0;
        foreach (var record in _records)
        {
            if (record.RetainedResult is null)
                continue;
            if (retained >= MaxRetainedResults)
                record.RetainedResult = null;
            else
                retained++;
        }
    }
}
