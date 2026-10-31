using System;

namespace TSCutter.GUI.Player.Audio;

/// <summary>
/// 音频设备输出的最小接口，供测试注入替代实现。
/// 实现需允许控制线程、PCM 写入线程及音频时钟读数并发调用。
/// </summary>
internal interface IAudioOutput : IDisposable
{
    /// <summary>设备已实际播放的帧数（音频主时钟来源）。</summary>
    ulong ConsumedFrames { get; }

    /// <summary>欠载导致的静音帧数。</summary>
    ulong UnderflowFrames { get; }

    bool IsPlaying { get; }

    bool Start();

    bool Stop();

    /// <summary>设备停止后丢弃旧位置的 PCM，保留设备和消费计数。</summary>
    bool Flush();

    /// <summary>调整设备输出音量，已缓冲的 PCM 也使用新音量。</summary>
    void SetVolume(double value);

    /// <summary>写入交错 f32 采样；返回被完整接受的帧数，0 表示环形缓冲已满。</summary>
    int Write(ReadOnlySpan<float> samples);
}
