using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Models;

public partial class ExportQueueItem : ObservableObject
{
    public long QueueItemId { get; init; }

    public int? ProgramId { get; init; }

    public string SourceFilePath { get; init; } = "";

    public string OutputFilePath { get; init; } = "";

    public long StartPosition { get; init; }

    public long EndPosition { get; init; }

    public string SourceFileName { get; init; } = "";

    public double StartTimeSeconds { get; init; }

    public double EndTimeSeconds { get; init; }

    public long EstimatedBytes { get; init; }

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
        + (ProgramId is null ? CommonUtil.FormatFileSize(Math.Max(0, EstimatedBytes))
            : LocalizationManager.Instance.String_PreviewTrack_SizeAfterExport);

    public string? OutputFilePathStr => OutputFilePath;

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(SourceDisplayName));
        OnPropertyChanged(nameof(EstimatedSizeStr));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(EndBoundaryTimeStr));
    }
}
