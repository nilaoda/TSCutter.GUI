using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Player;
using TSCutter.GUI.Player.Audio;
using TSCutter.GUI.Player.Video;
using TSCutter.GUI.Tests.FFmpeg;
using Xunit;

namespace TSCutter.GUI.Tests.Player;

/// <summary>记录写入 PCM 的假音频输出：验证管线与音频时钟不依赖真实声卡。</summary>
internal sealed class FakeAudioOutput : IAudioOutput
{
    public readonly ConcurrentQueue<float[]> Writes = new();
    private bool playing;
    private ulong consumed;
    private double gain = 1;

    public ulong ConsumedFrames => consumed;
    public ulong UnderflowFrames => 0;
    public bool IsPlaying => playing;

    public bool Start()
    {
        playing = true;
        // 模拟设备实时消费：起播时立即消费已写入的一半，剩余由 ConsumeUntil 驱动。
        consumed = (ulong)(TotalWrittenFrames() / 2);
        return true;
    }

    public bool Stop()
    {
        playing = false;
        return true;
    }

    public bool Flush()
    {
        Writes.Clear();
        return true;
    }

    public void SetVolume(double value) => gain = value;

    public int Write(ReadOnlySpan<float> samples)
    {
        var frames = samples.Length / AudioOutputFormat.Channels;
        var output = samples.ToArray();
        for (var index = 0; index < output.Length; index++)
            output[index] *= (float)gain;
        Writes.Enqueue(output);
        consumed += (ulong)frames; // 模拟设备即时消费。
        return frames;
    }

    public int TotalWrittenFrames() => Writes.Sum(buffer => buffer.Length / AudioOutputFormat.Channels);

    /// <summary>把消费推进到指定帧数（模拟设备播放进度）。</summary>
    public void ConsumeUntil(ulong frames) => consumed = frames;

    public void Dispose() { }
}

/// <summary>音频管线的注入设备验证：真实解码 + 重采样 + 假设备。</summary>
[Collection("Avalonia UI")]
public sealed class AudioPlaybackPipelineTests
{
    private static string SamplePath(string name) =>
        Path.Combine(NativeTestEnvironment.FixtureDirectory("remux"), name);

    [NativeMediaFact("remux")]
    public unsafe void AudioChoicesKeepUnsupportedTracksWithoutReadingPackets()
    {
        using var input = TSCutter.GUI.Utils.Media.InterruptibleInputFormatContext.Open(SamplePath("multi.mp4"), () => false);
        input.LoadStreamInfo();
        var audio = input.Streams.Where(stream => stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_AUDIO).ToArray();
        Assert.True(audio.Length >= 2);
        AVStream* unsupported = audio[0];
        unsupported->codecpar->codec_id = AVCodecID.AV_CODEC_ID_NONE;
        var before = (input.Pb->pos, (nint)input.Pb->buf_ptr);
        var choices = TSCutter.GUI.Utils.Media.PreviewStreamUtil.CreateAudioOptions(input, null);
        Assert.Equal(audio.Length, choices.Count);
        Assert.False(choices[0].CanDecode);
        Assert.True(choices[1].CanDecode);
        Assert.Equal(audio[1].Index, choices[1].Selection.StreamIndex);
        Assert.Equal(before, (input.Pb->pos, (nint)input.Pb->buf_ptr));
    }

    [NativeMediaFact("remux")]
    public async Task PipelineDecodesWritesAndDrainsToCompletion()
    {
        var path = SamplePath("long-audio.mp4");
        using var formatContext = TSCutter.GUI.Utils.Media.InterruptibleInputFormatContext.Open(path, () => false);
        formatContext.LoadStreamInfo();
        var audioStream = formatContext.Streams.First(stream =>
            stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_AUDIO);

        var fake = new FakeAudioOutput();
        await using var pipeline = await AudioPlaybackPipeline.TryCreateAsync(
            audioStream, startSeconds: 0, endSeconds: null, originSeconds: 0, deviceFactory: () => fake);
        Assert.NotNull(pipeline);
        pipeline!.Start();
        var run = pipeline.RunAsync();

        // 直接喂入文件的全部音频包（无视频竞争）。
        var videoIndex = formatContext.Streams.First(stream =>
                stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_VIDEO).Index;
        foreach (var packet in formatContext.ReadPackets())
        {
            if (packet.StreamIndex == audioStream.Index)
                await pipeline.EnqueuePacketAsync(packet, CancellationToken.None);
            else if (packet.StreamIndex != videoIndex)
                packet.Unref();
        }
        pipeline.CompleteInput();
        await run;

        Assert.Null(pipeline.Failure);
        Assert.True(pipeline.WrittenFrames > 40_000, $"写入帧数过少：{pipeline.WrittenFrames}");
        // 10 秒 48kHz 单声道 → 立体声输出约 48 万帧（±1s）。
        Assert.InRange(pipeline.WrittenFrames, 430_000, 530_000);
        Assert.True(fake.Writes.All(buffer => buffer.All(sample => sample is >= -1.0f and <= 1.0f)));
        Assert.True(pipeline.Completion.IsCompleted);
    }

    [NativeMediaFact("remux")]
    public async Task MidpointAudioUsesItsOwnTimeBaseAndAppliesVolumeToEverySample()
    {
        async Task<float[]> Decode(double start, double? end, double gain)
        {
            using var input = TSCutter.GUI.Utils.Media.InterruptibleInputFormatContext.Open(SamplePath("long-audio.mp4"), () => false);
            input.LoadStreamInfo();
            var stream = input.Streams.First(item => item.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_AUDIO);
            var output = new FakeAudioOutput();
            await using var pipeline = await AudioPlaybackPipeline.TryCreateAsync(stream, start, end, 0, () => output);
            Assert.NotNull(pipeline);
            pipeline.SetVolume(gain);
            pipeline.Start();
            var work = pipeline.RunAsync();
            try
            {
                foreach (var packet in input.ReadPackets())
                    if (packet.StreamIndex == stream.Index)
                        await pipeline.EnqueuePacketAsync(packet, CancellationToken.None);
            }
            catch (ChannelClosedException) { }
            pipeline.CompleteInput();
            await work.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(pipeline.Failure);
            return output.Writes.SelectMany(samples => samples).ToArray();
        }
        var whole = await Decode(0, null, 1);
        var middle = await Decode(5, 7, 0.25);
        Assert.Equal(2 * AudioOutputFormat.SampleRate * AudioOutputFormat.Channels, middle.Length);
        for (var index = 0; index < middle.Length; index++)
            Assert.Equal(whole[5 * AudioOutputFormat.SampleRate * AudioOutputFormat.Channels + index] * 0.25f,
                middle[index], 5);
        var muted = await Decode(5, 5.2, 0);
        Assert.NotEmpty(muted);
        Assert.All(muted, sample => Assert.Equal(0, sample));
    }

    [NativeMediaFact("remux")]
    public async Task AudioKeepsReceivingPacketsWhenTheVideoQueueIsFull()
    {
        using var queue = new VideoFrameQueue(1);
        await using var engine = new VideoPlaybackEngine(new PlaybackOptions
        {
            FilePath = SamplePath("long-audio.mp4"), EndSeconds = 2,
            TimelineOriginSeconds = 0, EstimatedKeyFrameIntervalSeconds = 2
        }, queue);
        await engine.OpenAsync();
        var output = new FakeAudioOutput();
        await using var audio = await AudioPlaybackPipeline.TryCreateAsync(engine.AudioStream!.Value,
            0, engine.AudioEndSeconds, 0, () => output);
        Assert.NotNull(audio);
        engine.SetAudioPipeline(audio);
        audio.Start();
        var audioRun = audio.RunAsync();
        var videoRun = Task.Run(engine.RunAsync);
        try
        {
            await engine.PrerollCompletion.WaitAsync(TimeSpan.FromSeconds(5));
            // 不消费画面，模拟视频转换或呈现阻塞；音频仍应收到至少一秒的数据。
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (audio.WrittenFrames < AudioOutputFormat.SampleRate && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.True(queue.HasReadyFrames);
            Assert.False(videoRun.IsCompleted);
            Assert.True(audio.WrittenFrames >= AudioOutputFormat.SampleRate);
            Assert.Null(audio.Failure);
        }
        finally
        {
            engine.Cancel();
            audio.Cancel();
            await videoRun.WaitAsync(TimeSpan.FromSeconds(5));
            await audioRun.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [NativeMediaFact("remux")]
    public async Task EndBoundaryStopsTheAudioTail()
    {
        var path = SamplePath("long-audio.mp4");
        using var formatContext = TSCutter.GUI.Utils.Media.InterruptibleInputFormatContext.Open(path, () => false);
        formatContext.LoadStreamInfo();
        var audioStream = formatContext.Streams.First(stream =>
            stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_AUDIO);
        // 时间基换算：终点 2 秒。
        var timeBase = audioStream.TimeBase;
        var endPts = (long)(2.0 * timeBase.den / timeBase.num);

        var fake = new FakeAudioOutput();
        await using var pipeline = await AudioPlaybackPipeline.TryCreateAsync(
            audioStream, startSeconds: 0, endSeconds: 2, originSeconds: 0, deviceFactory: () => fake);
        Assert.NotNull(pipeline);
        pipeline!.Start();
        var run = pipeline.RunAsync();
        try
        {
            foreach (var packet in formatContext.ReadPackets())
            {
                if (packet.StreamIndex == audioStream.Index)
                    await pipeline.EnqueuePacketAsync(packet, CancellationToken.None);
                else
                    packet.Unref();
            }
        }
        catch (ChannelClosedException)
        {
            // 到达区间终点后管线自行关闭输入队列：停止喂包属预期。
        }
        pipeline.CompleteInput();
        await run;

        Assert.Null(pipeline.Failure);
        Assert.True(pipeline.Completion.IsCompleted);
        // 终点 2 秒后不应继续写出（留 0.5 秒重采样延迟余量）。
        Assert.True(pipeline.WrittenFrames < 48_000 * 3, $"音频未在区间终点停止：{pipeline.WrittenFrames}");
        Assert.True(pipeline.WrittenFrames > 48_000, $"音频过早停止：{pipeline.WrittenFrames}");
    }
}

/// <summary>音频主时钟：以设备消费进度为时间源，暂停（消费冻结）即时钟冻结。</summary>
public class PlaybackClockAudioModeTests
{
    [Fact]
    public async Task AudioClockFollowsConsumedFramesAndFallsBackOnFailure()
    {
        var clock = new PlaybackClock();
        ulong consumed = 0;
        clock.StartAudio(10, () => consumed, 48_000, anchorConsumedFrames: consumed);
        Assert.Equal(10, clock.CurrentSeconds, 6);

        consumed = 48_000; // 设备播放了 1 秒。
        Assert.Equal(11, clock.CurrentSeconds, 3);

        clock.Pause();
        var pausedAt = clock.CurrentSeconds;
        await Task.Delay(30);
        Assert.Equal(pausedAt, clock.CurrentSeconds, 6);
        clock.Resume();
        consumed += 24_000;
        Assert.Equal(pausedAt + 0.5, clock.CurrentSeconds, 6);
        await Task.Delay(30);
        Assert.Equal(pausedAt + 0.5, clock.CurrentSeconds, 6);

        // 设备失效：切回单调时钟，从当前媒体时间继续走。
        clock.FallbackToMonotonic();
        var atFallback = clock.CurrentSeconds;
        await Task.Delay(40);
        Assert.True(clock.CurrentSeconds > atFallback);
    }
}
