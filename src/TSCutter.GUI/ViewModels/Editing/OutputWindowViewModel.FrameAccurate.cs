using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Files;

namespace TSCutter.GUI.ViewModels.Editing;

// 帧精确导出的过程展示：计划条（重新编码/直接复制的分布与当前位置）、当前阶段说明和累计统计。
public partial class OutputWindowViewModel
{
    internal Func<IReadOnlyList<string>, CancellationToken, Task<bool>>? ConfirmTimestampNormalizationAsync { get; set; }
    private readonly HashSet<string> timestampNormalizationApprovedSources = new(MediaFileUtil.PathComparer);
    private bool timestampsPrepared;

    internal void SetTimestampNormalizationChoice(IReadOnlyList<string> sources)
    {
        timestampNormalizationApprovedSources.Clear();
        timestampNormalizationApprovedSources.UnionWith(sources);
        timestampsPrepared = true;
    }

    internal async Task<IReadOnlyList<string>> FindTimestampNormalizationSourcesAsync(CancellationToken token)
    {
        var requests = new List<FrameAccurateExportRequest>();
        var batch = IsBatchMode;
        if (!IsRawByteRange)
        {
            if (_frameAccurateRequest is { } merge)
                requests.Add(merge);
            else if (batch)
            {
                foreach (var task in _tasks.ToArray())
                    if (task.FrameAccurate is { } settings)
                        requests.Add(new FrameAccurateExportRequest(task.DestinationPath,
                            [new(task.SourcePath, task.StartPts, task.EndPts, settings)]));
            }
            else if (FrameAccurate is { } settings)
                requests.Add(new FrameAccurateExportRequest(OutputPath ?? "",
                    [new(SourceFilePath!, StartPts, EndPts, settings)]));
        }
        var sources = new HashSet<string>(MediaFileUtil.PathComparer);
        foreach (var request in requests)
        {
            token.ThrowIfCancellationRequested();
            var source = request.Segments[0].SourcePath;
            try
            {
                if (!sources.Contains(source) &&
                    await new FrameAccurateExportService().NeedsTimestampNormalizationAsync(request, token).ConfigureAwait(false))
                    sources.Add(source);
            }
            catch (Exception exception) when (batch && exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or FFmpegException or FrameAccurateExportException)
            {
                // 单项错误留给导出记录，其他任务仍继续检查。
                Console.Error.WriteLine($"Timestamp check skipped: {exception.Message}");
            }
        }
        token.ThrowIfCancellationRequested();
        return sources.ToArray();
    }

    private async Task<bool> CheckTimestampNormalizationAsync()
    {
        if (timestampsPrepared)
            return true;
        try
        {
            var sources = await FindTimestampNormalizationSourcesAsync(_cts.Token);
            _cts.Token.ThrowIfCancellationRequested();
            if (sources.Count > 0 && (ConfirmTimestampNormalizationAsync is null ||
                !await ConfirmTimestampNormalizationAsync(sources.Select(path => Path.GetFileName(path)).ToArray(), _cts.Token)))
            {
                DialogResult = false;
                RequestClose?.Invoke();
                return false;
            }
            _cts.Token.ThrowIfCancellationRequested();
            SetTimestampNormalizationChoice(sources);
            return true;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Exception = exception; }
        DialogResult = false;
        RequestClose?.Invoke();
        return false;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsFrameAccuratePlan), nameof(FrameAccuratePlanText))]
    public partial IReadOnlyList<FrameAccuratePlanPart>? FrameAccuratePlan { get; set; }

    [ObservableProperty]
    public partial double FrameAccurateTotal { get; set; }

    [ObservableProperty]
    public partial double FrameAccuratePosition { get; set; }

    [ObservableProperty]
    public partial string FrameAccuratePhaseText { get; set; } = "";

    [ObservableProperty]
    public partial string FrameAccurateStatsText { get; set; } = "";

    public bool ShowsFrameAccuratePlan => FrameAccuratePlan is { Count: > 0 };

    /// <summary>计划摘要：重新编码与直接复制各占的输出时长，以及无需重新编码的比例。</summary>
    public string FrameAccuratePlanText
    {
        get
        {
            if (FrameAccuratePlan is not { Count: > 0 } plan)
                return "";
            var encode = plan.Where(part => part.Kind == FrameAccuratePartKind.Encode).Sum(part => part.Duration);
            var copy = plan.Where(part => part.Kind == FrameAccuratePartKind.Copy).Sum(part => part.Duration);
            return string.Format(LocalizationManager.Instance.String_Output_FrameAccurate_Plan,
                PlanSeconds(encode), PlanSeconds(copy), CopiedPercent(encode, copy));
        }
    }

    /// <summary>
    /// 时长保留 1 位小数；不足 0.1 秒（例如只重编码 1 帧）时保留 2 位，且至少显示 0.01，不会显示成 0.0。
    /// </summary>
    internal static string PlanSeconds(double seconds) =>
        seconds <= 0 ? 0.0.ToString("0.0", CultureInfo.CurrentCulture)
        : seconds < 0.1 ? Math.Max(seconds, 0.01).ToString("0.00", CultureInfo.CurrentCulture)
        : seconds.ToString("0.0", CultureInfo.CurrentCulture);

    /// <summary>
    /// 无需重新编码的比例，精确到 0.1%，向下取整：只要有重编码就不会显示 100%（例如 99.9%）；
    /// 有复制但不足 0.1% 时显示“&lt;0.1”，不会显示成 0%。整数比例不带小数（100%、0%、50%）。
    /// </summary>
    internal static string CopiedPercent(double encode, double copy)
    {
        var total = encode + copy;
        if (total <= 0 || copy <= 0)
            return 0.ToString(CultureInfo.CurrentCulture);
        if (encode <= 0)
            return 100.ToString(CultureInfo.CurrentCulture);
        // 千分比向下取整；浮点误差（如 52.3 算成 52.2999…）用很小的容差修正，再限定在 (0, 1000) 内。
        var tenths = Math.Min(999, Math.Floor(copy / total * 1000 + 1e-9));
        return tenths < 1 ? "<" + 0.1.ToString("0.0", CultureInfo.CurrentCulture)
            : (tenths / 10).ToString("0.#", CultureInfo.CurrentCulture);
    }

    // 每个任务开始时设置文件名；切换任务即清除上一任务的计划，非帧精确任务不显示计划条。
    partial void OnCurrentFileNameChanged(string value) => FrameAccuratePlan = null;

    internal void ApplyFrameAccurateProgress(FrameAccurateExportProgress value)
    {
        if (!ReferenceEquals(FrameAccuratePlan, value.Plan))
            FrameAccuratePlan = value.Plan;
        FrameAccurateTotal = value.TotalSeconds;
        var strings = LocalizationManager.Instance;
        var phase = value.Phase switch
        {
            FrameAccuratePhase.Preparing => strings.String_Output_FrameAccurate_Preparing,
            FrameAccuratePhase.EncodingHead => strings.String_Output_FrameAccurate_EncodingHead,
            FrameAccuratePhase.Copying => strings.String_Output_FrameAccurate_Copying,
            FrameAccuratePhase.EncodingTail => strings.String_Output_FrameAccurate_EncodingTail,
            _ => strings.String_Output_FrameAccurate_Finishing
        };
        FrameAccuratePhaseText = value.SegmentCount > 1 && value.Phase != FrameAccuratePhase.Finishing
            ? string.Format(strings.String_Output_FrameAccurate_Segment, value.Segment + 1, value.SegmentCount, phase)
            : phase;
        FrameAccurateStatsText = string.Format(strings.String_Output_FrameAccurate_Stats, value.EncodedFrames, value.CopiedPackets);
        // 位置最后更新：观察者看到位置变化时，阶段与统计已是同一次报告的值。
        FrameAccuratePosition = value.Position;
    }
}
