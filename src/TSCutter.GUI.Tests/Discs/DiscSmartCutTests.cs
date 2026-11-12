using System.Runtime.InteropServices;
using Avalonia;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Discs;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Discs;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Services.Preview;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Utils.Discs;
using TSCutter.GUI.Utils.Editing;
using Xunit;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Tests.Discs;

[Collection("Avalonia UI")]
public sealed class DiscSmartCutTests
{
    private const int GridWidth = 32;
    private const int GridHeight = 18;

    /// <summary>
    /// 光盘标题的智能剪辑：预览中跳转后逐帧选定起止（DVD 每次跳转都会重建时间戳），导出的首尾画面就是选定的画面，
    /// 并且比相邻画面更接近；DVD 字幕随 MKV 输出。
    /// </summary>
    [DiscMediaFact]
    public async Task ExportedFirstAndLastFramesAreTheFramesPickedInThePreview()
    {
        foreach (var name in new[] { "dvd", "bluray" })
        {
            var scan = await DiscScanService.ScanAsync(Path.Combine(NativeTestEnvironment.FixtureDirectory("iso"), name),
                CancellationToken.None);
            // 固定选择正常标题，避免最长标题恰好是验证异常入点的播放列表。
            var input = scan.Inputs.Single(title => title.TitleNumber == 2);
            var output = Path.Combine(Path.GetTempPath(), $"tscutter-disc-smart-{Guid.NewGuid():N}.mkv");
            try
            {
                await DiscIntegrationTests.RunUiAsync(async () =>
                {
                    using var video = new VideoInstance(input.SourcePath, discInput: input);
                    video.InitVideo();
                    Assert.Equal(FrameAccurateSupport.Supported, video.FrameAccurateSupport);
                    var duration = input.Duration.TotalSeconds;
                    // 起点与终点各自跳转后再步进，中间另跳到别处，起止画面来自不同的读取。
                    var start = await Pick(video, duration * 0.3, 3);
                    Dispose(await video.DecodeAtTimeAsync(TimeSpan.FromSeconds(duration * 0.9)));
                    var end = await Pick(video, duration * 0.6, -2);
                    var settings = video.CreateFrameAccurateSettings(video.CreateExportSettings());
                    settings.Container = "mkv";
                    await new FrameAccurateExportService().ExportAsync(new FrameAccurateExportRequest(output,
                    [
                        new FrameAccurateExportSegment(input.SourcePath, start.Pts, end.Pts, settings,
                            StartSignature: start.Anchor, EndSignature: end.Anchor)
                    ]));
                    var frames = DecodeGrids(output, out var streams);
                    AssertPicked(start.Grids, frames[0], name);
                    AssertPicked(end.Grids, frames[^1], name);
                    if (name == "dvd")
                    {
                        Assert.Contains(AVCodecID.AV_CODEC_ID_DVD_SUBTITLE, streams);
                        var tail = await Pick(video, duration - 1.5, 1);
                        await new FrameAccurateExportService().ExportAsync(new FrameAccurateExportRequest(output,
                            [new FrameAccurateExportSegment(input.SourcePath, tail.Pts, null, settings,
                                StartSignature: tail.Anchor)]));
                        AssertPicked(tail.Grids, DecodeGrids(output, out _)[0], name);
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

    /// <summary>
    /// 蓝光 PGS 字幕：合并两段，都从字幕显示中途开始、在显示中途结束。
    /// 输出中每一时刻是否显示字幕都与原片相同（起点补写了当时的显示状态），结尾之后已清屏。
    /// </summary>
    [DiscMediaFact]
    public async Task BlurayPgsSubtitlesFollowTheSourceAcrossTheCutAndTheSeam()
    {
        var scan = await DiscScanService.ScanAsync(Path.Combine(NativeTestEnvironment.FixtureDirectory("iso"), "bluray"),
            CancellationToken.None);
        // 重复同一字幕片段三次的 60 秒标题，第二组字幕约在每次重复的 3.5–6.5 秒显示。
        var input = scan.Inputs[scan.Titles.ToList().FindIndex(title => Math.Abs(title.Duration.TotalSeconds - 60) < 0.5)];
        var output = Path.Combine(Path.GetTempPath(), $"tscutter-disc-pgs-{Guid.NewGuid():N}.mkv");
        try
        {
            await DiscIntegrationTests.RunUiAsync(async () =>
            {
                using var video = new VideoInstance(input.SourcePath, discInput: input);
                video.InitVideo();
                var track = Assert.Single(video.PreviewSubtitleTracks);
                var cuts = new List<(double Start, double End, FrameAccurateExportSegment Segment)>();
                var settings = video.CreateFrameAccurateSettings(video.CreateExportSettings());
                settings.Container = "mkv";
                foreach (var from in new[] { 4.2, 44.2 })
                {
                    // 起点是关键帧之后第 5 帧，终点再往后约 1.25 秒，都在这组字幕的显示期间。
                    var start = await Pick(video, from, 5);
                    var end = await Pick(video, from, 35);
                    cuts.Add((start.Seconds, end.Seconds,
                        new FrameAccurateExportSegment(input.SourcePath, start.Pts, end.Pts, settings)));
                }
                await new FrameAccurateExportService().ExportAsync(new FrameAccurateExportRequest(output,
                    cuts.Select(cut => cut.Segment).ToArray()));
                using var result = new VideoInstance(output);
                result.InitVideo();
                var resultTrack = Assert.Single(result.PreviewSubtitleTracks);
                Assert.True(SubtitleVisible(input.SourcePath, track, video, cuts[0].Start + 0.2, input));
                var outputStart = 0.0;
                foreach (var (start, end, _) in cuts)
                {
                    // 避开片段两端的一帧，按 0.25 秒采样比较。
                    for (var offset = 0.1; offset < end - start - 0.1; offset += 0.25)
                        Assert.Equal(SubtitleVisible(input.SourcePath, track, video, start + offset, input),
                            SubtitleVisible(output, resultTrack, result, outputStart + offset, null));
                    outputStart += end - start + 1 / 24.0;
                }
                // 原片在终点之后仍显示字幕：输出最后一个字幕包是结尾处的清屏。
                Assert.True(SubtitleVisible(input.SourcePath, track, video, cuts[1].End + 0.2, input));
                Assert.InRange(LastSubtitleSeconds(output), outputStart - 0.1, outputStart + 0.1);
                return true;
            });
        }
        finally
        {
            File.Delete(output);
        }
    }

    [DiscMediaFact]
    public async Task UnsafeBlurayPlayItemBoundaryDoesNotProduceAnOutputFile()
    {
        var scan = await DiscScanService.ScanAsync(Path.Combine(NativeTestEnvironment.FixtureDirectory("iso"), "bluray"),
            CancellationToken.None);
        // 播放列表 00006 故意从 GOP 中间接入下一播放项，不能把重复的预滚包直接复制过去。
        var input = scan.Inputs.Single(title => title.TitleNumber == 6);
        var output = Path.Combine(Path.GetTempPath(), $"tscutter-disc-unsafe-{Guid.NewGuid():N}.mkv");
        try
        {
            await DiscIntegrationTests.RunUiAsync(async () =>
            {
                using var video = new VideoInstance(input.SourcePath, discInput: input);
                video.InitVideo();
                var start = await Pick(video, 60, 3);
                var end = await Pick(video, 120, -2);
                var settings = video.CreateFrameAccurateSettings(video.CreateExportSettings());
                settings.Container = "mkv";
                var error = await Assert.ThrowsAsync<FrameAccurateExportException>(() =>
                    new FrameAccurateExportService().ExportAsync(new FrameAccurateExportRequest(output,
                        [new FrameAccurateExportSegment(input.SourcePath, start.Pts, end.Pts, settings)])));
                Assert.Equal(FrameAccurateExportError.NonMonotonicTimestamp, error.Code);
                Assert.False(File.Exists(output));
                return true;
            });
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void EarlierDvdReadsCannotOverwriteNewerKeyFramePositions()
    {
        var index = new DvdKeyFrameIndex();
        var key = PacketEndSignature.Create(new byte[32]);
        var earlier = index.NextGeneration();
        var later = index.NextGeneration();
        index.Set(key, 9000, later);
        index.Set(key, 4500, earlier);
        Assert.True(index.TryGet(key, out var entry));
        Assert.Equal((9000L, later), entry);
    }

    private static unsafe double LastSubtitleSeconds(string path)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        var last = double.NaN;
        foreach (var packet in input.ReadPackets())
        {
            AVStream* stream = input.Streams[packet.StreamIndex];
            if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_SUBTITLE && packet.Pts != FF.AV_NOPTS_VALUE)
                last = packet.Pts * (double)stream->time_base.num / stream->time_base.den;
        }
        return last;
    }

    private static bool SubtitleVisible(string path, PreviewSubtitleTrackOption track, VideoInstance video, double seconds,
        DiscInput? disc)
    {
        using var subtitles = SubtitlePreviewService.Load(path, track.Selection!, video.CodedVideoSize, video.TimelineOriginSeconds,
            video.GetVideoDurationInSeconds(), -1, CancellationToken.None, seconds, disc);
        using var overlay = subtitles.Render(seconds, new PixelSize(320, 180), CancellationToken.None);
        return overlay is not null;
    }

    private static void AssertPicked(double[][] neighbors, double[] frame, string name)
    {
        var target = Correlation(frame, neighbors[1]);
        Assert.True(target > 0.99, $"{name}: {target}");
        Assert.True(target >= Correlation(frame, neighbors[0]) - 0.001 && target >= Correlation(frame, neighbors[2]) - 0.001,
            $"{name}: {Correlation(frame, neighbors[0])} {target} {Correlation(frame, neighbors[2])}");
    }

    /// <summary>跳到某处后逐帧步进；返回选定画面的 PTS、时间轴秒数、锚点，以及它与前后各一帧的亮度网格。</summary>
    private static async Task<(long Pts, double Seconds, string? Anchor, double[][] Grids)> Pick(VideoInstance video, double seconds,
        int steps)
    {
        var frame = await video.DecodeAtTimeAsync(TimeSpan.FromSeconds(seconds));
        for (var i = 0; i < Math.Abs(steps); i++)
        {
            var next = (await video.DecodeAdjacentFrameAsync(frame.FramePts, Math.Sign(steps), frame.FrameAnchor))!;
            Dispose(frame);
            frame = next;
        }
        var before = (await video.DecodeAdjacentFrameAsync(frame.FramePts, -1, frame.FrameAnchor))!;
        var after = (await video.DecodeAdjacentFrameAsync(frame.FramePts, 1, frame.FrameAnchor))!;
        var result = (frame.FramePts, frame.FrameTimestamp.TotalSeconds, frame.FrameAnchor.ToTextOrNull(),
            new[] { Grid(before), Grid(frame), Grid(after) });
        Dispose(before);
        Dispose(frame);
        Dispose(after);
        return result;
    }

    private static void Dispose(DecodeResult result)
    {
        result.BitmapLease?.Dispose();
        if (result.BitmapLease is null)
            result.Bitmap?.Dispose();
        result.GpuFrame?.Dispose();
    }

    private static double Correlation(double[] a, double[] b)
    {
        double ma = a.Average(), mb = b.Average(), ab = 0, aa = 0, bb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            ab += (a[i] - ma) * (b[i] - mb);
            aa += (a[i] - ma) * (a[i] - ma);
            bb += (b[i] - mb) * (b[i] - mb);
        }
        // 只有一侧失去全部纹理时不能视为匹配。
        if (aa < 1e-6 || bb < 1e-6)
            return aa < 1e-6 && bb < 1e-6 ? 1 : 0;
        return ab / Math.Sqrt(aa * bb);
    }

    // 预览位图（BGRA）按亮度取 32×18 网格。
    private static double[] Grid(DecodeResult result)
    {
        var bitmap = result.BitmapLease?.Bitmap ?? result.Bitmap!;
        var size = bitmap.PixelSize;
        var stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), buffer.Length, stride); }
        finally { handle.Free(); }
        var grid = new double[GridWidth * GridHeight];
        for (var y = 0; y < GridHeight; y++)
        for (var x = 0; x < GridWidth; x++)
        {
            var offset = (y * size.Height / GridHeight + size.Height / (2 * GridHeight)) * stride
                         + (x * size.Width / GridWidth + size.Width / (2 * GridWidth)) * 4;
            grid[y * GridWidth + x] = 0.114 * buffer[offset] + 0.587 * buffer[offset + 1] + 0.299 * buffer[offset + 2];
        }
        return grid;
    }

    // 输出文件按显示顺序解码，每帧取亮度平面的同一网格。
    private static unsafe List<double[]> DecodeGrids(string path, out List<AVCodecID> streams)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        streams = input.Streams.Select(stream => stream.Codecpar?.CodecId ?? AVCodecID.AV_CODEC_ID_NONE).ToList();
        AVStream* stream = input.GetVideoStream();
        using var decoder = new CodecContext(Codec.FindDecoderById(stream->codecpar->codec_id));
        decoder.FillParameters(new CodecParameters(stream->codecpar));
        decoder.PacketTimeBase = stream->time_base;
        FFmpegException.Check(FF.avcodec_open2(decoder, FF.avcodec_find_decoder(stream->codecpar->codec_id), null));
        using var frame = new Frame();
        var frames = new List<(long Pts, double[] Grid)>();
        void Receive()
        {
            while (FF.avcodec_receive_frame(decoder, frame) >= 0)
            {
                AVFrame* raw = frame;
                var grid = new double[GridWidth * GridHeight];
                for (var y = 0; y < GridHeight; y++)
                for (var x = 0; x < GridWidth; x++)
                    grid[y * GridWidth + x] = raw->data[0][(y * raw->height / GridHeight + raw->height / (2 * GridHeight)) * raw->linesize[0]
                                                          + x * raw->width / GridWidth + raw->width / (2 * GridWidth)];
                frames.Add((raw->best_effort_timestamp, grid));
                frame.Unref();
            }
        }
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex != stream->index)
                continue;
            FFmpegException.Check(FF.avcodec_send_packet(decoder, packet));
            Receive();
        }
        FF.avcodec_send_packet(decoder, null);
        Receive();
        return frames.OrderBy(item => item.Pts).Select(item => item.Grid).ToList();
    }
}
