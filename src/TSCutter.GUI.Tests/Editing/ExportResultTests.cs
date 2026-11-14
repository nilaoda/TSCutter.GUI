using System.Reflection;
using Avalonia.Headless;
using Splat;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels.Editing;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

/// <summary>
/// 单个导出的结果窗口：成功时显示类型、大小、耗时与平均速度、内容、检测结论，摘要与导出记录一致；
/// 失败时显示错误与已用时间。取消按字节复制不再被当作导出完成，只清理本次写过的半成品。
/// </summary>
[Collection("Avalonia UI")]
public sealed class ExportResultTests
{
    [Fact]
    public async Task SuccessShowsRecordDetailsAndFailureShowsTheError()
    {
        var directory = Directory.CreateTempSubdirectory("tscutter-export-result-");
        try
        {
            var output = Path.Combine(directory.FullName, "节目_cut.mp4");
            File.WriteAllBytes(output, new byte[2 * 1024 * 1024]);
            using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
            await session.Dispatch<bool>(() =>
            {
                using var resolver = new ModernDependencyResolver();
                using var scope = resolver.WithResolver();
                var locale = new LocalizationService();
                locale.SwitchLanguage("zh-CN");
                Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
                var strings = LocalizationManager.Instance;
                var record = new ExportRecord
                {
                    Timestamp = DateTime.Now, OutputPath = output, Kind = ExportKind.Mp4Remux, FileSize = 2 * 1024 * 1024,
                    Elapsed = TimeSpan.FromSeconds(4), ClipCount = 2, ClipRangeSummary = "00:00:00.000 – 00:00:02.000",
                    CheckEnabled = true, CheckApplicable = false
                };
                var success = new ExportResultWindowViewModel(null!);
                success.Initialize(new ExportResultInfo
                {
                    OutputPath = output, Success = true, Record = record, Elapsed = record.Elapsed,
                    ExtraTags = [strings.String_ExportResult_RemoveTag], ContentText = "删除 1 段 · 剩余 00:00:02.000"
                });
                Assert.Equal(strings.String_ExportResult_TitleSuccess, success.Title);
                Assert.Equal("节目_cut.mp4", success.FileName);
                Assert.Equal(new[] { strings.String_Export_Kind_Mp4Remux, strings.String_ExportResult_RemoveTag }, success.Tags);
                // 导出类型在前，类型之外的改动按固定顺序作为附加标签；没有改动时只有类型。
                Assert.Equal(strings.String_Export_Kind_TsCopy,
                    ExportRecordView.FormatKind(ExportKind.TsCopy, ExportModifiers.None, 0, strings));
                Assert.Equal(new[]
                    {
                        strings.String_Export_Kind_TsMerge, strings.String_Export_Tag_ProgramOnly,
                        strings.String_Export_Tag_TablesSupplemented, strings.String_Export_Tag_TracksSelected,
                        string.Format(strings.String_Export_Tag_AudioDelay, "+120"), strings.String_Export_Tag_DolbyE
                    },
                    ExportRecordView.FormatTags(ExportKind.TsMerge, ExportModifiers.DolbyETranscoded | ExportModifiers.AudioDelay |
                        ExportModifiers.TracksSelected | ExportModifiers.TablesSupplemented | ExportModifiers.ProgramOnly, 120, strings));
                Assert.Equal($"{strings.String_Export_Kind_TsPacket} · {string.Format(strings.String_Export_Tag_AudioDelay, "-40")}",
                    ExportRecordView.FormatKind(ExportKind.TsPacket, ExportModifiers.AudioDelay, -40, strings));
                Assert.Equal(CommonUtil.FormatFileSize(2 * 1024 * 1024), success.SizeText);
                Assert.Equal("00:00:04.0 · " + string.Format(strings.String_ExportResult_AverageSpeed,
                    CommonUtil.FormatFileSize(512 * 1024)), success.ElapsedText);
                Assert.True(success.HasContent && success.HasRanges && success.HasCheck && success.ShowsLocation);
                Assert.False(success.HasReencode || success.CanViewCheckDetails || success.ShowsNoFile);
                Assert.Equal(strings.String_Export_Check_NotApplicable, success.CheckStatusText);
                Assert.Equal(ExportRecordsWindowViewModel.BuildSummaryText(record), success.BuildSummaryText());

                var missing = Path.Combine(directory.FullName, "missing.ts");
                var failure = new ExportResultWindowViewModel(null!);
                failure.Initialize(new ExportResultInfo
                {
                    OutputPath = missing, Success = false, Elapsed = TimeSpan.FromMilliseconds(3250), ErrorText = "边界无效"
                });
                Assert.Equal(strings.String_ExportResult_TitleFailure, failure.Title);
                Assert.True(failure.IsFailure && failure.ShowsNoFile);
                Assert.False(failure.HasCheck || failure.HasContent || failure.HasTags || failure.ShowsLocation);
                Assert.Equal("00:00:03.2", failure.ElapsedText);
                Assert.Contains("边界无效", failure.BuildSummaryText());
                Assert.Contains(missing, failure.BuildSummaryText());
                failure.OpenFileCommand.Execute(null);
                Assert.Equal(strings.String_Export_Records_FileChanged, failure.StatusText);
                return Task.FromResult(true);
            }, CancellationToken.None);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>导出开始前取消：以前按字节复制会被报告为完成，主界面随即把残留文件标成已导出。</summary>
    [Fact]
    public async Task CancelledByteCopyIsNotReportedAsCompleted()
    {
        var directory = Directory.CreateTempSubdirectory("tscutter-export-cancel-");
        try
        {
            var source = Path.Combine(directory.FullName, "source.bin");
            var target = Path.Combine(directory.FullName, "existing.bin");
            File.WriteAllBytes(source, new byte[1024 * 1024]);
            File.WriteAllBytes(target, [1, 2, 3]);
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddDays(-1));
            var output = new OutputWindowViewModel
            {
                IsRawByteRange = true, SourceFilePath = source, OutputPath = target, StartPosition = 0, EndPosition = -1
            };
            output.CancelOutputCommand.Execute(null);
            await output.OutputCommand.ExecuteAsync(null);
            Assert.False(output.DialogResult);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(target));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>只删除本次导出开始后被写过的文件；导出前已存在、尚未改写的文件保留。</summary>
    [Fact]
    public void PartialOutputCleanupKeepsFilesThatWereNotWritten()
    {
        var directory = Directory.CreateTempSubdirectory("tscutter-export-partial-");
        try
        {
            var delete = typeof(OutputWindowViewModel).GetMethod("DeletePartialOutput", BindingFlags.Static | BindingFlags.NonPublic)!;
            var started = DateTime.UtcNow;
            var written = Path.Combine(directory.FullName, "written.ts");
            var untouched = Path.Combine(directory.FullName, "untouched.ts");
            File.WriteAllBytes(written, [1]);
            File.WriteAllBytes(untouched, [2]);
            File.SetLastWriteTimeUtc(untouched, started.AddMinutes(-5));
            delete.Invoke(null, [written, started]);
            delete.Invoke(null, [untouched, started]);
            delete.Invoke(null, [Path.Combine(directory.FullName, "absent.ts"), started]);
            Assert.False(File.Exists(written));
            Assert.True(File.Exists(untouched));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
