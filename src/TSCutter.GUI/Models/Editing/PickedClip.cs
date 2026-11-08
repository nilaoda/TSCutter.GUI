using System;
using System.IO;
using System.Threading;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Models.TransportStream.Analysis;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.Models.Editing;

public enum ClipExportStatus
{
    Pending,
    Exporting,
    Done,
    Failed,
    Cancelled
}

/// <summary>多个片段共享的一个导出文件；Removed 表示删除模式导出（文件是删除这些范围后的视频）。</summary>
public sealed record MergedClipOutput(string GroupId, int ClipCount, bool Removed = false);

public partial class PickedClip : ObservableObject, IDisposable
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string _name = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes))]
    private string _notes = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabelBrush), nameof(HasColor))]
    private string _color = "";
    /// <summary>删除模式：片段是要从视频中删除的范围，卡片用删除颜色显示。由主窗口按当前模式同步。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedSizeStr), nameof(ExportSizeStr))]
    private bool _isRemoval;

    public bool RequiresRemux { get; init; }

    /// <summary>边界在帧精确模式下标记，可能不是关键帧，只能按帧精确方式导出。</summary>
    public bool RequiresFrameAccurate { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedSizeStr))]
    private bool _usesFrameAccurateExport;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? $"#{ClipID}" : $"#{ClipID} · {Name}";
    public bool HasNotes => !string.IsNullOrWhiteSpace(Notes);
    public bool HasColor => !string.IsNullOrEmpty(Color);
    public Avalonia.Media.IBrush LabelBrush => ClipLabels.Brush(Color);

    private static long _idCounter = 0;
    public long ClipID { get; }

    public PickedClip()
    {
        ClipID = Interlocked.Increment(ref _idCounter);
    }

    // 撤销/重做恢复片段时保留原 ID，避免选中状态和时间轴映射发生变化。
    internal PickedClip(long clipId)
    {
        ClipID = clipId;
    }
    
    /// <summary>
    /// 起点、终点关键帧压缩包的签名文本（见 PacketEndSignature）。DVD 跳转后的时间戳并不稳定，
    /// 导出按签名找到标记时看到的那一帧；其他来源或旧工程为 null，按时间定位。
    /// </summary>
    public string? StartSignature { get; set; }
    public string? EndSignature { get; set; }
    public long StartPts {get; set;}
    public long EndPts {get; set;}
    /// <summary>
    /// 终点画面所在包的字节位置。<see cref="EndPosition"/> 是保留片段的结束字节（含终点画面）；
    /// 删除模式下终点是恢复播放的画面，TS 关键帧剪辑从这个位置开始保留。未记录时为 -1。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedSizeStr))]
    private long _endFramePosition = -1;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartBoundaryTimeStr))]
    private double _startTime = 0;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndBoundaryTimeStr))]
    private double _endTime = -1.0;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedSizeStr))]
    private long _startPosition = 0;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedSizeStr))]
    private long _endPosition = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputFileSizeStr), nameof(EstimatedSizeStr), nameof(ExportSizeStr), nameof(OutputFilePathStr))]
    private FileInfo? _outputFileInfo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedSizeStr), nameof(ExportSizeStr))]
    private MergedClipOutput? _mergedOutput;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasExportStatus), nameof(HasExportCheck), nameof(HasExportWarning), nameof(HasExportError))]
    private ClipExportStatus _exportStatus = ClipExportStatus.Pending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private double _exportPercent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ExportCheckTooltip), nameof(HasExportCheck), nameof(HasExportWarning), nameof(HasExportError))]
    private ClipExportCheck? _exportCheck;

    public bool HasExportCheck => ExportStatus == ClipExportStatus.Done && ExportCheck is not null;

    public bool HasExportWarning => ExportStatus == ClipExportStatus.Done && ExportCheck is
    {
        CheckApplicable: true, CheckEnabled: true,
        CheckState: TsExportCheckState.Completed, Verdict: TsCheckVerdict.Warning
    };

    public bool HasExportError => ExportStatus == ClipExportStatus.Failed ||
        ExportStatus == ClipExportStatus.Done && ExportCheck is { CheckApplicable: true, CheckEnabled: true } check &&
        (check.CheckState == TsExportCheckState.Failed ||
         check is { CheckState: TsExportCheckState.Completed, Verdict: TsCheckVerdict.Error });

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isActive;

    private Bitmap? _startThumbnail;
    private Bitmap? _endThumbnail;

    public Bitmap? StartThumbnail => _startThumbnail;
    public Bitmap? EndThumbnail => _endThumbnail;
    public bool HasStartThumbnail => _startThumbnail is not null;
    public bool HasEndThumbnail => _endThumbnail is not null;

    public required FileInfo InFileInfo { get; init; }

    public string StartBoundaryTimeStr => $"[ {CommonUtil.FormatSeconds(StartTime)}";
    public string? OutputFileSizeStr => OutputFileInfo is { Exists: true } file ? CommonUtil.FormatFileSize(file.Length) : null;
    public string? OutputFilePathStr => OutputFileInfo?.FullName;
    public string EndBoundaryTimeStr => $"{(EndTime < 0 ? "Inf." : CommonUtil.FormatSeconds(EndTime))} ]";
    /// <summary>删除范围 [起点, 终点画面) 在源文件中的字节数；删除到文件结尾或旧工程未记录终点画面位置时按片段范围计算。</summary>
    private long RemovedBytes => Math.Max(0, (EndPosition > 0 ? EndFramePosition >= 0 ? EndFramePosition : EndPosition
        : InFileInfo.Length) - StartPosition);

    // 删除模式的片段显示删除的源字节数（TS 按字节剪辑时与输出减少的大小一致；智能剪辑重新编码首尾画面，带“~”）。
    // 需要重新封装或只导出所选节目时，删除的字节数与输出大小无关，显示不适用；导出后的总大小由主界面汇总显示。
    private string RemovalSizeStr => LocalizationManager.Instance.String_RemoveMode_SizePrefix
        + (RequiresRemux || UsesFrameAccurateExport && !CanEstimateFrameAccurateSize
            ? LocalizationManager.Instance.String_RemoveMode_SizeNotApplicable
            : UsesFrameAccurateExport
                ? string.Format(LocalizationManager.Instance.String_SizeApproximate, CommonUtil.FormatFileSize(RemovedBytes))
                : CommonUtil.FormatFileSize(RemovedBytes));

    private string RemovalExportSizeStr => LocalizationManager.Instance.String_RemoveMode_SizePrefix
                                           + LocalizationManager.Instance.String_RemoveMode_SizeNotApplicable;

    public string ExportSizeStr => IsRemoval ? RemovalExportSizeStr
        : MergedOutput is not null && OutputFileInfo is { Exists: true }
        ? LocalizationManager.Instance.String_Clips_Merged
        : LocalizationManager.Instance.String_SizePrefix
          + (OutputFileSizeStr ?? LocalizationManager.Instance.String_PreviewTrack_SizeAfterExport);
    /// <summary>
    /// 帧精确导出能否按源字节范围预估大小：单节目 TS 只在首尾重新编码，输出与源字节范围接近；
    /// 多节目 TS 只输出预览所在节目，MP4/MKV 等需要重新封装，都只能导出后确定。
    /// </summary>
    public bool CanEstimateFrameAccurateSize { get; init; }

    private long SourceRangeBytes => Math.Max(0, (EndPosition > 0 ? EndPosition : InFileInfo.Length) - StartPosition);

    // TS 合并后仍显示各片段的字节范围大小；共享输出的总大小由主界面汇总显示。
    // 帧精确导出前显示带“~”的预估值，导出后显示实际大小或“已合并导出”。
    public string? EstimatedSizeStr => IsRemoval ? RemovalSizeStr
        : RequiresRemux ? ExportSizeStr
        : UsesFrameAccurateExport
            ? CanEstimateFrameAccurateSize && MergedOutput is null && OutputFileSizeStr is null
                ? LocalizationManager.Instance.String_SizePrefix + string.Format(
                    LocalizationManager.Instance.String_SizeApproximate, CommonUtil.FormatFileSize(SourceRangeBytes))
                : ExportSizeStr
            : LocalizationManager.Instance.String_SizePrefix
              + ((MergedOutput is null ? OutputFileSizeStr : null) ?? CommonUtil.FormatFileSize(SourceRangeBytes));

    public bool HasExportStatus => ExportStatus != ClipExportStatus.Pending;

    public string StatusText => ExportStatus switch
    {
        ClipExportStatus.Pending => "---",
        ClipExportStatus.Exporting => $"{ExportPercent:0.0}%",
        ClipExportStatus.Done => LocalizationManager.Instance.String_Clips_Done + ExportCheckSuffix,
        ClipExportStatus.Failed => LocalizationManager.Instance.String_Clips_Failed,
        ClipExportStatus.Cancelled => LocalizationManager.Instance.String_Clips_Cancelled,
        _ => "---"
    };

    /// <summary>导出状态后追加的轻量检测结论；详细数量与原因放在 tooltip 或导出记录中。</summary>
    public string ExportCheckSuffix => ExportCheck is not { } check ? "" :
        !check.CheckApplicable ? "" :
        !check.CheckEnabled || check.CheckState is TsExportCheckState.NotStarted or TsExportCheckState.Aborted
            ? " · " + LocalizationManager.Instance.String_Export_Check_NotRun
            : check.CheckState == TsExportCheckState.Failed
                ? " · " + LocalizationManager.Instance.String_Export_Check_Failed
                : check.Verdict switch
                {
                    TsCheckVerdict.Pass => " · " + LocalizationManager.Instance.String_Export_Check_PassShort,
                    null or TsCheckVerdict.Incomplete => " · " + LocalizationManager.Instance.String_Export_Check_NotRun,
                    TsCheckVerdict.Warning => " · " + LocalizationManager.Instance.String_Export_Check_Warnings,
                    _ => " · " + LocalizationManager.Instance.String_Export_Check_Errors
                };

    public string ExportCheckTooltip
    {
        get
        {
            if (ExportCheck is not { } check)
                return "";
            var strings = LocalizationManager.Instance;
            var applicability = !check.CheckApplicable ? strings.String_Export_Check_NotApplicable
                : check.CheckEnabled ? strings.String_Export_Check_Enabled
                : strings.String_Export_Check_Disabled;
            var conclusion = !check.CheckApplicable || !check.CheckEnabled ||
                             check.CheckState is TsExportCheckState.NotStarted or TsExportCheckState.Aborted
                ? strings.String_Export_Check_NotRun
                : check.CheckState == TsExportCheckState.Failed
                    ? strings.String_Export_Check_Failed
                    : check.Verdict switch
                    {
                        TsCheckVerdict.Pass => strings.String_Export_Check_Pass,
                        null or TsCheckVerdict.Incomplete => strings.String_Export_Check_NotRun,
                        TsCheckVerdict.Warning => string.Format(strings.String_Export_Check_WarningCount, check.WarningCount),
                        _ => string.Format(strings.String_Export_Check_ErrorCount, check.ErrorCount, check.WarningCount)
                    };
            return $"{strings.String_Export_Check_Title}: {applicability}\n{conclusion}";
        }
    }

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(EstimatedSizeStr));
        OnPropertyChanged(nameof(ExportSizeStr));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ExportCheckTooltip));
    }

    // 已导出的文件仅对应当时的剪辑边界和轨道设置；修改后清除记录，不删除磁盘上的文件。
    internal void InvalidateExport()
    {
        OutputFileInfo = null;
        MergedOutput = null;
        ExportStatus = ClipExportStatus.Pending;
        ExportPercent = 0;
        ExportCheck = null;
    }

    partial void OnStartTimeChanged(double value) => InvalidateExport();
    partial void OnEndTimeChanged(double value) => InvalidateExport();
    partial void OnStartPositionChanged(long value) => InvalidateExport();
    partial void OnEndPositionChanged(long value) => InvalidateExport();

    internal void ReplaceStartThumbnail(Bitmap? thumbnail)
    {
        if (ReferenceEquals(_startThumbnail, thumbnail))
            return;

        _startThumbnail?.Dispose();
        _startThumbnail = thumbnail;
        OnPropertyChanged(nameof(StartThumbnail));
        OnPropertyChanged(nameof(HasStartThumbnail));
    }

    internal void ReplaceEndThumbnail(Bitmap? thumbnail)
    {
        if (ReferenceEquals(_endThumbnail, thumbnail))
            return;

        _endThumbnail?.Dispose();
        _endThumbnail = thumbnail;
        OnPropertyChanged(nameof(EndThumbnail));
        OnPropertyChanged(nameof(HasEndThumbnail));
    }

    public void Dispose()
    {
        ReplaceStartThumbnail(null);
        ReplaceEndThumbnail(null);
    }
}
