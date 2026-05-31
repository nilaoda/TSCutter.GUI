using System;
using System.IO;
using System.Threading;
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

public partial class PickedClip : ObservableObject
{
    private static long _idCounter = 0;
    public long ClipID { get; } = Interlocked.Increment(ref _idCounter);
    
    public long StartPts {get; set;}
    public long EndPts {get; set;}
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartTimeStr))]
    private double _startTime = 0;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndTimeStr))]
    private double _endTime = -1.0;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedSizeStr))]
    private long _startPosition = 0;
    
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EstimatedSizeStr))]
    private long _endPosition = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputFileSizeStr), nameof(EstimatedSizeStr), nameof(OutputFilePathStr))]
    private FileInfo? _outputFileInfo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private ClipExportStatus _exportStatus = ClipExportStatus.Pending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private double _exportPercent;

    [ObservableProperty]
    private bool _isSelected;

    public required FileInfo InFileInfo { get; init; }

    public string StartTimeStr => CommonUtil.FormatSeconds(StartTime);
    public string? OutputFileSizeStr => OutputFileInfo == null ? null : CommonUtil.FormatFileSize(OutputFileInfo.Length);
    public string? OutputFilePathStr => OutputFileInfo?.FullName;
    public string EndTimeStr => EndTime < 0 ? "Inf." : CommonUtil.FormatSeconds(EndTime);
    public string? EstimatedSizeStr => LocalizationManager.Instance.String_SizePrefix
        + CommonUtil.FormatFileSize(
            Math.Max(0, (EndPosition > 0 ? EndPosition : InFileInfo.Length) - StartPosition)
        );

    public string StatusText => ExportStatus switch
    {
        ClipExportStatus.Pending => "---",
        ClipExportStatus.Exporting => $"{ExportPercent:0.0}%",
        ClipExportStatus.Done => LocalizationManager.Instance.String_Clips_Done,
        ClipExportStatus.Failed => LocalizationManager.Instance.String_Clips_Failed,
        ClipExportStatus.Cancelled => LocalizationManager.Instance.String_Clips_Cancelled,
        _ => "---"
    };
}