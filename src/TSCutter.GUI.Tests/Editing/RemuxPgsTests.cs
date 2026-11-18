using Avalonia;
using Avalonia.Headless;
using System.IO.Compression;
using System.Security.Cryptography;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Services.Preview;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Utils.Media;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

[Collection("Avalonia UI")]
public sealed class RemuxPgsTests
{
    [RemuxExportFact]
    public async Task PgsClipsPreserveActivePicturesPaletteUpdatesAndClears()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("remux"), "bitmap.mkv");
            using var originalVideo = OpenVideo(source);
            using var original = Load(source, originalVideo);
            var settings = new RemuxExportSettings
            {
                Container = "mkv", ReferenceVideoIndex = 0, StreamIndices = [0, 1, 2],
                TimelineOriginSeconds = originalVideo.TimelineOriginSeconds
            };
            foreach (var (start, end) in new (double, double?)[] { (0, 2), (2, 4), (4, 6), (6, null) })
            {
                var output = Path.Combine(Path.GetTempPath(), $"tscutter-pgs-{Guid.NewGuid():N}.mkv");
                try
                {
                    await new RemuxExportService().ExportAsync(new(output, [new(source, start, end, settings)]));
                    using var video = OpenVideo(output);
                    using var subtitles = Load(output, video);
                    foreach (var time in new[] { 0d, 0.5, 1.5, 2.02 })
                    {
                        if (time >= video.GetVideoDurationInSeconds() - 0.002)
                            continue;
                        using var expected = original.Render(start + time, new PixelSize(320, 180));
                        using var actual = subtitles.Render(time, new PixelSize(320, 180));
                        Assert.Equal(Signature(expected), Signature(actual));
                    }
                    Assert.Null(subtitles.Render(video.GetVideoDurationInSeconds() + 0.1, new PixelSize(320, 180)));
                }
                finally { File.Delete(output); }
            }
            return true;
        }, CancellationToken.None);
    }

    [RemuxExportFact]
    public async Task PgsMergeStartsTheNextPictureAtTheSpliceAndMp4StillRejectsPgs()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("remux"), "bitmap.mkv");
            using var originalVideo = OpenVideo(source);
            using var original = Load(source, originalVideo);
            var settings = new RemuxExportSettings
            {
                Container = "mkv", ReferenceVideoIndex = 0, StreamIndices = [0, 2],
                TimelineOriginSeconds = originalVideo.TimelineOriginSeconds
            };
            var output = Path.Combine(Path.GetTempPath(), $"tscutter-pgs-{Guid.NewGuid():N}.mkv");
            var mp4 = Path.ChangeExtension(output, ".mp4");
            try
            {
                await new RemuxExportService().ExportAsync(new(output,
                    [new(source, 0, 2, settings), new(source, 6, null, settings)]));
                using var video = OpenVideo(output);
                using var subtitles = Load(output, video);
                using var expected = original.Render(6.5, new PixelSize(320, 180));
                using var actual = subtitles.Render(2.54, new PixelSize(320, 180));
                Assert.Equal(Signature(expected), Signature(actual));
                Assert.Null(subtitles.Render(3.54, new PixelSize(320, 180)));
                settings.Container = "mp4";
                var error = await Assert.ThrowsAsync<RemuxExportException>(() => new RemuxExportService()
                    .ExportAsync(new(mp4, [new(source, 0, 2, settings)])));
                Assert.Equal(RemuxExportError.UnsupportedTrack, error.Code);
                Assert.False(File.Exists(mp4));
            }
            finally { File.Delete(output); File.Delete(mp4); }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public void ZlibPgsRestoresTheSameDisplaySetWithoutChangingGraphics()
    {
        byte[] displaySet = [0x16, 0, 11, 1, 64, 0, 180, 0x10, 0, 0, 0x80, 0, 0, 0, 0x80, 0, 0];
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(displaySet);
        var compressed = output.ToArray();
        Assert.Equal(displaySet, PgsSubtitlePacket.Decompress(compressed));
        Assert.Null(PgsSubtitlePacket.Decompress(displaySet));
        var original = new PgsSubtitleState();
        var restored = new PgsSubtitleState();
        original.Read(displaySet.AsSpan(0, displaySet.Length - 3));
        Assert.Null(original.CreateEpoch(false));
        original.Read(displaySet.AsSpan(displaySet.Length - 3));
        Assert.Equal(displaySet, original.CreateEpoch(false));
        restored.Read(compressed);
        Assert.Equal(original.CreateEpoch(false), restored.CreateEpoch(false));
    }

    /// <summary>
    /// 光盘的显示集合按段分包：PCS 在前且显示时间最晚，其后的段更早，只有 DTS 单调。
    /// 集合按 PCS 识别；输出沿用源时间戳，只在补齐的状态或清屏之后把更早的 DTS 抬到上一包的 DTS，
    /// 此时早于该 DTS 的段 PTS 一并抬高（集合在拼接时刻之后不久开始时，PCS 之后的段会早于拼接时刻）。
    /// </summary>
    [Fact]
    public void DiscDisplaySetSegmentsKeepTheirDecodeOrder()
    {
        Assert.True(PgsSubtitlePacket.StartsDisplaySet([0x16, 0, 11]));
        Assert.False(PgsSubtitlePacket.StartsDisplaySet([0x17, 0, 1]));
        Assert.False(PgsSubtitlePacket.StartsDisplaySet([]));

        const long none = long.MinValue;
        // PCS、WDS、PDS、ODS、END：PTS 先降后升，DTS 单调不减，原样沿用。
        (long Pts, long Dts)[] segments = [(5_990, 5_984), (5_989, 5_984), (5_984, 5_984), (5_985, 5_984), (5_985, 5_985)];
        var last = none;
        foreach (var (pts, dts) in segments)
        {
            Assert.Equal((pts, dts), RemuxExportService.PgsTimestamps(pts, dts, last));
            last = dts;
        }
        // 起点补齐的状态写在拼接时刻 5_986，集合的 PCS 在 5_986 之后：各段 DTS 抬到 5_986，
        // PCS 与 WDS 的 PTS 不变，早于 5_986 的 PDS、ODS 与 END 的 PTS 也抬到 5_986。
        last = 5_986;
        (long Pts, long Dts)[] expected = [(5_990, 5_986), (5_989, 5_986), (5_986, 5_986), (5_986, 5_986), (5_986, 5_986)];
        for (var i = 0; i < segments.Length; i++)
        {
            var output = RemuxExportService.PgsTimestamps(segments[i].Pts, segments[i].Dts, last);
            Assert.Equal(expected[i], output);
            Assert.True(output.Dts >= last && output.Dts <= output.Pts);
            last = output.Dts;
        }
    }

    [Fact]
    public void TruncatedPgsIsRejectedBeforeRebuildingAFrame()
    {
        var state = new PgsSubtitleState();
        Assert.Throws<RemuxExportException>(() => state.Read(new byte[] { 0x16, 0, 11 }));
    }

    private static VideoInstance OpenVideo(string path)
    {
        var video = new VideoInstance(path, enableHardwareDecoding: false);
        try { video.InitVideo(); return video; }
        catch { video.Dispose(); throw; }
    }

    private static SubtitlePreviewService Load(string path, VideoInstance video) => SubtitlePreviewService.Load(path,
        Assert.Single(video.PreviewSubtitleTracks).Selection!, video.CodedVideoSize, video.TimelineOriginSeconds,
        video.GetVideoDurationInSeconds(), video.TsSyncOffset, CancellationToken.None);

    private static unsafe string? Signature(SubtitleOverlay? overlay)
    {
        if (overlay is null)
            return null;
        using var bytes = new MemoryStream();
        foreach (var part in overlay.Parts)
        {
            var size = part.Bitmap.PixelSize;
            var pixels = new byte[size.Width * size.Height * 4];
            fixed (byte* data = pixels)
                part.Bitmap.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), (nint)data, pixels.Length, size.Width * 4);
            bytes.Write(BitConverter.GetBytes(part.X));
            bytes.Write(BitConverter.GetBytes(part.Y));
            bytes.Write(pixels);
        }
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }
}
