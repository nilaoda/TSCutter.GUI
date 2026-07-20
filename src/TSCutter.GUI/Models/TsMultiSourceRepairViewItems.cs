using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Models;

public sealed partial class TsRepairSourceItem(string filePath) : ObservableObject
{
    public string FilePath { get; } = filePath;
    public string FileName => Path.GetFileName(FilePath);
    public string FileSizeText => File.Exists(FilePath)
        ? CommonUtil.FormatFileSize(new FileInfo(FilePath).Length)
        : "-";

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private int _errorCount;
}

public sealed partial class TsRepairTrackItem : ObservableObject
{
    public required TsRepairTrackAnalysis Analysis { get; init; }
    public required string PidText { get; init; }
    public required string ProgramText { get; init; }
    [ObservableProperty]
    private string _streamText = string.Empty;

    [ObservableProperty]
    private string _repairSourcesText = string.Empty;
    public int GapCount => Analysis.IssueCount;
    public int RepairableGapCount => Analysis.RepairableIssueCount;

    [ObservableProperty]
    private bool _isSelected = true;

    public event Action? SelectionChanged;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();
}
