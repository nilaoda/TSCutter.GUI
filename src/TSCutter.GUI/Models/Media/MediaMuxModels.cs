using System;
using System.Collections.Generic;
using FFmpeg.AutoGen.Abstractions;

namespace TSCutter.GUI.Models.Media;

public sealed record MediaMuxStream(int Index, AVMediaType Type, AVCodecID CodecId, string Codec,
    string Parameters, string Language, string Title, int Disposition, IReadOnlyList<string> Containers)
{
    public IReadOnlyDictionary<string, AVCodecID> SubtitleConversions { get; init; } = new Dictionary<string, AVCodecID>();
    public bool IsAttachment => Type == AVMediaType.AVMEDIA_TYPE_ATTACHMENT;
    public bool IsCover => (Disposition & 1024) != 0;
    public bool IsTimed => !IsAttachment && !IsCover;
}

public sealed record MediaMuxSource(string Path, long Size, DateTime Modified, string Format,
    double Duration, IReadOnlyList<MediaMuxStream> Streams)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

public sealed record MediaMuxTrack(MediaMuxSource Source, MediaMuxStream Stream, string Language,
    string Title, decimal OffsetMilliseconds, bool Default, bool Forced);

public sealed record MediaMuxRequest(string OutputPath, string Container, IReadOnlyList<MediaMuxTrack> Tracks,
    bool Shortest = false, bool ShortestIncludesSubtitles = false, bool FastStart = false);
public readonly record struct MediaMuxProgress(double Percent, long Bytes, double BytesPerSecond);

public enum MediaMuxError
{
    InvalidRequest, SameFile, SourceChanged, UnsupportedTrack, MissingTimestamp, NonMonotonicTimestamp, NoPackets
}

public sealed class MediaMuxException(MediaMuxError code, string detail = "") : Exception(detail)
{
    public MediaMuxError Code { get; } = code;
}

public enum MediaMuxOperation
{
    OpenInput, PrepareTracks, CreateOutput, WriteHeader, WritePackets, ConvertSubtitle, FinalizeOutput
}

public sealed class MediaMuxFailureException(MediaMuxOperation operation, string container, string track,
    string diagnostic, Exception cause) : Exception(cause.Message, cause)
{
    public MediaMuxOperation Operation { get; } = operation;
    public string Container { get; } = container;
    public string Track { get; } = track;
    public string Diagnostic { get; } = diagnostic;
}
