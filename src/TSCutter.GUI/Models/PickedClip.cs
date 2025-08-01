﻿using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Models;

public partial class PickedClip : ObservableObject
{
    public string ClipID { get; } = Guid.NewGuid().ToString();
    
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
    [NotifyPropertyChangedFor(nameof(OutputFileSizeStr), nameof(EstimatedSizeStr))]
    private FileInfo? _outputFileInfo;
    
    public required FileInfo InFileInfo { get; init; }

    public string StartTimeStr => CommonUtil.FormatSeconds(StartTime);
    public string? OutputFileSizeStr => OutputFileInfo == null ? null : CommonUtil.FormatFileSize(OutputFileInfo.Length);
    public string EndTimeStr => EndTime < 0 ? "Inf." : CommonUtil.FormatSeconds(EndTime);
    public string? EstimatedSizeStr => "Size: " + CommonUtil.FormatFileSize(
        (EndPosition > 0 ? EndPosition : InFileInfo.Length) - StartPosition
    );
}