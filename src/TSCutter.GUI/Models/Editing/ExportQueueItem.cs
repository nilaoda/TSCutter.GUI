using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.Models.Editing;

public partial class ExportQueueItem : ObservableObject
{
    public string Name { get; init; } = "";
    public string Notes { get; init; } = "";
    public string Color { get; init; } = "";
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
    public bool HasColor => !string.IsNullOrEmpty(Color);
    public Avalonia.Media.IBrush LabelBrush => ClipLabels.Brush(Color);
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? SourceDisplayName : $"{Name} · {SourceDisplayName}";

    public long QueueItemId { get; init; }

    public int ClipIndex { get; init; } = 1;

    public string GeneratedFileName { get; init; } = "";

    public RemuxExportSettings? RemuxSettings { get; init; }
    // 帧精确任务按视频原始 PTS 定位，加入队列时固定设置快照。
    public FrameAccurateExportSettings? FrameAccurate { get; init; }
    public long StartPts { get; init; }
    /// <summary>起点、终点关键帧压缩包的签名（DVD 导出按画面定位切点）；没有时为 null。</summary>
    public string? StartSignature { get; init; }
    public string? EndSignature { get; init; }
    public long? EndPts { get; init; }
    public int? ProgramId { get; init; }
    /// <summary>关键帧模式 TS 任务的音频延迟（毫秒）；封装与帧精确任务的延迟在各自设置中。</summary>
    public int AudioDelayMs { get; init; }
    /// <summary>音频延迟作用的节目号（所编辑的节目）；null 表示全部音轨。</summary>
    public int? AudioDelayProgramId { get; init; }

    public string SourceFilePath { get; init; } = "";

    public string OutputFilePath { get; init; } = "";

    public long StartPosition { get; init; }

    public long EndPosition { get; init; }

    public string SourceFileName { get; init; } = "";

    public double StartTimeSeconds { get; init; }

    public double EndTimeSeconds { get; init; }

    public long EstimatedBytes { get; init; }

    /// <summary>帧精确任务按单节目 TS 的源字节范围预估大小，显示时带“~”。</summary>
    public bool SizeIsApproximate { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private ClipExportStatus _status = ClipExportStatus.Pending;

    public string StatusText => Status switch
    {
        ClipExportStatus.Pending => "---",
        ClipExportStatus.Done => LocalizationManager.Instance.String_Clips_Done,
        ClipExportStatus.Failed => LocalizationManager.Instance.String_Clips_Failed,
        _ => "---"
    };

    public string OutputFileName => Path.GetFileName(OutputFilePath);
    public string SourceDisplayName => ProgramId is { } programId
        ? $"{SourceFileName} · {string.Format(LocalizationManager.Instance.String_PreviewTrack_ProgramFallback, programId)}"
        : SourceFileName;

    public string StartBoundaryTimeStr => $"[ {CommonUtil.FormatSeconds(StartTimeSeconds)}";

    public string EndBoundaryTimeStr => EndPosition < 0
        ? $"{LocalizationManager.Instance.String_Project_FileEnd} ]"
        : $"{CommonUtil.FormatSeconds(EndTimeSeconds)} ]";

    public string EstimatedSizeStr => LocalizationManager.Instance.String_SizePrefix
        + (ProgramId is null && RemuxSettings is null && FrameAccurate is null ? CommonUtil.FormatFileSize(Math.Max(0, EstimatedBytes))
            : FrameAccurate is not null && SizeIsApproximate
                ? string.Format(LocalizationManager.Instance.String_SizeApproximate, CommonUtil.FormatFileSize(Math.Max(0, EstimatedBytes)))
                : LocalizationManager.Instance.String_PreviewTrack_SizeAfterExport);

    public string? OutputFilePathStr => OutputFilePath;

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(SourceDisplayName));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(EstimatedSizeStr));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(EndBoundaryTimeStr));
    }
}
