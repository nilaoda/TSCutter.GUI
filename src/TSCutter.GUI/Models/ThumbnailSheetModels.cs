using System;
using System.Collections.Generic;

namespace TSCutter.GUI.Models;

/// <summary>
/// 缩略图总览的取帧策略。
/// </summary>
public enum ThumbnailSheetSampling
{
    /// <summary>在整段时长上均匀分布时间点，最直观，适合快速浏览。</summary>
    Uniform,

    /// <summary>只落在关键帧上，抽帧最快，且跳转回视频时定位最准。</summary>
    KeyFrame,

    /// <summary>按固定间隔（秒）取帧，间隔超过时长时自动退化为一帧。</summary>
    Interval
}

/// <summary>视频扫描方式；无法从流参数可靠判断时保持 Unknown。</summary>
public enum VideoScanMode
{
    Unknown,
    Progressive,
    Interlaced
}

/// <summary>缩略图总览中轨道的编码及可选语言标签。</summary>
public sealed record ThumbnailSheetTrackInfo(string Codec, string? Language = null);

/// <summary>可为其中一段文字指定强调色的表头行。</summary>
public sealed record ThumbnailSheetHeaderLine(
    string Text,
    int AccentStart = -1,
    int AccentLength = 0);

/// <summary>
/// 缩略图总览表头所需的结构化媒体信息。
/// 与 <see cref="Utils.MediaInfoBuilder"/> 的区别：后者输出的是对齐好的英文长文本，
/// 这里保留字段语义，便于按当前语言绘制表头。
/// </summary>
public sealed record ThumbnailSheetInfo
{
    public string FileName { get; init; } = string.Empty;

    /// <summary>容器封装格式，例如 mpegts。</summary>
    public string Format { get; init; } = string.Empty;

    public long FileSize { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>整体码率，单位 bit/s；未知时为 0。</summary>
    public long OverallBitRate { get; init; }

    public string? VideoCodec { get; init; }

    public int VideoWidth { get; init; }

    public int VideoHeight { get; init; }

    public VideoScanMode VideoScanMode { get; init; }

    public VideoDynamicRange VideoDynamicRange { get; init; }

    /// <summary>应用像素宽高比后的显示宽高比（宽 / 高）。未知时为 0。</summary>
    public double VideoDisplayAspectRatio { get; init; }

    public double VideoFrameRate { get; init; }

    public long VideoBitRate { get; init; }

    public string? AudioCodec { get; init; }

    public string? AudioLanguage { get; init; }

    public int AudioChannels { get; init; }

    public int AudioSampleRate { get; init; }

    public long AudioBitRate { get; init; }

    /// <summary>除首条视频流之外的其他视频轨道编码。</summary>
    public IReadOnlyList<string> AdditionalVideoCodecs { get; init; } = [];

    /// <summary>除首条音频流之外的其他音频轨道。</summary>
    public IReadOnlyList<ThumbnailSheetTrackInfo> AdditionalAudioTracks { get; init; } = [];

    /// <summary>所有字幕轨道。</summary>
    public IReadOnlyList<ThumbnailSheetTrackInfo> SubtitleTracks { get; init; } = [];

    public bool HasVideo => VideoCodec is not null;

    public static ThumbnailSheetInfo Empty { get; } = new();
}

/// <summary>
/// 缩略图总览里单个格子的状态。
/// </summary>
public enum ThumbnailSheetCellState
{
    Pending,
    Loading,
    Ready,
    Failed
}

/// <summary>
/// 缩略图总览中的一个格子。持有该时间点对应的缩略图位图。
/// 生命周期与 <see cref="ViewModels.ThumbnailSheetWindowViewModel"/> 绑定，
/// 重新生成整张图时统一 Dispose。
/// </summary>
public sealed class ThumbnailSheetCell : IDisposable
{
    public ThumbnailSheetCell(int index, TimeSpan timestamp)
    {
        Index = index;
        Timestamp = timestamp;
    }

    /// <summary>从 0 开始的格子序号，用于显示 "1/30" 之类的角标。</summary>
    public int Index { get; }

    public TimeSpan Timestamp { get; }

    public ThumbnailSheetCellState State { get; internal set; } = ThumbnailSheetCellState.Pending;

    public Avalonia.Media.Imaging.Bitmap? Thumbnail { get; internal set; }

    /// <summary>时间码文本，由 ViewModel 在取帧后填充，保持与表格显示一致。</summary>
    public string TimestampText { get; internal set; } = string.Empty;

    public void Dispose()
    {
        Thumbnail?.Dispose();
        Thumbnail = null;
        State = ThumbnailSheetCellState.Pending;
    }
}
