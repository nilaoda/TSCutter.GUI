using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Utils.Media;
using TSCutter.GUI.Utils.TransportStream.Packets;
using Xunit;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Tests.Editing;

/// <summary>
/// 帧精确导出的端到端验证：解码真实输出，按画面中的帧序号核对首尾和帧数，
/// 并逐字节核对复制区间的切片数据与源码流一致。
/// </summary>
public sealed class FrameAccurateExportTests
{
    private static string Fixture(string name) => Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), name);

    internal static FrameAccurateExportSettings Settings(string path)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        var container = Path.GetExtension(path).TrimStart('.');
        return new FrameAccurateExportSettings
        {
            Container = container,
            VideoIndex = input.GetVideoStream().Index,
            StreamIndices = input.Streams.Where(stream => stream.Codecpar?.CodecType is AVMediaType.AVMEDIA_TYPE_VIDEO or
                AVMediaType.AVMEDIA_TYPE_AUDIO or AVMediaType.AVMEDIA_TYPE_SUBTITLE).Select(stream => stream.Index).ToList()
        };
    }

    [NativeMediaFact("frameaccurate")]
    public async Task SplicesBoundariesAndCopiesTheMiddle()
    {
        foreach (var name in new[] { "h264.ts", "h264.m2ts", "h264.mp4", "h264.mkv" })
        foreach (var (start, end, expectCopy) in new (int, int?, bool)[]
                 {
                     (40, 200, true),   // 起止都在非关键帧，跨多个 GOP
                     (65, 80, false),   // 同一 GOP 内
                     (60, 179, true),   // 恰好是完整 GOP：不重编码
                     (77, 77, false),   // 单帧
                     (0, 10, false),    // 首帧
                     (60, 60, true),    // 只取一个 IDR：直接复制
                     (300, null, true), // 到文件末尾
                 })
        {
            var source = Fixture(name);
            var frames = Decode(source, out _);
            var output = Temporary(name);
            try
            {
                var service = new FrameAccurateExportService();
                await service.ExportAsync(new FrameAccurateExportRequest(output,
                    [new FrameAccurateExportSegment(source, frames[start].Pts, end is { } e ? frames[e].Pts : null, Settings(source))]));
                var decoded = Decode(output, out var errors);
                var last = end ?? frames.Count - 1;
                Assert.Equal(0, errors);
                Assert.Equal(Enumerable.Range(start, last - start + 1), decoded.Select(frame => frame.Number));
                var report = Assert.Single(service.Reports);
                Assert.Equal(expectCopy, report.CopiedPackets > 0);
                if (end is { } finish && (finish - start + 1) % 30 == 0 && start % 30 == 0)
                    Assert.Equal(0, report.EncodedFrames);
                if (report.CopyStartPts is { } copyStart)
                    AssertSlicesCopied(source, output, copyStart, report.CopyEndPts);
                AssertTimestamps(output);
            }
            finally { File.Delete(output); }
        }
    }

    /// <summary>
    /// 进度报告附带导出计划：各片段按输出时间轴依次排列的重新编码/直接复制部分（时长与帧数成比例、首尾相接、总和等于总时长），
    /// 阶段按 定位 → 片头重编码 → 复制 → 片尾重编码 → 完成 的顺序推进，位置不回退，最终计数与实际处理结果一致。
    /// </summary>
    [NativeMediaFact("frameaccurate")]
    public async Task ReportsPlanPhasesAndCounts()
    {
        var source = Fixture("h264.mp4");
        var frames = Decode(source, out _);
        var output = Temporary("plan.mp4");
        var values = new List<FrameAccurateExportProgress>();
        try
        {
            var service = new FrameAccurateExportService();
            // 两段：40–200（片头 40–59、复制 60–179、片尾 180–200）与 250–290（片头 250–269、复制 270 起）。
            await service.ExportAsync(new FrameAccurateExportRequest(output,
                [
                    new FrameAccurateExportSegment(source, frames[40].Pts, frames[200].Pts, Settings(source)),
                    new FrameAccurateExportSegment(source, frames[250].Pts, frames[290].Pts, Settings(source))
                ]), new SynchronousProgress(values.Add));
            var last = values[^1];
            Assert.Equal(100, last.Percent);
            Assert.Equal(FrameAccuratePhase.Finishing, last.Phase);
            Assert.Equal(service.Reports.Sum(report => report.EncodedFrames), last.EncodedFrames);
            Assert.Equal(service.Reports.Sum(report => report.CopiedPackets), last.CopiedPackets);
            var plan = last.Plan;
            Assert.Equal([FrameAccuratePartKind.Encode, FrameAccuratePartKind.Copy, FrameAccuratePartKind.Encode],
                plan.Where(part => part.Segment == 0).Select(part => part.Kind));
            Assert.Equal(FrameAccuratePartKind.Encode, plan.First(part => part.Segment == 1).Kind);
            var first = plan.Where(part => part.Segment == 0).ToList();
            var frame = first[1].Duration / 120;
            Assert.Equal(20 * frame, first[0].Duration, 6);
            Assert.Equal(21 * frame, first[2].Duration, 6);
            Assert.Equal(0, plan[0].Start);
            for (var i = 1; i < plan.Count; i++)
                Assert.Equal(plan[i - 1].Start + plan[i - 1].Duration, plan[i].Start, 6);
            Assert.Equal(last.TotalSeconds, plan.Sum(part => part.Duration), 6);
            Assert.Equal(161 + 41, (int)Math.Round(last.TotalSeconds / frame));
            // 各报告共用同一份计划；阶段在片段内只前进，片段序号与位置不回退。
            Assert.All(values, value => Assert.Same(plan, value.Plan));
            for (var i = 1; i < values.Count; i++)
            {
                Assert.True(values[i].Position >= values[i - 1].Position);
                Assert.True(values[i].Segment >= values[i - 1].Segment);
                if (values[i].Segment == values[i - 1].Segment)
                    Assert.True(values[i].Phase >= values[i - 1].Phase, $"{values[i - 1].Phase} -> {values[i].Phase}");
            }
            foreach (var segment in new[] { 0, 1 })
                Assert.Contains(values, value => value.Segment == segment && value.Phase == FrameAccuratePhase.Copying);
            Assert.Contains(values, value => value.Segment == 0 && value.Phase == FrameAccuratePhase.EncodingHead);
            Assert.Contains(values, value => value.Segment == 0 && value.Phase == FrameAccuratePhase.EncodingTail);
        }
        finally { File.Delete(output); }
    }

    [NativeMediaFact("frameaccurate")]
    public async Task SplicesHevcWithLeadingPicturesAndKeepsHdr()
    {
        // HEVC 10 位：IDR 带 RADL 前导画面；TS 为 HLG，MP4 为 HDR10（静态元数据在 SEI 中）。
        foreach (var (name, transfer) in new[]
                 {
                     ("hevc-hlg.ts", AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67),
                     ("hevc-hdr10.mp4", AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084)
                 })
        {
            var source = Fixture(name);
            var frames = DecodeFrames(source);
            foreach (var (start, end, expectCopy) in new (int, int?, bool)[] { (40, 160, true), (60, 70, false), (5, 5, false), (150, null, true) })
            {
                var output = Temporary(name);
                try
                {
                    var service = new FrameAccurateExportService();
                    await service.ExportAsync(new FrameAccurateExportRequest(output,
                        [new FrameAccurateExportSegment(source, frames[start], end is { } e ? frames[e] : null, Settings(source))]));
                    Assert.Equal(Enumerable.Range(start, (end ?? frames.Count - 1) - start + 1), DecodeNumbers(output));
                    var report = Assert.Single(service.Reports);
                    Assert.Equal(expectCopy, report.CopiedPackets > 0);
                    if (report.CopyStartPts is { } copyStart)
                        AssertSlicesCopied(source, output, copyStart, report.CopyEndPts);
                    AssertTimestamps(output);
                    AssertFirstFrameHdr(output, transfer, expectMastering: name.EndsWith(".mp4"));
                    if (name.EndsWith(".mp4"))
                        AssertHvc1(output, expectSecondEntry: report.EncodedFrames > 0);
                }
                finally { File.Delete(output); }
            }
            var merged = Temporary(name);
            try
            {
                var settings = Settings(source);
                await new FrameAccurateExportService().ExportAsync(new FrameAccurateExportRequest(merged,
                [
                    new FrameAccurateExportSegment(source, frames[30], frames[80], settings),
                    new FrameAccurateExportSegment(source, frames[120], frames[170], settings.Snapshot())
                ]));
                Assert.Equal(Enumerable.Range(30, 51).Concat(Enumerable.Range(120, 51)), DecodeNumbers(merged));
                AssertTimestamps(merged);
            }
            finally { File.Delete(merged); }
        }
    }

    /// <summary>重编码的首帧保留传输特性；源带 HDR10 静态元数据时，首帧也带有。</summary>
    /// <summary>
    /// HEVC MP4 为严格的 hvc1：样本中没有 VPS/SPS/PPS；有重编码画面时，重编码参数集位于第二个样本描述中。
    /// </summary>
    private static unsafe void AssertHvc1(string path, bool expectSecondEntry)
    {
        using (var input = FormatContext.OpenInputUrl(path))
        {
            input.LoadStreamInfo();
            AVStream* stream = input.GetVideoStream();
            Assert.Equal(0x31637668u, stream->codecpar->codec_tag);
            var lengthSize = VideoBitstream.ReadParameterSets(new ReadOnlySpan<byte>(stream->codecpar->extradata,
                stream->codecpar->extradata_size), true, []);
            foreach (var packet in input.ReadPackets())
            {
                if (packet.StreamIndex != stream->index)
                    continue;
                AVPacket* raw = packet;
                var data = new ReadOnlySpan<byte>(raw->data, raw->size);
                Assert.False(VideoBitstream.Contains(data, lengthSize, true, type => VideoBitstream.ParameterSetOrder(type, true) >= 0),
                    "parameter set inside an hvc1 sample");
            }
        }
        var file = File.ReadAllBytes(path);
        var stsd = FindBox(file, ["moov", "trak", "mdia", "minf", "stbl", "stsd"]);
        var entries = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stsd + 12));
        Assert.Equal(expectSecondEntry ? 2u : 1u, entries);
    }

    private static int FindBox(byte[] file, string[] path)
    {
        int offset = 0, end = file.Length;
        foreach (var name in path)
        {
            var found = -1;
            for (var position = offset; position + 8 <= end;)
            {
                var size = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(position));
                if (System.Text.Encoding.ASCII.GetString(file, position + 4, 4) == name)
                {
                    found = position;
                    end = position + size;
                    break;
                }
                position += size;
            }
            Assert.True(found >= 0, name);
            offset = found + 8;
        }
        return offset - 8;
    }

    private static unsafe void AssertFirstFrameHdr(string path, AVColorTransferCharacteristic transfer, bool expectMastering)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        AVStream* stream = input.GetVideoStream();
        using var decoder = new CodecContext(Codec.FindDecoderById(stream->codecpar->codec_id));
        decoder.FillParameters(new CodecParameters(stream->codecpar));
        FFmpegException.Check(FF.avcodec_open2(decoder, FF.avcodec_find_decoder(stream->codecpar->codec_id), null));
        using var frame = new Frame();
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex != stream->index)
                continue;
            FFmpegException.Check(FF.avcodec_send_packet(decoder, packet));
            if (FF.avcodec_receive_frame(decoder, frame) < 0)
                continue;
            AVFrame* raw = frame;
            Assert.Equal(transfer, raw->color_trc);
            Assert.Equal(AVPixelFormat.AV_PIX_FMT_YUV420P10LE, (AVPixelFormat)raw->format);
            Assert.Equal(expectMastering,
                FF.av_frame_get_side_data(raw, AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA) != null);
            return;
        }
        Assert.Fail("no decoded frame");
    }

    /// <summary>
    /// 可选真实广播素材（4K HEVC、1080i H.264、1080i MPEG-2 开放 GOP）：在非关键帧处剪切，核对帧数、解码无错，
    /// 并用画面相似度确认首尾画面就是所选帧（与相邻帧相比 PSNR 最高）。
    /// </summary>
    [NativeRuntimeFact("TSCUTTER_FFMPEG_TEST_SAMPLES")]
    public async Task CutsRealBroadcastSamplesAtArbitraryFrames()
    {
        var directory = Environment.GetEnvironmentVariable("TSCUTTER_FFMPEG_TEST_SAMPLES")!;
        foreach (var name in new[] { "4K.ts", "1080i.ts", "MPEG2.ts" })
        {
            var source = Path.Combine(directory, name);
            var settings = Settings(source);
            var pts = SourceFramePts(source, 10, 16);
            var (start, end) = (pts[7], pts[^9]);
            var output = Temporary(name);
            try
            {
                var service = new FrameAccurateExportService();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                await service.ExportAsync(new FrameAccurateExportRequest(output,
                    [new FrameAccurateExportSegment(source, start, end, settings)]));
                var report = Assert.Single(service.Reports);
                Console.WriteLine($"{name}: {watch.Elapsed.TotalSeconds:0.0}s, encoded {report.EncodedFrames}, " +
                                  $"copied {report.CopiedPackets} packets, {new FileInfo(output).Length} bytes");
                var decoded = Decode(output, out var errors);
                Assert.Equal(0, errors);
                Assert.Equal(pts.Count(value => value >= start && value <= end), decoded.Count);
                Assert.True(report.CopiedPackets > 0);
                var wanted = new[] { pts[6], start, pts[8], pts[^10], end, pts[^8] };
                var reference = Luma(source, pts[0], value => wanted.Contains(value));
                var outputFrames = Luma(output, long.MinValue, _ => true);
                var first = outputFrames.First().Value;
                var last = outputFrames.Last().Value;
                Assert.True(Psnr(first, reference[start]) > Math.Max(Psnr(first, reference[pts[6]]), Psnr(first, reference[pts[8]])));
                Assert.True(Psnr(last, reference[end]) > Math.Max(Psnr(last, reference[pts[^10]]), Psnr(last, reference[pts[^8]])));
                Assert.True(Psnr(first, reference[start]) > 35, $"first frame PSNR {Psnr(first, reference[start]):0.0}");
                AssertTimestamps(output, audioTolerance: 0.04);
                if (name != "4K.ts")
                    AssertFieldFlags(output, 3 << 3);
            }
            finally { File.Delete(output); }
        }
    }

    /// <summary>
    /// 杜比视界 Profile 5：重编码画面由 x265 按解码出的杜比元数据重新生成 RPU，每一帧都带有杜比元数据；
    /// MP4 为 dvh1 并保留配置记录，MKV 保留配置记录；由样本转出、PMT 带杜比描述符的 TS 剪切后描述符原样保留。
    /// 设置 TSCUTTER_FRAME_ACCURATE_KEEP_DIR 时保留输出文件，供外部工具逐帧比对 RPU。
    /// </summary>
    [NativeRuntimeFact("TSCUTTER_DOVI_TEST_SAMPLE")]
    public Task CutsDolbyVisionProfile5AtArbitraryFrames() =>
        CutsDolbyVisionAtArbitraryFrames(Environment.GetEnvironmentVariable("TSCUTTER_DOVI_TEST_SAMPLE")!, 5, 0);

    /// <summary>
    /// 杜比视界 Profile 8.1（HDR10 兼容基础层）：除重新生成 RPU 外，重编码画面还须带有 HDR10 静态元数据与 PQ 颜色描述；
    /// MP4 保留 hvc1，使不支持杜比视界的播放器仍按 HDR10 播放。
    /// </summary>
    [NativeRuntimeFact("TSCUTTER_DOVI_P81_TEST_SAMPLE")]
    public Task CutsDolbyVisionProfile81AtArbitraryFrames() =>
        CutsDolbyVisionAtArbitraryFrames(Environment.GetEnvironmentVariable("TSCUTTER_DOVI_P81_TEST_SAMPLE")!, 8, 1);

    private static async Task CutsDolbyVisionAtArbitraryFrames(string sample, byte profile, byte compatibility)
    {
        // 与广播一致的杜比 TS：样本转为 TS（ffmpeg 不写杜比描述符），再在 PMT 的视频项后补上描述符。
        var transport = Temporary(".ts");
        try
        {
            WriteDolbyTransportStream(sample, transport);
            foreach (var container in new[] { "mp4", "mkv", "ts" })
                await CutDolbyVision(container == "ts" ? transport : sample, container, profile, compatibility);
        }
        finally { File.Delete(transport); }
        await MergeDolbyVision(sample, profile, compatibility);
    }

    private static async Task CutDolbyVision(string source, string container, byte profile, byte compatibility)
    {
        // 窗口需跨越至少两个 IDR，使中间有直接复制的部分。
        var pts = SourceFramePts(source, 2, 12);
        var (start, end) = (pts[7], pts[^9]);
        {
            var settings = Settings(source);
            settings.Container = container;
            // MP4 源中的 mov_text 字幕不能写入 MKV。
            if (container == "mkv")
                settings.StreamIndices = settings.StreamIndices.Where(index => !IsSubtitle(source, index)).ToList();
            var output = Temporary("." + container);
            try
            {
                var service = new FrameAccurateExportService();
                Assert.Equal(FrameAccurateSupport.Supported, CheckSupport(source, settings));
                var watch = System.Diagnostics.Stopwatch.StartNew();
                await service.ExportAsync(new FrameAccurateExportRequest(output,
                    [new FrameAccurateExportSegment(source, start, end, settings)]));
                var report = Assert.Single(service.Reports);
                Console.WriteLine($"DoVi {container}: {watch.Elapsed.TotalSeconds:0.0}s, encoded {report.EncodedFrames}, " +
                                  $"copied {report.CopiedPackets} packets");
                if (Environment.GetEnvironmentVariable("TSCUTTER_FRAME_ACCURATE_KEEP_DIR") is { Length: > 0 } keep)
                    File.Copy(output, Path.Combine(keep, $"dovi{profile}{compatibility}-cut.{container}"), overwrite: true);
                Assert.True(report.EncodedFrames > 0 && report.CopiedPackets > 0);
                // 部分杜比片源自带 FFmpeg 严格模式无法解析的 SEI（原片同样报错），这里按常规模式解码；
                // 复制部分与原片逐字节一致由外部比对确认。
                Decode(output, out var errors, explode: false);
                Assert.Equal(0, errors);
                var (frames, withMetadata) = DolbyFrames(output);
                Assert.Equal(pts.Count(value => value >= start && value <= end), frames);
                Assert.Equal(frames, withMetadata);
                AssertDolbyConfiguration(output, container, profile, compatibility);
                // HDR10 兼容基础层：首帧（重编码）带母版显示元数据，颜色为 PQ。
                if (compatibility == 1)
                    AssertFirstFrameHdr(output, AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084, expectMastering: true);
                // 首尾画面与目标帧最接近，而不是相邻帧（Luma 在取满 6 帧后停止解码源文件）。
                var wanted = new[] { pts[6], start, pts[8], pts[^10], end, pts[^8] };
                var reference = Luma(source, pts[0], value => wanted.Contains(value));
                var outputFrames = Luma(output, long.MinValue, _ => true);
                var (first, last) = (outputFrames.First().Value, outputFrames.Last().Value);
                Assert.True(Psnr(first, reference[start]) > Math.Max(Psnr(first, reference[pts[6]]), Psnr(first, reference[pts[8]])));
                Assert.True(Psnr(last, reference[end]) > Math.Max(Psnr(last, reference[pts[^10]]), Psnr(last, reference[pts[^8]])));
                Assert.True(Psnr(first, reference[start]) > 35, $"first frame PSNR {Psnr(first, reference[start]):0.0}");
                // TS：按传输包写出，不新增传输层异常。
                if (container == "ts")
                    Assert.Equal(0, (await new TsStreamAnalyzer().AnalyzeAsync(output)).TotalErrorCount);
            }
            finally { File.Delete(output); }
        }
    }

    private static async Task MergeDolbyVision(string source, byte profile, byte compatibility)
    {
        var pts = SourceFramePts(source, 2, 12);
        var (start, end) = (pts[7], pts[^9]);
        // 同一源的两段合并：样本描述在原始/重编码参数集之间多次切换，每帧仍带杜比元数据。
        var merged = Temporary(".mp4");
        try
        {
            var settings = Settings(source);
            settings.Container = "mp4";
            settings.StreamIndices = settings.StreamIndices.Where(index => !IsSubtitle(source, index)).ToList();
            var middle = pts.Count / 2;
            var segments = new[] { (start, pts[middle - 10]), (pts[middle + 10], end) };
            await new FrameAccurateExportService().ExportAsync(new FrameAccurateExportRequest(merged,
                segments.Select(segment => new FrameAccurateExportSegment(source, segment.Item1, segment.Item2, settings)).ToList()));
            Decode(merged, out var errors, explode: false);
            Assert.Equal(0, errors);
            var (frames, withMetadata) = DolbyFrames(merged);
            Assert.Equal(segments.Sum(segment => pts.Count(value => value >= segment.Item1 && value <= segment.Item2)), frames);
            Assert.Equal(frames, withMetadata);
            AssertDolbyConfiguration(merged, "mp4", profile, compatibility);
        }
        finally { File.Delete(merged); }
    }

    /// <summary>
    /// 杜比视界支持判断（在合成素材上附加配置记录）：单层 Profile 5 与 8.1 输出 MP4/MKV 可用；
    /// TS 输出同样可用（按传输包写出、保留杜比描述符）；其他 profile（含 8.2/8.4）、带增强层、压缩元数据导出前即说明原因。
    /// </summary>
    [NativeMediaFact("frameaccurate")]
    public unsafe void ClassifiesDolbyVisionConfigurations()
    {
        foreach (var (name, profile, compatibility, enhancementLayer, compression, expected) in new[]
                 {
                     ("hevc-hdr10.mp4", 5, 0, false, 0, FrameAccurateSupport.Supported),
                     ("hevc-hdr10.mp4", 8, 1, false, 0, FrameAccurateSupport.Supported),
                     ("hevc-hdr10.mp4", 8, 2, false, 0, FrameAccurateSupport.DolbyVision),
                     ("hevc-hdr10.mp4", 8, 4, false, 0, FrameAccurateSupport.DolbyVision),
                     ("hevc-hdr10.mp4", 5, 1, false, 0, FrameAccurateSupport.DolbyVision),
                     ("hevc-hdr10.mp4", 7, 6, true, 0, FrameAccurateSupport.DolbyVision),
                     ("hevc-hdr10.mp4", 5, 0, false, 2, FrameAccurateSupport.DolbyVision),
                     ("hevc-hlg.ts", 5, 0, false, 0, FrameAccurateSupport.Supported)
                 })
        {
            var path = Fixture(name);
            using var input = FormatContext.OpenInputUrl(path);
            input.LoadStreamInfo();
            AVStream* stream = input.GetVideoStream();
            var raw = stream->codecpar;
            var side = FF.av_packet_side_data_new(&raw->coded_side_data, &raw->nb_coded_side_data,
                AVPacketSideDataType.AV_PKT_DATA_DOVI_CONF, 9, 0);
            Assert.True(side != null);
            // 版本 1.0、profile、level 6、rpu、el、bl、基础层兼容编号、元数据压缩方式。
            byte[] record = [1, 0, (byte)profile, 6, 1, (byte)(enhancementLayer ? 1 : 0), 1, (byte)compatibility, (byte)compression];
            record.CopyTo(new Span<byte>(side->data, record.Length));
            var settings = Settings(path);
            foreach (var container in settings.Container == "ts" ? new[] { "ts" } : ["mp4", "mkv"])
                Assert.Equal(expected, FrameAccurateExportService.CheckSupport(input, settings.VideoIndex, settings.StreamIndices, container));
        }
    }

    /// <summary>
    /// 把杜比视界 MP4 转为 TS（视频与音频，时间戳换算到 TS 的时间基），并在每个 PMT 的 HEVC 项后补上
    /// DOVI_video_stream_descriptor（标签 0xB0，内容取自样本的配置记录），重算 CRC。
    /// </summary>
    private static unsafe void WriteDolbyTransportStream(string source, string target)
    {
        using (var input = FormatContext.OpenInputUrl(source))
        {
            input.LoadStreamInfo();
            AVFormatContext* raw = input;
            AVFormatContext* output = null;
            FFmpegException.Check(FF.avformat_alloc_output_context2(&output, null, "mpegts", target));
            try
            {
                var map = new int[raw->nb_streams];
                for (var i = 0; i < raw->nb_streams; i++)
                {
                    map[i] = -1;
                    var type = raw->streams[i]->codecpar->codec_type;
                    if (type is not (AVMediaType.AVMEDIA_TYPE_VIDEO or AVMediaType.AVMEDIA_TYPE_AUDIO) ||
                        (raw->streams[i]->disposition & 1024) != 0)
                        continue;
                    var stream = FF.avformat_new_stream(output, null);
                    FFmpegException.Check(FF.avcodec_parameters_copy(stream->codecpar, raw->streams[i]->codecpar));
                    stream->codecpar->codec_tag = 0;
                    map[i] = stream->index;
                }
                FFmpegException.Check(FF.avio_open2(&output->pb, target, 2, null, null));
                FFmpegException.Check(FF.avformat_write_header(output, null));
                foreach (var packet in input.ReadPackets())
                {
                    AVPacket* p = packet;
                    if (map[p->stream_index] < 0)
                        continue;
                    var from = raw->streams[p->stream_index]->time_base;
                    var to = output->streams[map[p->stream_index]]->time_base;
                    long Convert(long value) => value == FF.AV_NOPTS_VALUE ? value
                        : (long)Math.Round((decimal)value * from.num * to.den / ((decimal)from.den * to.num));
                    p->pts = Convert(p->pts);
                    p->dts = Convert(p->dts);
                    p->duration = Convert(p->duration);
                    p->stream_index = map[p->stream_index];
                    p->pos = -1;
                    FFmpegException.Check(FF.av_interleaved_write_frame(output, p));
                }
                FFmpegException.Check(FF.av_write_trailer(output));
            }
            finally
            {
                FF.avio_closep(&output->pb);
                FF.avformat_free_context(output);
            }
        }
        DolbyVisionRecord record;
        int level;
        using (var input = FormatContext.OpenInputUrl(source))
        {
            input.LoadStreamInfo();
            AVStream* stream = input.GetVideoStream();
            record = FrameAccurateVideoEncoder.DolbyVisionConfiguration(stream->codecpar)!.Value;
            level = DolbyLevel(stream->codecpar);
        }
        byte[] descriptor = [0xB0, 5, 1, 0, (byte)(record.Profile << 1 | level >> 5),
            (byte)((level & 0x1F) << 3 | (record.Rpu ? 4 : 0) | (record.EnhancementLayer ? 2 : 0) | (record.BaseLayer ? 1 : 0)),
            (byte)(record.Compatibility << 4)];
        var data = File.ReadAllBytes(target);
        var pmtPid = -1;
        var patched = 0;
        for (var offset = 0; offset + 188 <= data.Length; offset += 188)
        {
            var packet = data.AsSpan(offset, 188);
            var pid = ((packet[1] & 0x1F) << 8) | packet[2];
            if ((packet[1] & 0x40) == 0 || pid != 0 && pid != pmtPid)
                continue;
            var start = 4 + ((packet[3] & 0x20) != 0 ? 1 + packet[4] : 0);
            start += 1 + packet[start];
            if (pid == 0)
            {
                pmtPid = ((packet[start + 10] & 0x1F) << 8) | packet[start + 11];
                continue;
            }
            var length = ((packet[start + 1] & 0x0F) << 8) | packet[start + 2];
            var section = packet.Slice(start, 3 + length).ToArray().ToList();
            var position = 12 + (((section[10] & 0x0F) << 8) | section[11]);
            while (position < section.Count - 4 && section[position] != 0x24)
                position += 5 + (((section[position + 3] & 0x0F) << 8) | section[position + 4]);
            if (position >= section.Count - 4)
                continue;
            var info = ((section[position + 3] & 0x0F) << 8) | section[position + 4];
            section.InsertRange(position + 5 + info, descriptor);
            info += descriptor.Length;
            section[position + 3] = (byte)(0xF0 | info >> 8);
            section[position + 4] = (byte)info;
            length += descriptor.Length;
            section[1] = (byte)((section[1] & 0xF0) | length >> 8);
            section[2] = (byte)length;
            var bytes = section.ToArray();
            TsPsiSectionBuilder.WriteCrc(bytes);
            Assert.True(start + bytes.Length <= 188);
            packet[start..].Fill(0xFF);
            bytes.CopyTo(packet[start..]);
            patched++;
        }
        Assert.True(patched > 0);
        File.WriteAllBytes(target, data);
    }

    private static unsafe int DolbyLevel(AVCodecParameters* parameters)
    {
        for (var i = 0; i < parameters->nb_coded_side_data; i++)
            if (parameters->coded_side_data[i].type == AVPacketSideDataType.AV_PKT_DATA_DOVI_CONF)
                return parameters->coded_side_data[i].data[3];
        throw new InvalidOperationException("no DOVI configuration");
    }

    private static bool IsSubtitle(string source, int index)
    {
        using var input = FormatContext.OpenInputUrl(source);
        input.LoadStreamInfo();
        return input.Streams[index].Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_SUBTITLE;
    }

    private static FrameAccurateSupport CheckSupport(string source, FrameAccurateExportSettings settings)
    {
        using var input = FormatContext.OpenInputUrl(source);
        input.LoadStreamInfo();
        return FrameAccurateExportService.CheckSupport(input, settings.VideoIndex, settings.StreamIndices, settings.Container);
    }

    private static unsafe (int Frames, int WithMetadata) DolbyFrames(string path)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        AVStream* stream = input.GetVideoStream();
        using var decoder = new CodecContext(Codec.FindDecoderById(stream->codecpar->codec_id));
        decoder.FillParameters(new CodecParameters(stream->codecpar));
        decoder.PacketTimeBase = stream->time_base;
        FFmpegException.Check(FF.avcodec_open2(decoder, FF.avcodec_find_decoder(stream->codecpar->codec_id), null));
        int frames = 0, withMetadata = 0;
        using var frame = new Frame();
        void Receive()
        {
            while (FF.avcodec_receive_frame(decoder, frame) >= 0)
            {
                frames++;
                if (FF.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DOVI_METADATA) != null)
                    withMetadata++;
                frame.Unref();
            }
        }
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex != stream->index)
                continue;
            FF.avcodec_send_packet(decoder, packet);
            Receive();
        }
        FF.avcodec_send_packet(decoder, null);
        Receive();
        return (frames, withMetadata);
    }

    private static unsafe void AssertDolbyConfiguration(string path, string container, byte profile, byte compatibility)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        AVStream* stream = input.GetVideoStream();
        var record = FrameAccurateVideoEncoder.DolbyVisionConfiguration(stream->codecpar);
        Assert.True(record.HasValue, "DOVI configuration record");
        Assert.Equal(profile, record.Value.Profile);
        Assert.Equal(compatibility, record.Value.Compatibility);
        if (container != "mp4")
            return;
        // 基础层不兼容（Profile 5）为 dvh1，兼容 HDR10 的 8.1 保留 hvc1。
        Assert.Equal(compatibility == 0 ? 0x31687664u : 0x31637668u, stream->codecpar->codec_tag);
        // 每个样本描述（原始参数集、重编码参数集）都应带有配置盒：profile 不超过 7 为 dvcC，8 起为 dvvC。
        var box = profile <= 7 ? "dvcC"u8.ToArray() : "dvvC"u8.ToArray();
        var file = File.ReadAllBytes(path);
        var stsd = FindBox(file, ["moov", "trak", "mdia", "minf", "stbl", "stsd"]);
        var size = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(stsd));
        var body = file.AsSpan(stsd, size);
        var entries = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(body[12..]);
        var dvcc = 0;
        for (var i = body.IndexOf(box); i >= 0; i = body[(i + 4)..].IndexOf(box) is var next and >= 0 ? i + 4 + next : -1)
            dvcc++;
        Console.WriteLine($"DoVi mp4: stsd entries {entries}, configuration boxes {dvcc}");
        Assert.Equal(2u, entries);
        Assert.Equal((int)entries, dvcc);
    }

    /// <summary>读取时间窗口内全部视频包的 PTS（显示顺序），不解码。</summary>
    private static unsafe List<long> SourceFramePts(string path, double from, double to)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        AVStream* stream = input.GetVideoStream();
        var origin = stream->start_time;
        var scale = stream->time_base.den / (double)stream->time_base.num;
        input.SeekFrame(origin + (long)(from * scale), stream->index);
        var result = new List<long>();
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex != stream->index || packet.Pts == FF.AV_NOPTS_VALUE)
                continue;
            if (packet.Pts > origin + (long)(to * scale))
                break;
            result.Add(packet.Pts);
        }
        result.Sort();
        // 去掉开头可能不完整的 GOP 预读，只取完整窗口内的帧。
        return result.Where(value => value >= origin + (long)((from + 1) * scale)).ToList();
    }

    /// <summary>按 8 像素间隔采样亮度平面，供 PSNR 比较；只保留满足条件的帧。</summary>
    private static unsafe SortedDictionary<long, ushort[]> Luma(string path, long seekPts, Func<long, bool> keep)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        AVStream* stream = input.GetVideoStream();
        if (seekPts != long.MinValue)
            input.SeekFrame(seekPts - stream->time_base.den / stream->time_base.num * 3, stream->index);
        using var decoder = new CodecContext(Codec.FindDecoderById(stream->codecpar->codec_id));
        decoder.FillParameters(new CodecParameters(stream->codecpar));
        decoder.PacketTimeBase = stream->time_base;
        FFmpegException.Check(FF.avcodec_open2(decoder, FF.avcodec_find_decoder(stream->codecpar->codec_id), null));
        var result = new SortedDictionary<long, ushort[]>();
        using var frame = new Frame();
        void Receive()
        {
            while (FF.avcodec_receive_frame(decoder, frame) >= 0)
            {
                AVFrame* raw = frame;
                if (keep(raw->pts))
                {
                    var wide = (AVPixelFormat)raw->format == AVPixelFormat.AV_PIX_FMT_YUV420P10LE;
                    var samples = new List<ushort>();
                    for (var y = 0; y < raw->height; y += 8)
                    for (var x = 0; x < raw->width; x += 8)
                    {
                        var row = raw->data[0] + y * raw->linesize[0];
                        samples.Add(wide ? ((ushort*)row)[x] : (ushort)(row[x] << 2));
                    }
                    result[raw->pts] = samples.ToArray();
                }
                frame.Unref();
            }
        }
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex != stream->index)
                continue;
            FF.avcodec_send_packet(decoder, packet);
            Receive();
            // 源素材只需要定位点附近的 6 个参考帧。
            if (seekPts != long.MinValue && result.Count >= 6)
                break;
        }
        FF.avcodec_send_packet(decoder, null);
        Receive();
        return result;
    }

    private static double Psnr(ushort[] a, ushort[] b)
    {
        double error = 0;
        for (var i = 0; i < a.Length; i++)
            error += (a[i] - b[i]) * (double)(a[i] - b[i]);
        return 10 * Math.Log10(1023.0 * 1023.0 / Math.Max(error / a.Length, 1e-9));
    }

    [NativeMediaFact("frameaccurate")]
    public async Task SplicesOpenGopMpeg2()
    {
        // GOP 24 帧，第一个之后均为开放 GOP：每个 I 帧后有 3 个参考上一 GOP 的前导 B 帧；隔行上场优先。
        var source = Fixture("mpeg2.ts");
        var frames = DecodeFrames(source);
        foreach (var (start, end, expectCopy) in new (int, int?, bool)[]
                 {
                     (40, 160, true),  // 跨 GOP：复制起点的前导 B 帧并入头段重编码
                     (30, 40, false),  // 同一 GOP
                     (48, 95, true),   // 起点即开放 GOP 的 I 帧；终点落在下一个 I 帧的前导 B 帧上（尾段预热）
                     (47, 47, false),  // 单个前导 B 帧
                     (200, null, true)
                 })
        {
            var output = Temporary("mpeg2.ts");
            try
            {
                var service = new FrameAccurateExportService();
                await service.ExportAsync(new FrameAccurateExportRequest(output,
                    [new FrameAccurateExportSegment(source, frames[start], end is { } e ? frames[e] : null, Settings(source))]));
                Assert.Equal(Enumerable.Range(start, (end ?? frames.Count - 1) - start + 1), DecodeNumbers(output));
                var report = Assert.Single(service.Reports);
                Assert.Equal(expectCopy, report.CopiedPackets > 0);
                if (report.CopyStartPts is { } copyStart)
                    AssertSlicesCopied(source, output, copyStart, report.CopyEndPts);
                AssertTimestamps(output, audioTolerance: 1152 / 48000.0);
                AssertFieldFlags(output, FieldFlags(source).Single());
            }
            finally { File.Delete(output); }
        }
        var merged = Temporary("mpeg2.ts");
        try
        {
            var settings = Settings(source);
            await new FrameAccurateExportService().ExportAsync(new FrameAccurateExportRequest(merged,
            [
                new FrameAccurateExportSegment(source, frames[30], frames[80], settings),
                new FrameAccurateExportSegment(source, frames[100], frames[150], settings.Snapshot())
            ]));
            Assert.Equal(Enumerable.Range(30, 51).Concat(Enumerable.Range(100, 51)), DecodeNumbers(merged));
            AssertTimestamps(merged, audioTolerance: 1152 / 48000.0);
        }
        finally { File.Delete(merged); }
    }

    [NativeMediaFact("frameaccurate")]
    public async Task KeepsInterlacedFieldOrder()
    {
        // 隔行 H.264（MBAFF，上场优先）按完整帧剪切；重编码段保持隔行与场序。
        var source = Fixture("interlaced.ts");
        var frames = DecodeFrames(source);
        foreach (var (start, end) in new[] { (10, 80), (30, 35) })
        {
            var output = Temporary("interlaced.ts");
            try
            {
                var service = new FrameAccurateExportService();
                await service.ExportAsync(new FrameAccurateExportRequest(output,
                    [new FrameAccurateExportSegment(source, frames[start], frames[end], Settings(source))]));
                Assert.Equal(Enumerable.Range(start, end - start + 1), DecodeNumbers(output));
                AssertTimestamps(output);
                AssertFieldFlags(output, FieldFlags(source).Single());
            }
            finally { File.Delete(output); }
        }
    }

    /// <summary>输出每一帧的隔行/场序标志（AV_FRAME_FLAG_INTERLACED、TOP_FIELD_FIRST）都与源一致。</summary>
    private static void AssertFieldFlags(string path, int expected)
    {
        var flags = FieldFlags(path);
        Assert.Equal(expected, Assert.Single(flags));
        Assert.NotEqual(0, expected);
    }

    private static unsafe HashSet<int> FieldFlags(string path)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        AVStream* stream = input.GetVideoStream();
        using var decoder = new CodecContext(Codec.FindDecoderById(stream->codecpar->codec_id));
        decoder.FillParameters(new CodecParameters(stream->codecpar));
        FFmpegException.Check(FF.avcodec_open2(decoder, FF.avcodec_find_decoder(stream->codecpar->codec_id), null));
        using var frame = new Frame();
        var flags = new HashSet<int>();
        var count = 0;
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex != stream->index)
                continue;
            FFmpegException.Check(FF.avcodec_send_packet(decoder, packet));
            while (FF.avcodec_receive_frame(decoder, frame) >= 0)
            {
                AVFrame* raw = frame;
                flags.Add(raw->flags & (3 << 3));
                frame.Unref();
            }
            // 真实素材只需检查开头若干帧。
            if (++count > 600)
                break;
        }
        FF.avcodec_send_packet(decoder, null);
        while (FF.avcodec_receive_frame(decoder, frame) >= 0)
        {
            AVFrame* raw = frame;
            flags.Add(raw->flags & (3 << 3));
            frame.Unref();
        }
        return flags;
    }

    [NativeMediaFact("frameaccurate")]
    public async Task KeepsFramesAcrossTsTimestampWrap()
    {
        // 33 位 PTS 约在第 92 帧回绕；跨回绕和回绕后单 GOP 内的范围都必须精确。
        var source = Fixture("wrap.ts");
        var frames = DecodeFrames(source);
        foreach (var (start, end) in new[] { (60, 140), (90, 99), (100, 124) })
        {
            var output = Temporary("wrap.ts");
            try
            {
                await new FrameAccurateExportService().ExportAsync(new FrameAccurateExportRequest(output,
                    [new FrameAccurateExportSegment(source, frames[start], frames[end], Settings(source))]));
                Assert.Equal(Enumerable.Range(start, end - start + 1), DecodeNumbers(output));
                AssertTimestamps(output);
            }
            finally { File.Delete(output); }
        }
    }

    [NativeMediaFact("frameaccurate")]
    public async Task MergesSegmentsOfTheSameSource()
    {
        foreach (var name in new[] { "h264.ts", "h264.mp4", "h264.mkv" })
        {
            var source = Fixture(name);
            var frames = Decode(source, out _);
            var output = Temporary(name);
            try
            {
                var settings = Settings(source);
                var check = name.EndsWith(".ts") ? new ExportTsCheckSession(output) : null;
                await new FrameAccurateExportService().ExportAsync(new FrameAccurateExportRequest(output,
                [
                    new FrameAccurateExportSegment(source, frames[40].Pts, frames[100].Pts, settings),
                    new FrameAccurateExportSegment(source, frames[150].Pts, frames[230].Pts, settings.Snapshot()),
                    new FrameAccurateExportSegment(source, frames[240].Pts, frames[240].Pts, settings.Snapshot())
                ]), checkSession: check);
                if (check is not null)
                {
                    // 导出检测分析的是封装器写出的最终字节，结论须与对成品文件的独立分析一致且无错误。
                    check.Complete();
                    var reference = await new TsStreamAnalyzer().AnalyzeAsync(output);
                    Assert.Equal(TsExportCheckState.Completed, check.State);
                    Assert.Equal(new FileInfo(output).Length, check.Result!.FileSize);
                    Assert.Equal(reference.PacketCount, check.Result.PacketCount);
                    Assert.Equal(0, check.Result.TotalErrorCount);
                    Assert.Equal(reference.TotalErrorCount, check.Result.TotalErrorCount);
                    Assert.Equal(reference.TotalWarningCount, check.Result.TotalWarningCount);
                }
                var decoded = Decode(output, out var errors);
                Assert.Equal(0, errors);
                Assert.Equal(Enumerable.Range(40, 61).Concat(Enumerable.Range(150, 81)).Append(240),
                    decoded.Select(frame => frame.Number));
                AssertTimestamps(output);
            }
            finally { File.Delete(output); }
        }
    }

    [NativeMediaFact("frameaccurate")]
    public async Task RejectsUnsupportedStructuresBeforeWriting()
    {
        // 4:4:4 H.264（High 4:4:4 Predictive 档次）不支持，在写文件前说明原因。
        var chroma = Fixture("h264-444.mp4");
        var chromaFrames = Decode(chroma, out _);
        var output = Temporary("h264-444.mp4");
        var error = await Assert.ThrowsAsync<FrameAccurateExportException>(() => new FrameAccurateExportService().ExportAsync(
            new FrameAccurateExportRequest(output, [new FrameAccurateExportSegment(chroma,
                chromaFrames[10].Pts, chromaFrames[40].Pts, Settings(chroma))])));
        Assert.Equal(FrameAccurateSupport.Codec, error.Support);
        Assert.False(File.Exists(output));
    }

    /// <summary>
    /// 相机 MOV 的 PCM 音频不带 chan 原子，声道布局只有声道数；MP4 的 PCM（ipcm）必须声明布局，否则写文件头时报“参数无效”。
    /// 智能剪辑与关键帧剪辑输出 MP4 时按声道数补默认布局（双声道为立体声），MKV 本来就不需要布局。
    /// </summary>
    [NativeMediaFact("frameaccurate")]
    public async Task FillsTheUnspecifiedPcmChannelLayout()
    {
        var source = Fixture("pcm-no-layout.mov");
        using (var input = FormatContext.OpenInputUrl(source))
        {
            input.LoadStreamInfo();
            var layout = input.Streams[1].Codecpar!.ChLayout;
            Assert.Equal(AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC, layout.order);
            Assert.Equal(2, layout.nb_channels);
        }
        var frames = Decode(source, out _).OrderBy(frame => frame.Pts).ToList();
        foreach (var container in new[] { "mp4", "mkv" })
        {
            var smart = Temporary("out." + container);
            var key = Temporary("out." + container);
            try
            {
                var settings = Settings(source);
                settings.Container = container;
                await new FrameAccurateExportService().ExportAsync(new FrameAccurateExportRequest(smart,
                    [new FrameAccurateExportSegment(source, frames[10].Pts, frames[70].Pts, settings)]));
                Assert.Equal(Enumerable.Range(10, 61), DecodeNumbers(smart));
                await new RemuxExportService().ExportAsync(new RemuxExportRequest(key, [new RemuxExportSegment(source, 1, 3,
                    new RemuxExportSettings
                    {
                        Container = container, ReferenceVideoIndex = 0, TimelineOriginSeconds = 0, StreamIndices = [0, 1]
                    })]));
                foreach (var output in new[] { smart, key })
                {
                    using var result = FormatContext.OpenInputUrl(output);
                    result.LoadStreamInfo();
                    var audio = Assert.Single(result.Streams, stream => stream.Codecpar!.CodecType == AVMediaType.AVMEDIA_TYPE_AUDIO);
                    Assert.Equal(AVCodecID.AV_CODEC_ID_PCM_S24LE, audio.Codecpar!.CodecId);
                    Assert.Equal(2, audio.Codecpar.ChLayout.nb_channels);
                    Assert.Contains(result.ReadPackets(), packet => packet.StreamIndex == audio.Index);
                }
            }
            finally
            {
                File.Delete(smart);
                File.Delete(key);
            }
        }
    }

    /// <summary>
    /// H.264 High 4:2:2（广播交换常用）：8 位逐行 MP4（闭合 GOP）；10 位 50p 开放 GOP——B 帧金字塔，I 帧之后紧跟作参考的前导 B 帧，
    /// SPS 不声明重排序深度；8 位隔行（MBAFF）开放 GOP；HEVC 4:2:2 的 10 位 TS 与 8 位 MP4。剪切后档次与像素格式不变，严格模式解码无错、帧号连续，确有复制区；
    /// 不沿用探测得到的重排序深度解码（播放器从文件开头打开）也不丢帧。开放 GOP 素材的复制起点确实带有作参考的前导画面。
    /// </summary>
    [NativeMediaTheory("frameaccurate")]
    [InlineData("h264-422.mp4", 10, 60, false)]
    // 复制起点为第 50 帧的 I 帧：其后紧跟作参考的前导 B 帧（带取消 I 帧之前画面的 MMCO）。
    [InlineData("h264-422-10bit.ts", 30, 330, true)]
    [InlineData("h264-422-10bit.ts", 51, 249, false)]
    [InlineData("h264-422-10bit.ts", 0, 399, false)]
    [InlineData("h264-422-interlaced.ts", 20, 250, false)]
    [InlineData("h264-422-interlaced.ts", 7, 99, false)]
    // HEVC 格式范围扩展 4:2:2（Main 4:2:2 10 档次）：10 位 TS 带 RADL 前导画面，8 位 MP4 为 hvc1。
    [InlineData("hevc-422.ts", 17, 160, false)]
    [InlineData("hevc-422.ts", 30, 99, false)]
    [InlineData("hevc-422-8bit.mp4", 33, 170, false)]
    public async Task Splices422Video(string name, int start, int end, bool leadingReference)
    {
        var source = Fixture(name);
        var frames = DecodeFrames(source);
        var output = Temporary(name);
        try
        {
            var service = new FrameAccurateExportService();
            await service.ExportAsync(new FrameAccurateExportRequest(output,
                [new FrameAccurateExportSegment(source, frames[start], frames[end], Settings(source))]));
            var report = Assert.Single(service.Reports);
            Assert.True(report.CopiedPackets > 0);
            Assert.Equal(Enumerable.Range(start, end - start + 1), Decode(output, out var errors).Select(frame => frame.Number));
            Assert.Equal(0, errors);
            // 有重编码片头时，输出从重编码段的 SPS 开始（复制起点为文件开头时与源文件相同，不在此列）。
            if (frames[start] < report.CopyStartPts)
            {
                Assert.Equal(Enumerable.Range(start, end - start + 1),
                    Decode(output, out errors, fresh: true).Select(frame => frame.Number));
                Assert.Equal(0, errors);
            }
            Assert.Equal(StreamFormat(source), StreamFormat(output));
            if (leadingReference)
                Assert.True(HasLeadingReference(source, report.CopyStartPts!.Value));
            AssertTimestamps(output);
            if (name.Contains("interlaced", StringComparison.Ordinal))
                AssertFieldFlags(output, FieldFlags(source).Single());
        }
        finally { File.Delete(output); }
    }

    /// <summary>
    /// AV1 与 VP9（在线视频站常见）：显示的关键帧作为衔接点，复制区的样本与源逐字节一致
    /// （AV1 含隐藏帧与 show_existing_frame，VP9 含带隐藏替代参考帧的超帧）；AV1 重编码段以编码器自己的序列头开始、
    /// 复制区沿用源的序列头，VP9 每个关键帧自带完整帧头；帧号连续、严格模式解码无错误，音频（Opus）随之复制。
    /// </summary>
    [NativeMediaTheory("frameaccurate")]
    [InlineData("av1.mp4", 40, 160)]
    [InlineData("av1.mp4", 13, 77)]
    [InlineData("av1.mp4", 0, 199)]
    [InlineData("av1.mkv", 31, 130)]
    [InlineData("av1-10bit.mkv", 17, 140)]
    [InlineData("vp9.mp4", 40, 160)]
    [InlineData("vp9.mp4", 13, 77)]
    [InlineData("vp9.mp4", 0, 199)]
    [InlineData("vp9.mkv", 31, 130)]
    [InlineData("vp9-10bit.mkv", 17, 140)]
    public async Task SplicesAv1AndVp9(string name, int start, int end)
    {
        var source = Fixture(name);
        var frames = DecodeFrames(source);
        var output = Temporary(name);
        try
        {
            Assert.Equal(FrameAccurateSupport.Supported, CheckSupport(source, Settings(source)));
            var service = new FrameAccurateExportService();
            await service.ExportAsync(new FrameAccurateExportRequest(output,
                [new FrameAccurateExportSegment(source, frames[start], frames[end], Settings(source))]));
            var report = Assert.Single(service.Reports);
            Assert.True(report.CopiedPackets > 0);
            Assert.Equal(Enumerable.Range(start, end - start + 1), Decode(output, out var errors).Select(frame => frame.Number));
            Assert.Equal(0, errors);
            Assert.Equal(StreamFormat(source), StreamFormat(output));
            var copied = UnitsByPts(source, report.CopyStartPts!.Value, report.CopyEndPts);
            var written = UnitsByPts(output, long.MinValue, null);
            Assert.NotEmpty(copied);
            Assert.All(copied, unit => Assert.Contains(written, item => item.SequenceEqual(unit)));
            // VP9 复制区确实包含超帧（末尾为超帧索引）。
            if (name.StartsWith("vp9", StringComparison.Ordinal))
                Assert.Contains(copied, unit => (unit[^1] & 0xE0) == 0xC0);
            AssertTimestamps(output, audioTolerance: 0.03);
        }
        finally { File.Delete(output); }
    }

    /// <summary>显示时间在 [from, to) 内的视频包（AV1 时间单元、VP9 样本）数据。</summary>
    private static unsafe List<byte[]> UnitsByPts(string path, long from, long? to)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        AVStream* stream = input.GetVideoStream();
        var result = new List<byte[]>();
        foreach (var packet in input.ReadPackets())
        {
            AVPacket* p = packet;
            if (p->stream_index != stream->index || p->pts < from || to is { } end && p->pts >= end)
                continue;
            result.Add(new ReadOnlySpan<byte>(p->data, p->size).ToArray());
        }
        return result;
    }

    /// <summary>
    /// 接收中断的录制（信号丢失）：中断前最后一个 PES 不完整、时间戳向前跳变、恢复后从 GOP 中间开始（参考帧缺失）。
    /// 跨越中断、在中断前结束、从中断后第一个可解码画面开始，都能导出：输出画面与源文件连续解码得到的画面一一对应
    /// （按显示时间排列后帧号相同、时间间隔相同，原时间线中的跳变保留）。重编码时长按实际画面计算，
    /// 紧邻中断的切点不会因时间戳跳变被拒绝；中断处解码器输出顺序与时间戳不一致（中断前最后的画面晚于中断后的画面输出）时，
    /// 重编码按显示时间排列。
    /// </summary>
    [NativeMediaFact("frameaccurate")]
    public async Task CutsAroundSignalLoss()
    {
        var source = Fixture("h264-422-loss.ts");
        var frames = Decode(source, out _, explode: false).OrderBy(frame => frame.Pts).ToList();
        var gap = Enumerable.Range(1, frames.Count - 1).Single(i => frames[i].Pts - frames[i - 1].Pts > 2 * 3600);
        foreach (var (first, last) in new[] { (gap - 30, gap + 40), (gap, gap + 60), (gap - 50, gap - 1), (gap - 3, gap + 3) })
        {
            var output = Temporary("loss.ts");
            try
            {
                await new FrameAccurateExportService().ExportAsync(new FrameAccurateExportRequest(output,
                    [new FrameAccurateExportSegment(source, frames[first].Pts, frames[last].Pts, Settings(source))]));
                var expected = frames.Skip(first).Take(last - first + 1).ToList();
                var decoded = Decode(output, out _, explode: false).OrderBy(frame => frame.Pts).ToList();
                Assert.Equal(expected.Select(frame => frame.Number), decoded.Select(frame => frame.Number));
                Assert.Equal(expected.Select(frame => frame.Pts - expected[0].Pts), decoded.Select(frame => frame.Pts - decoded[0].Pts));
                // 中断处同时丢失了一段音频（TS 中音频先于对应画面复用），输出音频从源文件中起点之后的第一个音频包开始。
                AssertTimestamps(output, alignedStart: false);
                Assert.Equal(FirstAudioAfter(source, expected[0].Pts) - Seconds(source, expected[0].Pts),
                    FirstAudioAfter(output, long.MinValue) - Seconds(output, decoded[0].Pts), 3);
            }
            finally { File.Delete(output); }
        }
    }

    /// <summary>首个显示时间不早于视频时间戳 videoPts 的音频包时间（秒）。</summary>
    private static unsafe double FirstAudioAfter(string path, long videoPts)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        AVFormatContext* raw = input;
        var start = videoPts == long.MinValue ? double.MinValue : Seconds(path, videoPts);
        foreach (var packet in input.ReadPackets())
        {
            AVPacket* p = packet;
            var stream = raw->streams[p->stream_index];
            if (stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO || p->pts == FF.AV_NOPTS_VALUE)
                continue;
            var seconds = p->pts * (double)stream->time_base.num / stream->time_base.den;
            if (seconds >= start - 0.0005)
                return seconds;
        }
        throw new InvalidOperationException("no audio");
    }

    private static unsafe double Seconds(string path, long videoPts)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        AVStream* stream = input.GetVideoStream();
        return videoPts * (double)stream->time_base.num / stream->time_base.den;
    }

    private static unsafe (int Profile, AVPixelFormat Format) StreamFormat(string path)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        AVStream* stream = input.GetVideoStream();
        return (stream->codecpar->profile, (AVPixelFormat)stream->codecpar->format);
    }

    /// <summary>解码顺序紧跟 splice 的包中，显示早于它（前导画面）且作参考的切片是否存在。</summary>
    private static unsafe bool HasLeadingReference(string path, long splice)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        AVStream* stream = input.GetVideoStream();
        var found = false;
        foreach (var packet in input.ReadPackets())
        {
            AVPacket* p = packet;
            if (p->stream_index != stream->index)
                continue;
            if (!found)
            {
                found = p->pts == splice;
                continue;
            }
            if (p->pts >= splice)
                return false;
            var data = new ReadOnlySpan<byte>(p->data, p->size);
            foreach (var range in VideoBitstream.EnumerateNals(data, 0))
                if ((data[range][0] & 0x1F) == 1 && (data[range][0] & 0x60) != 0)
                    return true;
        }
        return false;
    }

    /// <summary>
    /// 只有普通 I 帧（开放 GOP、前导 B 帧不作参考，与常见广播一致）的 H.264：复制区首个 I 帧改写为 IDR，
    /// 之后的 frame_num 重编号、删除指向 I 帧之前的 MMCO，前导画面并入片头重新编码。
    /// 严格模式解码无错误、帧号连续，且确实有复制区（不是整段重编码）。
    /// </summary>
    [NativeMediaTheory("frameaccurate")]
    // B 帧金字塔：I 帧之后的前导画面中有作参考的 B 帧，丢弃后其后画面重新编号；不能重新编号的 I 帧改用其他 I 帧衔接。
    [InlineData("open-gop.ts", 30, 120)]
    [InlineData("open-gop.ts", 60, 190)]
    [InlineData("open-gop-flat.ts", 30, 120)]
    [InlineData("open-gop-flat.ts", 49, 151)]
    [InlineData("open-gop-flat.ts", 50, 175)]
    [InlineData("open-gop-flat.mp4", 30, 120)]
    [InlineData("open-gop-cavlc.ts", 30, 120)]
    [InlineData("open-gop-cavlc.ts", 3, 199)]
    [InlineData("open-gop-mbaff.ts", 15, 100)]
    [InlineData("open-gop-mbaff.ts", 23, 61)]
    public async Task SplicesH264AtNonIdrIntraFrames(string name, int start, int end)
    {
        var source = Fixture(name);
        var frames = DecodeFrames(source);
        var output = Temporary(name);
        try
        {
            var service = new FrameAccurateExportService();
            await service.ExportAsync(new FrameAccurateExportRequest(output,
                [new FrameAccurateExportSegment(source, frames[start], frames[end], Settings(source))]));
            var decoded = Decode(output, out var errors);
            Assert.Equal(0, errors);
            Assert.Equal(Enumerable.Range(start, end - start + 1), decoded.Select(frame => frame.Number));
            AssertTimestamps(output);
            var report = Assert.Single(service.Reports);
            Assert.True(report.CopiedPackets > 0, "copy region");
            Assert.True(report.EncodedFrames < end - start + 1, "not fully re-encoded");
            if (name.Contains("mbaff"))
                AssertFieldFlags(output, FieldFlags(source).Single());
        }
        finally { File.Delete(output); }
    }

    /// <summary>
    /// HEVC 开放 GOP（与常见 4K 广播一致，除开头外没有 IDR）：关键帧是 CRA，其后的 RASL 前导画面参考上一 GOP。
    /// 复制区从 CRA 开始，RASL 丢弃并随片头重新编码，CRA 改写为 BLA；尾段的 RASL 从上一个 CRA 起预热解码后重新编码。
    /// 严格模式解码无错误、帧号连续，不沿用探测延迟的解码也不丢帧；确有复制区，输出中有改写出的 BLA。
    /// </summary>
    [NativeMediaTheory("frameaccurate")]
    [InlineData("hevc-cra.ts", 30, 120)]
    [InlineData("hevc-cra.ts", 13, 177)]
    [InlineData("hevc-cra.ts", 0, 199)]
    [InlineData("hevc-cra.mp4", 30, 120)]
    public async Task SplicesHevcAtCra(string name, int start, int end)
    {
        var source = Fixture(name);
        var frames = DecodeFrames(source);
        var output = Temporary(name);
        try
        {
            Assert.Equal(FrameAccurateSupport.Supported, CheckSupport(source, Settings(source)));
            var service = new FrameAccurateExportService();
            await service.ExportAsync(new FrameAccurateExportRequest(output,
                [new FrameAccurateExportSegment(source, frames[start], frames[end], Settings(source))]));
            var report = Assert.Single(service.Reports);
            Assert.True(report.CopiedPackets > 0, "copy region");
            Assert.True(report.EncodedFrames < end - start + 1, "not fully re-encoded");
            Assert.Equal(Enumerable.Range(start, end - start + 1), DecodeNumbers(output));
            Assert.Equal(Enumerable.Range(start, end - start + 1),
                Decode(output, out var errors, fresh: true).Select(frame => frame.Number));
            Assert.Equal(0, errors);
            if (start > 0)
                Assert.Contains(16, NalTypes(output));
            AssertTimestamps(output);
        }
        finally { File.Delete(output); }
    }

    /// <summary>输出视频中出现的全部 HEVC NAL 类型。</summary>
    private static unsafe HashSet<int> NalTypes(string path)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        AVStream* stream = input.GetVideoStream();
        // MP4/MKV 的附加数据是 hvcC（首字节为版本 1），样本为四字节长度前缀；TS 为 Annex B。
        var lengthSize = stream->codecpar->extradata_size > 0 && stream->codecpar->extradata[0] == 1 ? 4 : 0;
        var types = new HashSet<int>();
        foreach (var packet in input.ReadPackets())
        {
            AVPacket* raw = packet;
            if (raw->stream_index != stream->index)
                continue;
            var data = new ReadOnlySpan<byte>(raw->data, raw->size);
            foreach (var range in VideoBitstream.EnumerateNals(data, lengthSize))
                types.Add(VideoBitstream.NalType(data[range], true));
        }
        return types;
    }

    /// <summary>
    /// 切片头解析与改写逐位正确：每个切片按原 frame_num、原标记重写后与原始 NAL 逐字节相同
    /// （覆盖 CABAC、CAVLC 与 MBAFF，含加权预测、参考列表修改与 MMCO）。
    /// </summary>
    [NativeMediaTheory("frameaccurate")]
    [InlineData("open-gop-flat.ts")]
    [InlineData("open-gop-cavlc.ts")]
    [InlineData("open-gop-mbaff.ts")]
    [InlineData("open-gop.ts")]
    public unsafe void RewritesSliceHeadersBitExactly(string name)
    {
        using var input = FormatContext.OpenInputUrl(Fixture(name));
        input.LoadStreamInfo();
        AVStream* stream = input.GetVideoStream();
        var sets = new H264Splice.ParameterSets();
        var mmco = new List<H264Splice.Mmco>();
        var slices = 0;
        var withMarking = 0;
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex != stream->index)
                continue;
            AVPacket* raw = packet;
            var data = new ReadOnlySpan<byte>(raw->data, raw->size);
            foreach (var range in VideoBitstream.EnumerateNals(data, 0))
            {
                var nal = data[range];
                sets.Add(nal);
                if ((nal[0] & 0x1F) is not (1 or 5))
                    continue;
                var rbsp = new byte[nal.Length];
                var length = H264Splice.Unescape(nal[1..], rbsp);
                Assert.True(H264Splice.TryParseSlice(rbsp.AsSpan(0, length), nal[0], sets, mmco, out var header));
                var rewritten = new byte[length + 64];
                var size = H264Splice.RewriteSlice(rbsp.AsSpan(0, length), header, header.FrameNum, false, 0,
                    header.Adaptive ? mmco.ToList() : null, rewritten);
                var escaped = new byte[H264Splice.MaxEscapedLength(size)];
                var escapedLength = H264Splice.Escape(rewritten.AsSpan(0, size), escaped);
                Assert.Equal(nal[1..].ToArray(), escaped.AsSpan(0, escapedLength).ToArray());
                slices++;
                if (mmco.Count > 0)
                    withMarking++;
            }
        }
        Assert.True(slices >= 100);
        if (name is "open-gop-mbaff.ts" or "open-gop.ts")
            Assert.True(withMarking > 0);
    }

    [NativeMediaFact("frameaccurate")]
    public async Task MergesSegmentsSplicedAtNonIdrIntraFrames()
    {
        var source = Fixture("open-gop-flat.ts");
        var frames = DecodeFrames(source);
        var output = Temporary("open-gop-flat.ts");
        try
        {
            var service = new FrameAccurateExportService();
            await service.ExportAsync(new FrameAccurateExportRequest(output,
            [
                new FrameAccurateExportSegment(source, frames[30], frames[80], Settings(source)),
                new FrameAccurateExportSegment(source, frames[100], frames[160], Settings(source))
            ]));
            var decoded = Decode(output, out var errors);
            Assert.Equal(0, errors);
            Assert.Equal(Enumerable.Range(30, 51).Concat(Enumerable.Range(100, 61)), decoded.Select(frame => frame.Number));
            AssertTimestamps(output);
            Assert.All(service.Reports, report => Assert.True(report.CopiedPackets > 0));
        }
        finally { File.Delete(output); }
    }

    [NativeMediaFact("frameaccurate")]
    public async Task CancellationLeavesNoOutput()
    {
        var source = Fixture("h264.ts");
        var frames = Decode(source, out _);
        var output = Temporary("h264.ts");
        using var cancellation = new CancellationTokenSource();
        // 首次进度在写完文件头后报告，此时取消会中断后续读写。
        var progress = new SynchronousProgress(_ => cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FrameAccurateExportService().ExportAsync(
            new FrameAccurateExportRequest(output, [new FrameAccurateExportSegment(source, frames[40].Pts, frames[300].Pts, Settings(source))]),
            progress, cancellation.Token));
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(output)!, ".tscutter-*.tmp"));
    }

    private sealed class SynchronousProgress(Action<FrameAccurateExportProgress> handler) : IProgress<FrameAccurateExportProgress>
    {
        public void Report(FrameAccurateExportProgress value) => handler(value);
    }

    /// <summary>源素材的帧序号与显示顺序一致；返回按序号排列的原始 PTS。</summary>
    internal static List<long> DecodeFrames(string path)
    {
        var frames = Decode(path, out _);
        Assert.Equal(Enumerable.Range(0, frames.Count), frames.Select(frame => frame.Number));
        return frames.Select(frame => frame.Pts).ToList();
    }

    internal static List<int> DecodeNumbers(string path)
    {
        var frames = Decode(path, out var errors);
        Assert.Equal(0, errors);
        return frames.Select(frame => frame.Number).ToList();
    }

    private static string Temporary(string name) =>
        Path.Combine(Path.GetTempPath(), $"tscutter-frame-accurate-{Guid.NewGuid():N}{Path.GetExtension(name)}");

    /// <summary>
    /// 按显示顺序解码视频，读出每帧顶部条码中的帧序号；errors 统计解码错误和损坏帧。
    /// fresh 时不沿用探测得到的重排序深度，相当于播放器从文件开头打开、只按码流自身的声明输出画面。
    /// </summary>
    internal static unsafe List<(int Number, long Pts)> Decode(string path, out int errors, bool explode = true,
        bool fresh = false)
    {
        using var input = FormatContext.OpenInputUrl(path);
        input.LoadStreamInfo();
        AVStream* stream = input.GetVideoStream();
        using var decoder = new CodecContext(Codec.FindDecoderById(stream->codecpar->codec_id));
        decoder.FillParameters(new CodecParameters(stream->codecpar));
        decoder.PacketTimeBase = stream->time_base;
        AVCodecContext* context = decoder;
        if (explode)
            context->err_recognition = 1 << 3; // AV_EF_EXPLODE：码流错误直接报错，不隐藏。
        if (fresh)
            context->has_b_frames = 0;
        FFmpegException.Check(FF.avcodec_open2(decoder, FF.avcodec_find_decoder(stream->codecpar->codec_id), null));
        var result = new List<(int, long)>();
        var failures = 0;
        using var frame = new Frame();
        void Receive()
        {
            while (FF.avcodec_receive_frame(decoder, frame) is var code && code >= 0)
            {
                AVFrame* raw = frame;
                if (raw->decode_error_flags != 0)
                    failures++;
                var row = raw->data[0] + 8 * raw->linesize[0];
                // 10 位格式每个样本占两字节，亮/暗阈值按位深缩放。
                var wide = (AVPixelFormat)raw->format is AVPixelFormat.AV_PIX_FMT_YUV420P10LE or AVPixelFormat.AV_PIX_FMT_YUV422P10LE;
                var number = 0;
                for (var bit = 0; bit < 16; bit++)
                {
                    var sum = 0;
                    for (var x = bit * 16 + 4; x < bit * 16 + 12; x++)
                        sum += wide ? ((ushort*)row)[x] : row[x];
                    if (sum / 8 > (wide ? 512 : 128))
                        number |= 1 << bit;
                }
                result.Add((number, raw->pts));
                frame.Unref();
            }
        }
        foreach (var packet in input.ReadPackets())
        {
            if (packet.StreamIndex != stream->index)
                continue;
            if (FF.avcodec_send_packet(decoder, packet) < 0)
                failures++;
            Receive();
        }
        FF.avcodec_send_packet(decoder, null);
        Receive();
        errors = failures;
        return result;
    }

    /// <summary>源复制区间内的切片 NAL 必须按原顺序、逐字节出现在输出中。</summary>
    private static unsafe void AssertSlicesCopied(string source, string output, long copyStart, long? copyEnd)
    {
        var expected = new List<byte[]>();
        using (var input = FormatContext.OpenInputUrl(source))
        {
            input.LoadStreamInfo();
            AVStream* stream = input.GetVideoStream();
            var hevc = stream->codecpar->codec_id == AVCodecID.AV_CODEC_ID_HEVC;
            var mpeg2 = stream->codecpar->codec_id == AVCodecID.AV_CODEC_ID_MPEG2VIDEO;
            var lengthSize = mpeg2 ? 0 : VideoBitstream.ReadParameterSets(new ReadOnlySpan<byte>(stream->codecpar->extradata,
                stream->codecpar->extradata_size), hevc, []);
            var copying = false;
            foreach (var packet in input.ReadPackets())
            {
                if (packet.StreamIndex != stream->index)
                    continue;
                if (packet.Pts == copyStart)
                    copying = true;
                if (copying && packet.Pts == copyEnd)
                    break;
                // MPEG-2 开放 GOP 复制起点之后的前导 B 帧不复制，由头段重编码。
                if (copying && mpeg2 && packet.Pts < copyStart)
                    continue;
                if (copying)
                    expected.AddRange(mpeg2 ? Pictures(packet) : Slices(packet, lengthSize, hevc));
            }
        }
        var actual = new List<byte[]>();
        using (var input = FormatContext.OpenInputUrl(output))
        {
            input.LoadStreamInfo();
            AVStream* stream = input.GetVideoStream();
            var hevc = stream->codecpar->codec_id == AVCodecID.AV_CODEC_ID_HEVC;
            var mpeg2 = stream->codecpar->codec_id == AVCodecID.AV_CODEC_ID_MPEG2VIDEO;
            var lengthSize = mpeg2 ? 0 : VideoBitstream.ReadParameterSets(new ReadOnlySpan<byte>(stream->codecpar->extradata,
                stream->codecpar->extradata_size), hevc, []);
            foreach (var packet in input.ReadPackets())
                if (packet.StreamIndex == stream->index)
                    actual.AddRange(mpeg2 ? Pictures(packet) : Slices(packet, lengthSize, hevc));
        }
        Assert.NotEmpty(expected);
        var first = actual.FindIndex(nal => nal.AsSpan().SequenceEqual(expected[0]));
        Assert.True(first >= 0, "copied region start not found");
        Assert.True(first + expected.Count <= actual.Count);
        for (var i = 0; i < expected.Count; i++)
            Assert.True(expected[i].AsSpan().SequenceEqual(actual[first + i]), $"slice {i} differs");
    }

    /// <summary>MPEG-2：从图像起始码起的图像头与全部条带（序列头、GOP 头不比较，复制起点的 GOP 头会改为闭合）。</summary>
    private static unsafe IEnumerable<byte[]> Pictures(Packet packet)
    {
        AVPacket* raw = packet;
        var data = new ReadOnlySpan<byte>(raw->data, raw->size);
        var picture = data.IndexOf((ReadOnlySpan<byte>)[0, 0, 1, 0]);
        if (picture < 0)
            return [];
        // 重编码段与复制段之间插入的 sequence_end_code 由解析器归到前一画面的包尾，不属于图像数据。
        var end = data.EndsWith((ReadOnlySpan<byte>)[0, 0, 1, 0xB7]) ? data.Length - 4 : data.Length;
        return [data[picture..end].ToArray()];
    }

    private static unsafe IEnumerable<byte[]> Slices(Packet packet, int lengthSize, bool hevc)
    {
        AVPacket* raw = packet;
        var data = new ReadOnlySpan<byte>(raw->data, raw->size);
        var result = new List<byte[]>();
        foreach (var range in VideoBitstream.SplitNals(data, lengthSize))
            if (IsSlice(VideoBitstream.NalType(data[range], hevc), hevc))
                result.Add(data[range].ToArray());
        return result;
    }

    /// <summary>VCL NAL：HEVC 为类型 0–31，H.264 为非 IDR 与 IDR 切片。</summary>
    private static bool IsSlice(int type, bool hevc) => hevc ? type is >= 0 and <= 31 : type is 1 or 5;

    /// <summary>
    /// 各轨道 DTS 严格递增、视频 PTS 不早于 DTS。音频按包复制：首包不早于视频起点，
    /// 且晚于视频起点不超过一个 AAC 帧（1024/48000 秒）。
    /// </summary>
    /// <summary>各轨道 DTS 严格递增；alignedStart 时其余轨道与视频同时开始（相差不超过一个音频帧）。</summary>
    private static unsafe void AssertTimestamps(string output, double audioTolerance = 1024 / 48000.0, bool alignedStart = true)
    {
        using var input = FormatContext.OpenInputUrl(output);
        input.LoadStreamInfo();
        AVFormatContext* raw = input;
        var last = new Dictionary<int, long>();
        var first = new Dictionary<int, double>();
        foreach (var packet in input.ReadPackets())
        {
            AVPacket* p = packet;
            var stream = raw->streams[p->stream_index];
            if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_SUBTITLE)
                continue;
            var seconds = p->pts * (double)stream->time_base.num / stream->time_base.den;
            first[p->stream_index] = first.TryGetValue(p->stream_index, out var value) ? Math.Min(value, seconds) : seconds;
            // Matroska 不保存 DTS，读回的视频 DTS 由解复用器推测，不能据此判断；画面顺序由解码帧号核对。
            if (input.InputFormat!.Name.Contains("matroska") && stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                continue;
            // TS 的 PES 头在 DTS 等于 PTS 时只写 PTS，读回时视为 DTS = PTS。
            if (p->dts == FF.AV_NOPTS_VALUE && input.InputFormat!.Name == "mpegts")
                p->dts = p->pts;
            Assert.NotEqual(FF.AV_NOPTS_VALUE, p->dts);
            if (last.TryGetValue(p->stream_index, out var previous))
                Assert.True(p->dts > previous, $"#{p->stream_index} dts {p->dts} <= {previous}");
            last[p->stream_index] = p->dts;
            if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                Assert.True(p->pts >= p->dts);
        }
        var video = input.GetVideoStream().Index;
        if (!alignedStart)
            return;
        foreach (var (index, start) in first)
            Assert.True(start - first[video] >= -0.0005 && start - first[video] < audioTolerance + 0.0005,
                $"#{index} starts at {start}, video at {first[video]}");
    }
}
