using System.Security.Cryptography;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using Xunit;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Utils.Media;
using TSCutter.GUI.ViewModels.Editing;

namespace TSCutter.GUI.Tests.Editing;

public sealed class RemuxExportFactAttribute : TSCutter.GUI.Tests.FFmpeg.NativeMediaFactAttribute
{
    public RemuxExportFactAttribute(string? requiredVariable = null)
        : base(requiredVariable is null ? "remux" : null, requiredVariable) { }
}

public sealed class RemuxExportServiceTests
{
    private static string Fixture(string name) => Path.Combine(TSCutter.GUI.Tests.FFmpeg.NativeTestEnvironment.FixtureDirectory("remux"), name);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FastSubtitleOptionFollowsSourceAndSelectedSubtitles(bool isMatroskaSource)
    {
        var settings = Settings("mkv", 0, 1);
        Assert.True(settings.FastSubtitleSeek);
        var dialog = new RemuxExportOptionsViewModel();
        dialog.Initialize(settings,
            [new() { Index = 0, IsSelected = true, CanChange = false },
             new() { Index = 1, IsSelected = true, IsTextSubtitle = true },
             new() { Index = 2, IsSelected = true, Description = "PGS" }], isMatroskaSource);
        Assert.Equal(isMatroskaSource, dialog.CanUseFastSubtitleSeek);
        dialog.Container = "mp4";
        Assert.Equal(isMatroskaSource, dialog.CanUseFastSubtitleSeek);
        var notified = false;
        dialog.PropertyChanged += (_, args) => notified |= args.PropertyName == nameof(dialog.CanUseFastSubtitleSeek);
        dialog.Tracks[1].IsSelected = false;
        Assert.True(notified);
        Assert.False(dialog.CanUseFastSubtitleSeek);
        dialog.Tracks[1].IsSelected = true;
        dialog.FastSubtitleSeek = false;
        var updated = dialog.Apply(settings);
        Assert.False(updated.FastSubtitleSeek);
        Assert.True(settings.FastSubtitleSeek);
        Assert.Equal(isMatroskaSource, dialog.CanUseFastSubtitleSeek);
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
    public async Task MarkedEndFrameSurvivesDecodingInBothContainers()
    {
        foreach (var name in new[] { "multi.mp4", "multi.mkv", "hdr.mp4", "vfr.mp4", "last-keyframe.mp4" })
        {
            var source = Fixture(name);
            var origin = VideoStart(source);
            var end = name switch { "hdr.mp4" => 2d, "vfr.mp4" => 7d, _ => 4d };
            var frameEnd = end + (name == "vfr.mp4" ? 0.1 : 0.04);
            var expected = DecodeVideoFrames(source).Where(frame => frame.Time <= origin + end + 0.002).ToArray();
            Assert.InRange(expected[^1].Time, origin + end - 0.002, origin + end + 0.002);
            foreach (var container in new[] { "mp4", "mkv" })
            {
                var settings = Settings(container, 0);
                settings.TimelineOriginSeconds = origin;
                using var target = new TemporaryOutput(container);
                await new RemuxExportService().ExportAsync(new RemuxExportRequest(target.Path,
                    [new(source, 0, end, settings)]));
                var actual = DecodeVideoFrames(target.Path);
                Assert.Equal(expected.Select(frame => frame.Hash), actual.Select(frame => frame.Hash));
                Assert.InRange(actual[^1].Time, end - 0.002, end + 0.002);
                using var output = Open(target.Path);
                Assert.InRange(output.Duration / 1e6, frameEnd - 0.002, frameEnd + 0.002);
                AssertMonotonicDts(target.Path, allowMissingInitialVideoDts: container == "mkv");
            }
        }
    }

    [RemuxExportFact]
    public async Task VariableFrameRateRangesMergeWithDifferentDecoderPreroll()
    {
        var source = Fixture("vfr.mp4");
        var frames = DecodeVideoFrames(source);
        var expected = frames.Where(frame => frame.Time <= 2.002)
            .Concat(frames.Where(frame => frame.Time >= 4.498 && frame.Time <= 7.002)
                .Select(frame => (Time: frame.Time - 4.5 + 2.1, frame.Hash))).ToArray();
        foreach (var container in new[] { "mp4", "mkv" })
        {
            using var target = new TemporaryOutput(container);
            await new RemuxExportService().ExportAsync(new RemuxExportRequest(target.Path,
                [new(source, 0, 2, Settings(container, 0)), new(source, 4.5, 7, Settings(container, 0))]));
            var actual = DecodeVideoFrames(target.Path);
            Assert.Equal(expected.Select(frame => frame.Hash), actual.Select(frame => frame.Hash));
            Assert.Equal(expected.Select(frame => Math.Round(frame.Time, 3)),
                actual.Select(frame => Math.Round(frame.Time, 3)));
            using var output = Open(target.Path);
            Assert.InRange(output.Duration / 1e6, 4.698, 4.702);
            AssertMonotonicDts(target.Path, allowMissingInitialVideoDts: container == "mkv");
        }
    }

    private static unsafe List<(double Time, string Hash)> DecodeVideoFrames(string path)
    {
        using var input = Open(path);
        var stream = input.GetVideoStream();
        using var decoder = new CodecContext(Codec.FindDecoderById(stream.Codecpar!.CodecId));
        decoder.FillParameters(stream.Codecpar);
        decoder.PacketTimeBase = stream.TimeBase;
        decoder.Open();
        using var frame = new Frame();
        var frames = new List<(double, string)>();
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex != stream.Index)
                continue;
            foreach (var decoded in decoder.DecodePacket(packet, frame))
                AddFrame(decoded);
        }
        foreach (var decoded in decoder.DecodePacket(null, frame))
            AddFrame(decoded);
        return frames;

        void AddFrame(Frame decoded)
        {
            Assert.Contains((AVPixelFormat)decoded.Format,
                new[] { AVPixelFormat.AV_PIX_FMT_YUV420P, AVPixelFormat.AV_PIX_FMT_YUV420P10LE });
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var bytes = decoded.Format == (int)AVPixelFormat.AV_PIX_FMT_YUV420P10LE ? 2 : 1;
            for (var plane = 0; plane < 3; plane++)
            {
                var width = plane == 0 ? decoded.Width : (decoded.Width + 1) / 2;
                var height = plane == 0 ? decoded.Height : (decoded.Height + 1) / 2;
                for (var row = 0; row < height; row++)
                    hash.AppendData(new ReadOnlySpan<byte>(decoded.Data[(uint)plane] + row * decoded.Linesize[(uint)plane], width * bytes));
            }
            frames.Add((decoded.Pts * (double)stream.TimeBase.num / stream.TimeBase.den,
                Convert.ToHexString(hash.GetHashAndReset())));
        }
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
        Assert.InRange(input.Duration / 1e6, 2.039, 2.1);
        Assert.Equal(HashPackets(source, 2, 4.04), HashPackets(target.Path, 0, double.MaxValue));
        Assert.Equal(HashPackets(source, 2, 4.04, 1), HashPackets(target.Path, 0, double.MaxValue, 1));
        Assert.Equal(HashPackets(source, 2, 4.04, 2), HashPackets(target.Path, 0, double.MaxValue, 2));
        AssertSubtitleRange(target.Path, 0, 2.04);
        AssertMetadataAndChapters(input, "Export regression", 2.04);
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
            Assert.InRange(exported.Duration / 1e6, 2.039, 2.1);
            for (var i = 0; i < indices.Length; i++)
            {
                var stream = original.Streams[indices[i]];
                if (stream.IsAttachedPicture) AssertCover(stream, Assert.Single(exported.Streams, value => value.IsAttachedPicture));
                else if (stream.Codecpar!.CodecType is AVMediaType.AVMEDIA_TYPE_VIDEO or AVMediaType.AVMEDIA_TYPE_AUDIO)
                    Assert.Equal(HashPackets(source, 2 + origin, 4.04 + origin, indices[i]), HashPackets(target.Path, 0, double.MaxValue, i));
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
        Assert.Equal(HashPackets(source, 2 + origin, 4.04 + origin), HashPackets(target.Path, 0, double.MaxValue));
        AssertSubtitleRange(target.Path, 0, 2.04);
        AssertMetadataAndChapters(input, "Export regression", 2.04);
        using var merged = new TemporaryOutput("mkv");
        await new RemuxExportService().ExportAsync(new RemuxExportRequest(merged.Path,
            [new(source, 0, 2, settings), new(source, 4, 6, settings)]));
        AssertSubtitleRange(merged.Path, 0, 4.08);
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
        Assert.InRange(input.Duration / 1e6, 4.079, 4.15);
        var expected = HashPackets(source, 0, 2.04).Concat(HashPackets(source, 4, 6.04)).ToArray();
        Assert.Equal(expected, HashPackets(target.Path, 0, double.MaxValue));
        var decoded = DecodeVideoFrames(source);
        var expectedFrames = decoded.Where(frame => frame.Time <= 2.002)
            .Concat(decoded.Where(frame => frame.Time >= 3.998 && frame.Time <= 6.002));
        Assert.Equal(expectedFrames.Select(frame => frame.Hash), DecodeVideoFrames(target.Path).Select(frame => frame.Hash));
        AssertMonotonicDts(target.Path);
        AssertSubtitleRange(target.Path, 0, 4.08);
    }

    [RemuxExportFact]
    public async Task EofKeepsLongerAudioAndMergeStartsAfterAllRetainedTracks()
    {
        var source = Fixture("long-audio.mp4");
        using var target = new TemporaryOutput("mp4");
        await new RemuxExportService().ExportAsync(new RemuxExportRequest(target.Path,
            [new(source, 0, null, Settings("mp4", 0, 1)), new(source, 0, 2, Settings("mp4", 0, 1))]));
        using var output = Open(target.Path);
        Assert.InRange(output.Duration / 1e6, 12.039, 12.1);
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
    public async Task Mp4SeekKeepsActiveEventsFromMultipleSubtitleTracks()
    {
        var source = Fixture("seek-subtitles.mp4");
        using var target = new TemporaryOutput("mp4");
        await new RemuxExportService().ExportAsync(new RemuxExportRequest(target.Path,
            [new(source, 60, 62, new RemuxExportSettings
            {
                Container = "mp4", ReferenceVideoIndex = 0, StreamIndices = [0, 1, 2], FastSubtitleSeek = true
            })]));
        Assert.Equal(HashPackets(source, 60, 62.04), HashPackets(target.Path, 0, double.MaxValue));
        var overlapping = ReadMovTextCues(target.Path, 1);
        Assert.Equal(new[] { (0d, 0.5), (0.5, 1.5), (1.5, 2d), (2d, 2.04) },
            overlapping.Select(cue => (cue.Start, cue.End)).ToArray());
        Assert.Contains("Long subtitle", overlapping[0].Text);
        Assert.Contains("Overlapping subtitle", overlapping[1].Text);
        Assert.Empty(overlapping[2].Text);
        var sparse = Assert.Single(ReadMovTextCues(target.Path, 2));
        Assert.Equal((0d, 2.04), (sparse.Start, sparse.End));
        Assert.Contains("Sparse subtitle", sparse.Text);
    }

    private static unsafe List<(double Start, double End, string Text)> ReadMovTextCues(string path, int index)
    {
        using var input = Open(path);
        var stream = input.Streams[index];
        var scale = stream.TimeBase.num / (double)stream.TimeBase.den;
        var cues = new List<(double, double, string)>();
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex != index) continue;
            AVPacket* raw = packet;
            if (raw->duration <= 0) continue;
            var data = new ReadOnlySpan<byte>(raw->data, raw->size);
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data);
            var text = System.Text.Encoding.UTF8.GetString(data.Slice(2, length));
            cues.Add((Math.Round(raw->pts * scale, 3), Math.Round((raw->pts + raw->duration) * scale, 3), text));
        }
        return cues;
    }

    [RemuxExportFact]
    public async Task Mp4LateRangesMergeWithSubtitleEventsAtEachBoundary()
    {
        var source = Fixture("seek-subtitles.mp4");
        using var direct = new TemporaryOutput("mp4");
        var service = new RemuxExportService();
        await service.ExportAsync(new RemuxExportRequest(direct.Path,
            [new(source, 54, 56, Settings("mp4", 0, 1, 2)), new(source, 60, 62, Settings("mp4", 0, 1, 2))]));
        var expected = HashPackets(source, 54, 56.04).Concat(HashPackets(source, 60, 62.04)).ToArray();
        Assert.Equal(expected, HashPackets(direct.Path, 0, double.MaxValue));
        AssertMonotonicDts(direct.Path);
        using var output = Open(direct.Path);
        Assert.InRange(output.Duration / 1e6, 4.079, 4.15);
        Assert.Equal(new[] { (0d, 2.04), (2.04, 2.54), (2.54, 3.54), (3.54, 4.04), (4.04, 4.08) },
            ReadMovTextCues(direct.Path, 1).Select(cue => (cue.Start, cue.End)).ToArray());
        Assert.Equal(new[] { (0d, 2.04), (2.04, 4.08) },
            ReadMovTextCues(direct.Path, 2).Select(cue => (cue.Start, cue.End)).ToArray());
    }

    [RemuxExportFact]
    public async Task MkvSubtitleLookbackWorksForSingleCutsAndMergedRanges()
    {
        var source = Fixture("seek-subtitles.mkv");
        foreach (var fast in new[] { false, true })
        foreach (var merge in new[] { false, true })
        {
            var settings = Settings("mkv", 0, 1);
            settings.TimelineOriginSeconds = VideoStart(source);
            settings.FastSubtitleSeek = fast;
            using var target = new TemporaryOutput("mkv");
            List<RemuxExportSegment> segments = [new(source, 60, 62, settings)];
            if (merge) segments.Insert(0, new(source, 54, 56, settings.Snapshot()));
            await new RemuxExportService().ExportAsync(new RemuxExportRequest(target.Path, segments));
            var expected = HashPackets(source, 60, 62.04);
            if (merge) expected = HashPackets(source, 54, 56.04).Concat(expected).ToArray();
            Assert.Equal(expected, HashPackets(target.Path, 0, double.MaxValue));
            AssertMonotonicDts(target.Path, allowMissingInitialVideoDts: true);
            using var output = Open(target.Path);
            Assert.InRange(output.Duration / 1e6, merge ? 4.079 : 2.039, merge ? 4.11 : 2.07);
            var cues = ReadAssCues(target.Path);
            var offset = merge ? 2.04 : 0d;
            var expectedCues = new List<(double, double, string)>();
            if (merge)
            {
                if (!fast) expectedCues.Add((0, 2.04, "Long subtitle"));
                expectedCues.Add((0, 2.04, "Recent subtitle"));
            }
            if (!fast) expectedCues.Add((offset, offset + 2.04, "Long subtitle"));
            expectedCues.Add((offset, offset + 2.04, "Recent subtitle"));
            expectedCues.Add((offset + 0.5, offset + 1.5, "Overlapping subtitle"));
            expectedCues.Add((offset + 2, offset + 2.04, "After cut"));
            Assert.Equal(expectedCues, cues);
            AssertUniqueAssEvents(target.Path);
        }
    }

    [RemuxExportFact]
    public async Task FastMkvSeekHandlesAnEmptySubtitleTrackAndMergingThroughEof()
    {
        var source = Fixture("seek-subtitles.mkv");
        var settings = Settings("mkv", 0, 1);
        settings.TimelineOriginSeconds = VideoStart(source);
        using var target = new TemporaryOutput("mkv");
        await new RemuxExportService().ExportAsync(new RemuxExportRequest(target.Path,
            [new(source, 70, 72, settings), new(source, 74, null, settings.Snapshot())]));
        Assert.Equal(HashPackets(source, 70, 72.04).Concat(HashPackets(source, 74, 80)).ToArray(),
            HashPackets(target.Path, 0, double.MaxValue));
        Assert.Empty(ReadAssCues(target.Path));
        using var output = Open(target.Path);
        Assert.Contains(output.Streams, stream => stream.Codecpar!.CodecType == AVMediaType.AVMEDIA_TYPE_SUBTITLE);
        Assert.InRange(output.Duration / 1e6, 8.039, 8.07);
        AssertMonotonicDts(target.Path, allowMissingInitialVideoDts: true);
    }

    private static unsafe List<(double Start, double End, string Text)> ReadAssCues(string path)
    {
        using var input = Open(path);
        var cues = new List<(double, double, string)>();
        foreach (var packet in input.ReadPackets())
        {
            var stream = input.Streams[packet.StreamIndex];
            if (stream.Codecpar!.CodecId != AVCodecID.AV_CODEC_ID_ASS) continue;
            AVPacket* raw = packet;
            var scale = stream.TimeBase.num / (double)stream.TimeBase.den;
            var text = System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>(raw->data, raw->size)).Split(',', 9)[8];
            cues.Add((Math.Round(raw->pts * scale, 3), Math.Round((raw->pts + raw->duration) * scale, 3), text));
        }
        return cues;
    }

    [RemuxExportFact]
    public async Task LongSubtitleIsClippedWithoutLosingLaterOverlappingEvents()
    {
        var source = Fixture("overlapping.mkv");
        foreach (var fast in new[] { false, true })
        {
            using var target = new TemporaryOutput("mkv");
            var settings = Settings("mkv", 0, 1, 2);
            settings.TimelineOriginSeconds = VideoStart(source);
            settings.FastSubtitleSeek = fast;
            await new RemuxExportService().ExportAsync(new RemuxExportRequest(target.Path, [new(source, 2, 4, settings)]));
            AssertOverlappingSubtitleTimes(target.Path);
        }
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
        Assert.Equal(3, times.Count);
        Assert.InRange(times[0].Start, 0, 0.002);
        Assert.InRange(times[0].End, 2.038, 2.042);
        Assert.InRange(times[1].Start, 0.498, 0.502);
        Assert.InRange(times[1].End, 1.498, 1.502);
        Assert.InRange(times[2].Start, 1.998, 2.002);
        Assert.InRange(times[2].End, 2.038, 2.042);
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
        Assert.Equal(HashPackets(source, 0, 2.04), HashPackets(target.Path, 0, double.MaxValue));
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
    public async Task AudioVividRangesMergeInRequestedOrder()
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
        for (var track = 0; track < 2; track++)
        {
            var sourceIndex = track == 0 ? video.Index : audio.Index;
            var hashes = ranges.Select(range => HashPackets(source, range.Item1,
                VideoPacketEnd(source, range.Item2), sourceIndex)).ToArray();
            Assert.Equal(hashes.SelectMany(value => value).ToArray(), HashPackets(direct.Path, 0, double.MaxValue, track));
        }
        using var output = Open(direct.Path);
        var expectedDuration = ranges.Sum(range => VideoPacketEnd(source, range.Item2) - range.Item1);
        Assert.InRange(output.Duration / 1e6, expectedDuration - 0.01, expectedDuration + 0.1);
        AssertMonotonicDts(direct.Path);
        AssertAv3aParameters(source, audio.Index, direct.Path);
    }

    private static unsafe double VideoPacketEnd(string path, double time)
    {
        using var input = Open(path);
        var video = input.GetVideoStream();
        input.SeekFrame((long)Math.Round(time * video.TimeBase.den / video.TimeBase.num), video.Index);
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex != video.Index)
                continue;
            AVPacket* raw = packet;
            var pts = raw->pts * (double)video.TimeBase.num / video.TimeBase.den;
            if (Math.Abs(pts - time) > 0.002)
                continue;
            Assert.True(raw->duration > 0);
            return (raw->pts + raw->duration) * (double)video.TimeBase.num / video.TimeBase.den;
        }
        throw new InvalidOperationException("Missing boundary frame.");
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

    [RemuxExportFact]
    public async Task FractionalMatroskaPrerollKeepsTheSameMarkedFramesWithSubtitles()
    {
        var source = Fixture("fractional-subtitles.mkv");
        var origin = VideoStart(source);
        var keys = VideoKeyframeTimes(source, 34);
        var start = keys[30];
        var end = keys[32];
        var expected = HashPackets(source, start, end + 0.0021);
        Assert.NotEmpty(expected);
        foreach (var fast in new[] { true, false })
        {
            var settings = Settings("mkv", 0, 1);
            settings.TimelineOriginSeconds = origin;
            settings.FastSubtitleSeek = fast;
            using var output = new TemporaryOutput("mkv");
            await new RemuxExportService().ExportAsync(new(output.Path,
                [new(source, start - origin, end - origin, settings)]));
            Assert.Equal(expected, HashPackets(output.Path, 0, double.MaxValue));
            AssertMonotonicDts(output.Path, allowMissingInitialVideoDts: true);
        }
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
