using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Models;

public sealed partial class TsBinaryMergeFileItem(
    string filePath,
    long fileSize) : ObservableObject
{
    public string FilePath { get; } = filePath;
    public string FileName => Path.GetFileName(FilePath);
    public long FileSize { get; } = fileSize;
    public string FileSizeText => CommonUtil.FormatFileSize(FileSize);

    [ObservableProperty]
    private int _order;

    [ObservableProperty]
    private string _overlapText = "-";

    [ObservableProperty]
    private string _writeRangeText = "-";

    [ObservableProperty]
    private string _statusText = string.Empty;
}
