using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Player.Audio;
using TSCutter.GUI.Services.Preview;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Utils.Media;
using Xunit;

namespace TSCutter.GUI.Tests.Player;

/// <summary>多轨合并试听：三条来源合成 5.1 后由播放的重采样下混为立体声，波形按合成结果绘制。</summary>
[Collection("Avalonia UI")]
public sealed class ChannelMergePreviewTests
{
    private static string Fixture() => Path.Combine(NativeTestEnvironment.FixtureDirectory("remux"), "channel-merge.ts");

    [NativeMediaFact("remux")]
    public async Task PipelinePlaysTheDownmixOfAllSixChannels()
    {
        using var input = InterruptibleInputFormatContext.Open(Fixture(), () => false);
        input.LoadStreamInfo();
        var streams = new[] { 0x101, 0x102, 0x103 }
            .Select(pid => input.Streams.Single(stream => stream.Id == pid)).ToArray();
        var fake = new FakeAudioOutput();
        var origin = streams[0].StartTime * (double)streams[0].TimeBase.num / streams[0].TimeBase.den;
        await using var pipeline = await AudioPlaybackPipeline.TryCreateAsync(streams[0], startSeconds: 0, endSeconds: null,
            originSeconds: origin, deviceFactory: () => fake,
            channelMerge: streams.Select(stream => (stream, false)).ToArray());
        Assert.NotNull(pipeline);
        pipeline!.Start();
        var run = pipeline.RunAsync();
        var indices = streams.Select(stream => stream.Index).ToHashSet();
        foreach (var packet in input.ReadPackets())
        {
            if (indices.Contains(packet.StreamIndex))
                await pipeline.EnqueuePacketAsync(packet, CancellationToken.None);
        }
        pipeline.CompleteInput();
        await run;

        Assert.Null(pipeline.Failure);
        // 20 秒的素材，前置来源从头到尾都有。
        Assert.InRange(pipeline.WrittenFrames, 19 * 48_000, 21 * 48_000);
        var samples = fake.Writes.SelectMany(buffer => buffer).ToArray();
        float[] Channel(int channel, double from) => Enumerable.Range((int)(from * 48_000), 48_000)
            .Select(frame => samples[frame * AudioOutputFormat.Channels + channel]).ToArray();
        var left = Channel(0, 8);
        var right = Channel(1, 8);
        // 左声道含 L 与 Ls、右声道含 R 与 Rs，中置两边都有；L 不进右声道。
        Assert.True(Energy(left, 440) > 100 * Energy(right, 440));
        Assert.True(Energy(right, 550) > 100 * Energy(left, 550));
        Assert.True(Energy(left, 770) > 100 * Energy(right, 770));
        Assert.True(Energy(right, 880) > 100 * Energy(left, 880));
        Assert.True(Energy(left, 660) > 0.5 * Energy(right, 660) && Energy(right, 660) > 0.5 * Energy(left, 660));
        Assert.True(Energy(left, 660) > 100 * Energy(left, 1000));
    }

    [NativeMediaFact("remux")]
    public void WaveformReadsTheMergedTracks()
    {
        string path = Fixture();
        PreviewStreamSelection anchor;
        double origin;
        using (var input = InterruptibleInputFormatContext.Open(path, () => false))
        {
            input.LoadStreamInfo();
            var stream = input.Streams.Single(item => item.Id == 0x101);
            anchor = new PreviewStreamSelection(stream.Index, stream.Id, 1);
            var video = input.Streams.Single(item => item.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_VIDEO);
            origin = video.StartTime * (double)video.TimeBase.num / video.TimeBase.den;
        }
        var source = new AudioWaveformSource(path, null, 0, null, anchor, origin,
            ChannelMerge: [new(0x101, false), new(0x102, false), new(0x103, false)]);
        using var reader = AudioWaveformReader.TryOpen(source);
        Assert.NotNull(reader);
        var seconds = new Dictionary<int, float[]>();
        reader!.Read(2, 5, (second, buckets) => seconds[second] = buckets, CancellationToken.None);
        Assert.Equal([2, 3, 4], seconds.Keys.Order());
        Assert.All(seconds.Values, buckets => Assert.True(buckets.Count(value => !float.IsNaN(value) && value > 0.01f) > 900));
    }

    /// <summary>Goertzel：某个频率上的能量。</summary>
    private static double Energy(float[] samples, int frequency)
    {
        double s1 = 0, s2 = 0;
        var coefficient = 2 * Math.Cos(2 * Math.PI * frequency / 48000);
        foreach (var sample in samples)
        {
            var s0 = sample + coefficient * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        return s1 * s1 + s2 * s2 - coefficient * s1 * s2;
    }
}
