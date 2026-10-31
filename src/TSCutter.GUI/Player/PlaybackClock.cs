using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace TSCutter.GUI.Player;

/// <summary>
/// 播放主时钟。有声播放以音频设备的实际播放位置为时钟源（设备未消费的缓冲不计入），
/// 无声播放回退到单调时钟。暂停/停止由调用方冻结时钟源或调用 Pause 实现，
/// 恢复时重新建立锚点，不把暂停期间的墙钟时间计入媒体时间。
/// </summary>
public sealed class PlaybackClock
{
    private readonly Stopwatch stopwatch = new();
    private readonly Stopwatch activeTime = new();
    private readonly object sync = new();
    private double anchorMediaSeconds;
    private double pausedMediaSeconds;
    private bool paused;
    private Func<ulong>? audioConsumedSource;
    private int audioSampleRate;
    private ulong anchorConsumedFrames;

    /// <summary>当前媒体时间线秒数。</summary>
    public double CurrentSeconds
    {
        get
        {
            lock (sync)
            {
                if (paused)
                    return pausedMediaSeconds;
                return anchorMediaSeconds + ElapsedSecondsCore();
            }
        }
    }

    private double ElapsedSecondsCore()
    {
        // 调用方已持有 sync。
        if (audioConsumedSource is { } source)
        {
            var consumed = source();
            return (consumed >= anchorConsumedFrames ? consumed - anchorConsumedFrames : 0) / (double)audioSampleRate;
        }
        return stopwatch.ElapsedTicks * (1.0 / Stopwatch.Frequency);
    }

    /// <summary>以单调时钟从指定媒体时间开始（或重新锚定）走表。</summary>
    public void Start(double mediaSeconds)
    {
        lock (sync)
        {
            audioConsumedSource = null;
            anchorMediaSeconds = mediaSeconds;
            pausedMediaSeconds = mediaSeconds;
            paused = false;
            stopwatch.Restart();
            activeTime.Restart();
        }
    }

    /// <summary>
    /// 以音频设备实际播放进度为时钟源。anchorConsumedFrames 是设备开始播放时
    /// 已消费的帧数（通常为起播瞬间的读数）。
    /// </summary>
    public void StartAudio(double mediaSeconds, Func<ulong> consumedSource, int sampleRate,
        ulong anchorConsumedFrames)
    {
        lock (sync)
        {
            audioConsumedSource = consumedSource ?? throw new ArgumentNullException(nameof(consumedSource));
            audioSampleRate = sampleRate > 0 ? sampleRate : 48_000;
            this.anchorConsumedFrames = anchorConsumedFrames;
            anchorMediaSeconds = mediaSeconds;
            pausedMediaSeconds = mediaSeconds;
            paused = false;
            stopwatch.Restart();
            activeTime.Restart();
        }
    }

    /// <summary>
    /// 音频链路失效时把时钟切回单调模式，从当前媒体时间继续，避免画面冻结。
    /// </summary>
    public void FallbackToMonotonic()
    {
        lock (sync)
        {
            if (audioConsumedSource is null)
                return;
            var current = CurrentSecondsCore();
            audioConsumedSource = null;
            anchorMediaSeconds = current;
            pausedMediaSeconds = current;
            if (!paused)
                stopwatch.Restart();
        }
    }

    public void Pause()
    {
        lock (sync)
        {
            if (paused)
                return;
            // 两种时钟模式都要用实际推进值冻结（音频模式不能读墙钟 Stopwatch）。
            pausedMediaSeconds = CurrentSecondsCore();
            paused = true;
            stopwatch.Stop();
            activeTime.Stop();
        }
    }

    /// <summary>恢复原时钟源；音频消费位置重新锚定，不计入暂停期间的时间。</summary>
    public void Resume(ulong? audioAnchor = null)
    {
        lock (sync)
        {
            if (!paused)
                return;
            anchorMediaSeconds = pausedMediaSeconds;
            if (audioConsumedSource is { } source)
                anchorConsumedFrames = audioAnchor ?? source();
            paused = false;
            stopwatch.Restart();
            activeTime.Start();
        }
    }

    internal double ActiveElapsedSeconds
    {
        get { lock (sync) return activeTime.Elapsed.TotalSeconds; }
    }

    public void Stop()
    {
        lock (sync)
        {
            pausedMediaSeconds = CurrentSecondsCore();
            paused = true;
            audioConsumedSource = null;
            stopwatch.Stop();
            activeTime.Stop();
        }
    }

    /// <summary>
    /// 等待到指定的媒体时间。暂停期间时钟不前进，等待会随之延长；
    /// seek 等操作重新锚定时，短轮询会在下一拍感知新的时间关系。
    /// </summary>
    public async Task WaitUntilAsync(double mediaSeconds, CancellationToken cancellationToken)
    {
        while (true)
        {
            double behind;
            lock (sync)
                behind = mediaSeconds - CurrentSecondsCore();
            if (behind <= 0)
                return;
            var delay = Math.Min(behind, 0.05);
            await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
        }
    }

    private double CurrentSecondsCore()
    {
        // 调用方已持有 sync；与 CurrentSeconds 保持一致的计算。
        return paused ? pausedMediaSeconds : anchorMediaSeconds + ElapsedSecondsCore();
    }
}
