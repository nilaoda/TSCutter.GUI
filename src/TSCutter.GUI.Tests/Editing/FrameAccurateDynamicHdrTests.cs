using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Utils.Media;
using Xunit;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Tests.Editing;

public sealed class FrameAccurateDynamicHdrTests
{
    [NativeMediaTheory(sampleVariable: "TSCUTTER_SMART_HDR_TEST_SAMPLES")]
    [InlineData("hdr-vivid-hevc.mp4", "mp4", false)]
    [InlineData("hdr10plus.mkv", "mkv", false)]
    [InlineData("hdr-vivid-hevc.ts", "ts", false)]
    [InlineData("hdr10plus.ts", "ts", false)]
    [InlineData("hdr-vivid-hevc.mp4", "mkv", false)]
    [InlineData("hdr10plus.mkv", "mp4", false)]
    [InlineData("hdr-vivid-hevc.mp4", "mp4", true)]
    [InlineData("hdr10plus.mkv", "mkv", true)]
    public async Task ReencodedAndCopiedFramesKeepTheirOriginalDynamicMetadata(string name, string container, bool singleFrame)
    {
        var root = Environment.GetEnvironmentVariable("TSCUTTER_SMART_HDR_TEST_SAMPLES")!;
        var source = Path.Combine(root, name);
        var input = ReadPackets(source, 320);
        var points = input.Frames.Select(frame => frame.Pts).ToArray();
        Assert.True(input.Keys.Count >= 3);
        var start = singleFrame ? points[7] : points.Last(pts => pts < input.Keys[1]);
        var end = singleFrame ? start : points.First(pts => pts > input.Keys[2]);
        var expected = input.Frames.Where(frame => frame.Pts >= start && frame.Pts <= end).ToArray();
        Assert.All(expected, frame => Assert.NotEmpty(frame.Metadata));
        var output = Path.Combine(Path.GetTempPath(), $"tscutter-hdr-{Guid.NewGuid():N}.{container}");
        try
        {
            input.Settings.Container = container;
            var service = new FrameAccurateExportService();
            await service.ExportAsync(new FrameAccurateExportRequest(output,
                [new FrameAccurateExportSegment(source, start, end, input.Settings)]));
            var actual = ReadPackets(output, int.MaxValue).Frames;
            Assert.Equal(expected.Length, actual.Count);
            for (var index = 0; index < expected.Length; index++)
                Assert.Equal(expected[index].Metadata, actual[index].Metadata);
            var report = Assert.Single(service.Reports);
            Assert.True(report.EncodedFrames > 0);
            if (!singleFrame)
                Assert.True(report.CopiedPackets > 0);
            var metadataType = name.StartsWith("hdr10plus", StringComparison.Ordinal)
                ? AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_PLUS : AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_VIVID;
            AssertDecodesWithMetadata(output, expected.Length, metadataType);
        }
        finally { File.Delete(output); }
    }

    private sealed record PacketInfo(long Pts, byte[] Metadata);
    private sealed record InputInfo(FrameAccurateExportSettings Settings, List<PacketInfo> Frames, List<long> Keys);

    private static unsafe InputInfo ReadPackets(string path, int limit)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        var video = input.GetVideoStream();
        AVCodecParameters* parameters = video.Codecpar!;
        var lengthSize = VideoBitstream.ReadParameterSets(
            new ReadOnlySpan<byte>(parameters->extradata, parameters->extradata_size), true, []);
        var frames = new List<PacketInfo>();
        var keys = new List<long>();
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex != video.Index)
                continue;
            AVPacket* raw = packet;
            frames.Add(new PacketInfo(packet.Pts,
                HevcDynamicHdrMetadata.Read(new ReadOnlySpan<byte>(raw->data, raw->size), lengthSize)?.Sei ?? []));
            if ((packet.Flags & 1) != 0)
                keys.Add(packet.Pts);
            if (frames.Count >= limit)
                break;
        }
        frames.Sort((left, right) => left.Pts.CompareTo(right.Pts));
        keys.Sort();
        return new InputInfo(new FrameAccurateExportSettings
        {
            Container = Path.GetExtension(path).TrimStart('.'), VideoIndex = video.Index, StreamIndices = [video.Index]
        }, frames, keys);
    }

    private static unsafe void AssertDecodesWithMetadata(string path, int expectedFrames, AVFrameSideDataType metadataType)
    {
        using var capture = new FFmpegLogCapture();
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        var video = input.GetVideoStream();
        using var decoder = new CodecContext(Codec.FindDecoderById(video.Codecpar!.CodecId));
        decoder.FillParameters(video.Codecpar);
        decoder.PacketTimeBase = video.TimeBase;
        decoder.ThreadCount = 2;
        decoder.Open();
        using var frame = new Frame();
        var count = 0;
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex != video.Index)
                continue;
            foreach (var decoded in decoder.DecodePacket(packet, frame))
                Check(decoded);
        }
        foreach (var decoded in decoder.DecodePacket(null, frame))
            Check(decoded);
        Assert.Equal(expectedFrames, count);
        Assert.Empty(capture.Messages);

        void Check(Frame decoded)
        {
            count++;
            Assert.Equal(0, decoded.Flags & 1);
            Assert.True(FF.av_frame_get_side_data(decoded, metadataType) != null);
        }
    }
}
