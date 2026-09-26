namespace TSCutter.GUI.Models;

/// <summary>缩略图图集窗口上次使用的选项。</summary>
public sealed record ThumbnailSheetPreferences
{
    public int Columns { get; set; } = 4;
    public int Rows { get; set; } = 3;
    public double OutputWidth { get; set; } = 4000;
    public ThumbnailSheetSampling Sampling { get; set; } = ThumbnailSheetSampling.Uniform;
    public double IntervalSeconds { get; set; } = 10;
    public bool ShowHeader { get; set; } = true;
    public bool ShowCaption { get; set; } = true;
    public bool ShowIndex { get; set; } = true;
    public bool IsPngFormat { get; set; } = true;
    public double JpegQuality { get; set; } = 90;
}
