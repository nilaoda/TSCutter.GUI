using Avalonia;
using Avalonia.Headless;
using TSCutter.GUI.FFmpeg;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.Utils.Media;
using TSCutter.GUI.Models.Discs;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Discs;
using TSCutter.GUI.Services.Preview;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Utils.Discs;
using TSCutter.GUI.Tests.UI;
using Xunit;

namespace TSCutter.GUI.Tests.Discs;

/// <summary>
/// 光盘集成测试需要同时具备合成素材与光盘能力的 FFmpeg 运行库
/// （libbluray 公开接口、dvdvideo 解复用器及其光盘依赖）。
/// 运行库缺少这些能力时明确跳过并说明原因，而不是伪装成通过。
/// </summary>
public sealed class DiscMediaFactAttribute : FactAttribute
{
    public DiscMediaFactAttribute()
    {
        var missing = NativeTestEnvironment.MissingRequirement("iso", null);
        if (missing is not null)
        {
            Skip = missing;
            return;
        }
        var capabilities = DiscCapabilities.DescribeSupport();
        if (capabilities is not null)
            Skip = capabilities;
    }
}

/// <summary>
/// 通过真实光盘结构（libbluray 读取层 / libdvdread + FFmpeg dvdvideo 解复用器）
/// 验证 ISO 打开、标题时间轴、跨片段导航与图形字幕预览。
/// </summary>
[Collection("Avalonia UI")]
public sealed class DiscIntegrationTests
{
    private static string BlurayIso() => Path.Combine(
        NativeTestEnvironment.FixtureDirectory("iso"), "bluray.iso");
    private static string DvdIso() => Path.Combine(
        NativeTestEnvironment.FixtureDirectory("iso"), "dvd.iso");

    private static void Dispose(DecodeResult result)
    {
        result.BitmapLease?.Dispose();
        if (result.BitmapLease is null)
            result.Bitmap?.Dispose();
        result.GpuFrame?.Dispose();
    }

    internal static async Task RunUiAsync(Func<Task<bool>> action)
    {
        var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        try
        {
            await session.Dispatch(action, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            // 失败时 Dispatch 的延续也可能在 UI 线程内联执行；不能在该线程等待自身退出。
            await Task.Run(session.Dispose).ConfigureAwait(false);
        }
    }

    [Fact]
    public void DiscTimelineLocatesRepeatedSegmentsByPositionAndTime()
    {
        var repeated = new DiscTimeline([
            new(1000, 2000, 0, 0, 1920),
            new(1000, 2000, 1000, 1920, 3840)
        ]);
        Assert.Equal(0, repeated.FindByByte(1919)!.RelativeStart90k);
        Assert.Equal(1000, repeated.FindByByte(1920)!.RelativeStart90k);
        Assert.Null(repeated.FindByByte(-1));
        Assert.Null(repeated.FindByByte(3840));
        Assert.Equal(0, repeated.FindByRelativeTime(999)!.RelativeStart90k);
        Assert.Equal(1000, repeated.FindByRelativeTime(1000)!.RelativeStart90k);
        var first = new DiscSegment(1000, 2000, 0);
        var next = new DiscSegment(2000, 3000, 1000);
        var continuous = new DiscTimeline([first, next]);
        Assert.True(continuous.Continues(first, next));
        Assert.Equal(2000, continuous.ContinuousEnd90k(first));
        Assert.False(continuous.Continues(next, first));
    }

    [Fact]
    public void BlurayVersionCheckRejectsIncompatibleAbis()
    {
        Assert.False(DiscCapabilities.SupportsBlurayVersion(1, 4));
        Assert.True(DiscCapabilities.SupportsBlurayVersion(1, 5));
        Assert.True(DiscCapabilities.SupportsBlurayVersion(1, 6));
        Assert.False(DiscCapabilities.SupportsBlurayVersion(2, 0));
    }

    [Fact]
    public void DvdProtectionProbeChecksHeadersAndRejectsInvalidIfoOffsets()
    {
        var sector = new byte[2048];
        sector[2] = 1;
        sector[3] = 0xBA;
        sector[4] = 0x44;
        sector[16] = 1;
        sector[17] = 0xE0;
        sector[18] = 0x07;
        sector[19] = 0xEC;
        sector[20] = 0x80;
        // 载荷中类似加扰 PES 的字节不能影响分类。
        sector[102] = 1;
        sector[103] = 0xE0;
        sector[106] = 0xB0;
        Assert.False(DvdDisc.HasScrambledPes(sector));
        sector[20] = 0xB0;
        Assert.True(DvdDisc.HasScrambledPes(sector));
        var ifo = new byte[0xD0];
        ifo.AsSpan(0xC8, 4).Fill(0xFF);
        var error = Assert.Throws<DiscScanException>(() => DvdDisc.ParseTitleDuration(ifo, 1));
        Assert.Equal(DiscFailureKind.StructureUnsupported, error.Kind);
    }

    [Fact]
    public void DvdDurationUsesCellTimesAndCountsOnlyTheFirstAngle()
    {
        var ifo = new byte[6144];
        void Be16(int offset, ushort value) =>
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(offset), value);
        void Be32(int offset, uint value) =>
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(offset), value);
        Be32(0xC8, 1);
        Be32(0xCC, 2);
        Be16(2048, 1);
        Be32(2052, 15);
        Be32(2056, 12);
        Be16(2060, 1);
        Be16(2062, 1);
        Be16(4096, 1);
        Be32(4108, 16);
        const int pgc = 4112;
        ifo[pgc + 3] = 3;
        ifo[pgc + 6] = 0x2B; // 无效的 PGC 汇总秒数不能遮盖可播放的 Cell。
        Be16(pgc + 0xE8, 236);
        const int cells = pgc + 236;
        ifo[cells] = 0x50; // 多角度块的第一个角度：5 秒。
        ifo[cells + 6] = 5;
        ifo[cells + 24] = 0xD0; // 替代角度：7 秒，不重复计时。
        ifo[cells + 30] = 7;
        ifo[cells + 54] = 3; // 普通 Cell：3 秒。
        Assert.Equal(TimeSpan.FromSeconds(8), DvdDisc.ParseTitleDuration(ifo, 1));
    }

    [Fact]
    public void DiscFoldersRequireStructureAndNormalizeRootAndStructureDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "tscutter-disc-Café 目录 " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(DiscScanService.GetDirectoryRoot(root));
            Assert.False(DiscScanService.IsDiscPath(root));
            var input = new DiscInput(DiscKind.Dvd, root, 1, 1, TimeSpan.FromSeconds(8), []);
            var settings = new RemuxExportSettings
            {
                Disc = input, Container = "vob", ReferenceVideoIndex = 0, StreamIndices = [0]
            };
            var error = Assert.Throws<RemuxExportException>(() => RemuxExportService.Validate(new(
                Path.Combine(root, "VIDEO_TS", "VTS_01_1.VOB"), [new(root, 0, 2, settings)])));
            Assert.Equal(RemuxExportError.SameFile, error.Code);
            Assert.True(TSCutter.GUI.Utils.Files.MediaFileUtil.ConflictsWithSource(
                Path.Combine(root, "VIDEO_TS", "VTS_01_1.VOB"), root));
            Assert.False(TSCutter.GUI.Utils.Files.MediaFileUtil.ConflictsWithSource(root + "-output.vob", root));
            if (OperatingSystem.IsMacOS())
                Assert.True(TSCutter.GUI.Utils.Files.MediaFileUtil.ConflictsWithSource(
                    Path.Combine(root, "VIDEO_TS", "VTS_01_1.VOB"), root.Normalize(System.Text.NormalizationForm.FormD)));
            foreach (var (folder, marker) in new[] { ("VIDEO_TS", "VIDEO_TS.IFO"), ("BDMV", "index.bdmv") })
            {
                var structure = Path.Combine(root, folder);
                Directory.CreateDirectory(structure);
                Assert.False(DiscScanService.IsDiscPath(structure));
                File.WriteAllBytes(Path.Combine(structure, marker), []);
                Assert.Equal(root, DiscScanService.GetDirectoryRoot(root));
                Assert.Equal(root, DiscScanService.GetDirectoryRoot(structure + Path.DirectorySeparatorChar));
                Assert.True(DiscScanService.IsDiscPath(structure));
                var nested = Path.Combine(structure, "STREAM");
                Directory.CreateDirectory(nested);
                Assert.False(DiscScanService.IsDiscPath(nested));
                Assert.False(DiscScanService.IsDiscPath(Path.Combine(nested, "00001.m2ts")));
                Directory.Delete(structure, recursive: true);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [DiscMediaFact]
    public async Task ScanRejectsNonIsoFilesAsNotDisc()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"tscutter-not-disc-{Guid.NewGuid():N}.iso");
        try
        {
            await File.WriteAllBytesAsync(temp, new byte[4096]);
            var exception = await Assert.ThrowsAsync<DiscScanException>(
                () => DiscScanService.ScanAsync(temp, CancellationToken.None));
            Assert.Equal(DiscFailureKind.NotDiscOrCorrupt, exception.Kind);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [DiscMediaFact]
    public async Task BlurayTitleOpensWithEditingAndUnifiedTimeline()
    {
        var scan = await DiscScanService.ScanAsync(BlurayIso(), CancellationToken.None);
        Assert.Equal(DiscKind.BluRay, scan.Kind);
        Assert.Equal(6, scan.Titles.Count);
        Assert.Equal(1, scan.RecommendedIndex);
        var main = scan.Titles[scan.RecommendedIndex];
        // 多片段正片：200s，三个片段。
        Assert.Equal(200.0, main.Duration.TotalSeconds, 1);
        Assert.Equal(3, main.ClipCount);
        Assert.True(main.Recommended);
        // 播放列表顺序与文件名不同：正片引用 00011/00012/00011。
        Assert.Equal(2, scan.Inputs[1].TitleNumber);
        Assert.Equal(3, scan.Inputs[1].Segments.Count);
        // 片段区间与标题相对起点来自 MPLS。
        Assert.Equal(0, scan.Inputs[1].Segments[0].RelativeStart90k);
        Assert.Equal(8_100_000, scan.Inputs[1].Segments[1].RelativeStart90k);
        Assert.Equal(15_300_000, scan.Inputs[1].Segments[2].RelativeStart90k);
        // 片段 2 保留原始 PTS（+300s 偏移的片段）。
        Assert.True(scan.Inputs[1].Segments[1].InPts90k > 27_000_000);
        await RunUiAsync(async () =>
        {
            using var video = new VideoInstance(scan.Inputs[1].SourcePath, discInput: scan.Inputs[1]);
            video.InitVideo(); // 同步打开：headless 会话内避免后台线程与 UI 线程互相等待
            Assert.True(video.Inited);
            // 光盘通过重新封装剪辑导出，不提供 TS 字节剪切。
            Assert.True(video.CanClip);
            Assert.False(video.CanBinaryClip);
            Assert.True(video.CanRemuxClip);
            Assert.Equal(-1, video.TsSyncOffset);
            // 时长来自 MPLS（多片段标题的 mpegts 估算不可靠）。
            Assert.Equal(200.0, video.GetVideoDurationInSeconds(), 1);
            var info = video.GetDiscMediaInfoText(main);
            Assert.Contains("Playlist                           : 00002.mpls", info);
            Assert.Contains("Duration                           : 3 min 20 s", info);
            Assert.Contains("Image size", info);
            Assert.DoesNotContain("Stream size", info);
            Assert.DoesNotContain("Overall bit rate", info);
            Assert.Contains("Video\n", info.Replace("\r\n", "\n"));
            Assert.Contains("Sampling rate", info);
            Assert.Contains("00:01:30.000", info);
            Assert.Contains("00:02:50.000", info);
            var sheetInfo = ThumbnailSheetInfoBuilder.Build(scan.Inputs[1].SourcePath, disc: scan.Inputs[1]);
            Assert.Equal(scan.Inputs[1].Duration, sheetInfo.Duration);
            Assert.Equal(320, sheetInfo.VideoWidth);
            Assert.Equal(0, sheetInfo.OverallBitRate);
            Assert.Equal(0, sheetInfo.FileSize);
            // 跨三个片段的任意位置跳转都落在正确片段并返回正确的标题相对时间。
            foreach (var requested in new[] { 5.0, 79.0, 100.0, 175.0, 199.9 })
            {
                var frame = VideoInstance.DecodeAtTimeSync(video, TimeSpan.FromSeconds(requested));
                Assert.InRange(frame.FrameTimestamp.TotalSeconds, requested - 2.5, requested + 2.5);
                if (requested > 199)
                    Assert.InRange(frame.FrameTimestamp.TotalSeconds, 198, 199.999);
                Dispose(frame);
            }
            return true;
        });
    }

    [DiscMediaFact]
    public async Task BlurayKeyframeNavigationCrossesSegmentBoundaries()
    {
        var scan = await DiscScanService.ScanAsync(BlurayIso(), CancellationToken.None);
        await RunUiAsync(async () =>
        {
            using var video = new VideoInstance(scan.Inputs[1].SourcePath, discInput: scan.Inputs[1]);
            video.InitVideo(); // 同步打开：headless 会话内避免后台线程与 UI 线程互相等待
            // 片段 2 起点 90s：从其内部连续前进必须单调递增。
            var current = VideoInstance.DecodeAtTimeSync(video, TimeSpan.FromSeconds(92.0));
            Dispose(current);
            for (var step = 0; step < 3; step++)
            {
                var next = video.DecodeNextFrameSync(1);
                Assert.True(next.FrameTimestamp.TotalSeconds > current.FrameTimestamp.TotalSeconds,
                    $"forward step {step}: {current.FrameTimestamp} -> {next.FrameTimestamp}");
                current = next;
                Dispose(next);
            }
            // 真正回到第二片段开头，再验证跨 90 秒边界的单步回退。
            current = VideoInstance.DecodeAtTimeSync(video, TimeSpan.FromSeconds(90.1));
            Dispose(current);
            var previous = video.DecodeNextFrameSync(-1);
            Assert.True(previous.FrameTimestamp.TotalSeconds < current.FrameTimestamp.TotalSeconds,
                $"backward: {current.FrameTimestamp} -> {previous.FrameTimestamp}");
            Assert.True(previous.FrameTimestamp.TotalSeconds < 90 && previous.FrameTimestamp.TotalSeconds >= 84,
                $"backward landed too early: {previous.FrameTimestamp}");
            Dispose(previous);
            // 从片段 1 内部大幅回退（-10）后，时间戳仍在标题时间轴内。
            var back = video.DecodeNextFrameSync(-10);
            Assert.InRange(back.FrameTimestamp.TotalSeconds, 0, 90);
            Dispose(back);
            return true;
        });
        using var input = InterruptibleInputFormatContext.Open(scan.Inputs[1], static () => false);
        input.LoadStreamInfo();
        var videoStream = input.GetVideoStream();
        var audioStream = input.GetAudioStream();
        input.SeekFrame((long)Math.Round(89 / videoStream.TimeBase.ToDouble()), videoStream.Index);
        var missingPositionSides = new HashSet<bool>();
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex == videoStream.Index && packet.Pts * videoStream.TimeBase.ToDouble() >= 93)
                break;
            if (packet.StreamIndex != audioStream.Index)
                continue;
            var seconds = packet.Pts * audioStream.TimeBase.ToDouble();
            Assert.InRange(seconds, 87, 94);
            if (packet.Position < 0)
                missingPositionSides.Add(seconds >= 90);
        }
        // 一个 PES 拆出的 AC-3 包确实缺少 pos，跨 +300s 原始 PTS 基准后仍须归一化到附近。
        Assert.Equal(2, missingPositionSides.Count);
    }

    [DiscMediaFact]
    public async Task BlurayKeyFrameStripReadsAcrossSegments()
    {
        var scan = await DiscScanService.ScanAsync(BlurayIso(), CancellationToken.None);
        await RunUiAsync(async () =>
        {
            var source = new KeyFrameStripSource(scan.Inputs[1].SourcePath,
                new PreviewStreamSelection(0, 0x1011, null), ToneMapping: false, scan.Inputs[1]);
            var service = new KeyFrameStripService();
            try
            {
                await service.OpenAsync(source, CancellationToken.None);
                // 两处片段边界附近（90s、170s）都要能取到缩略图。
                foreach (var target in new[] { 95.0, 175.0 })
                {
                    var received = new List<KeyFrameStripFrame>();
                    var range = await service.ReadAsync(target, _ => false, received.Add,
                        CancellationToken.None, frameCount: 5);
                    Assert.NotEmpty(range.Frames);
                    Assert.All(range.Frames, frame =>
                        Assert.InRange(frame.Timestamp.TotalSeconds, 0, 200));
                    // 相邻帧时间戳单调递增，且与请求位置邻近。
                    for (var i = 1; i < range.Frames.Count; i++)
                        Assert.True(range.Frames[i].Timestamp > range.Frames[i - 1].Timestamp,
                            $"non-monotonic at {target}s: {range.Frames[i - 1].Timestamp} -> {range.Frames[i].Timestamp}");
                    Assert.Contains(range.Frames,
                        frame => Math.Abs(frame.Timestamp.TotalSeconds - target) < 12);
                }
            }
            finally
            {
                await service.DisposeAsync();
            }
            return true;
        });
    }

    [DiscMediaFact]
    public async Task RepeatedClipKeepsUniqueFrameIdentityAndSubtitleTimeline()
    {
        var scan = await DiscScanService.ScanAsync(BlurayIso(), CancellationToken.None);
        var input = scan.Inputs.Single(input => input.TitleNumber == 4);
        Assert.Equal(60, input.Duration.TotalSeconds);
        await RunUiAsync(async () =>
        {
            using var video = new VideoInstance(input.SourcePath, discInput: input);
            video.InitVideo();
            var first = VideoInstance.DecodeAtTimeSync(video, TimeSpan.FromSeconds(2.1));
            var repeated = VideoInstance.DecodeAtTimeSync(video, TimeSpan.FromSeconds(42.1));
            try
            {
                Assert.NotEqual(first.FramePts, repeated.FramePts);
                Assert.InRange(repeated.FrameTimestamp.TotalSeconds, 42, 42.2);
                using var restored = new VideoInstance(input.SourcePath, discInput: input);
                restored.InitVideo();
                var exact = await restored.DecodeKeyFrameAtPtsAsync(repeated.FramePts);
                Assert.NotNull(exact);
                Assert.Equal(repeated.FrameTimestamp, exact.FrameTimestamp);
                Dispose(exact);
            }
            finally
            {
                Dispose(first);
                Dispose(repeated);
            }
            var track = Assert.Single(video.PreviewSubtitleTracks);
            using var subtitles = SubtitlePreviewService.Load(input.SourcePath, track.Selection!,
                video.CodedVideoSize, video.TimelineOriginSeconds, 60, -1, CancellationToken.None,
                displayedSeconds: 43, disc: input);
            using var shown = subtitles.Render(44.5, new PixelSize(320, 180), CancellationToken.None);
            Assert.NotNull(shown);
            Assert.Null(subtitles.Render(47.5, new PixelSize(320, 180), CancellationToken.None));
            Assert.InRange(subtitles.ReadThroughSeconds, 48, 50);
            return true;
        });
    }

    [DiscMediaFact]
    public async Task PlaylistOutPointClipsSubtitleBeforeTheNextSegment()
    {
        var scan = await DiscScanService.ScanAsync(BlurayIso(), CancellationToken.None);
        var input = scan.Inputs.Single(input => input.TitleNumber == 5);
        await RunUiAsync(async () =>
        {
            using var video = new VideoInstance(input.SourcePath, discInput: input);
            video.InitVideo();
            Assert.Equal(0, video.TimelineOriginSeconds);
            var frame = VideoInstance.DecodeAtTimeSync(video, TimeSpan.FromSeconds(1.5));
            Assert.InRange(frame.FrameTimestamp.TotalSeconds, 1.25, 2);
            Dispose(frame);
            var track = Assert.Single(video.PreviewSubtitleTracks);
            using var subtitles = SubtitlePreviewService.Load(input.SourcePath, track.Selection!,
                video.CodedVideoSize, video.TimelineOriginSeconds, 6.25, -1, CancellationToken.None, disc: input);
            using var shown = subtitles.Render(0.75, new PixelSize(320, 180), CancellationToken.None);
            Assert.NotNull(shown);
            Assert.Null(subtitles.Render(1.5, new PixelSize(320, 180), CancellationToken.None));
            return true;
        });
    }

    private static unsafe void AssertAudioDecodes(string path)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        var audio = input.Streams.First(stream => stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_AUDIO);
        using var decoder = new CodecContext(Codec.FindDecoderById(audio.Codecpar!.CodecId));
        decoder.FillParameters(audio.Codecpar);
        decoder.PacketTimeBase = audio.TimeBase;
        decoder.Open();
        using var destination = new Frame();
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex != audio.Index)
                continue;
            foreach (var frame in decoder.DecodePacket(packet, destination))
            {
                AVFrame* raw = frame;
                Assert.True(raw->nb_samples > 0);
                return;
            }
        }
        Assert.Fail("No audio samples were decoded from the exported clip.");
    }

    [Fact]
    public void VobSubtitleClippingRetimesDisplayControlsAndRejectsBrokenLinks()
    {
        byte[] packet = [0, 18, 0, 4, 0, 0, 0, 12, 1, 0xFF, 0, 0, 0, 100, 0, 12, 2, 0xFF];
        var clipped = VobSubtitlePacket.Clip(packet, 10, 10.5, 10.9)!;
        Assert.Equal(10.5, clipped.Start);
        Assert.Equal(0.4, clipped.Duration, 6);
        Assert.Equal(0, clipped.Bytes[5]);
        Assert.Equal(35, clipped.Bytes[13]);
        Assert.Equal(100, packet[13]);
        Assert.Null(VobSubtitlePacket.Clip(packet, 10, 12, 13));
        packet[15] = 4;
        Assert.Throws<InvalidDataException>(() => VobSubtitlePacket.Clip(packet, 10, 10, 11));
    }

    [DiscMediaFact]
    public async Task CrossSegmentClipsExportToNativeContainersWithVideoAudioAndPictureSubtitles()
    {
        foreach (var (name, titleIndex, start, end, unsafeBoundary) in new[]
                 {
                     ("dvd", 1, 3.0, 11.0, false),
                     ("bluray", 3, 22.0, 44.0, false),
                     ("bluray", 1, 79.0, 100.0, false),
                     ("bluray", 5, 79.0, 100.0, true)
                 })
        {
            var path = Path.Combine(NativeTestEnvironment.FixtureDirectory("iso"), name + ".iso");
            var scan = await DiscScanService.ScanAsync(path, CancellationToken.None);
            var input = scan.Inputs[titleIndex];
            var extension = name == "dvd" ? "vob" : "m2ts";
            var output = Path.Combine(Path.GetTempPath(), $"tscutter-disc-export-{Guid.NewGuid():N}.{extension}");
            try
            {
                await RunUiAsync(async () =>
                {
                    using var source = new VideoInstance(input.SourcePath, discInput: input);
                    source.InitVideo();
                    var settings = source.CreateExportSettings();
                    Assert.Equal(name == "dvd" ? "vob" : "m2ts", settings.Container);
                    settings.Container = extension;
                    var startFrame = VideoInstance.DecodeAtTimeSync(source, TimeSpan.FromSeconds(start));
                    var endFrame = VideoInstance.DecodeAtTimeSync(source, TimeSpan.FromSeconds(end));
                    var selectedStart = startFrame.FrameTimestamp.TotalSeconds;
                    var selectedEnd = endFrame.FrameTimestamp.TotalSeconds;
                    Dispose(startFrame);
                    Dispose(endFrame);
                    var request = new RemuxExportRequest(output,
                        [new(input.SourcePath, selectedStart, selectedEnd, settings)]);
                    if (unsafeBoundary)
                    {
                        var exception = await Assert.ThrowsAsync<RemuxExportException>(() => new RemuxExportService().ExportAsync(request));
                        Assert.Equal(RemuxExportError.UnsafeDiscBoundary, exception.Code);
                        Assert.False(File.Exists(output));
                        return true;
                    }
                    try
                    {
                        await new RemuxExportService().ExportAsync(request);
                    }
                    catch (RemuxExportException exception)
                    {
                        throw new Xunit.Sdk.XunitException($"{name}, container={extension}: {exception.Code}: {exception}");
                    }
                    if (name == "bluray")
                        Assert.Equal(192, TSCutter.GUI.Utils.TransportStream.Packets.TsPacketLayout.Detect(output).PacketSize);
                    using var video = new VideoInstance(output);
                    video.InitVideo();
                    Assert.InRange(video.GetVideoDurationInSeconds(), selectedEnd - selectedStart - 1, selectedEnd - selectedStart + 2);
                    Assert.Equal(source.PreviewSubtitleTracks.Count, video.PreviewSubtitleTracks.Count);
                    AssertAudioDecodes(output);
                    var frame = VideoInstance.DecodeAtTimeSync(video, TimeSpan.FromSeconds(0.5));
                    try
                    {
                        Assert.NotNull(frame.Bitmap);
                    }
                    finally
                    {
                        Dispose(frame);
                    }
                    if (video.PreviewSubtitleTracks.FirstOrDefault() is { } track)
                    {
                        using var subtitles = SubtitlePreviewService.Load(output, track.Selection!, video.CodedVideoSize,
                            video.TimelineOriginSeconds, video.GetVideoDurationInSeconds(), -1, CancellationToken.None);
                        using var overlay = subtitles.Render(0.5, new PixelSize(320, 180), CancellationToken.None);
                        Assert.NotNull(overlay);
                    }
                    return true;
                });
            }
            finally
            {
                File.Delete(output);
            }
        }
    }

    [DiscMediaFact]
    public async Task DvdTitleOpensWithCellTimelineAndSampleAspectRatio()
    {
        var scan = await DiscScanService.ScanAsync(DvdIso(), CancellationToken.None);
        Assert.Equal(DiscKind.Dvd, scan.Kind);
        // 两个标题：单 Cell（10s）与三 Cell（24s）。
        Assert.Equal(2, scan.Titles.Count);
        Assert.Equal(10.0, scan.Titles[0].Duration.TotalSeconds, 1);
        Assert.Equal(24.0, scan.Titles[1].Duration.TotalSeconds, 1);
        Assert.Equal(3, scan.Titles[1].ChapterCount);
        Assert.Equal(1, scan.Titles[1].AngleCount);
        // DVD 由 dvdvideo 重写时间轴，不需要片段映射表。
        Assert.Empty(scan.Inputs[1].Segments);
        await RunUiAsync(async () =>
        {
            using var video = new VideoInstance(scan.Inputs[1].SourcePath, discInput: scan.Inputs[1]);
            video.InitVideo(); // 同步打开：headless 会话内避免后台线程与 UI 线程互相等待
            Assert.True(video.Inited);
            Assert.True(video.CanClip);
            // dvdvideo 从 IFO 换算的 PGC 时长。
            var duration = video.GetVideoDurationInSeconds();
            Assert.InRange(duration, 24.0, 34.0);
            var info = video.GetDiscMediaInfoText(scan.Titles[1]);
            Assert.Contains("Format                             : DVD-Video", info);
            Assert.Contains("Duration                           : 0 min 24 s", info);
            Assert.Contains("Sampling rate", info);
            Assert.Contains("Subtitle", info);
            Assert.DoesNotContain("Stream size", info);
            Assert.Contains("00:00:08.000", info);
            Assert.Contains("00:00:16.000", info);
            var folder = scan.Inputs[1] with { SourcePath = Path.Combine(NativeTestEnvironment.FixtureDirectory("iso"), "dvd") };
            var sheetInfo = ThumbnailSheetInfoBuilder.Build(folder.SourcePath, disc: folder);
            Assert.Equal(folder.Duration, sheetInfo.Duration);
            Assert.Equal(352, sheetInfo.VideoWidth);
            Assert.Single(sheetInfo.SubtitleTracks);
            Assert.Equal(0, sheetInfo.FileSize);
            // 三个 Cell 组成的连续时间轴：跨 Cell 的任意位置跳转时间正确。
            foreach (var requested in new[] { 0.5, 8.5, 16.5, Math.Min(23.0, duration - 1.0) })
            {
                var frame = VideoInstance.DecodeAtTimeSync(video, TimeSpan.FromSeconds(requested));
                Assert.InRange(frame.FrameTimestamp.TotalSeconds, requested - 2.5, requested + 2.5);
                // DVD 4:3 非方形像素：需要 SAR 校正。
                Assert.True(frame.RequiresSampleAspectRatioCorrection || frame.SourcePixelSize.Width == 352);
                Dispose(frame);
            }
            return true;
        });
    }

    [DiscMediaFact]
    public async Task DvdSubpicturePreviewUsesPgcPaletteAndRespectsClears()
    {
        var scan = await DiscScanService.ScanAsync(DvdIso(), CancellationToken.None);
        await RunUiAsync(async () =>
        {
            using var video = new VideoInstance(scan.Inputs[1].SourcePath, discInput: scan.Inputs[1]);
            video.InitVideo(); // 同步打开：headless 会话内避免后台线程与 UI 线程互相等待
            var track = Assert.Single(video.PreviewSubtitleTracks);
            Assert.NotNull(track.Selection);
            using var service = await Task.Run(() => SubtitlePreviewService.Load(
                scan.Inputs[1].SourcePath, track.Selection!, video.CodedVideoSize, video.TimelineOriginSeconds,
                video.GetVideoDurationInSeconds(), video.TsSyncOffset, CancellationToken.None,
                displayedSeconds: 0, video.Disc));
            // Cell 1 的 SPU 在 1.5-7.5s 显示，其余时间清除；Cell 2 从 9.5s 起有自己的 SPU。
            var visible = service.Render(3.0, new PixelSize(352, 240), CancellationToken.None);
            Assert.NotNull(visible);
            Assert.Equal(new PixelSize(352, 240), visible.CanvasSize);
            // 调色板来自 PGC：白色文字在位图中呈现为高亮度不透明像素（避免调色板错位）。
            var sawOpaque = false;
            foreach (var part in visible.Parts)
            {
                var size = part.Bitmap.PixelSize;
                var data = new byte[size.Width * size.Height * 4];
                unsafe
                {
                    fixed (byte* pointer = data)
                        part.Bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height),
                            (nint)pointer, data.Length, size.Width * 4);
                }
                for (var i = 0; i < data.Length && !sawOpaque; i += 4)
                {
                    if (data[i + 3] > 220)
                    {
                        sawOpaque = true;
                        // 白色文字：RGB 三个通道都应接近 255（BMP 布局 BGRA）。
                        Assert.True(data[i] > 180, $"blue={data[i]}");
                        Assert.True(data[i + 1] > 180, $"green={data[i + 1]}");
                        Assert.True(data[i + 2] > 180, $"red={data[i + 2]}");
                    }
                }
            }
            Assert.True(sawOpaque, "subtitle bitmap has no opaque pixel");
            visible.Dispose();
            // 显示区间结束（7.5s）后清屏；Cell 2 字幕从 9.5s 开始。
            var cleared = service.Render(8.5, new PixelSize(352, 240), CancellationToken.None);
            Assert.Null(cleared);
            var nextCell = service.Render(10.5, new PixelSize(352, 240), CancellationToken.None);
            Assert.NotNull(nextCell);
            nextCell.Dispose();
            return true;
        });
    }
}
