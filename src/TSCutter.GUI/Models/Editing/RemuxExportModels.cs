using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.Models.Discs;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.Models.Editing;

// 时间均相对于预览视频轨道的首个显示时间戳。
public sealed class RemuxExportSettings
{
    public string Container { get; set; } = "";
    public int ReferenceVideoIndex { get; set; }
    public double TimelineOriginSeconds { get; set; }
    public List<int> StreamIndices { get; set; } = [];
    public bool FastSubtitleSeek { get; set; } = true;
    // 光盘标题随导出任务快照保留；光盘来源仍不写入工程文件。
    [JsonIgnore]
    public DiscInput? Disc { get; set; }

    public RemuxExportSettings Snapshot() => new()
    {
        Container = Container, ReferenceVideoIndex = ReferenceVideoIndex,
        TimelineOriginSeconds = TimelineOriginSeconds, StreamIndices = [.. StreamIndices],
        FastSubtitleSeek = FastSubtitleSeek, Disc = Disc
    };

    [JsonIgnore]
    public bool IsValid => (Container is "mp4" or "mkv" ||
        Disc is { Kind: DiscKind.Dvd } && Container == "vob" ||
        Disc is { Kind: DiscKind.BluRay } && Container == "m2ts") && ReferenceVideoIndex >= 0 &&
        double.IsFinite(TimelineOriginSeconds) && StreamIndices is { Count: > 0 and <= 256 } &&
        StreamIndices.TrueForAll(index => index >= 0) &&
        new HashSet<int>(StreamIndices).Count == StreamIndices.Count && StreamIndices.Contains(ReferenceVideoIndex);
}

// EndSeconds 为包含式终点关键帧；null 表示保留到文件末尾。
/// <param name="EndExclusive">止于终点关键帧之前：该关键帧不写入，音频、字幕在它的显示时间截止（删除模式的保留段）。</param>
/// <param name="StartSignature">起点关键帧压缩包的签名文本；DVD 按签名定位，找不到或为 null 时按时间定位。</param>
/// <param name="EndSignature">终点关键帧压缩包的签名文本，用法同上。</param>
internal sealed record RemuxExportSegment(string SourcePath, double StartSeconds, double? EndSeconds,
    RemuxExportSettings Settings, bool EndExclusive = false, string? StartSignature = null, string? EndSignature = null);

internal sealed record RemuxExportRequest(string OutputPath, IReadOnlyList<RemuxExportSegment> Segments);
internal readonly record struct RemuxExportProgress(double Percent, double BytesPerSecond, double RemainingSeconds);
internal sealed record RemuxExportTrack(int Index, string Codec, AVMediaType Type, string Language, string Title, int Disposition)
{
    public string Description
    {
        get
        {
            var strings = LocalizationManager.Instance;
            var kind = Type switch
            {
                AVMediaType.AVMEDIA_TYPE_VIDEO when (Disposition & 1024) != 0 => strings.String_RemuxExport_Cover,
                AVMediaType.AVMEDIA_TYPE_VIDEO => strings.String_TsRemux_Role_Video,
                AVMediaType.AVMEDIA_TYPE_AUDIO => strings.String_TsRemux_Role_Audio,
                AVMediaType.AVMEDIA_TYPE_SUBTITLE => strings.String_TsRemux_Role_Subtitle,
                AVMediaType.AVMEDIA_TYPE_ATTACHMENT => strings.String_RemuxExport_Attachment,
                _ => strings.String_RemuxExport_Data
            };
            return $"#{Index} · {kind} · {Codec} {Language} {Title}".TrimEnd() +
                ((Disposition & 1) != 0 ? " · " + strings.String_RemuxExport_Default : "") +
                ((Disposition & 64) != 0 ? " · " + strings.String_RemuxExport_Forced : "");
        }
    }
}

internal enum RemuxExportError
{
    InvalidRequest, SameFile, UnsupportedContainer, UnsupportedTrack, IncompatibleStreams,
    InvalidBoundary, UnsafeGop, UnsafeDiscBoundary, SubtitleTiming, MissingTimestamp, NonMonotonicTimestamp, SourceChanged, NoPackets
}

internal sealed class RemuxExportException(RemuxExportError code, string detail = "") : Exception(detail)
{
    public RemuxExportError Code { get; } = code;
}
