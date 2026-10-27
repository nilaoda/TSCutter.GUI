using System;
using System.Collections.Generic;

namespace TSCutter.GUI.Models.Discs;

public enum DiscKind
{
    BluRay,
    Dvd
}

/// <summary>
/// 光盘来源中一个可播放的逻辑标题：蓝光为播放列表（MPLS 编号），DVD 为 tt_srpt 标题号（1 起）。
/// </summary>
public sealed record DiscTitleInfo(
    int Number,
    TimeSpan Duration,
    int ChapterCount,
    int AngleCount,
    int ClipCount,
    string? VideoSpec,
    IReadOnlyList<string> AudioLanguages,
    IReadOnlyList<string> SubtitleLanguages,
    bool Recommended)
{
    public IReadOnlyList<DiscChapter> Chapters { get; init; } = [];
}

public sealed record DiscChapter(TimeSpan Start, string? Name = null);

/// <summary>
/// 光盘扫描结果与打开失败的原因分类，供界面区分受保护、损坏、结构不支持与运行库缺失。
/// </summary>
public enum DiscFailureKind
{
    NotDiscOrCorrupt,
    ProtectedDisc,
    StructureUnsupported,
    RuntimeMissing
}

public sealed class DiscScanException(DiscFailureKind kind, string detail) : Exception(detail)
{
    public DiscFailureKind Kind { get; } = kind;
}

/// <summary>FFmpeg 打开光盘标题失败（协议层会记录 AACS/BD+/播放列表等具体原因）。</summary>
public sealed class DiscOpenException(string message) : Exception(message)
{
    public static DiscOpenException FromErrorCode(int code)
    {
        var inner = FFmpeg.FFmpegException.FromErrorCode(code);
        return new DiscOpenException(inner.Message);
    }
}

/// <summary>
/// 播放列表中一个片段（PlayItem）在原始时间戳域的区间，以及它在标题相对时间轴上的起点。
/// 描述各片段的原始 PTS，读取层按此表统一包的时间戳；标题内字节区间用于
/// 片段定位（原始 PTS 在重复引用同一文件时无法区分片段）。DVD 由 dvdvideo
/// 解复用器在原生层重写为连续时间轴，因此为空表。
/// </summary>
public sealed record DiscSegment(
    long InPts90k,
    long OutPts90k,
    long RelativeStart90k,
    long TitleStartByte = -1,
    long TitleEndByte = -1);

/// <summary>
/// 光盘路径（镜像或目录）、所选标题与读取选项的统一表示；预览、字幕、缩略图等读取器据此以相同参数重新打开。
/// </summary>
public sealed record DiscInput(
    DiscKind Kind,
    string SourcePath,
    int TitleNumber,
    int Angle,
    TimeSpan Duration,
    IReadOnlyList<DiscSegment> Segments)
{
    public string OpenUrl => SourcePath;

    public string? InputFormatName => Kind == DiscKind.BluRay ? null : "dvdvideo";

    public IReadOnlyList<(string Key, string Value)> OpenOptions =>
        [("title", TitleNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)),
         ("angle", Angle.ToString(System.Globalization.CultureInfo.InvariantCulture))];

    public DiscTimeline CreateTimeline() => new(Segments);
}

/// <summary>
/// 光盘片段的定位与连续性判定。
/// 原始包的时间戳平移由蓝光读取层完成；此处只按字节位置或标题时间定位片段。
/// </summary>
public sealed class DiscTimeline
{
    private readonly DiscSegment[] segments;

    public DiscTimeline(IReadOnlyList<DiscSegment> segments)
    {
        this.segments = [.. segments];
    }

    /// <summary>按标题内字节位置定位片段；字节区间由播放列表的包数累加得到，互不重叠。</summary>
    public DiscSegment? FindByByte(long titleBytePosition)
    {
        foreach (var candidate in segments)
        {
            if (candidate.TitleStartByte < 0 || candidate.TitleEndByte <= candidate.TitleStartByte)
                continue;
            if (titleBytePosition >= candidate.TitleStartByte && titleBytePosition < candidate.TitleEndByte)
                return candidate;
        }
        return null;
    }

    // 相邻片段的原始时间与标题时间均连续时保留字幕对象状态；发生跳变或
    // 重复引用时，旧状态必须在当前播放项的出点终止。
    public bool Continues(DiscSegment? previous, DiscSegment next)
    {
        var index = previous is null ? -1 : Array.IndexOf(segments, previous);
        return index >= 0 && index + 1 < segments.Length && ReferenceEquals(segments[index + 1], next) &&
            previous!.OutPts90k == next.InPts90k &&
            previous.RelativeStart90k + previous.OutPts90k - previous.InPts90k == next.RelativeStart90k;
    }

    public long ContinuousEnd90k(DiscSegment segment)
    {
        var index = Array.IndexOf(segments, segment);
        while (index >= 0 && index + 1 < segments.Length && Continues(segments[index], segments[index + 1]))
            segment = segments[++index];
        return segment.RelativeStart90k + segment.OutPts90k - segment.InPts90k;
    }

    /// <summary>按标题相对时间（90kHz）定位片段，用于把相对时间换算为原始 PTS。</summary>
    public DiscSegment? FindByRelativeTime(long relative90k)
    {
        if (segments.Length == 0)
            return null;
        foreach (var candidate in segments)
        {
            var end = candidate.RelativeStart90k + (candidate.OutPts90k - candidate.InPts90k);
            if (relative90k < end)
                return candidate;
        }
        return segments[^1];
    }

}
