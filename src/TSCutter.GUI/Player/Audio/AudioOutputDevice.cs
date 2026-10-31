using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TSCutter.GUI.Player.Audio;

internal enum AudioOutputFailure
{
    None,
    LibraryMissing,
    NoDevice,
    InitFailed,
}

/// <summary>
/// 默认音频设备上的交错 float32 PCM 输出。
/// 设备回调完全位于原生桥（native/audio/tsaudio_bridge.c）内部，不回托管代码，
/// 因此不会有异常穿越 ABI 边界；托管侧只做有界的环形写入与时钟读取。
/// 消费帧计数由桥维护，作为有声播放的主时钟来源。
/// </summary>
internal sealed unsafe partial class AudioOutputDevice : IAudioOutput
{
    private readonly object sync = new();
    private void* handle;
    private readonly int channels;
    private bool disposed;

    private AudioOutputDevice(void* handle, int channels)
    {
        this.handle = handle;
        this.channels = channels;
    }

    /// <summary>最近一次 TryCreate 失败的原因；成功时为 None。</summary>
    public static AudioOutputFailure LastFailure { get; private set; }

    /// <summary>设备实际渲染的帧数（含欠载时的静音），即音频设备已播放的进度。</summary>
    public ulong ConsumedFrames
    {
        get { lock (sync) return handle != null ? tsaudio_consumed_frames(handle) : 0; }
    }

    /// <summary>环形缓冲欠载导致设备播放的静音帧数，持续增长说明解码供给不足。</summary>
    public ulong UnderflowFrames
    {
        get { lock (sync) return handle != null ? tsaudio_underflow_frames(handle) : 0; }
    }

    public bool IsPlaying
    {
        get { lock (sync) return handle != null && tsaudio_is_playing(handle) != 0; }
    }

    public static AudioOutputDevice? TryCreate(int sampleRate, int channels, int ringCapacityFrames)
    {
        LastFailure = AudioOutputFailure.None;
        if (sampleRate <= 0 || channels <= 0 || ringCapacityFrames <= 0)
        {
            LastFailure = AudioOutputFailure.InitFailed;
            return null;
        }
        try
        {
            int code;
            var handle = tsaudio_open(sampleRate, channels, ringCapacityFrames, &code);
            if (handle != null)
                return new AudioOutputDevice(handle, channels);
            LastFailure = code == 2 ? AudioOutputFailure.NoDevice : AudioOutputFailure.InitFailed;
            return null;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // 音频桥未随构建产出（如设置了 TSCutterSkipAudioBridge），降级为无声播放。
            LastFailure = AudioOutputFailure.LibraryMissing;
            return null;
        }
    }

    public bool Start()
    {
        lock (sync) return handle != null && tsaudio_start(handle) == 0;
    }

    public bool Stop()
    {
        lock (sync) return handle == null || tsaudio_stop(handle) == 0;
    }

    /// <summary>
    /// 写入交错的 float32 采样。返回被完整接受的帧数（0 表示环形缓冲已满，
    /// 生产者应等待后重试）；部分帧不被接受。输入长度必须是声道数的整数倍。
    /// </summary>
    public int Write(ReadOnlySpan<float> samples)
    {
        lock (sync)
        {
            if (handle == null || samples.IsEmpty || samples.Length % channels != 0)
                return 0;
            fixed (float* pointer = samples)
                return tsaudio_write(handle, pointer, samples.Length / channels);
        }
    }

    public void SetVolume(double value)
    {
        lock (sync)
        {
            if (handle != null)
                tsaudio_set_volume(handle, (float)Math.Clamp(value, 0, 1));
        }
    }

    /// <summary>设备停止后丢弃缓冲中的 PCM，保留设备和累计消费计数。</summary>
    public bool Flush()
    {
        lock (sync) return handle == null || tsaudio_flush(handle) == 0;
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            if (handle != null)
            {
                tsaudio_close(handle);
                handle = null;
            }
        }
        GC.SuppressFinalize(this);
    }

    ~AudioOutputDevice() => Dispose();

    [LibraryImport("tscutter_audio")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void* tsaudio_open(int sampleRate, int channels, int ringCapacityFrames, int* errorCode);

    [LibraryImport("tscutter_audio")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void tsaudio_close(void* handle);

    [LibraryImport("tscutter_audio")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tsaudio_start(void* handle);

    [LibraryImport("tscutter_audio")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tsaudio_stop(void* handle);

    [LibraryImport("tscutter_audio")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void tsaudio_set_volume(void* handle, float volume);

    [LibraryImport("tscutter_audio")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tsaudio_flush(void* handle);

    [LibraryImport("tscutter_audio")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tsaudio_is_playing(void* handle);

    [LibraryImport("tscutter_audio")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int tsaudio_write(void* handle, float* frames, int frameCount);

    [LibraryImport("tscutter_audio")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial ulong tsaudio_consumed_frames(void* handle);

    [LibraryImport("tscutter_audio")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial ulong tsaudio_underflow_frames(void* handle);
}
