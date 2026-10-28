using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.TransportStream.Analysis;
using TSCutter.GUI.Services.Editing;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

public sealed class ExportRecordRegistryTests
{
    [Fact]
    public void ExportRecordRegistryKeepsBoundedRecordsAndDistinguishesRepeatedPaths()
    {
        var registry = new ExportRecordRegistry();
        var records = new List<ExportRecord>();
        for (var index = 0; index < ExportRecordRegistry.MaxRecords + 20; index++)
        {
            records.Add(registry.Register(CreateRecord("same-output.ts", index >= 30)));
        }

        Assert.Equal(ExportRecordRegistry.MaxRecords, registry.Records.Count);
        // 最新记录在最前，重复导出到相同路径生成独立记录，互不串用。
        Assert.Equal(records[^1].Id, registry.Records[0].Id);
        Assert.Equal(records[^2].Id, registry.Records[1].Id);
        Assert.True(registry.Records[0].Id != registry.Records[1].Id);
        // 完整结果只保留最近几条；更早记录的详细结果被释放，摘要仍可用。
        Assert.Equal(ExportRecordRegistry.MaxRetainedResults,
            registry.Records.Count(record => record.HasRetainedResult));
        Assert.Null(registry.Records[ExportRecordRegistry.MaxRetainedResults].RetainedResult);
        Assert.NotNull(registry.Records[0].RetainedResult);
        var failed = registry.Register(new ExportRecord
        {
            OutputPath = "failed.ts", CheckState = TsExportCheckState.Failed,
            RetainedResult = new TsCheckResult { FilePath = "failed.ts", FileSize = 100 }
        });
        Assert.Null(failed.RetainedResult);
        Assert.Equal(ExportRecordRegistry.MaxRetainedResults,
            registry.Records.Count(record => record.HasRetainedResult));
    }

    [Fact]
    public void ExportRecordStalenessReflectsOutputFileChanges()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tscutter-record-{Guid.NewGuid():N}.ts");
        try
        {
            File.WriteAllBytes(path, new byte[1000]);
            var info = new FileInfo(path);
            var registry = new ExportRecordRegistry();
            var record = registry.Register(new ExportRecord
            {
                Timestamp = DateTime.Now,
                OutputPath = path,
                Kind = ExportKind.TsCopy,
                FileSize = info.Length,
                FileLengthAtExport = info.Length,
                FileModifiedAtExport = info.LastWriteTimeUtc,
                CheckEnabled = true,
                CheckApplicable = true,
                CheckState = TsExportCheckState.Completed,
                RetainedResult = new TsCheckResult { FilePath = path, FileSize = info.Length }
            });

            Assert.True(ExportRecordRegistry.IsOutputUnchanged(record));
            File.WriteAllBytes(path, new byte[2000]);
            Assert.False(ExportRecordRegistry.IsOutputUnchanged(record));
            File.Delete(path);
            Assert.False(ExportRecordRegistry.IsOutputUnchanged(record));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ExportRecord CreateRecord(string outputPath, bool withResult) => new()
    {
        Timestamp = DateTime.Now,
        OutputPath = outputPath,
        Kind = ExportKind.TsCopy,
        FileSize = 1234,
        Elapsed = TimeSpan.FromMilliseconds(10),
        ClipCount = 1,
        CheckEnabled = true,
        CheckApplicable = true,
        CheckState = TsExportCheckState.Completed,
        CheckVerdict = TsCheckVerdict.Pass,
        FileLengthAtExport = 1234,
        FileModifiedAtExport = DateTime.Now,
        RetainedResult = withResult ? new TsCheckResult { FilePath = outputPath, FileSize = 1234 } : null
    };

}
