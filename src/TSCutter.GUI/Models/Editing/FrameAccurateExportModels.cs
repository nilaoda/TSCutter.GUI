using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TSCutter.GUI.Models.Editing;

public enum ClipCutMode
{
    KeyFrame,
    FrameAccurate
}

/// <summary>帧精确导出的轨道与容器设置；加入任务时复制快照，之后不再读取界面状态。</summary>
public sealed class FrameAccurateExportSettings
{
    public string Container { get; set; } = "";
    public int VideoIndex { get; set; }
    public List<int> StreamIndices { get; set; } = [];

    public FrameAccurateExportSettings Snapshot() => new()
    {
        Container = Container, VideoIndex = VideoIndex, StreamIndices = [.. StreamIndices]
    };

    [JsonIgnore]
    public bool IsValid => Container is "ts" or "m2ts" or "mp4" or "mkv" && VideoIndex >= 0 &&
        StreamIndices is { Count: > 0 and <= 256 } && StreamIndices.Contains(VideoIndex) &&
        StreamIndices.TrueForAll(index => index >= 0) && new HashSet<int>(StreamIndices).Count == StreamIndices.Count;
}

/// <summary>起止均为视频流原始 PTS，包含所选帧；EndPts 为 null 表示保留到文件末尾。</summary>
/// <param name="EndExclusive">终点画面本身不输出，片段止于它之前的画面（删除模式的保留段止于删除起点之前）。</param>
internal sealed record FrameAccurateExportSegment(string SourcePath, long StartPts, long? EndPts,
    FrameAccurateExportSettings Settings, bool EndExclusive = false);

internal sealed record FrameAccurateExportRequest(string OutputPath, IReadOnlyList<FrameAccurateExportSegment> Segments);

public enum FrameAccurateSupport
{
    Supported,
    Disc,
    Codec,
    Interlaced,
    PixelFormat,
    DolbyVision,
    DolbyVisionContainer,
    DynamicHdr,
    Pulldown,
    MultipleVideo,
    Encoder,
    Container,
    /// <summary>H.264 只有 P/B 条带的恢复点、没有完整关键帧（逐行刷新），复制部分无法无缝衔接。</summary>
    GradualRefresh
}

internal enum FrameAccurateExportError
{
    InvalidRequest,
    SameFile,
    Unsupported,
    UnsupportedTrack,
    FrameNotFound,
    NoRandomAccessPoint,
    ReencodeTooLong,
    IncompatibleStream,
    NonMonotonicTimestamp,
    SourceChanged,
    EncodeFailed
}

internal sealed class FrameAccurateExportException(FrameAccurateExportError code, string detail = "",
    FrameAccurateSupport support = FrameAccurateSupport.Supported) : Exception(string.IsNullOrEmpty(detail) ? code.ToString() : $"{code}: {detail}")
{
    public FrameAccurateExportError Code { get; } = code;
    public string Detail { get; } = detail;
    public FrameAccurateSupport Support { get; } = support;
}

/// <summary>导出完成后的实际处理范围，用于测试和日志记录重编码比例。</summary>
internal sealed record FrameAccurateSegmentReport(int EncodedFrames, int CopiedPackets, long? CopyStartPts, long? CopyEndPts);

/// <summary>帧精确导出当前所处的处理阶段，供导出窗口说明正在做什么。</summary>
public enum FrameAccuratePhase
{
    /// <summary>定位并解码到起点所在的关键帧，尚未写出画面。</summary>
    Preparing,
    /// <summary>重新编码起点到首个可直接复制的关键帧之间的画面。</summary>
    EncodingHead,
    /// <summary>直接复制原始压缩数据。</summary>
    Copying,
    /// <summary>重新编码最后一个可复制关键帧到终点之间的画面。</summary>
    EncodingTail,
    /// <summary>写入其余轨道、文件尾并校验来源。</summary>
    Finishing
}

public enum FrameAccuratePartKind { Encode, Copy }

/// <summary>导出计划中的一段：输出时间轴上的起点与时长（秒），以及它属于第几个片段。</summary>
public sealed record FrameAccuratePlanPart(int Segment, FrameAccuratePartKind Kind, double Start, double Duration);

/// <summary>
/// 帧精确导出进度：除总体百分比外，附带整份导出计划（各片段的重编码/复制分布）、当前阶段与输出时间轴上的位置，
/// 以及已重新编码的画面数和已复制的视频包数（各片段累计）。
/// </summary>
internal sealed record FrameAccurateExportProgress(double Percent, double BytesPerSecond, double RemainingSeconds,
    IReadOnlyList<FrameAccuratePlanPart> Plan, double TotalSeconds, int Segment, int SegmentCount,
    FrameAccuratePhase Phase, double Position, int EncodedFrames, long CopiedPackets);
