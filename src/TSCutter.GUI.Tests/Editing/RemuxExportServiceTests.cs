using System.Security.Cryptography;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using Xunit;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Utils.Media;

namespace TSCutter.GUI.Tests.Editing;

public sealed class RemuxExportFactAttribute : FactAttribute
{
    public RemuxExportFactAttribute(string? requiredVariable = null)
    {
        var sampleVariable = requiredVariable ?? "TSCUTTER_REMUX_TEST_SAMPLES";
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(sampleVariable)))
            Skip = "需要设置 " + sampleVariable + " 和 FFmpeg 9 运行库目录。";
    }
}

public sealed class RemuxExportServiceTests
{
    private static string Fixture(string name) => Path.Combine(Environment.GetEnvironmentVariable("TSCUTTER_REMUX_TEST_SAMPLES")!, name);
    private static RemuxExportSettings Settings(string container, params int[] indices) => new()
    {
        Container = container, ReferenceVideoIndex = 0, TimelineOriginSeconds = 0, StreamIndices = [.. indices]
    };

    [Theory]
    [InlineData("mp4", true)]
    [InlineData("mkv", true)]
    [InlineData("ts", false)]
    public void SettingsRejectUnsupportedContainersAndDuplicateTracks(string container, bool expected)
    {
        var settings = Settings(container, 0, 1);
        Assert.Equal(expected, settings.IsValid);
        settings.StreamIndices.Add(0);
        Assert.False(settings.IsValid);
    }

    [Fact]
    public void ValidationProtectsEverySourceAndRequiresMatchingContainers()
    {
        var source = Path.Combine(Path.GetTempPath(), "source.mp4");
        var segment = new RemuxExportSegment(source, 0, 2, Settings("mp4", 0));
        Assert.Equal(RemuxExportError.SameFile, Assert.Throws<RemuxExportException>(() =>
            RemuxExportService.Validate(new RemuxExportRequest(source, [segment]))).Code);
        Assert.Equal(RemuxExportError.UnsupportedContainer, Assert.Throws<RemuxExportException>(() =>
            RemuxExportService.Validate(new RemuxExportRequest(Path.ChangeExtension(source, ".mkv"), [segment]))).Code);
        var copy = segment.Settings.Snapshot();
        copy.StreamIndices.Clear();
        Assert.Single(segment.Settings.StreamIndices);
    }

    [Fact]
    public void SourceProtectionRecognizesDirectoryAliasesAndCaseVariants()
    {
        using var files = new TemporaryOutput("mp4");
        var root = Path.GetDirectoryName(files.Path)!;
        var sourceDirectory = Path.Combine(root, "source");
        Directory.CreateDirectory(sourceDirectory);
        var source = Path.Combine(sourceDirectory, "Source.mp4");
        File.WriteAllText(source, "source must survive");
        void AssertProtected(string output, string? input = null) => Assert.Equal(RemuxExportError.SameFile,
            Assert.Throws<RemuxExportException>(() => RemuxExportService.Validate(new RemuxExportRequest(output,
                [new(input ?? source, 0, 2, Settings("mp4", 0))]))).Code);
        var caseVariant = Path.Combine(sourceDirectory, "source.mp4");
        if (File.Exists(caseVariant)) AssertProtected(caseVariant);
        if (!OperatingSystem.IsWindows())
        {
            var alias = Path.Combine(root, "alias");
            Directory.CreateSymbolicLink(alias, sourceDirectory);
            AssertProtected(Path.Combine(alias, "Source.mp4"));
        }
        var unicodeSource = Path.Combine(sourceDirectory, "Café.mp4");
        File.WriteAllText(unicodeSource, "source must survive");
        var unicodeAlias = Path.Combine(sourceDirectory, "Cafe\u0301.mp4");
        if (OperatingSystem.IsMacOS() && File.Exists(unicodeAlias)) AssertProtected(unicodeAlias, unicodeSource);
        Assert.Equal("source must survive", File.ReadAllText(source));
    }

    [RemuxExportFact]
    public async Task Mp4CutPreservesCompressedVideoMultipleAudioAndClippedSubtitles()
    {
        var source = Fixture("multi.mp4");
        using var target = new TemporaryOutput("mp4");
        await new RemuxExportService().ExportAsync(new RemuxExportRequest(target.Path,
            [new(source, 2, 4, Settings("mp4", 0, 1, 2, 3))]));
        using var input = Open(target.Path);
        Assert.Equal(4, input.Streams.Count(stream => stream.Codecpar!.CodecType != AVMediaType.AVMEDIA_TYPE_DATA));
        Assert.Equal(2, input.Streams.Count(stream => stream.Codecpar!.CodecType == AVMediaType.AVMEDIA_TYPE_AUDIO));
        Assert.InRange(input.Duration / 1e6, 1.99, 2.1);
        Assert.Equal(HashPackets(source, 2, 4), HashPackets(target.Path, 0, 2));
        Assert.Equal(HashPackets(source, 2, 4, 1), HashPackets(target.Path, 0, 2, 1));
        Assert.Equal(HashPackets(source, 2, 4, 2), HashPackets(target.Path, 0, 2, 2));
        AssertSubtitleRange(target.Path, 0, 2);
        AssertMetadataAndChapters(input, "Export regression", 2);
        AssertMonotonicDts(target.Path);
    }

    [RemuxExportFact]
    public async Task BoundedCutsWithoutSubtitlesKeepSelectedMediaAndAttachments()
    {
        foreach (var name in new[] { "multi.mp4", "multi.mkv", "cover.mp4" })
        {
            var source = Fixture(name);
            using var original = Open(source);
            var indices = original.Streams.Where(stream => stream.Codecpar!.CodecType is not
                (AVMediaType.AVMEDIA_TYPE_SUBTITLE or AVMediaType.AVMEDIA_TYPE_DATA))
                .Select(stream => stream.Index).ToArray();
            var container = Path.GetExtension(source)[1..];
            var origin = VideoStart(source);
            var settings = Settings(container, indices);
            settings.TimelineOriginSeconds = origin;
            using var target = new TemporaryOutput(container);
            await new RemuxExportService().ExportAsync(new RemuxExportRequest(target.Path,
                [new(source, 2, 4, settings)]));
            using var exported = Open(target.Path);
            Assert.DoesNotContain(exported.Streams, stream => stream.Codecpar!.CodecType == AVMediaType.AVMEDIA_TYPE_SUBTITLE);
            Assert.Equal(indices.Length, exported.Streams.Count(stream => stream.Codecpar!.CodecType != AVMediaType.AVMEDIA_TYPE_DATA));
            Assert.InRange(exported.Duration / 1e6, 1.99, 2.1);
            for (var i = 0; i < indices.Length; i++)
            {
                var stream = original.Streams[indices[i]];
                if (stream.IsAttachedPicture) AssertCover(stream, Assert.Single(exported.Streams, value => value.IsAttachedPicture));
                else if (stream.Codecpar!.CodecType is AVMediaType.AVMEDIA_TYPE_VIDEO or AVMediaType.AVMEDIA_TYPE_AUDIO)
                    Assert.Equal(HashPackets(source, 2 + origin, 4 + origin, indices[i]), HashPackets(target.Path, 0, 2, i));
                else Assert.Equal(stream.Codecpar.CodecType, exported.Streams[i].Codecpar!.CodecType);
            }
            AssertMonotonicDts(target.Path, allowMissingInitialVideoDts: container == "mkv");
        }
    }

    [RemuxExportFact]
    public async Task Mp4TrackNamesSurviveExportWithAndWithoutCoverArt()
    {
        foreach (var name in new[] { "multi.mp4", "cover.mp4" })
        {
            var source = Fixture(name);
            using var original = Open(source);
            var indices = original.Streams.Where(stream => stream.Codecpar!.CodecType != AVMediaType.AVMEDIA_TYPE_DATA)
                .Select(stream => stream.Index).ToArray();
            var subtitle = Assert.Single(original.Streams, stream => stream.Codecpar!.CodecType == AVMediaType.AVMEDIA_TYPE_SUBTITLE);
            Assert.True(subtitle.Metadata.TryGetValue("name", out var title));
            Assert.Equal("简体中文字幕", title);
            Assert.Equal(title, RemuxExportService.DescribeTracks(original).Single(track => track.Index == subtitle.Index).Title);
            Assert.Contains(title, TSCutter.GUI.Utils.Media.PreviewStreamUtil.CreateSubtitleOptions(original, null).Single().DisplayText);
            using var target = new TemporaryOutput("mp4");
            await new RemuxExportService().ExportAsync(new RemuxExportRequest(target.Path,
                [new(source, 2, 4, Settings("mp4", indices))]));
            using var exported = Open(target.Path);
            var actual = Assert.Single(exported.Streams, stream => stream.Codecpar!.CodecType == AVMediaType.AVMEDIA_TYPE_SUBTITLE);
            Assert.True(actual.Metadata.TryGetValue("name", out var actualTitle));
            Assert.Equal(title, actualTitle);
            Assert.True(actual.Metadata.TryGetValue("handler_name", out var handler));
            Assert.Equal("SubtitleHandler", handler);
            Assert.True(exported.Streams[1].Metadata.TryGetValue("name", out var audioTitle));
            Assert.Equal("English", audioTitle);
            Assert.True(exported.Streams[2].Metadata.TryGetValue("name", out audioTitle));
            Assert.Equal("Japanese", audioTitle);
        }
    }

    [RemuxExportFact]
    public async Task MkvCutRetainsAssAttachmentAndChapterTimes()
    {
        var source = Fixture("multi.mkv");
        using var target = new TemporaryOutput("mkv");
        var origin = VideoStart(source);
        var settings = Settings("mkv", 0, 1, 2, 3, 4);
        settings.TimelineOriginSeconds = origin;
        await new RemuxExportService().ExportAsync(new RemuxExportRequest(target.Path, [new(source, 2, 4, settings)]));
        using var input = Open(target.Path);
        Assert.Equal(5, input.Streams.Count);
        Assert.True(input.Streams[3].Metadata.TryGetValue("title", out var subtitleTitle));
        Assert.Equal("简体中文字幕", subtitleTitle);
        Assert.Contains(input.Streams, stream => stream.Codecpar!.CodecType == AVMediaType.AVMEDIA_TYPE_ATTACHMENT);
        Assert.Equal(HashPackets(source, 2 + origin, 4 + origin), HashPackets(target.Path, 0, 2));
        AssertSubtitleRange(target.Path, 0, 2);
        AssertMetadataAndChapters(input, "Export regression", 2);
        using var merged = new TemporaryOutput("mkv");
        await new RemuxExportService().ExportAsync(new RemuxExportRequest(merged.Path,
            [new(source, 0, 2, settings), new(source, 4, 6, settings)]));
        AssertSubtitleRange(merged.Path, 0, 4);
        AssertUniqueAssEvents(merged.Path);
    }

    [RemuxExportFact]
    public async Task NonAdjacentRangesMergeWithoutVideoReencoding()
    {
        var source = Fixture("multi.mp4");
        using var target = new TemporaryOutput("mp4");
        await new RemuxExportService().ExportAsync(new RemuxExportRequest(target.Path,
            [new(source, 0, 2, Settings("mp4", 0, 1, 2, 3)), new(source, 4, 6, Settings("mp4", 0, 1, 2, 3))]));
        using var input = Open(target.Path);
        Assert.InRange(input.Duration / 1e6, 3.99, 4.1);
        var expected = HashPackets(source, 0, 2).Concat(HashPackets(source, 4, 6)).ToArray();
        Assert.Equal(expected, HashPackets(target.Path, 0, 4));
        AssertMonotonicDts(target.Path);
        AssertSubtitleRange(target.Path, 0, 4);
    }

    [RemuxExportFact]
    public async Task QueueCanMergeSeparateCompatibleFiles()
    {
        var source = Fixture("multi.mp4");
        using var part = new TemporaryOutput("mp4");
        using var target = new TemporaryOutput("mp4");
        var service = new RemuxExportService();
        await service.ExportAsync(new RemuxExportRequest(part.Path, [new(source, 2, 4, Settings("mp4", 0, 1))]));
        await service.ExportAsync(new RemuxExportRequest(target.Path,
            [new(source, 0, 2, Settings("mp4", 0, 1)), new(part.Path, 0, null, Settings("mp4", 0, 1))]));
        Assert.Equal(HashPackets(source, 0, 4), HashPackets(target.Path, 0, 4));
        AssertMonotonicDts(target.Path);
    }

    [RemuxExportFact]
    public async Task EofKeepsLongerAudioAndMergeStartsAfterAllRetainedTracks()
    {
        var source = Fixture("long-audio.mp4");
        using var target = new TemporaryOutput("mp4");
        await new RemuxExportService().ExportAsync(new RemuxExportRequest(target.Path,
            [new(source, 0, null, Settings("mp4", 0, 1)), new(source, 0, 2, Settings("mp4", 0, 1))]));
        using var output = Open(target.Path);
        Assert.InRange(output.Duration / 1e6, 11.99, 12.03);
        Assert.Equal(HashPackets(source, 0, double.MaxValue, 1), HashPackets(target.Path, 0, 10, 1));
        AssertLastChapterStarts(output, 10);
        AssertMonotonicDts(target.Path);
        using var videoOnly = new TemporaryOutput("mp4");
        await new RemuxExportService().ExportAsync(new RemuxExportRequest(videoOnly.Path,
            [new(source, 0, null, Settings("mp4", 0)), new(source, 0, null, Settings("mp4", 0))]));
        using var videoOutput = Open(videoOnly.Path);
        Assert.InRange(videoOutput.Duration / 1e6, 15.99, 16.03);
        AssertLastChapterStarts(videoOutput, 11);
    }

    private static unsafe void AssertLastChapterStarts(FormatContext input, double seconds)
    {
        AVFormatContext* raw = input;
        Assert.True(raw->nb_chapters > 0);
        var chapter = raw->chapters[raw->nb_chapters - 1];
        Assert.InRange(chapter->start * (double)chapter->time_base.num / chapter->time_base.den, seconds - 0.002, seconds + 0.002);
    }

    [RemuxExportFact]
    public async Task LongSubtitleIsClippedWithoutLosingLaterOverlappingEvents()
    {
        var source = Fixture("overlapping.mkv");
        using var target = new TemporaryOutput("mkv");
        var settings = Settings("mkv", 0, 1, 2);
        settings.TimelineOriginSeconds = VideoStart(source);
        await new RemuxExportService().ExportAsync(new RemuxExportRequest(target.Path, [new(source, 2, 4, settings)]));
        AssertOverlappingSubtitleTimes(target.Path);
    }

    private static unsafe void AssertOverlappingSubtitleTimes(string path)
    {
        using var input = Open(path);
        var times = new List<(double Start, double End)>();
        foreach (var packet in input.ReadPackets())
        {
            var stream = input.Streams[packet.StreamIndex];
            if (stream.Codecpar!.CodecType != AVMediaType.AVMEDIA_TYPE_SUBTITLE) continue;
            AVPacket* raw = packet;
            var scale = stream.TimeBase.num / (double)stream.TimeBase.den;
            times.Add((raw->pts * scale, (raw->pts + raw->duration) * scale));
        }
        Assert.Equal(2, times.Count);
        Assert.InRange(times[0].Start, 0, 0.002);
        Assert.InRange(times[0].End, 1.998, 2.002);
        Assert.InRange(times[1].Start, 0.498, 0.502);
        Assert.InRange(times[1].End, 1.498, 1.502);
    }

    [RemuxExportFact]
    public async Task AnimatedAssEventsMustBeRetainedInFull()
    {
        var source = Fixture("animated.mkv");
        var settings = Settings("mkv", 0, 1, 2);
        settings.TimelineOriginSeconds = VideoStart(source);
        using var target = new TemporaryOutput("mkv");
        var error = await Assert.ThrowsAsync<RemuxExportException>(() => new RemuxExportService().ExportAsync(
            new RemuxExportRequest(target.Path, [new(source, 2, 4, settings)])));
        Assert.Equal(RemuxExportError.SubtitleTiming, error.Code);
        Assert.False(File.Exists(target.Path));
        await new RemuxExportService().ExportAsync(new RemuxExportRequest(target.Path, [new(source, 0, 6, settings)]));
        AssertSubtitleRange(target.Path, 0, 6);
    }

    [RemuxExportFact]
    public async Task OpenGopCutIsRejectedBeforeWritingAndWholeFileCanStillBeCopied()
    {
        var source = Fixture("open-gop.mp4");
        using var target = new TemporaryOutput("mp4");
        var error = await Assert.ThrowsAsync<RemuxExportException>(() => new RemuxExportService().ExportAsync(
            new RemuxExportRequest(target.Path, [new(source, 0, 2, Settings("mp4", 0))])));
        Assert.Equal(RemuxExportError.UnsafeGop, error.Code);
        Assert.False(File.Exists(target.Path));
        await new RemuxExportService().ExportAsync(new RemuxExportRequest(target.Path,
            [new(source, 0, null, Settings("mp4", 0))]));
        Assert.Equal(HashPackets(source, 0, double.MaxValue), HashPackets(target.Path, 0, double.MaxValue));
    }

    [RemuxExportFact]
    public async Task HdrColorParametersAndHevcPayloadSurviveExport()
    {
        var source = Fixture("hdr.mp4");
        using var target = new TemporaryOutput("mp4");
        await new RemuxExportService().ExportAsync(new RemuxExportRequest(target.Path, [new(source, 0, 2, Settings("mp4", 0))]));
        Assert.Equal(HashPackets(source, 0, 2), HashPackets(target.Path, 0, 2));
        AssertVideoParameters(source, target.Path);
    }

    [RemuxExportFact("TSCUTTER_DOVI_TEST_SAMPLE")]
    public async Task DolbyVisionConfigAndRpuPayloadSurviveMp4AndMkvExports()
    {
        var source = Environment.GetEnvironmentVariable("TSCUTTER_DOVI_TEST_SAMPLE")!;
        var expected = HashPackets(source, 0, double.MaxValue);
        foreach (var container in new[] { "mp4", "mkv" })
        {
            using var target = new TemporaryOutput(container);
            var settings = Settings(container, 0, 1);
            settings.TimelineOriginSeconds = VideoStart(source);
            await new RemuxExportService().ExportAsync(new RemuxExportRequest(target.Path,
                [new(source, 0, 2, settings)]));
            var actual = HashPackets(target.Path, 0, double.MaxValue);
            Assert.NotEmpty(actual);
            Assert.Equal(expected.Take(actual.Length), actual);
            AssertVideoParameters(source, target.Path);
        }
    }

    [RemuxExportFact("TSCUTTER_AV3A_TEST_SAMPLE")]
    public async Task AudioVividPacketsAndConfigurationSurviveMp4Export()
    {
        var source = Environment.GetEnvironmentVariable("TSCUTTER_AV3A_TEST_SAMPLE")!;
        using var original = Open(source);
        var audio = Assert.Single(original.Streams, stream => stream.Codecpar!.IsAv3a);
        Assert.True(audio.Codecpar!.HasReliableCodecParameters);
        var settings = Settings("mp4", original.GetVideoStream().Index, audio.Index);
        settings.ReferenceVideoIndex = original.GetVideoStream().Index;
        settings.TimelineOriginSeconds = VideoStart(source);
        using var target = new TemporaryOutput("mp4");
        await new RemuxExportService().ExportAsync(new RemuxExportRequest(target.Path,
            [new(source, 0, null, settings)]));
        Assert.Equal(HashPackets(source, settings.TimelineOriginSeconds, double.MaxValue, audio.Index),
            HashPackets(target.Path, 0, double.MaxValue, 1));
        AssertAv3aParameters(source, audio.Index, target.Path);
        AssertMonotonicDts(target.Path);
        using var mkv = new TemporaryOutput("mkv");
        settings.Container = "mkv";
        var error = await Assert.ThrowsAsync<RemuxExportException>(() => new RemuxExportService().ExportAsync(
            new RemuxExportRequest(mkv.Path, [new(source, 0, null, settings)])));
        Assert.Equal(RemuxExportError.UnsupportedTrack, error.Code);
        Assert.False(File.Exists(mkv.Path));
    }

    [RemuxExportFact("TSCUTTER_AV3A_TEST_SAMPLE")]
    public async Task AudioVividRangesAndSeparateFilesMergeInRequestedOrder()
    {
        var source = Environment.GetEnvironmentVariable("TSCUTTER_AV3A_TEST_SAMPLE")!;
        using var original = Open(source);
        var video = original.GetVideoStream();
        var audio = Assert.Single(original.Streams, stream => stream.Codecpar!.IsAv3a);
        var origin = VideoStart(source);
        var boundaries = VideoKeyframeTimes(source, 12);
        Assert.Equal(12, boundaries.Length);
        var ranges = new[] { (boundaries[1], boundaries[3]), (boundaries[5], boundaries[7]),
            (boundaries[9], boundaries[11]) };
        var settings = Settings("mp4", video.Index, audio.Index);
        settings.ReferenceVideoIndex = video.Index;
        settings.TimelineOriginSeconds = origin;
        var service = new RemuxExportService();
        using var direct = new TemporaryOutput("mp4");
        await service.ExportAsync(new RemuxExportRequest(direct.Path, ranges.Select(range =>
            new RemuxExportSegment(source, range.Item1 - origin, range.Item2 - origin, settings)).ToArray()));
        using var first = new TemporaryOutput("mp4");
        using var second = new TemporaryOutput("mp4");
        using var third = new TemporaryOutput("mp4");
        var parts = new[] { first.Path, second.Path, third.Path };
        for (var i = 0; i < parts.Length; i++)
            await service.ExportAsync(new RemuxExportRequest(parts[i],
                [new(source, ranges[i].Item1 - origin, ranges[i].Item2 - origin, settings)]));
        using var queued = new TemporaryOutput("mp4");
        var order = new[] { 2, 0, 1 };
        await service.ExportAsync(new RemuxExportRequest(queued.Path, order.Select(index =>
            new RemuxExportSegment(parts[index], 0, null, Settings("mp4", 0, 1))).ToArray()));
        for (var track = 0; track < 2; track++)
        {
            var sourceIndex = track == 0 ? video.Index : audio.Index;
            var hashes = ranges.Select(range => HashPackets(source, range.Item1, range.Item2, sourceIndex)).ToArray();
            Assert.Equal(hashes.SelectMany(value => value).ToArray(), HashPackets(direct.Path, 0, double.MaxValue, track));
            Assert.Equal(order.SelectMany(index => hashes[index]).ToArray(), HashPackets(queued.Path, 0, double.MaxValue, track));
        }
        foreach (var path in new[] { direct.Path, queued.Path })
        {
            using var output = Open(path);
            var expectedDuration = ranges.Sum(range => range.Item2 - range.Item1);
            Assert.InRange(output.Duration / 1e6, expectedDuration - 0.01, expectedDuration + 0.1);
            AssertMonotonicDts(path);
            AssertAv3aParameters(source, audio.Index, path);
        }
    }

    private static unsafe double[] VideoKeyframeTimes(string path, int count)
    {
        using var input = Open(path);
        var video = input.GetVideoStream();
        var times = new List<double>();
        foreach (var packet in input.ReadPackets())
        {
            AVPacket* raw = packet;
            if (packet.StreamIndex != video.Index || (raw->flags & 1) == 0) continue;
            times.Add(raw->pts * (double)video.TimeBase.num / video.TimeBase.den);
            if (times.Count == count) break;
        }
        return times.ToArray();
    }

    private static unsafe void AssertAv3aParameters(string source, int index, string target)
    {
        using var original = Open(source);
        using var exported = Open(target);
        var audio = Assert.Single(exported.Streams, stream => stream.Codecpar!.IsAv3a);
        AVStream* left = original.Streams[index], right = audio;
        var a = left->codecpar;
        var b = right->codecpar;
        Assert.Equal((a->codec_id, a->sample_rate, a->ch_layout.nb_channels, a->bits_per_raw_sample),
            (b->codec_id, b->sample_rate, b->ch_layout.nb_channels, b->bits_per_raw_sample));
        Assert.True(a->extradata_size > 0);
        Assert.Equal(new ReadOnlySpan<byte>(a->extradata, a->extradata_size).ToArray(),
            new ReadOnlySpan<byte>(b->extradata, b->extradata_size).ToArray());
    }

    [RemuxExportFact]
    public async Task CoverPictureIsWrittenOnce()
    {
        var source = Fixture("cover.mp4");
        using var original = Open(source);
        var indices = original.Streams.Where(stream => stream.Codecpar!.CodecType != AVMediaType.AVMEDIA_TYPE_DATA).Select(stream => stream.Index).ToArray();
        using var target = new TemporaryOutput("mp4");
        await new RemuxExportService().ExportAsync(new RemuxExportRequest(target.Path, [new(source, 2, 4, Settings("mp4", indices))]));
        using var exported = Open(target.Path);
        var expected = Assert.Single(original.Streams, stream => stream.IsAttachedPicture);
        var actual = Assert.Single(exported.Streams, stream => stream.IsAttachedPicture);
        AssertCover(expected, actual);
    }

    private static unsafe void AssertCover(MediaStream expected, MediaStream actual)
    {
        AVStream* left = expected, right = actual;
        Assert.Equal(new ReadOnlySpan<byte>(left->attached_pic.data, left->attached_pic.size).ToArray(),
            new ReadOnlySpan<byte>(right->attached_pic.data, right->attached_pic.size).ToArray());
    }

    private static unsafe void AssertVideoParameters(string source, string target)
    {
        using var original = Open(source);
        using var exported = Open(target);
        AVStream* left = original.GetVideoStream(), right = exported.GetVideoStream();
        var a = left->codecpar;
        var b = right->codecpar;
        Assert.Equal((a->color_primaries, a->color_trc, a->color_space, a->color_range, a->format),
            (b->color_primaries, b->color_trc, b->color_space, b->color_range, b->format));
        Assert.Equal(a->nb_coded_side_data, b->nb_coded_side_data);
        for (var i = 0; i < a->nb_coded_side_data; i++)
        {
            var x = a->coded_side_data[i];
            var y = b->coded_side_data[i];
            Assert.Equal(x.type, y.type);
            Assert.Equal(new ReadOnlySpan<byte>(x.data, checked((int)x.size)).ToArray(),
                new ReadOnlySpan<byte>(y.data, checked((int)y.size)).ToArray());
        }
    }

    [RemuxExportFact]
    public async Task IncompatibleTracksAndContainerNeverReplaceExistingOutput()
    {
        var source = Fixture("multi.mp4");
        using var target = new TemporaryOutput("mkv");
        var original = new byte[] { 1, 2, 3 };
        await File.WriteAllBytesAsync(target.Path, original);
        var error = await Assert.ThrowsAsync<RemuxExportException>(() => new RemuxExportService().ExportAsync(
            new RemuxExportRequest(target.Path, [new(source, 0, 2, Settings("mkv", 0, 3))])));
        Assert.Equal(RemuxExportError.UnsupportedTrack, error.Code);
        Assert.Equal(original, await File.ReadAllBytesAsync(target.Path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target.Path)!, ".tscutter-*.tmp"));
        using var mp4 = new TemporaryOutput("mp4");
        var mismatch = await Assert.ThrowsAsync<RemuxExportException>(() => new RemuxExportService().ExportAsync(
            new RemuxExportRequest(mp4.Path, [new(source, 0, 2, Settings("mp4", 0, 1)),
                new(source, 4, 6, Settings("mp4", 0, 2))])));
        Assert.Equal(RemuxExportError.IncompatibleStreams, mismatch.Code);
        Assert.False(File.Exists(mp4.Path));
    }

    [RemuxExportFact]
    public async Task CancellationLeavesExistingOutputIntact()
    {
        using var target = new TemporaryOutput("mp4");
        await File.WriteAllTextAsync(target.Path, "existing");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RemuxExportService().ExportAsync(
            new RemuxExportRequest(target.Path, [new(Fixture("multi.mp4"), 0, 2, Settings("mp4", 0))]),
            cancellationToken: cancellation.Token));
        Assert.Equal("existing", await File.ReadAllTextAsync(target.Path));
        using var duringExport = new CancellationTokenSource();
        var progress = new InlineProgress<RemuxExportProgress>(_ => duringExport.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RemuxExportService().ExportAsync(
            new RemuxExportRequest(target.Path, [new(Fixture("multi.mp4"), 0, 2, Settings("mp4", 0))]),
            progress, duringExport.Token));
        Assert.Equal("existing", await File.ReadAllTextAsync(target.Path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target.Path)!, ".tscutter-*.tmp"));
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static FormatContext Open(string path)
    {
        var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        return input;
    }

    private static double VideoStart(string path)
    {
        using var input = Open(path);
        var video = input.GetVideoStream();
        return video.StartTime * (double)video.TimeBase.num / video.TimeBase.den;
    }

    private static unsafe string[] HashPackets(string path, double start, double end, int streamIndex = 0)
    {
        using var input = Open(path);
        var hashes = new List<string>();
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex != streamIndex) continue;
            AVPacket* raw = packet;
            var stream = input.Streams[streamIndex];
            var seconds = raw->pts * (double)stream.TimeBase.num / stream.TimeBase.den;
            if (seconds < start - 0.002 || seconds >= end - 0.002) continue;
            hashes.Add(Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>(raw->data, raw->size))));
        }
        return hashes.ToArray();
    }

    private static unsafe void AssertUniqueAssEvents(string path)
    {
        using var input = Open(path);
        var orders = new HashSet<string>();
        foreach (var packet in input.ReadPackets())
        {
            if (input.Streams[packet.StreamIndex].Codecpar!.CodecId != AVCodecID.AV_CODEC_ID_ASS) continue;
            AVPacket* raw = packet;
            var text = System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>(raw->data, raw->size));
            Assert.True(orders.Add(text.Split(',')[0]));
        }
        Assert.True(orders.Count >= 2);
    }

    private static unsafe void AssertSubtitleRange(string path, double start, double end)
    {
        using var input = Open(path);
        var found = false;
        foreach (var packet in input.ReadPackets())
        {
            var stream = input.Streams[packet.StreamIndex];
            if (stream.Codecpar!.CodecType != AVMediaType.AVMEDIA_TYPE_SUBTITLE) continue;
            AVPacket* raw = packet;
            var seconds = raw->pts * (double)stream.TimeBase.num / stream.TimeBase.den;
            var duration = raw->duration * (double)stream.TimeBase.num / stream.TimeBase.den;
            Assert.InRange(seconds, start - 0.002, end + 0.002);
            Assert.True(seconds + duration <= end + 0.002);
            if (raw->size > 2 && duration > 0) found = true;
        }
        Assert.True(found);
    }

    private static unsafe void AssertMetadataAndChapters(FormatContext input, string title, double duration)
    {
        AVFormatContext* raw = input;
        Assert.True(new NativeMetadata(raw->metadata).TryGetValue("title", out var actual));
        Assert.Equal(title, actual);
        Assert.True(raw->nb_chapters > 0);
        for (var i = 0; i < raw->nb_chapters; i++)
        {
            var chapter = raw->chapters[i];
            Assert.InRange(chapter->start * (double)chapter->time_base.num / chapter->time_base.den, 0, duration);
            Assert.InRange(chapter->end * (double)chapter->time_base.num / chapter->time_base.den, 0, duration + 0.002);
        }
        Assert.True(input.Streams[1].Metadata.TryGetValue("language", out var language));
        Assert.Equal("eng", language);
    }

    private static unsafe void AssertMonotonicDts(string path, bool allowMissingInitialVideoDts = false)
    {
        using var input = Open(path);
        var previous = Enumerable.Repeat(long.MinValue, input.Streams.Count).ToArray();
        foreach (var packet in input.ReadPackets())
        {
            AVPacket* raw = packet;
            if (input.Streams[packet.StreamIndex].IsAttachedPicture) continue;
            if (allowMissingInitialVideoDts && raw->dts == NativeMethods.AV_NOPTS_VALUE &&
                input.Streams[packet.StreamIndex].Codecpar!.CodecType == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                Assert.Equal(long.MinValue, previous[packet.StreamIndex]);
                continue;
            }
            if (input.Streams[packet.StreamIndex].Codecpar!.CodecType is not
                (AVMediaType.AVMEDIA_TYPE_VIDEO or AVMediaType.AVMEDIA_TYPE_AUDIO)) continue;
            Assert.True(raw->dts > previous[packet.StreamIndex]);
            previous[packet.StreamIndex] = raw->dts;
        }
    }

    private sealed class TemporaryOutput : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tscutter-media-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public TemporaryOutput(string extension) { Directory.CreateDirectory(directory); Path = System.IO.Path.Combine(directory, "out." + extension); }
        public void Dispose() => Directory.Delete(directory, true);
    }
}
