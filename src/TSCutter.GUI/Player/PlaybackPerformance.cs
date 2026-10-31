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
        DecodeFps = Math.Round(decodeFps, 1);
        PresentedFps = Math.Round(presentedFps, 1);
        SourceFps = sourceFps;
        DroppedFrames = droppedFrames;
        // 解码队列会预读并按场成对产出，短窗口吞吐不代表播放是否跟得上。
        // 片源帧率保留完整精度供显示；判色仍按吞吐统计的一位小数精度比较。
        // 容限与速率相加后再取整，避免临界值反复变色。
        Fluency = sourceFps <= 0 ? PlaybackFluency.Unknown
            : droppedFrames == 0 && Math.Round(presentedFps + frameRateTolerance, 1) >= Math.Round(sourceFps, 1) ? PlaybackFluency.Smooth
            : presentedFps / sourceFps >= 0.7 ? PlaybackFluency.Limited
            : PlaybackFluency.Slow;
    }

    public double DecodeFps { get; }
    public double PresentedFps { get; }
    public double SourceFps { get; }
    // 无丢帧且达到片源速度时消除统计窗口的一帧量化抖动；其余情况显示实测速率。
    public double PlaybackFps => Fluency == PlaybackFluency.Smooth ? SourceFps
        : SourceFps > 0 ? Math.Min(PresentedFps, SourceFps) : PresentedFps;
    public long DroppedFrames { get; }
    public PlaybackFluency Fluency { get; }
}

/// <summary>以实际经过的时间统计约两秒内的吞吐；不把起播预滚计入 FPS。</summary>
internal sealed class PlaybackPerformanceCounter
{
    private readonly List<Sample> samples = [];
    private readonly record struct Sample(double Seconds, long Decoded, long Presented, long Dropped, double PresentedAt);

    public void Reset(double seconds, long decoded, long presented, long dropped = 0, double? presentedAt = null)
    {
        samples.Clear();
        samples.Add(new(seconds, decoded, presented, dropped, presentedAt ?? seconds));
    }

    public PlaybackPerformance? Measure(double seconds, long decoded, long presented, double sourceFps,
        long dropped = 0, double? presentedAt = null)
    {
        if (samples.Count == 0)
        {
            Reset(seconds, decoded, presented, dropped, presentedAt);
            return null;
        }
        samples.Add(new(seconds, decoded, presented, dropped, presentedAt ?? seconds));
        // 保留跨越窗口起点的样本；UI 长时间繁忙时仍能统计整段停顿。
        while (samples.Count > 2 && seconds - samples[1].Seconds >= 2)
            samples.RemoveAt(0);
        var first = samples[0];
        var elapsed = seconds - first.Seconds;
        if (elapsed < 1)
            return null;
        // 用实际呈现时刻消除 UI 采样与渲染线程的相位差；超过一帧仍无新画面时，
        // 分母继续走表，不能让卡住的画面一直保持停顿前的 FPS。
        var frameDuration = sourceFps > 0 ? 1 / sourceFps : 0;
        var presentationElapsed = Math.Max(presentedAt ?? seconds, seconds - frameDuration) - first.PresentedAt;
        return new(Math.Max(0, decoded - first.Decoded) / elapsed,
            presentationElapsed > 0 ? Math.Max(0, presented - first.Presented) / presentationElapsed : 0, sourceFps,
            frameRateTolerance: presentationElapsed > 0 ? 1 / presentationElapsed : 0,
            droppedFrames: Math.Max(0, dropped - first.Dropped));
    }
}
