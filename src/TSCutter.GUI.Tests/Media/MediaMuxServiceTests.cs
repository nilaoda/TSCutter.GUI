using System.Security.Cryptography;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Media;
using TSCutter.GUI.Services.Media;
using TSCutter.GUI.Tests.Editing;
using TSCutter.GUI.Utils.Media;
using Xunit;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Tests.Media;

public sealed class MediaMuxServiceTests
{
    private static string Fixture(string name) => Path.Combine(FFmpeg.NativeTestEnvironment.FixtureDirectory("remux"), name);
    private static MediaMuxTrack Select(MediaMuxSource source, int index, decimal offset = 0, string language = "eng", string title = "") =>
        new(source, source.Streams.Single(stream => stream.Index == index), language, title, offset, true, false);

    [Fact]
    public void ValidationProtectsSourcesAndRescalingKeepsLongFractionalTimestamps()
    {
        var path = Path.Combine(Path.GetTempPath(), "mux-source.mp4");
        var stream = new MediaMuxStream(0, AVMediaType.AVMEDIA_TYPE_VIDEO, AVCodecID.AV_CODEC_ID_H264,
            "h264", "", "", "", 0, ["mp4", "mkv"]);
        var source = new MediaMuxSource(path, 0, DateTime.MinValue, "mov", 1, [stream]);
        var track = Select(source, 0);
        Assert.Equal(MediaMuxError.SameFile, Assert.Throws<MediaMuxException>(() =>
            MediaMuxService.Validate(new(path, "mp4", [track]))).Code);
        Assert.Throws<MediaMuxException>(() => MediaMuxService.Validate(new(path + ".mkv", "mkv", [track, track])));
        Assert.Throws<MediaMuxException>(() => MediaMuxService.Validate(new(path + ".ts", "ts", [track])));
        Assert.Throws<MediaMuxException>(() => MediaMuxService.Validate(new(path + ".mkv", "mkv", [track with { OffsetMilliseconds = 86_400_001 }])));
        var rational = new AVRational { num = 1001, den = 24000 };
        var clock = new AVRational { num = 1, den = 24000 };
        Assert.Equal(100_000_000L * 1001, MediaMuxService.Rescale(100_000_000, rational, clock));
        Assert.Equal(-1001, MediaMuxService.Rescale(-1, rational, clock));
        Assert.Equal(FF.AV_NOPTS_VALUE, MediaMuxService.Rescale(FF.AV_NOPTS_VALUE, rational, clock));
    }

    [FFmpeg.NativeRuntimeFact]
    public void Av3aIsRejectedForMatroskaAndNativeHeaderErrorsCanBeCaptured()
    {
        using var directory = new TemporaryDirectory();
        var sourcePath = Path.Combine(directory.Path, "sample.av3a");
        var bytes = Enumerable.Repeat((byte)0x55, 4 * 1024).ToArray();
        byte[] header = [0xff, 0xf2, 0x10, 0x52, 0x42, 0x11, 0xb8, 0x0c, 0x24];
        for (var offset = 0; offset < bytes.Length; offset += 1024)
            header.CopyTo(bytes, offset);
        File.WriteAllBytes(sourcePath, bytes);
        var service = new MediaMuxService();
        var source = service.ProbeAsync(sourcePath).GetAwaiter().GetResult();
        var track = Assert.Single(source.Streams);
        Assert.Equal("AV3A", track.Codec);
        Assert.DoesNotContain("mkv", track.Containers);
        Assert.DoesNotContain("m2ts", track.Containers);
        Assert.Contains("mp4", track.Containers);
        Assert.Contains("ts", track.Containers);
        var target = Path.Combine(directory.Path, "unsupported.mkv");
        Assert.Equal(MediaMuxError.UnsupportedTrack, Assert.Throws<MediaMuxException>(() =>
            MediaMuxService.Validate(new(target, "mkv", [Select(source, 0)]))).Code);
        unsafe
        {
            using var input = FormatContext.OpenInputUrl(sourcePath);
            input.LoadStreamInfo();
            AVFormatContext* raw = input;
            AVFormatContext* output = null;
            try
            {
                using var capture = new FFmpegLogCapture();
                FFmpegException.Check(FF.avformat_alloc_output_context2(&output, null, "matroska", target));
                var stream = FF.avformat_new_stream(output, null);
                Assert.True(stream != null);
                FFmpegException.Check(FF.avcodec_parameters_copy(stream->codecpar, raw->streams[0]->codecpar));
                FFmpegException.Check(FF.avio_open2(&output->pb, target, 2, null, null));
                Assert.True(FF.avformat_write_header(output, null) < 0);
                Assert.Contains("av3a", capture.Messages, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (output != null)
                {
                    if (output->pb != null)
                        FF.avio_closep(&output->pb);
                    FF.avformat_free_context(output);
                }
            }
        }
    }

    [RemuxExportFact]
    public async Task MultipleSourcesKeepTrackOrderMetadataOffsetsAndCompressedPayloads()
    {
        using var directory = new TemporaryDirectory();
        var service = new MediaMuxService();
        var video = await service.ProbeAsync(Fixture("multi.mp4"));
        var audio = await service.ProbeAsync(Fixture("long-audio.mp4"));
        var subtitle = await service.ProbeAsync(Fixture("events.srt"));
        var path = Path.Combine(directory.Path, "mux.mkv");
        await service.ExportAsync(new(path, "mkv",
            [Select(audio, 1, -250, "jpn", "Japanese"), Select(video, 0),
             Select(subtitle, 0, 500, "chi", "Subtitle") with { Default = false, Forced = true }]));
        var output = await service.ProbeAsync(path);
        Assert.Equal(new[] { AVMediaType.AVMEDIA_TYPE_AUDIO, AVMediaType.AVMEDIA_TYPE_VIDEO, AVMediaType.AVMEDIA_TYPE_SUBTITLE },
            output.Streams.Select(stream => stream.Type));
        Assert.Equal("jpn", output.Streams[0].Language);
        Assert.Equal("Japanese", output.Streams[0].Title);
        Assert.True((output.Streams[2].Disposition & 64) != 0);
        var beforeVideo = Packets(video.Path, 0);
        var beforeAudio = Packets(audio.Path, 1);
        var beforeSubtitle = Packets(subtitle.Path, 0);
        var afterVideo = Packets(path, 1);
        var afterAudio = Packets(path, 0);
        var afterSubtitle = Packets(path, 2);
        Assert.Equal(beforeVideo.Select(item => item.Hash), afterVideo.Select(item => item.Hash));
        Assert.Equal(beforeAudio.Select(item => item.Hash), afterAudio.Select(item => item.Hash));
        Assert.Equal(beforeSubtitle.Select(item => item.Hash), afterSubtitle.Select(item => item.Hash));
        Assert.InRange(afterAudio[0].Time - afterVideo[0].Time - (beforeAudio[0].Time - beforeVideo[0].Time), -0.252, -0.248);
        Assert.InRange(afterSubtitle[0].Time - afterVideo[0].Time - beforeSubtitle[0].Time, 0.498, 0.502);
        // 8 秒视频配 10 秒音频：负偏移通过 MP4 编辑列表跳过音频开头，不推迟视频。
        foreach (var offset in new[] { -2000m, 0m, 2000m })
        {
            var shifted = Path.Combine(directory.Path, $"audio-{offset}.mp4");
            await service.ExportAsync(new(shifted, "mp4", [Select(video, 0), Select(audio, 1, offset)]));
            var shiftedVideo = Packets(shifted, 0);
            var shiftedAudio = Packets(shifted, 1);
            Assert.InRange(shiftedVideo[0].Time, -0.001, 0.001);
            Assert.Equal(beforeVideo.Select(item => item.Hash), shiftedVideo.Select(item => item.Hash));
            // MP4 读取时可能省略编辑列表之前的部分预滚包，剩余音频负载与时间偏移仍应一致。
            Assert.Equal(beforeAudio.TakeLast(shiftedAudio.Length).Select(item => item.Hash), shiftedAudio.Select(item => item.Hash));
            Assert.InRange(shiftedAudio[^1].Time - beforeAudio[^1].Time, (double)offset / 1000 - 0.002, (double)offset / 1000 + 0.002);
            using var input = FormatContext.OpenInputUrl(shifted);
            input.LoadStreamInfo();
            var stream = input.Streams[1];
            var duration = stream.Duration * (double)stream.TimeBase.num / stream.TimeBase.den;
            Assert.InRange(input.Duration / 1_000_000d, Math.Max(8, 10 + (double)offset / 1000) - 0.025,
                Math.Max(8, 10 + (double)offset / 1000) + 0.025);
            Assert.InRange(stream.StartTime * (double)stream.TimeBase.num / stream.TimeBase.den,
                Math.Max(0, (double)offset / 1000) - 0.025, Math.Max(0, (double)offset / 1000) + 0.025);
            Assert.InRange(duration, Math.Min(10, 10 + (double)offset / 1000) - 0.025,
                Math.Min(10, 10 + (double)offset / 1000) + 0.025);
        }
        foreach (var advance in new[] { -500m, -1500m })
        {
            var advanced = Path.Combine(directory.Path, $"advanced-{advance}.mkv");
            await service.ExportAsync(new(advanced, "mkv", [Select(video, 0), Select(subtitle, 0, advance)]));
            Assert.InRange(Packets(advanced, 0)[0].Time, advance == -500 ? -0.002 : 0.498, advance == -500 ? 0.002 : 0.502);
            Assert.InRange(Packets(advanced, 1)[0].Time, advance == -500 ? 0.498 : -0.002, advance == -500 ? 0.502 : 0.002);
        }
    }

    [RemuxExportFact]
    public async Task MatroskaBFramesAndMpegTsBitstreamsRemuxAcrossAllContainers()
    {
        using var directory = new TemporaryDirectory();
        var service = new MediaMuxService();
        foreach (var name in new[] { "multi.mkv", "hdr.mp4" })
        {
            var source = await service.ProbeAsync(Fixture(name));
            var expected = Packets(source.Path, 0);
            var indices = name == "multi.mkv" ? new[] { 0, 1 } : new[] { 0 };
            foreach (var container in MediaMuxService.Containers)
            {
                var path = Path.Combine(directory.Path, name + "." + container);
                await service.ExportAsync(new(path, container, indices.Select(index => Select(source, index)).ToArray()));
                var target = await service.ProbeAsync(path);
                Assert.Equal(indices.Length, target.Streams.Count);
                Assert.Equal(expected.Length, Packets(path, 0).Length);
                if (container is "mp4" or "mkv")
                    Assert.Equal(expected.Select(item => item.Hash), Packets(path, 0).Select(item => item.Hash));
                else if (container == "ts")
                {
                    // TS 的 Annex B / ADTS 包重新封回 MP4，同时覆盖 H.264 和 HEVC。
                    var roundTrip = Path.Combine(directory.Path, name + "." + container + ".mp4");
                    await service.ExportAsync(new(roundTrip, "mp4", indices.Select(index => Select(target, index)).ToArray()));
                    Assert.Equal(expected.Length, Packets(roundTrip, 0).Length);
                    if (name == "hdr.mp4")
                    {
                        using var mp4 = FormatContext.OpenInputUrl(roundTrip);
                        mp4.LoadStreamInfo();
                        Assert.Equal(VideoDynamicRangeDetector.MakeCodecTag('h', 'v', 'c', '1'), mp4.Streams[0].Codecpar!.CodecTag);
                    }
                }
                if (name == "hdr.mp4" && container == "mp4")
                {
                    using var output = FormatContext.OpenInputUrl(path);
                    output.LoadStreamInfo();
                    Assert.Equal(VideoDynamicRangeDetector.MakeCodecTag('h', 'v', 'c', '1'), output.Streams[0].Codecpar!.CodecTag);
                }
            }
        }
    }

    [RemuxExportFact]
    public async Task HevcAnnexBParametersSurviveDirectHvc1AndFastStart()
    {
        using var directory = new TemporaryDirectory();
        var service = new MediaMuxService();
        var source = await service.ProbeAsync(Fixture("dynamic-hevc.ts"));
        var expected = RemuxExportServiceTests.DecodeVideoFrames(source.Path).Select(frame => frame.Hash).ToArray();
        Assert.Equal(50, expected.Length);
        foreach (var fastStart in new[] { false, true })
        {
            var path = Path.Combine(directory.Path, $"dynamic-{fastStart}.mp4");
            await service.ExportAsync(new(path, "mp4", [Select(source, 0)], FastStart: fastStart));
            using (var input = FormatContext.OpenInputUrl(path))
            {
                input.LoadStreamInfo();
                Assert.Equal(VideoDynamicRangeDetector.MakeCodecTag('h', 'v', 'c', '1'), input.Streams[0].Codecpar!.CodecTag);
            }
            Assert.Equal(expected, RemuxExportServiceTests.DecodeVideoFrames(path).Select(frame => frame.Hash));
            var boxes = Mp4Boxes(path);
            Assert.Equal(fastStart, Array.IndexOf(boxes, "moov") < Array.IndexOf(boxes, "mdat"));
            // 已是 MP4 长度前缀的包保持直接复制，继续保留帧内参数。
            var mp4Source = await service.ProbeAsync(path);
            var copied = Path.Combine(directory.Path, $"copied-{fastStart}.mp4");
            await service.ExportAsync(new(copied, "mp4", [Select(mp4Source, 0)]));
            Assert.Equal(expected, RemuxExportServiceTests.DecodeVideoFrames(copied).Select(frame => frame.Hash));
        }
        unsafe
        {
            using var input = FormatContext.OpenInputUrl(Fixture("dynamic-hevc.ts"));
            input.LoadStreamInfo();
            AVFormatContext* raw = input;
            raw->streams[0]->codecpar->codec_tag = VideoDynamicRangeDetector.MakeCodecTag('d', 'v', 'h', 'e');
            Assert.Equal(VideoDynamicRangeDetector.MakeCodecTag('d', 'v', 'h', '1'), RemuxCodecTag.Select("mp4", raw->streams[0]->codecpar));
        }
        Assert.Empty(Directory.GetFiles(directory.Path, ".tscutter-mux-*.tmp"));
    }

    private static string[] Mp4Boxes(string path)
    {
        var data = File.ReadAllBytes(path);
        var result = new List<string>();
        for (var offset = 0; offset < data.Length;)
        {
            var size = checked((int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4)));
            Assert.InRange(size, 8, data.Length - offset);
            result.Add(System.Text.Encoding.ASCII.GetString(data, offset + 4, 4));
            offset += size;
        }
        Assert.Contains("moov", result);
        Assert.Contains("mdat", result);
        return result.ToArray();
    }

    [RemuxExportFact(requireSubtitleEncoders: true)]
    public async Task SubtitleConversionKeepsTextTimingAndCopiedVideoWhileRejectingBitmapToText()
    {
        using var directory = new TemporaryDirectory();
        var service = new MediaMuxService();
        var video = await service.ProbeAsync(Fixture("multi.mp4"));
        foreach (var (name, sourceIndex, container, expectedCodec) in new[]
                 {
                     ("multi.mp4", 3, "mkv", AVCodecID.AV_CODEC_ID_SUBRIP),
                     ("events.srt", 0, "mp4", AVCodecID.AV_CODEC_ID_MOV_TEXT),
                     ("animated.ass", 0, "mp4", AVCodecID.AV_CODEC_ID_MOV_TEXT)
                 })
        {
            var source = await service.ProbeAsync(Fixture(name));
            var track = Select(source, sourceIndex, 250, "chi", "Converted") with { Forced = true };
            Assert.Equal(expectedCodec, track.Stream.SubtitleConversions[container]);
            Assert.Contains(container, track.Stream.Containers);
            var path = Path.Combine(directory.Path, name + "." + container);
            await service.ExportAsync(new(path, container, [Select(video, 0), Select(video, 1), track]));
            var output = await service.ProbeAsync(path);
            Assert.Equal(expectedCodec, output.Streams[2].CodecId);
            Assert.True((output.Streams[2].Disposition & 64) != 0);
            Assert.Equal("chi", output.Streams[2].Language);
            var before = SubtitleEvents(source.Path, sourceIndex);
            var after = SubtitleEvents(path, 2);
            Assert.NotEmpty(before);
            Assert.Equal(before.Length, after.Length);
            for (var index = 0; index < before.Length; index++)
            {
                Assert.InRange(after[index].Time - before[index].Time, 0.249, 0.251);
                Assert.InRange(after[index].Duration - before[index].Duration, -0.001, 0.001);
                Assert.Contains(before[index].Text, after[index].Text);
            }
            Assert.Equal(Packets(video.Path, 0).Select(item => item.Hash), Packets(path, 0).Select(item => item.Hash));
            Assert.Equal(Packets(video.Path, 1).Select(item => item.Hash), Packets(path, 1).Select(item => item.Hash));
        }
        var srt = await service.ProbeAsync(Fixture("events.srt"));
        var ass = await service.ProbeAsync(Fixture("animated.ass"));
        var multiple = Path.Combine(directory.Path, "multiple-conversions.mp4");
        await service.ExportAsync(new(multiple, "mp4", [Select(video, 0), Select(srt, 0), Select(ass, 0)]));
        Assert.Equal(SubtitleEvents(srt.Path, 0), SubtitleEvents(multiple, 1));
        Assert.Equal(SubtitleEvents(ass.Path, 0), SubtitleEvents(multiple, 2));
        var advanced = Path.Combine(directory.Path, "advanced.mp4");
        await service.ExportAsync(new(advanced, "mp4", [Select(video, 0), Select(srt, 0, -1500)]));
        var advancedEvents = SubtitleEvents(advanced, 1);
        Assert.Equal(2, advancedEvents.Length);
        Assert.InRange(advancedEvents[0].Time, 0, 0.001);
        Assert.InRange(advancedEvents[0].Duration, 3.499, 3.501);
        Assert.InRange(advancedEvents[1].Time, 3.999, 4.001);
        Assert.InRange(advancedEvents[1].Duration, 1.999, 2.001);
        Assert.InRange(Packets(advanced, 0)[0].Time, -0.001, 0.001);
        var copied = Path.Combine(directory.Path, "advanced-copy.mp4");
        await service.ExportAsync(new(copied, "mp4", [Select(video, 0), Select(video, 3, -1500)]));
        Assert.Equal(advancedEvents, SubtitleEvents(copied, 1));
        var skipped = Path.Combine(directory.Path, "skipped-subtitle.mp4");
        await service.ExportAsync(new(skipped, "mp4", [Select(video, 0), Select(srt, 0, -5000)]));
        var remaining = Assert.Single(SubtitleEvents(skipped, 1));
        Assert.InRange(remaining.Time, 0.499, 0.501);
        Assert.InRange(remaining.Duration, 1.999, 2.001);
        var bitmap = await service.ProbeAsync(Fixture("bitmap.mkv"));
        var bitmapTrack = bitmap.Streams.Single(stream => stream.Type == AVMediaType.AVMEDIA_TYPE_SUBTITLE);
        Assert.Empty(bitmapTrack.SubtitleConversions);
        Assert.DoesNotContain("mp4", bitmapTrack.Containers);
        Assert.Equal(MediaMuxError.UnsupportedTrack, Assert.Throws<MediaMuxException>(() =>
            MediaMuxService.Validate(new(Path.Combine(directory.Path, "bitmap.mp4"), "mp4", [Select(bitmap, bitmapTrack.Index)]))).Code);
    }

    [RemuxExportFact(requireSubtitleEncoders: true)]
    public async Task ShortestUsesIndividualOffsetTrackEndsAndOptionalSubtitles()
    {
        using var directory = new TemporaryDirectory();
        var service = new MediaMuxService();
        var source = await service.ProbeAsync(Fixture("long-audio.mp4"));
        var subtitles = await service.ProbeAsync(Fixture("events.srt"));
        var originalVideo = Packets(source.Path, 0);
        Assert.False(new MediaMuxRequest("output.mp4", "mp4", []).Shortest);
        Assert.False(new MediaMuxRequest("output.mp4", "mp4", []).FastStart);
        // 同一文件中视频 8 秒、音频 10 秒，不能用源文件总时长判断 shortest。
        foreach (var container in MediaMuxService.Containers)
        foreach (var offset in new[] { -3000m, 0m, 2000m })
        {
            var path = Path.Combine(directory.Path, $"shortest-{offset}.{container}");
            await service.ExportAsync(new(path, container, [Select(source, 0), Select(source, 1, offset)], Shortest: true));
            var result = await service.ProbeAsync(path);
            var end = offset < 0 ? 7 : 8;
            // MKV/TS 会平移负起点，输出总时长还包含视频开始前保留的音频。
            var muxedEnd = end - (container == "mp4" ? 0 : Math.Min(0, (double)offset / 1000));
            Assert.InRange(result.Duration, muxedEnd - 0.25, muxedEnd + 0.25);
            var videoPackets = Packets(path, 0);
            Assert.InRange(videoPackets.Max(item => item.Time) - videoPackets[0].Time, end - 0.1, end + 0.2);
            if (container is "mp4" or "mkv")
            {
                var packets = Packets(path, 0);
                Assert.Equal(originalVideo.Take(packets.Length).Select(item => item.Hash), packets.Select(item => item.Hash));
            }
        }
        foreach (var includeSubtitles in new[] { false, true })
        {
            var path = Path.Combine(directory.Path, $"subtitles-{includeSubtitles}.mp4");
            await service.ExportAsync(new(path, "mp4", [Select(source, 0), Select(source, 1), Select(subtitles, 0)],
                Shortest: true, ShortestIncludesSubtitles: includeSubtitles));
            var result = await service.ProbeAsync(path);
            var end = includeSubtitles ? 7.5 : 8;
            Assert.InRange(result.Duration, end - 0.15, end + 0.15);
            Assert.All(SubtitleEvents(path, 2), item => Assert.True(item.Time + item.Duration <= end + 0.001));
        }
        // 长字幕跨过最短音视频终点时，仅保留输出范围内的显示时间。
        var sparse = await service.ProbeAsync(Fixture("sparse.srt"));
        var clipped = Path.Combine(directory.Path, "clipped-subtitle.mp4");
        await service.ExportAsync(new(clipped, "mp4", [Select(source, 0), Select(sparse, 0)], Shortest: true));
        var last = Assert.Single(SubtitleEvents(clipped, 1));
        Assert.InRange(last.Time + last.Duration, 7.999, 8.001);
        var missingFirst = Path.Combine(directory.Path, "missing-duration-first.mp4");
        await service.ExportAsync(new(missingFirst, "mp4", [Select(sparse, 0), Select(source, 0)],
            Shortest: true, ShortestIncludesSubtitles: true));
        var first = Assert.Single(SubtitleEvents(missingFirst, 0));
        Assert.InRange(first.Time + first.Duration, 7.999, 8.001);
        // Matroska 的 DURATION 已包含字幕起点，也允许九位小数。
        var matroska = await service.ProbeAsync(Fixture("multi.mkv"));
        var metadata = Path.Combine(directory.Path, "metadata.mkv");
        await service.ExportAsync(new(metadata, "mkv", [Select(matroska, 0), Select(matroska, 3)],
            Shortest: true, ShortestIncludesSubtitles: true));
        Assert.InRange((await service.ProbeAsync(metadata)).Duration, 7.35, 7.65);
        var empty = Path.Combine(directory.Path, "empty.mp4");
        await File.WriteAllTextAsync(empty, "existing");
        var error = await Assert.ThrowsAsync<MediaMuxException>(() => service.ExportAsync(new(empty, "mp4",
            [Select(source, 0), Select(source, 1, -11000)], Shortest: true)));
        Assert.Equal(MediaMuxError.NoPackets, error.Code);
        Assert.Equal("existing", await File.ReadAllTextAsync(empty));
    }

    [RemuxExportFact]
    public async Task FailuresAndCancellationKeepExistingOutputAndRemoveTemporaryFiles()
    {
        using var directory = new TemporaryDirectory();
        var service = new MediaMuxService();
        var source = await service.ProbeAsync(Fixture("multi.mp4"));
        var path = Path.Combine(directory.Path, "out.mkv");
        await File.WriteAllTextAsync(path, "original");
        var changed = source with { Size = source.Size + 1 };
        await Assert.ThrowsAsync<MediaMuxException>(() => service.ExportAsync(new(path, "mkv", [Select(changed, 0)])));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExportAsync(
            new(path, "mkv", [Select(source, 0)]), new InlineProgress<MediaMuxProgress>(_ => cancellation.Cancel()), cancellation.Token));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(directory.Path, ".tscutter-mux-*.tmp"));
    }

    private static unsafe (double Time, double Duration, string Text)[] SubtitleEvents(string path, int index)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        AVFormatContext* context = input;
        var stream = context->streams[index];
        var decoder = FF.avcodec_alloc_context3(FF.avcodec_find_decoder(stream->codecpar->codec_id));
        Assert.True(decoder != null);
        var events = new List<(double, double, string)>();
        try
        {
            FFmpegException.Check(FF.avcodec_parameters_to_context(decoder, stream->codecpar));
            decoder->pkt_timebase = stream->time_base;
            FFmpegException.Check(FF.avcodec_open2(decoder, null, null));
            foreach (var packet in input.ReadPackets())
            {
                if (packet.StreamIndex != index)
                    continue;
                AVSubtitle subtitle = default;
                var got = 0;
                try
                {
                    FFmpegException.Check(FF.avcodec_decode_subtitle2(decoder, &subtitle, &got, packet));
                    if (got == 0 || subtitle.num_rects == 0)
                        continue;
                    for (var rectangle = 0; rectangle < subtitle.num_rects; rectangle++)
                    {
                        var ass = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)subtitle.rects[rectangle]->ass)!;
                        var text = ass.Split(',', 9).Last();
                        text = System.Text.RegularExpressions.Regex.Replace(text, @"\{[^}]*\}", "");
                        events.Add((subtitle.pts / 1_000_000d + subtitle.start_display_time / 1000d,
                            (subtitle.end_display_time - subtitle.start_display_time) / 1000d, text));
                    }
                }
                finally { FF.avsubtitle_free(&subtitle); }
            }
        }
        finally { FF.avcodec_free_context(&decoder); }
        return events.ToArray();
    }

    private static unsafe (double Time, string Hash)[] Packets(string path, int index)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        var result = new List<(double, string)>();
        var lastDts = FF.AV_NOPTS_VALUE;
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex != index)
                continue;
            AVPacket* raw = packet;
            if (raw->dts != FF.AV_NOPTS_VALUE)
            {
                Assert.True(lastDts == FF.AV_NOPTS_VALUE || raw->dts >= lastDts);
                lastDts = raw->dts;
            }
            var timeBase = input.Streams[index].TimeBase;
            result.Add((raw->pts * (double)timeBase.num / timeBase.den,
                Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>(raw->data, raw->size)))));
        }
        return result.ToArray();
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tscutter-mux-test-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
