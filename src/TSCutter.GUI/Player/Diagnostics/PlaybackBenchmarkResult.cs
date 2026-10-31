using System;
using System.Collections.Generic;
using Avalonia;
using TSCutter.GUI.Models.Discs;
using TSCutter.GUI.Models.Preview;

namespace TSCutter.GUI.Player.Diagnostics;

public enum PlaybackBenchmarkStage { Decode, Conversion, Playback }
public enum PlaybackBenchmarkRating { Unknown, Good, Marginal, Insufficient }

public sealed record PlaybackBenchmarkTrack(PreviewStreamSelection Selection, string Codec,
    int Width, int Height, double FrameRate, double OriginSeconds, VideoDynamicRange DynamicRange = VideoDynamicRange.Standard)
{
    public string DisplayText => $"#{Selection.StreamIndex} · {Codec} · {Width}×{Height} · {FrameRate:0.###} FPS";
}

public sealed record PlaybackBenchmarkSource(string FilePath, IReadOnlyList<PlaybackBenchmarkTrack> Tracks,
    double DurationSeconds, DiscInput? Disc);

public sealed record PlaybackBenchmarkRequest(PlaybackBenchmarkSource Source, PlaybackBenchmarkTrack Track,
    double StartSeconds, PixelSize OutputSize, bool PreferHardwareDecoding, bool EnableSdrToneMapping);

public sealed record PlaybackBenchmarkResult(PlaybackBenchmarkStage Stage, double FramesPerSecond,
    double SourceFrameRate, int FrameCount, double ElapsedSeconds, bool HardwareDecoding,
    double ReadFraction = 0, PixelSize OutputSize = default, long LateDrops = 0, long BusyDrops = 0, long Recoveries = 0, bool GpuPresentation = false, bool HardwareSurfacePresentation = false)
{
    public double RealtimeFactor => SourceFrameRate > 0 ? FramesPerSecond / SourceFrameRate : 0;
    public PlaybackBenchmarkRating Rating => SourceFrameRate <= 0 ? PlaybackBenchmarkRating.Unknown
        : FramesPerSecond + 1 / Math.Max(ElapsedSeconds, 0.001) < SourceFrameRate
            ? PlaybackBenchmarkRating.Insufficient
        : LateDrops + BusyDrops + Recoveries > 0 || Stage != PlaybackBenchmarkStage.Playback && RealtimeFactor < 1.2
            ? PlaybackBenchmarkRating.Marginal : PlaybackBenchmarkRating.Good;
}

internal sealed record PlaybackBenchmarkProgress(PlaybackBenchmarkStage Stage, double Fraction,
    PlaybackBenchmarkResult? Completed = null, double? FramesPerSecond = null);

internal sealed record PlaybackBenchmarkLimits(double DecodeSeconds = 3, double ConvertSeconds = 2,
    double PlaybackSeconds = 5, int MaxFrames = 600, double MediaSeconds = 10);

internal enum PlaybackBenchmarkFailure { TooFewFrames, InvalidTimestamps, Timeout }
internal sealed class PlaybackBenchmarkException(PlaybackBenchmarkFailure failure) : Exception(failure.ToString())
{
    public PlaybackBenchmarkFailure Failure { get; } = failure;
}
