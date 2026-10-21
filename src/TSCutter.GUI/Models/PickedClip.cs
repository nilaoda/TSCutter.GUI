using System;
using System.IO;
using System.Threading;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Models;

public enum ClipExportStatus
{
    Pending,
    Exporting,
    Done,
    Failed,
    Cancelled
}

public sealed record MergedClipOutput(string GroupId, int ClipCount);

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

    public bool RequiresRemux { get; init; }

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
    
    public long StartPts {get; set;}
    public long EndPts {get; set;}
    
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
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasExportStatus))]
    private ClipExportStatus _exportStatus = ClipExportStatus.Pending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private double _exportPercent;

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
    public string ExportSizeStr => MergedOutput is not null && OutputFileInfo is { Exists: true }
        ? LocalizationManager.Instance.String_Clips_Merged
        : LocalizationManager.Instance.String_SizePrefix
          + (OutputFileSizeStr ?? LocalizationManager.Instance.String_PreviewTrack_SizeAfterExport);
    public string? EstimatedSizeStr => MergedOutput is not null && OutputFileInfo is { Exists: true }
        ? ExportSizeStr : LocalizationManager.Instance.String_SizePrefix
        + (OutputFileSizeStr ?? (RequiresRemux ? LocalizationManager.Instance.String_PreviewTrack_SizeAfterExport : CommonUtil.FormatFileSize(
            Math.Max(0, (EndPosition > 0 ? EndPosition : InFileInfo.Length) - StartPosition)
        )));

    public bool HasExportStatus => ExportStatus != ClipExportStatus.Pending;

    public string StatusText => ExportStatus switch
    {
        ClipExportStatus.Pending => "---",
        ClipExportStatus.Exporting => $"{ExportPercent:0.0}%",
        ClipExportStatus.Done => LocalizationManager.Instance.String_Clips_Done,
        ClipExportStatus.Failed => LocalizationManager.Instance.String_Clips_Failed,
        ClipExportStatus.Cancelled => LocalizationManager.Instance.String_Clips_Cancelled,
        _ => "---"
    };

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(EstimatedSizeStr));
        OnPropertyChanged(nameof(ExportSizeStr));
        OnPropertyChanged(nameof(StatusText));
    }

    // 已导出的文件仅对应当时的剪辑边界和轨道设置；修改后清除记录，不删除磁盘上的文件。
    internal void InvalidateExport()
    {
        OutputFileInfo = null;
        MergedOutput = null;
        ExportStatus = ClipExportStatus.Pending;
        ExportPercent = 0;
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
