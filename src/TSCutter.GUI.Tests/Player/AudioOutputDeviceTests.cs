using System;
using Xunit;

namespace TSCutter.GUI.Tests.Player;

/// <summary>
/// 真实音频设备验证默认跳过，只有设置 TSCUTTER_ENABLE_AUDIO_DEVICE_TESTS=1 时才运行，
/// 避免常规测试会话占用声卡。用例内容为数字静音，即使开启也不产生可听声音。
/// 发现阶段不触碰音频设备，设备探活放在测试体内。
/// </summary>
public sealed class AudioDeviceFact : FactAttribute
{
    public AudioDeviceFact()
    {
        var enabled = Environment.GetEnvironmentVariable("TSCUTTER_ENABLE_AUDIO_DEVICE_TESTS");
        if (!string.Equals(enabled, "1", StringComparison.OrdinalIgnoreCase))
            Skip = "真实设备验证默认关闭；设置 TSCUTTER_ENABLE_AUDIO_DEVICE_TESTS=1 启用"
                + "（用例输出数字静音，不产生可听声音）。";
    }
}

/// <summary>
/// 音频设备输出桥的真实设备验证：缓冲预滚后启动播放，确认消费时钟按实时推进。
/// 写入的是全零 PCM（数字静音），只验证设备回调与主时钟行为，不发出声音。
/// </summary>
public class AudioOutputDeviceTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int ChunkFrames = 480; // 10ms

    [AudioDeviceFact]
    public void StreamedPcmAdvancesTheConsumedClockInRealTime()
    {
        Console.WriteLine("checkpoint: creating device");
        using var device = TSCutter.GUI.Player.Audio.AudioOutputDevice.TryCreate(
            SampleRate, Channels, SampleRate / 4);
        Assert.NotNull(device);
        var chunk = new float[ChunkFrames * Channels]; // 全零静音

        // 预滚约 190ms（低于环形容量），随后启动设备并按实时节奏持续供给。
        for (var i = 0; i < 19; i++)
            WriteChunkWhenAccepted(device, chunk);
        Assert.True(device.Start());

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (device.ConsumedFrames < (ulong)(SampleRate * 1.2) && DateTime.UtcNow < deadline)
            WriteChunkWhenAccepted(device, chunk);

        // 设备时钟应与真实时间一致地走到 1.2 秒附近（允许调度抖动）。
        var consumedSeconds = device.ConsumedFrames / (double)SampleRate;
        Assert.True(consumedSeconds > 1.0, $"消费时钟推进过慢：{consumedSeconds:F3}s");
        Assert.True(device.UnderflowFrames < (ulong)SampleRate / 2,
            $"欠载帧数过多：{device.UnderflowFrames}");
        Assert.True(device.Stop());
    }

    private static void WriteChunkWhenAccepted(TSCutter.GUI.Player.Audio.AudioOutputDevice device,
        float[] chunk)
    {
        while (device.Write(chunk) < chunk.Length / Channels)
            Thread.Sleep(2);
    }
}
