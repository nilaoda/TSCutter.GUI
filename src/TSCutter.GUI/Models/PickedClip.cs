using System;
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
    private long _startPosition = 0;
    
    [ObservableProperty]
    private long _endPosition = -1;

    [ObservableProperty]
    private string? _outputPath;
    
    public required FileInfo FilePath { get; init; }

    public string StartTimeStr => CommonUtil.FormatSeconds(StartTime);
    public string EndTimeStr => EndTime < 0 ? "Inf." : CommonUtil.FormatSeconds(EndTime);
}