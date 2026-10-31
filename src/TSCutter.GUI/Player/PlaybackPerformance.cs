using System;
using System.Collections.Generic;

namespace TSCutter.GUI.Player;

internal enum PlaybackFluency
{
    Unknown,
    Smooth,
    Limited,
    Slow
}

internal readonly record struct PlaybackPerformance
{
    public PlaybackPerformance(double decodeFps, double presentedFps, double sourceFps,
        double frameRateTolerance = 0, long droppedFrames = 0)
    {
        // 显示和判色使用同一精度，避免 49.99 显示为 50.0 却被判为未达 50 FPS。
        DecodeFps = Math.Round(decodeFps, 1);
        PresentedFps = Math.Round(presentedFps, 1);
        SourceFps = Math.Round(sourceFps, 1);
        FrameRateTolerance = frameRateTolerance;
        DroppedFrames = droppedFrames;
    }

    public double DecodeFps { get; }
    public double PresentedFps { get; }
    public double SourceFps { get; }
    // 无丢帧且达到片源速度时消除统计窗口的一帧量化抖动；其余情况显示实测速率。
    public double PlaybackFps => Fluency == PlaybackFluency.Smooth ? SourceFps
        : SourceFps > 0 ? Math.Min(PresentedFps, SourceFps) : PresentedFps;
    public long DroppedFrames { get; }
    private double FrameRateTolerance { get; }

    // 同时参考显示速度，避免解码足够快、UI 丢帧时仍显示流畅。
    public PlaybackFluency Fluency => SourceFps <= 0 ? PlaybackFluency.Unknown
        : DroppedFrames == 0 && Math.Min(DecodeFps, PresentedFps) + FrameRateTolerance >= SourceFps ? PlaybackFluency.Smooth
        : Math.Min(DecodeFps, PresentedFps) / SourceFps >= 0.7 ? PlaybackFluency.Limited
        : PlaybackFluency.Slow;
}

/// <summary>以实际经过的时间统计约两秒内的吞吐；不把起播预滚计入 FPS。</summary>
internal sealed class PlaybackPerformanceCounter
{
    private readonly List<Sample> samples = [];
    private readonly record struct Sample(double Seconds, long Decoded, long Presented, long Dropped);

    public void Reset(double seconds, long decoded, long presented, long dropped = 0)
    {
        samples.Clear();
        samples.Add(new(seconds, decoded, presented, dropped));
    }

    public PlaybackPerformance? Measure(double seconds, long decoded, long presented, double sourceFps, long dropped = 0)
    {
        if (samples.Count == 0)
        {
            Reset(seconds, decoded, presented, dropped);
            return null;
        }
        samples.Add(new(seconds, decoded, presented, dropped));
        // 保留跨越窗口起点的样本；UI 长时间繁忙时仍能统计整段停顿。
        while (samples.Count > 2 && seconds - samples[1].Seconds >= 2)
            samples.RemoveAt(0);
        var first = samples[0];
        var elapsed = seconds - first.Seconds;
        if (elapsed < 1)
            return null;
        // 有限窗口的帧计数有最多一帧的量化误差；真正丢帧单独计数，不能用误差掩盖。
        return new(Math.Max(0, decoded - first.Decoded) / elapsed,
            Math.Max(0, presented - first.Presented) / elapsed, sourceFps,
            frameRateTolerance: 1 / elapsed, droppedFrames: Math.Max(0, dropped - first.Dropped));
    }
}
