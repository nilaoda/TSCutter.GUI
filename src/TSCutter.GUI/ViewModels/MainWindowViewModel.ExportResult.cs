using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels.Editing;

namespace TSCutter.GUI.ViewModels;

// 单个导出（保存片段、合并导出、删除模式导出）结束后的结果窗口；队列等批量导出不显示。
public partial class MainWindowViewModel
{
    /// <summary>
    /// 显示导出结果：成功时（未在设置中关闭）显示文件、大小、耗时与检测结论，失败时总是显示错误说明；取消不显示。
    /// 调用方应先完成片段状态的更新，再调用本方法。
    /// </summary>
    private async Task ShowExportResultAsync(OutputWindowViewModel output, string outputPath, string contentText,
        params string[] extraTags)
    {
        if (output.Exception is null && output.DialogResult != true)
            return;
        var success = output.Exception is null && File.Exists(outputPath);
        if (success && !_configService.CurrentConfig.ShowExportResult)
            return;
        var outcome = output.Outcomes.LastOrDefault(item => PathsEqual(item.OutputPath, outputPath));
        var record = success && outcome?.RecordId is { } id ? App.ExportRecordRegistry?.Find(id) : null;
        if (output.Exception is { } exception)
            Console.WriteLine(exception);
        var viewModel = new ExportResultWindowViewModel(_dialogService);
        viewModel.Initialize(new ExportResultInfo
        {
            OutputPath = outputPath,
            Success = success,
            Record = record,
            Kind = outcome?.Kind,
            DolbyETranscoded = outcome?.DolbyETranscoded ?? false,
            Elapsed = outcome?.Elapsed ?? record?.Elapsed ?? TimeSpan.Zero,
            ExtraTags = extraTags,
            ContentText = contentText,
            ReencodeText = success ? FormatReencodeSummary(output.LastFrameAccurateProgress) : null,
            ErrorText = output.Exception is { } error ? FormatMergeError(error) : null
        });
        await _dialogService.ShowDialogAsync(this, viewModel);
        if (success && viewModel.DontShowAgain)
        {
            _configService.CurrentConfig.ShowExportResult = false;
            _configService.Save();
        }
    }

    /// <summary>智能剪辑的重新编码汇总：累计重新编码的帧数，以及按计划时长计算的无需重新编码比例。</summary>
    private static string? FormatReencodeSummary(FrameAccurateExportProgress? progress)
    {
        if (progress is null)
            return null;
        var encode = progress.Plan.Where(part => part.Kind == FrameAccuratePartKind.Encode).Sum(part => part.Duration);
        var copy = progress.Plan.Where(part => part.Kind == FrameAccuratePartKind.Copy).Sum(part => part.Duration);
        return string.Format(LocalizationManager.Instance.String_ExportResult_ReencodeSummary,
            progress.EncodedFrames, OutputWindowViewModel.CopiedPercent(encode, copy));
    }

    private static string FormatExportDuration(double seconds) =>
        string.Format(LocalizationManager.Instance.String_ExportResult_Duration, CommonUtil.FormatSeconds(Math.Max(0, seconds)));

    /// <summary>片段覆盖的总时长：按时间合并重叠部分，未标记终点的片段算到文件结尾。</summary>
    private double ClipsDuration(IEnumerable<PickedClip> clips)
    {
        var total = 0.0;
        var covered = double.NegativeInfinity;
        foreach (var (start, end) in clips.Select(clip => (clip.StartTime, clip.EndPosition < 0 ? DurationMax : clip.EndTime))
                     .OrderBy(range => range.StartTime))
        {
            var from = Math.Max(start, covered);
            if (end > from)
                total += end - from;
            covered = Math.Max(covered, end);
        }
        return total;
    }
}
