using System.Security.Cryptography;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Media;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Services.Media;
using TSCutter.GUI.Tests.FFmpeg;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

public sealed class Av3aExportTests
{
    [NativeMediaTheory("remux")]
    [InlineData("remux", false)]
    [InlineData("remux", true)]
    [InlineData("smart", false)]
    [InlineData("smart", true)]
    [InlineData("mux", false)]
    [InlineData("mux", true)]
    public async Task MissingConfigurationIsRecoveredBeforeWritingOrRejectedWithoutReplacingOutput(string mode, bool corruptHeaders)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tscutter-av3a-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var (valid, source) = await CreateSample(directory, corruptHeaders);
            var output = Path.Combine(directory, "output.mp4");
            var smart = new FrameAccurateExportService();
            Func<Task<ExportModifiers>> export;
            if (mode == "smart")
            {
                var (start, end) = NonKeyFrameRange(source);
                var settings = new FrameAccurateExportSettings { Container = "mp4", VideoIndex = 0, StreamIndices = [0, 1] };
                export = () => smart.ExportAsync(new(output, [new(source, start, end, settings)]));
            }
            else if (mode == "mux")
            {
                export = async () =>
                {
                    var mux = new MediaMuxService();
                    var input = await mux.ProbeAsync(source);
                    var tracks = input.Streams.Select(stream => new MediaMuxTrack(input, stream, "", "", 0, true, false)).ToArray();
                    return await mux.ExportAsync(new(output, "mp4", tracks));
                };
            }
            else
            {
                var settings = new RemuxExportSettings { Container = "mp4", ReferenceVideoIndex = 0, StreamIndices = [0, 1] };
                export = () => new RemuxExportService().ExportAsync(new(output, [new(source, 0, 2, settings)]));
            }
            if (corruptHeaders)
            {
                File.WriteAllText(output, "existing output");
                switch (mode)
                {
                    case "smart":
                        Assert.Equal(FrameAccurateExportError.UnsupportedTrack,
                            (await Assert.ThrowsAsync<FrameAccurateExportException>(export)).Code);
                        break;
                    case "mux":
                        Assert.Equal(MediaMuxError.UnsupportedTrack, (await Assert.ThrowsAsync<MediaMuxException>(export)).Code);
                        break;
                    default:
                        Assert.Equal(RemuxExportError.UnsupportedTrack, (await Assert.ThrowsAsync<RemuxExportException>(export)).Code);
                        break;
                }
                Assert.Equal("existing output", File.ReadAllText(output));
                Assert.Empty(Directory.GetFiles(directory, ".tscutter-*.tmp"));
                return;
            }

            var modifiers = await export();
            Assert.Equal(ExportModifiers.Av3aConfigurationRecovered, modifiers);
            AssertAudioParameters(valid, output);
            var from = mode == "smart" ? 0.4 : 0;
            var to = mode switch { "smart" => 2.44, "remux" => 2.04, _ => double.MaxValue };
            var expected = PacketHashes(source, 1, from, to);
            Assert.NotEmpty(expected);
            Assert.Equal(expected, PacketHashes(output, 1));
            if (mode == "mux")
                Assert.Equal(PacketHashes(source, 0), PacketHashes(output, 0));
            if (mode == "smart")
            {
                Assert.True(Assert.Single(smart.Reports).EncodedFrames > 0);
                using var result = Open(output);
                Assert.InRange(result.Duration / 1e6, 2.03, 2.08);
            }
            source = valid;
            output = Path.Combine(directory, "configured-output.mp4");
            Assert.Equal(ExportModifiers.None, await export());
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task<(string Valid, string Source)> CreateSample(string directory, bool corruptHeaders)
    {
        var rawAudio = Path.Combine(directory, "audio.av3a");
        var bytes = Enumerable.Repeat((byte)0x55, 192 * 1024).ToArray();
        byte[] header = [0xff, 0xf2, 0x10, 0x52, 0x42, 0x11, 0xb8, 0x0c, 0x24];
        for (var offset = 0; offset < bytes.Length; offset += 1024)
            header.CopyTo(bytes, offset);
        File.WriteAllBytes(rawAudio, bytes);
        var mux = new MediaMuxService();
        var video = await mux.ProbeAsync(Path.Combine(NativeTestEnvironment.FixtureDirectory("remux"), "multi.mp4"));
        var audio = await mux.ProbeAsync(rawAudio);
        var valid = Path.Combine(directory, "valid.mp4");
        await mux.ExportAsync(new(valid, "mp4",
            [new(video, video.Streams.Single(stream => stream.Index == 0), "", "", 0, true, false),
             new(audio, Assert.Single(audio.Streams), "", "", 0, true, false)]));
        var source = Path.Combine(directory, "incomplete.mp4");
        var media = File.ReadAllBytes(valid);
        var tag = media.AsSpan().IndexOf("dca3"u8);
        Assert.True(tag >= 0);
        // 保持盒子长度和偏移，只让解复用器看不到音频配置；压缩包仍包含完整帧头。
        "free"u8.CopyTo(media.AsSpan(tag, 4));
        File.WriteAllBytes(source, media);
        AssertMissingConfiguration(source, corruptHeaders);
        return (valid, source);
    }

    private static unsafe void AssertMissingConfiguration(string path, bool corruptHeaders)
    {
        using var input = Open(path);
        var audio = Assert.Single(input.Streams, stream => stream.Codecpar!.IsAv3a);
        AVStream* stream = audio;
        Assert.Equal(0, stream->codecpar->extradata_size);
        if (!corruptHeaders)
            return;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        byte[] invalidHeader = new byte[9];
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex != audio.Index)
                continue;
            AVPacket* raw = packet;
            Assert.True(raw->pos >= 0 && raw->size >= invalidHeader.Length);
            file.Position = raw->pos;
            file.Write(invalidHeader);
        }
    }

    private static (long Start, long End) NonKeyFrameRange(string path)
    {
        using var input = Open(path);
        var video = input.GetVideoStream();
        var start = (long)Math.Round(0.4 * video.TimeBase.den / video.TimeBase.num);
        var end = (long)Math.Round(2.4 * video.TimeBase.den / video.TimeBase.num);
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex != video.Index || packet.Pts != start)
                continue;
            Assert.Equal(0, packet.Flags & 1);
            return (start, end);
        }
        throw new InvalidOperationException("Missing non-keyframe start.");
    }

    private static unsafe void AssertAudioParameters(string valid, string output)
    {
        using var original = Open(valid);
        using var exported = Open(output);
        AVStream* left = original.Streams[1], right = Assert.Single(exported.Streams, stream => stream.Codecpar!.IsAv3a);
        var a = left->codecpar;
        var b = right->codecpar;
        Assert.Equal((a->codec_id, a->sample_rate, a->ch_layout.nb_channels, a->bits_per_raw_sample, a->frame_size),
            (b->codec_id, b->sample_rate, b->ch_layout.nb_channels, b->bits_per_raw_sample, b->frame_size));
        Assert.True(b->extradata_size > 0);
        Assert.Equal(new ReadOnlySpan<byte>(a->extradata, a->extradata_size).ToArray(),
            new ReadOnlySpan<byte>(b->extradata, b->extradata_size).ToArray());
    }

    private static unsafe string[] PacketHashes(string path, int index, double start = 0, double end = double.MaxValue)
    {
        using var input = Open(path);
        var hashes = new List<string>();
        var lastDts = Enumerable.Repeat(NativeMethods.AV_NOPTS_VALUE, input.Streams.Count).ToArray();
        foreach (var packet in input.ReadPackets())
        {
            AVPacket* raw = packet;
            Assert.True(lastDts[packet.StreamIndex] == NativeMethods.AV_NOPTS_VALUE || raw->dts > lastDts[packet.StreamIndex]);
            lastDts[packet.StreamIndex] = raw->dts;
            if (packet.StreamIndex != index)
                continue;
            var timeBase = input.Streams[index].TimeBase;
            var time = raw->pts * (double)timeBase.num / timeBase.den;
            if (time < start || time >= end)
                continue;
            hashes.Add(Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>(raw->data, raw->size))));
        }
        return hashes.ToArray();
    }

    private static FormatContext Open(string path)
    {
        var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        return input;
    }
}
