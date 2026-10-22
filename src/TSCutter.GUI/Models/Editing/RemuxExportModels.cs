using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using FFmpeg.AutoGen.Abstractions;
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

    public RemuxExportSettings Snapshot() => new()
    {
        Container = Container, ReferenceVideoIndex = ReferenceVideoIndex,
        TimelineOriginSeconds = TimelineOriginSeconds, StreamIndices = [.. StreamIndices],
        FastSubtitleSeek = FastSubtitleSeek
    };

    [JsonIgnore]
    public bool IsValid => Container is "mp4" or "mkv" && ReferenceVideoIndex >= 0 &&
        double.IsFinite(TimelineOriginSeconds) && StreamIndices is { Count: > 0 and <= 256 } &&
        StreamIndices.TrueForAll(index => index >= 0) &&
        new HashSet<int>(StreamIndices).Count == StreamIndices.Count && StreamIndices.Contains(ReferenceVideoIndex);
}

internal sealed record RemuxExportSegment(string SourcePath, double StartSeconds, double? EndSeconds,
    RemuxExportSettings Settings);

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
    InvalidBoundary, UnsafeGop, SubtitleTiming, MissingTimestamp, NonMonotonicTimestamp, SourceChanged, NoPackets
}

internal sealed class RemuxExportException(RemuxExportError code, string detail = "") : Exception(detail)
{
    public RemuxExportError Code { get; } = code;
}
