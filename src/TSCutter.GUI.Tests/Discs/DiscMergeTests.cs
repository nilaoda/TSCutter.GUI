using System.Security.Cryptography;
using Avalonia;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Discs;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Services.Preview;
using TSCutter.GUI.Tests.FFmpeg;
using Xunit;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Tests.Discs;

[Collection("Avalonia UI")]
public sealed class DiscMergeTests
{
    [DiscMediaFact]
    public async Task MergingTwoDiscRangesKeepsAudioVideoOffsetsAndSubtitleState()
    {
        foreach (var (name, titleIndex, directory, extension, ranges) in new[]
                 {
                     ("dvd", 1, "dvd", "vob", new[] { (2.0, 4.0), (8.5, 11.0) }),
                     ("dvd", 1, "dvd.iso", "mkv", new[] { (2.0, 4.0), (8.5, 11.0) }),
                     ("bluray", 3, "bluray.iso", "m2ts", new[] { (2.1, 4.1), (50.1, 54.1) }),
                     ("bluray", 3, Path.Combine("bluray", "BDMV"), "mkv", new[] { (2.1, 4.1), (50.1, 54.1) })
                 })
        {
            var path = Path.Combine(NativeTestEnvironment.FixtureDirectory("iso"), directory);
            var scan = await DiscScanService.ScanAsync(path, CancellationToken.None);
            var input = scan.Inputs[titleIndex];
            var temporary = Directory.CreateTempSubdirectory("tscutter-disc-merge-");
            try
            {
                await VerifyMerge(input, ranges, extension, temporary.FullName,
                    $"{name}, {extension}");
            }
            finally
            {
                temporary.Delete(recursive: true);
            }
        }
    }

    private static async Task VerifyMerge(TSCutter.GUI.Models.Discs.DiscInput input, (double, double)[] ranges,
        string extension, string outputDirectory, string label)
    {
        await DiscIntegrationTests.RunUiAsync(async () =>
        {
            using var source = Open(input.SourcePath, input);
            var settings = source.CreateExportSettings();
            settings.Container = extension;
            var segments = ranges.Select(range => new RemuxExportSegment(input.SourcePath,
                SelectTime(source, range.Item1), SelectTime(source, range.Item2), settings.Snapshot())).ToArray();
            var outputs = Enumerable.Range(0, 3).Select(i => Path.Combine(outputDirectory, $"clip{i}.{extension}")).ToArray();
            var service = new RemuxExportService();
            for (var i = 0; i < 2; i++)
                await service.ExportAsync(new(outputs[i], [segments[i]]));
            var check = extension == "m2ts" ? new ExportTsCheckSession(outputs[2]) : null;
            try
            {
                await service.ExportAsync(new(outputs[2], segments), checkSession: check);
                check?.Complete();
            }
            catch (RemuxExportException exception)
            {
                Assert.Fail($"{label}: {exception.Code}: {exception}");
            }
            if (check is not null)
            {
                var reference = await new TsStreamAnalyzer().AnalyzeAsync(outputs[2]);
                Assert.Equal(TsExportCheckState.Completed, check.State);
                Assert.Equal(new FileInfo(outputs[2]).Length, check.Result!.FileSize);
                Assert.Equal(192, check.Result.PacketSize);
                Assert.Equal(reference.PacketCount, check.Result.PacketCount);
                Assert.Equal(reference.TotalErrorCount, check.Result.TotalErrorCount);
                Assert.Equal(reference.TotalWarningCount, check.Result.TotalWarningCount);
            }
            var first = Inspect(outputs[0]);
            var second = Inspect(outputs[1]);
            var merged = Inspect(outputs[2]);
            ComparePackets(first, second, merged, video: true);
            ComparePackets(first, second, merged, video: false);
            var firstAudioEnd = first.Audio.Max(packet => packet.Pts + packet.Duration)!.Value - first.Origin;
            var secondAudioStart = second.Audio.Min(packet => packet.Pts)!.Value - second.Origin;
            var audioGap = first.Duration + secondAudioStart - firstAudioEnd;
            var audioFrame = Math.Max(first.Audio.Max(packet => packet.Duration), second.Audio.Max(packet => packet.Duration));
            Assert.InRange(Math.Abs(audioGap), 0, audioFrame + 0.003);
            using var firstVideo = Open(outputs[0]);
            using var secondVideo = Open(outputs[1]);
            using var mergedVideo = Open(outputs[2]);
            using var firstSubtitles = Subtitles(outputs[0], firstVideo, first);
            using var secondSubtitles = Subtitles(outputs[1], secondVideo, second);
            using var mergedSubtitles = Subtitles(outputs[2], mergedVideo, merged);
            foreach (var time in new[] { 0.05, 0.5, first.Duration - 0.05 })
            {
                using var expected = firstSubtitles?.Render(time, new PixelSize(320, 180));
                using var actual = mergedSubtitles?.Render(time, new PixelSize(320, 180));
                Assert.Equal(Signature(expected), Signature(actual));
            }
            foreach (var time in new[] { 0.05, 0.5, 1.5, second.Duration - 0.1 })
            {
                using var expected = secondSubtitles?.Render(time, new PixelSize(320, 180));
                using var actual = mergedSubtitles?.Render(first.Duration + time, new PixelSize(320, 180));
                Assert.Equal(Signature(expected), Signature(actual));
            }
            return true;
        });
    }

    private sealed record PacketStamp(string Hash, double? Pts, double? Dts, double Duration);
    private sealed record Media(List<PacketStamp> Video, List<PacketStamp> Audio, double Origin, double Duration);

    private static unsafe Media Inspect(string path)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        var video = input.Streams.First(stream => stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_VIDEO);
        var audio = input.Streams.First(stream => stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_AUDIO);
        var videoPackets = new List<PacketStamp>();
        var audioPackets = new List<PacketStamp>();
        using var decoder = new CodecContext(Codec.FindDecoderById(video.Codecpar!.CodecId));
        decoder.FillParameters(video.Codecpar);
        decoder.PacketTimeBase = video.TimeBase;
        decoder.Open();
        using var destination = new Frame();
        var frameDuration = video.AvgFrameRate.num > 0 ? video.AvgFrameRate.den / (double)video.AvgFrameRate.num : 0;
        var origin = double.PositiveInfinity;
        var end = 0d;
        void RecordFrame(Frame frame)
        {
            AVFrame* raw = frame;
            // VOB 的最后一个 I 帧可能省略 PTS，解码输出顺序仍能恢复其显示终点。
            var pts = frame.BestEffortTimestamp == FF.AV_NOPTS_VALUE ? end : frame.BestEffortTimestamp * video.TimeBase.ToDouble();
            origin = Math.Min(origin, pts);
            end = Math.Max(end, pts + (raw->duration > 0 ? raw->duration * video.TimeBase.ToDouble() : frameDuration));
        }
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex != video.Index && packet.StreamIndex != audio.Index)
                continue;
            var stream = input.Streams[packet.StreamIndex];
            AVPacket* raw = packet;
            double? Time(long value) => value == FF.AV_NOPTS_VALUE ? null : value * stream.TimeBase.ToDouble();
            var stamp = new PacketStamp(Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>(raw->data, raw->size))),
                Time(raw->pts), Time(raw->dts), raw->duration * stream.TimeBase.ToDouble());
            (packet.StreamIndex == video.Index ? videoPackets : audioPackets).Add(stamp);
            if (packet.StreamIndex == video.Index)
                foreach (var frame in decoder.DecodePacket(packet, destination))
                    RecordFrame(frame);
        }
        foreach (var frame in decoder.DecodePacket(null, destination))
            RecordFrame(frame);
        Assert.True(double.IsFinite(origin) && end > origin);
        return new Media(videoPackets, audioPackets, origin, end - origin);
    }

    private static void ComparePackets(Media first, Media second, Media merged, bool video)
    {
        var expected = (video ? first.Video : first.Audio).Select(packet => (packet, first.Origin, offset: 0d))
            .Concat((video ? second.Video : second.Audio).Select(packet => (packet, second.Origin, offset: first.Duration))).ToArray();
        var actual = video ? merged.Video : merged.Audio;
        Assert.Equal(expected.Length, actual.Count);
        double? previousDts = null;
        for (var i = 0; i < actual.Count; i++)
        {
            Assert.Equal(expected[i].packet.Hash, actual[i].Hash);
            if (expected[i].packet.Pts is { } pts && actual[i].Pts is { } outputPts)
            {
                var difference = outputPts - merged.Origin - (pts - expected[i].Origin + expected[i].offset);
                Assert.True(Math.Abs(difference) <= 0.003,
                    $"{(video ? "video" : "audio")} packet {i}: splice offset error {difference:F6}s");
            }
            if (actual[i].Dts is { } dts)
            {
                Assert.True(previousDts is null || dts > previousDts,
                    $"{(video ? "video" : "audio")} DTS regressed at packet {i}");
                previousDts = dts;
            }
        }
    }

    private static VideoInstance Open(string path, TSCutter.GUI.Models.Discs.DiscInput? disc = null)
    {
        var video = new VideoInstance(path, discInput: disc);
        try
        {
            video.InitVideo();
            return video;
        }
        catch
        {
            video.Dispose();
            throw;
        }
    }

    private static double SelectTime(VideoInstance video, double seconds)
    {
        var frame = VideoInstance.DecodeAtTimeSync(video, TimeSpan.FromSeconds(seconds));
        try
        {
            return frame.FrameTimestamp.TotalSeconds;
        }
        finally
        {
            frame.BitmapLease?.Dispose();
            if (frame.BitmapLease is null)
                frame.Bitmap?.Dispose();
            frame.GpuFrame?.Dispose();
        }
    }

    private static SubtitlePreviewService? Subtitles(string path, VideoInstance video, Media media)
    {
        Assert.InRange(video.PreviewSubtitleTracks.Count, 0, 1);
        return video.PreviewSubtitleTracks.FirstOrDefault() is { } track
            ? SubtitlePreviewService.Load(path, track.Selection!, video.CodedVideoSize, media.Origin,
                media.Duration, video.TsSyncOffset, CancellationToken.None)
            : null;
    }

    private static unsafe string? Signature(SubtitleOverlay? overlay)
    {
        if (overlay is null)
            return null;
        using var bytes = new MemoryStream();
        foreach (var part in overlay.Parts)
        {
            var size = part.Bitmap.PixelSize;
            var pixels = new byte[size.Width * size.Height * 4];
            fixed (byte* pointer = pixels)
                part.Bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height),
                    (nint)pointer, pixels.Length, size.Width * 4);
            bytes.Write(BitConverter.GetBytes(part.X));
            bytes.Write(BitConverter.GetBytes(part.Y));
            bytes.Write(pixels);
        }
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }
}
