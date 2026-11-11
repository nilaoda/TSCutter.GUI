using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Services.TransportStream.Processing;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.Utils.Media;
using TSCutter.GUI.Utils.TransportStream.Packets;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Services.Editing;

/// <summary>
/// 帧精确剪辑（界面中为“智能剪辑”）：起点到下一个可衔接关键帧、最后一个可衔接关键帧到终点的画面解码后重新编码，
/// 两者之间的原始压缩视频包直接复制。音频和字幕按数据包复制。
/// 只在干净的随机访问点衔接，复制区不参考被替换的画面，重编码段与复制段之间没有相互依赖：
/// H.264 为闭合 IDR 或经验证、改写为 IDR 的普通 I 帧；HEVC 为 IDR（含 RADL 前导画面）；MPEG-2 为 I 帧（开放 GOP 的前导 B 帧重新编码）；
/// AV1 为显示的关键帧；VP9 为关键帧（可隐藏）。
/// </summary>
internal sealed unsafe class FrameAccurateExportService
{
    // 每段重编码的显示时长上限。超过时拒绝导出，不静默扩大为整段重编码。
    internal const double MaxReencodeSeconds = 60;
    private const double ScanMarginSeconds = 3;
    // 只处理短时解码偏移；更大的异常不能当作普通重排序延迟修正。
    private const double MaximumDtsCorrectionSeconds = 1;

    private readonly List<FrameAccurateSegmentReport> reports = [];
    internal IReadOnlyList<FrameAccurateSegmentReport> Reports => reports;

    public Task ExportAsync(FrameAccurateExportRequest request, IProgress<FrameAccurateExportProgress>? progress = null,
        CancellationToken cancellationToken = default, ExportTsCheckSession? checkSession = null) =>
        Task.Run(() => Export(request, progress, cancellationToken, checkSession), cancellationToken);

    /// <summary>只读取切点窗口的视频包时间戳，不验证衔接点、不创建编码器或输出文件。</summary>
    internal Task<bool> NeedsTimestampNormalizationAsync(FrameAccurateExportRequest request, CancellationToken token)
    {
        if (request.Segments.FirstOrDefault() is not { Settings.Container: "ts" or "m2ts" } first)
            return Task.FromResult(false);
        return Task.Run(() =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                using var input = InterruptibleInputFormatContext.Open(first.SourcePath, () => token.IsCancellationRequested);
                input.LoadStreamInfo();
                AVFormatContext* raw = input;
                var index = first.Settings.VideoIndex;
                if (input.InputFormat?.Name != "mpegts" || index < 0 || index >= raw->nb_streams ||
                    raw->streams[index]->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO)
                    throw new FrameAccurateExportException(FrameAccurateExportError.InvalidRequest);
                var video = raw->streams[index];
                var context = new SpliceContext(input, video, 0, video->codecpar->codec_id == AVCodecID.AV_CODEC_ID_HEVC);
                List<VideoPacket>? scanned = null;
                var reachedEof = false;
                var targets = request.Segments.SelectMany(segment => segment.EndPts is { } end
                    ? new[] { segment.StartPts, end } : new[] { segment.StartPts }).Distinct().Order();
                foreach (var target in targets)
                {
                    token.ThrowIfCancellationRequested();
                    // 同一窗口内的切点共享已经读过的时间戳，不重复定位和扫描。
                    if (scanned is not null && scanned.Any(packet => packet.Pts == target) &&
                        (reachedEof || Confirmed(context, scanned, target)))
                        continue;
                    var (packets, _, _, eof) = ScanAround(context, target, target, token);
                    if (context.MaxDtsAfterPts > context.Ticks(MaximumDtsCorrectionSeconds))
                        throw new FrameAccurateExportException(FrameAccurateExportError.NonMonotonicTimestamp, "video DTS offset");
                    if (context.MaxDtsAfterPts > context.Ticks(0.001))
                        return true;
                    scanned = packets;
                    reachedEof = eof;
                }
                token.ThrowIfCancellationRequested();
                return false;
            }
            catch (FFmpegException) when (token.IsCancellationRequested)
            {
                throw new OperationCanceledException(token);
            }
        }, token);
    }

    /// <summary>导出前和界面启用模式时调用；只读取已探测的流参数，不扫描文件。</summary>
    internal static FrameAccurateSupport CheckSupport(FormatContext input, int videoIndex, IEnumerable<int> streamIndices,
        string container)
    {
        if (container is not ("ts" or "m2ts" or "mp4" or "mkv"))
            return FrameAccurateSupport.Container;
        AVFormatContext* raw = input;
        if (videoIndex < 0 || videoIndex >= raw->nb_streams)
            return FrameAccurateSupport.Codec;
        var stream = raw->streams[videoIndex];
        var parameters = stream->codecpar;
        if (streamIndices.Count(index => index >= 0 && index < raw->nb_streams &&
                raw->streams[index]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO &&
                (raw->streams[index]->disposition & 1024) == 0) > 1)
            return FrameAccurateSupport.MultipleVideo;
        var hevc = parameters->codec_id == AVCodecID.AV_CODEC_ID_HEVC;
        var mpeg2 = parameters->codec_id == AVCodecID.AV_CODEC_ID_MPEG2VIDEO;
        var av1 = parameters->codec_id == AVCodecID.AV_CODEC_ID_AV1;
        var vp9 = parameters->codec_id == AVCodecID.AV_CODEC_ID_VP9;
        // HEVC 仅 Main/Main 10；H.264 仅 Baseline/Main/High/High 4:2:2（不含 Intra 档次）；MPEG-2 仅 Main/Simple（4:2:0）。
        // AV1 仅 Main 档次（8/10 位 4:2:0）；VP9 仅 Profile 0（8 位 4:2:0）与 Profile 2（10 位 4:2:0）。
        // 两者都只在 MP4/MKV 中（每个样本一个画面）。
        if ((av1 || vp9) && container is not ("mp4" or "mkv"))
            return FrameAccurateSupport.Container;
        // HEVC 另支持格式范围扩展（RExt，profile 4）中的 4:2:2 8/10 位，像素格式在下面检查。
        if (hevc ? parameters->profile is not (1 or 2 or 4)
                : mpeg2 ? parameters->profile is not (4 or 5)
                : av1 ? parameters->profile != 0
                : vp9 ? parameters->profile is not (0 or 2)
                : parameters->codec_id != AVCodecID.AV_CODEC_ID_H264 || FrameAccurateVideoEncoder.H264ProfileName(parameters->profile) is null)
            return FrameAccurateSupport.Codec;
        // 杜比视界仅支持单层 Profile 5 与 8.1（HDR10 兼容基础层），不带增强层、不压缩元数据：重编码画面由 x265
        // 按每帧解码出的杜比元数据重新生成 RPU，8.1 的 HDR10 静态元数据另行写入。8.2/8.4 暂无样本验证，不放开。
        // TS/M2TS 由 TSCutter 按传输包写出，源 PMT 中的杜比描述符原样保留（多节目时随节目表重建一并保留），
        // 重编码段的 RPU 在码流中；ffmpeg 的 TS 复用器不写杜比描述符，导出时若未按传输包写出则拒绝。
        if (FrameAccurateVideoEncoder.DolbyVisionConfiguration(parameters) is { } dovi &&
            (dovi is not ({ Profile: 5, Compatibility: 0 } or { Profile: 8, Compatibility: 1 }) ||
             !dovi.Rpu || !dovi.BaseLayer || dovi.EnhancementLayer || dovi.Compression != 0))
            return FrameAccurateSupport.DolbyVision;
        // 隔行 H.264 按完整帧选择，重编码保持原场序；HEVC 隔行及场序混合的码流不支持。
        if (parameters->field_order is AVFieldOrder.AV_FIELD_TB or AVFieldOrder.AV_FIELD_BT ||
            (hevc || av1 || vp9) && parameters->field_order is AVFieldOrder.AV_FIELD_TT or AVFieldOrder.AV_FIELD_BB)
            return FrameAccurateSupport.Interlaced;
        var format = (AVPixelFormat)parameters->format;
        if (!IsSupportedPixelFormat(format, hevc || av1 || vp9, parameters->codec_id == AVCodecID.AV_CODEC_ID_H264 && parameters->profile == 122,
                hevc && parameters->profile == 4))
            return FrameAccurateSupport.PixelFormat;
        // 编码器须能按原像素格式编码（例如运行库中的 x264 是否带 10 位支持）。
        return FrameAccurateVideoEncoder.SupportsPixelFormat(parameters->codec_id, format)
            ? FrameAccurateSupport.Supported : FrameAccurateSupport.Encoder;
    }

    /// <summary>
    /// 可重编码的像素格式：均为 4:2:0 8 位；HEVC、AV1 与 VP9 另支持 4:2:0 10 位；
    /// H.264 High 4:2:2 档次另支持 4:2:2 8 位与 4:2:0/4:2:2 10 位（档次允许的全部格式）；
    /// HEVC 格式范围扩展只支持 4:2:2 8/10 位（以 Main 4:2:2 10 档次重编码），4:4:4、12 位等不支持。
    /// </summary>
    private static bool IsSupportedPixelFormat(AVPixelFormat format, bool tenBit420, bool h264High422, bool hevcRext) => format switch
    {
        AVPixelFormat.AV_PIX_FMT_YUV420P or AVPixelFormat.AV_PIX_FMT_YUVJ420P => !hevcRext,
        AVPixelFormat.AV_PIX_FMT_YUV420P10LE => !hevcRext && (tenBit420 || h264High422),
        AVPixelFormat.AV_PIX_FMT_YUV422P or AVPixelFormat.AV_PIX_FMT_YUVJ422P or AVPixelFormat.AV_PIX_FMT_YUV422P10LE =>
            h264High422 || hevcRext,
        _ => false
    };

    internal static void Validate(FrameAccurateExportRequest request)
    {
        if (request.Segments is not { Count: > 0 and <= 256 } || string.IsNullOrWhiteSpace(request.OutputPath))
            throw new FrameAccurateExportException(FrameAccurateExportError.InvalidRequest);
        var first = request.Segments[0];
        if (!Path.GetExtension(request.OutputPath).Equals("." + first.Settings.Container, StringComparison.OrdinalIgnoreCase))
            throw new FrameAccurateExportException(FrameAccurateExportError.InvalidRequest, Path.GetExtension(request.OutputPath));
        foreach (var segment in request.Segments)
        {
            // 多片段合并只支持同一源文件，轨道结构和参数集一致。
            if (!segment.Settings.IsValid || segment.Settings.Container != first.Settings.Container ||
                segment.Settings.VideoIndex != first.Settings.VideoIndex ||
                !segment.Settings.StreamIndices.SequenceEqual(first.Settings.StreamIndices) ||
                !MediaFileUtil.PathsEqual(segment.SourcePath, first.SourcePath) ||
                segment.EndPts is { } end && end < segment.StartPts)
                throw new FrameAccurateExportException(FrameAccurateExportError.InvalidRequest);
            if (MediaFileUtil.ConflictsWithSource(request.OutputPath, segment.SourcePath))
                throw new FrameAccurateExportException(FrameAccurateExportError.SameFile);
        }
    }

    private void Export(FrameAccurateExportRequest request, IProgress<FrameAccurateExportProgress>? progress,
        CancellationToken token, ExportTsCheckSession? checkSession)
    {
        Validate(request);
        token.ThrowIfCancellationRequested();
        var settings = request.Segments[0].Settings;
        var sourcePath = request.Segments[0].SourcePath;
        var source = new FileInfo(sourcePath);
        var sourceLength = source.Length;
        var sourceModified = source.LastWriteTimeUtc;
        var temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!,
            $".tscutter-{Guid.NewGuid():N}.tmp");
        AVIOInterruptCB_callback interrupt = _ => token.IsCancellationRequested ? 1 : 0;
        AVFormatContext* output = null;
        M2tsAvioWriteContext? avioWriter = null;
        FormatContext? input = null;
        CodecContext? decoder = null;
        SpliceWriter? spliceWriter = null;
        FrameAccurateTsOutput? tsOutput = null;
        // 按传输包写出时 output->pb 是丢弃文件头用的内存缓冲，必须用 avio_close_dyn_buf 释放，不能按文件关闭。
        var dynamicBuffer = false;
        var isTsOutput = settings.Container is "ts" or "m2ts";
        try
        {
            input = InterruptibleInputFormatContext.Open(sourcePath, () => token.IsCancellationRequested);
            input.LoadStreamInfo();
            AVFormatContext* rawInput = input;
            var isTsInput = input.InputFormat?.Name == "mpegts";
            if (isTsInput != isTsOutput)
                throw new FrameAccurateExportException(FrameAccurateExportError.Unsupported, settings.Container,
                    FrameAccurateSupport.Container);
            if (settings.StreamIndices.Any(index => index >= rawInput->nb_streams))
                throw new FrameAccurateExportException(FrameAccurateExportError.InvalidRequest);
            var support = CheckSupport(input, settings.VideoIndex, settings.StreamIndices, settings.Container);
            if (support != FrameAccurateSupport.Supported)
                throw new FrameAccurateExportException(FrameAccurateExportError.Unsupported, support.ToString(), support);
            var video = rawInput->streams[settings.VideoIndex];
            var hevc = video->codecpar->codec_id == AVCodecID.AV_CODEC_ID_HEVC;
            // AV1 的附加数据是 av1C、VP9 的是 vpcC，都不是 NAL 参数集。
            var lengthSize = video->codecpar->codec_id is AVCodecID.AV_CODEC_ID_AV1 or AVCodecID.AV_CODEC_ID_VP9 ? 0
                : VideoBitstream.ReadParameterSets(
                    new ReadOnlySpan<byte>(video->codecpar->extradata, video->codecpar->extradata_size), hevc, []);
            var context = new SpliceContext(input, video, lengthSize, hevc);
            decoder = new CodecContext(Codec.FindDecoderById(video->codecpar->codec_id));
            decoder.FillParameters(new CodecParameters(video->codecpar));
            decoder.PacketTimeBase = video->time_base;
            decoder.ThreadCount = 0;
            FFmpegException.Check(FF.avcodec_open2(decoder, FF.avcodec_find_decoder(video->codecpar->codec_id), null));
            // 规划时用同一解码器验证普通 I 帧能否作为衔接点。
            context.Decoder = decoder;
            var plans = new List<SegmentPlan>();
            foreach (var segment in request.Segments)
            {
                token.ThrowIfCancellationRequested();
                var end = segment.EndExclusive && segment.EndPts is { } exclusive
                    ? PreviousFramePts(context, exclusive, token) : segment.EndPts;
                plans.Add(Plan(context, segment.StartPts, end, token));
            }
            if (isTsInput && context.MaxDtsAfterPts > context.Ticks(0.001))
            {
                if (context.MaxDtsAfterPts > context.Ticks(MaximumDtsCorrectionSeconds))
                    throw new FrameAccurateExportException(FrameAccurateExportError.NonMonotonicTimestamp, "video DTS offset");
                if (!request.NormalizeTimestamps)
                    throw new FrameAccurateExportException(FrameAccurateExportError.TimestampNormalizationRequired);
                // 先取得所有切点窗口的偏移，再统一重新规划；写出期间不动态改变时间基准。
                do
                {
                    context.DtsCorrection = context.MaxDtsAfterPts;
                    plans.Clear();
                    foreach (var segment in request.Segments)
                    {
                        token.ThrowIfCancellationRequested();
                        var end = segment.EndExclusive && segment.EndPts is { } exclusive
                            ? PreviousFramePts(context, exclusive, token) : segment.EndPts;
                        plans.Add(Plan(context, segment.StartPts, end, token));
                    }
                    if (context.MaxDtsAfterPts > context.Ticks(MaximumDtsCorrectionSeconds))
                        throw new FrameAccurateExportException(FrameAccurateExportError.NonMonotonicTimestamp, "video DTS offset");
                } while (context.MaxDtsAfterPts > context.DtsCorrection);
            }
            if (context.Nal &&
                !context.OriginalParameterSets.Any(set => VideoBitstream.IsSps(VideoBitstream.NalType(set, hevc), hevc)))
                throw new FrameAccurateExportException(FrameAccurateExportError.IncompatibleStream, "parameter sets");
            // H.264 的重编码参数集使用原码流未用的编号；HEVC 由每个 IDR 前的带内参数集重新定义。
            context.ParameterSetId = hevc ? 0 : Enumerable.Range(1, 31).First(id =>
                !VideoBitstream.OfType(context.OriginalParameterSets, false, 7).Any(sps => VideoBitstream.ReadSpsId(sps) == id) &&
                !VideoBitstream.OfType(context.OriginalParameterSets, false, 8).Any(pps => VideoBitstream.ReadPpsIds(pps).PpsId == id));
            context.HighTier = hevc && VideoBitstream.OfType(context.OriginalParameterSets, true, 33)
                .Any(sps => VideoBitstream.IsHevcHighTier(sps));
            // 流参数的场序可能与画面实际场序不同，以首个解码画面为准。
            using var firstFrame = new Frame();
            context.FieldOrder = ProbeFieldOrder(context, decoder, plans[0].DecodeStartPts, token, firstFrame);
            if (hevc && context.FieldOrder != AVFieldOrder.AV_FIELD_PROGRESSIVE)
                throw new FrameAccurateExportException(FrameAccurateExportError.Unsupported, "", FrameAccurateSupport.Interlaced);
            // 编码配置固定，先打开一次确认可用并取得参数集，写入容器头；后续每段会话核对参数集一致。
            // 首帧一并交给编码器：TS 中的 HDR10 静态元数据与杜比元数据只在画面里（MP4/MKV 在流参数中），
            // 杜比视界 8.1 缺少 HDR10 元数据时 x265 拒绝打开。
            using (var probe = FrameAccurateVideoEncoder.Open(video, context.ParameterSetId, hevc, context.HighTier, context.FieldOrder,
                       context.SourceBitrate, firstFrame))
                context.EncodedParameterSets.AddRange(probe.ParameterSets);
            if (video->codecpar->codec_id == AVCodecID.AV_CODEC_ID_H264 &&
                VideoBitstream.ReadPpsIds(context.EncodedParameterSets[1]).PpsId != context.ParameterSetId)
                throw new FrameAccurateExportException(FrameAccurateExportError.IncompatibleStream, "pps id");

            FFmpegException.Check(FF.avformat_alloc_output_context2(&output, null, settings.Container switch
            {
                "mp4" => "mp4", "mkv" => "matroska", _ => "mpegts"
            }, temporary));
            if (output == null)
                throw new OutOfMemoryException();
            output->interrupt_callback = new AVIOInterruptCB { callback = interrupt };
            output->avoid_negative_ts = isTsOutput ? 1 : 0;
            var map = CreateStreams(output, input, settings, context);
            var packetLayout = isTsOutput ? TsPacketLayout.Detect(sourcePath, token) : default;
            // 修正 DTS 时由复用器重建 PCR 与 PES（包括补写缺失的 DTS），不再复用原传输包。
            var native = isTsOutput && context.DtsCorrection == 0 && FrameAccurateTsOutput.CanUse(packetLayout, settings.Container);
            if (isTsOutput && !native && FrameAccurateVideoEncoder.DolbyVisionConfiguration(video->codecpar) != null)
                throw new FrameAccurateExportException(FrameAccurateExportError.Unsupported, settings.Container,
                    FrameAccurateSupport.DolbyVisionContainer);
            if (native)
            {
                // TSCutter 按源传输包写出：ffmpeg 复用器只用来建立输出流（时间基等），头部写入丢弃的内存缓冲。
                FFmpegException.Check(FF.avio_open_dyn_buf(&output->pb));
                dynamicBuffer = true;
                var program = FindProgram(rawInput, settings.VideoIndex);
                var tables = new List<int> { 0, 0x11 };
                if (program != null)
                    tables.Add(program->pmt_pid);
                // 多节目：只输出预览视频所在节目，节目表按该节目重建（与关键帧剪辑的按节目导出相同）。
                TsServiceFilterService.OutputContext? filter = null;
                if (rawInput->nb_programs > 1 && program != null)
                    filter = TsProgramClipExportService.OpenWriterAsync(sourcePath, temporary, program->id, token, checkSession)
                        .GetAwaiter().GetResult();
                try
                {
                    tsOutput = new FrameAccurateTsOutput(sourcePath, temporary, packetLayout, video->id,
                        MediaPids(rawInput, settings.StreamIndices), tables, hevc,
                        video->codecpar->codec_id == AVCodecID.AV_CODEC_ID_H264, checkSession, filter);
                }
                catch
                {
                    if (filter is not null)
                        filter.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    throw;
                }
            }
            else if (isTsOutput && checkSession is not null)
            {
                avioWriter = M2tsAvioWriteContext.Open(temporary, checkSession);
                output->pb = avioWriter.Io;
                output->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
            }
            else
            {
                using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                FFmpegException.Check(FF.avio_open2(&output->pb, temporary, 2, &output->interrupt_callback, null));
            }
            AVDictionary* options = null;
            try
            {
                if (settings.Container == "mp4")
                {
                    FFmpegException.Check(FF.av_dict_set(&options, "use_editlist", "1", 0));
                }
                // MOV 仅在 FF_COMPLIANCE_UNOFFICIAL 模式下写入 dvcC/dvvC。
                if (settings.Container == "mp4" && FrameAccurateVideoEncoder.DolbyVisionConfiguration(video->codecpar) != null)
                    output->strict_std_compliance = -1;
                if (isTsOutput)
                {
                    FFmpegException.Check(FF.av_dict_set(&options, "mpegts_copyts", "1", 0));
                    if (settings.Container == "m2ts")
                        FFmpegException.Check(FF.av_dict_set(&options, "mpegts_m2ts_mode", "1", 0));
                    var program = FindProgram(rawInput, settings.VideoIndex);
                    if (program != null)
                    {
                        FFmpegException.Check(FF.av_dict_set(&options, "mpegts_service_id", program->id.ToString(), 0));
                        if (program->pmt_pid is > 0x0F and < 0x1FFF)
                            FFmpegException.Check(FF.av_dict_set(&options, "mpegts_pmt_start_pid", program->pmt_pid.ToString(), 0));
                        FFmpegException.Check(FF.av_dict_copy(&output->metadata, program->metadata, 0));
                    }
                }
                FFmpegException.Check(FF.avformat_write_header(output, &options));
            }
            finally { FF.av_dict_free(&options); }
            if (dynamicBuffer)
            {
                DiscardDynamicBuffer(output);
                dynamicBuffer = false;
            }
            WriteAttachedPictures(output, input, settings, map);

            var writer = new SpliceWriter(output, map, isTsOutput, context)
            {
                SampleEntries = hevc && settings.Container == "mp4",
                Ts = tsOutput
            };
            spliceWriter = writer;
            var streamEnd = video->duration > 0 && video->start_time != FF.AV_NOPTS_VALUE
                ? video->start_time + video->duration
                : plans.Max(plan => plan.EndPts ?? plan.StartPts);
            var total = plans.Sum(plan => plan.DurationSeconds(video->time_base, streamEnd));
            var parts = PlanParts(plans, video->time_base, streamEnd);
            var timer = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            double done = 0;
            var segmentIndex = 0;
            var lastPhase = FrameAccuratePhase.Preparing;
            int encodedBefore = 0, copiedBefore = 0;
            void Report(double percent, FrameAccuratePhase phase, int encoded, long copied)
            {
                var elapsed = Math.Max(timer.Elapsed.TotalSeconds, 0.001);
                progress?.Report(new FrameAccurateExportProgress(percent, writer.Bytes / elapsed,
                    done > 0 && percent < 100 ? elapsed * (total - done) / done : 0, parts, total, segmentIndex, plans.Count,
                    phase, Math.Min(done, total), encoded, copied));
            }
            Report(0, FrameAccuratePhase.Preparing, 0, 0);
            foreach (var plan in plans)
            {
                token.ThrowIfCancellationRequested();
                var before = done;
                var duration = plan.DurationSeconds(video->time_base, streamEnd);
                var report = ExportSegment(context, decoder, writer, plan, settings, token, (seconds, phase, encoded, copied) =>
                {
                    // 视频包按解码顺序读取，带 B 帧时 PTS 会来回跳动；位置只前进不后退。
                    done = Math.Max(done, before + Math.Min(seconds, duration));
                    // 片段视频写完后只剩其余轨道的收尾，沿用上一阶段的说明；整份导出的“完成”阶段在全部片段之后报告。
                    if (phase == FrameAccuratePhase.Finishing)
                        phase = lastPhase;
                    // 阶段切换时立即报告，短暂的阶段（如很短的片头）也能显示出来。
                    var changed = phase != lastPhase;
                    if (!changed && timer.Elapsed - lastReport < TimeSpan.FromMilliseconds(100))
                        return;
                    lastReport = timer.Elapsed;
                    lastPhase = phase;
                    Report(Math.Min(99.9, done / Math.Max(total, 0.001) * 100), phase, encodedBefore + encoded, copiedBefore + copied);
                });
                reports.Add(report);
                encodedBefore += report.EncodedFrames;
                copiedBefore += report.CopiedPackets;
                done = before + duration;
                segmentIndex = Math.Min(segmentIndex + 1, plans.Count - 1);
            }
            Report(99.9, FrameAccuratePhase.Finishing, encodedBefore, copiedBefore);
            token.ThrowIfCancellationRequested();
            if (tsOutput is not null)
            {
                tsOutput.Complete();
                tsOutput.Dispose();
            }
            else if (FF.av_write_trailer(output) is var trailer && trailer < 0)
                FFmpegException.Check(trailer);
            else if (avioWriter is not null)
            {
                avioWriter.Complete();
                output->pb = null;
            }
            else
            {
                FFmpegException.Check(FF.avio_closep(&output->pb));
            }
            // TS 源可能仍在录制追加；只要已读取部分仍在，就不视为来源变化。
            source.Refresh();
            if (!source.Exists || (isTsInput ? source.Length < sourceLength
                    : source.Length != sourceLength || source.LastWriteTimeUtc != sourceModified))
                throw new FrameAccurateExportException(FrameAccurateExportError.SourceChanged, sourcePath);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, request.OutputPath, overwrite: true);
            done = total;
            Report(100, FrameAccuratePhase.Finishing, encodedBefore, copiedBefore);
        }
        catch (FFmpegException) when (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
        finally
        {
            if (output != null)
            {
                if (output->pb != null)
                {
                    if (dynamicBuffer)
                        DiscardDynamicBuffer(output);
                    else if (avioWriter is not null)
                    {
                        output->pb = null;
                        avioWriter.Dispose();
                    }
                    else
                    {
                        FF.avio_closep(&output->pb);
                    }
                }
                FF.avformat_free_context(output);
            }
            spliceWriter?.Dispose();
            tsOutput?.Dispose();
            decoder?.Dispose();
            input?.Dispose();
            if (File.Exists(temporary))
                File.Delete(temporary);
            GC.KeepAlive(interrupt);
        }
    }

    /// <summary>所选音视频与字幕流的 PID（mpegts 解复用器中 AVStream.id 即 PID，不含封面）。</summary>
    private static List<int> MediaPids(AVFormatContext* input, IEnumerable<int> indices)
    {
        var pids = new List<int>();
        foreach (var index in indices)
        {
            var stream = input->streams[index];
            if (stream->codecpar->codec_type is AVMediaType.AVMEDIA_TYPE_VIDEO or AVMediaType.AVMEDIA_TYPE_AUDIO or
                    AVMediaType.AVMEDIA_TYPE_SUBTITLE && (stream->disposition & 1024) == 0)
                pids.Add(stream->id);
        }
        return pids;
    }

    /// <summary>丢弃写入内存缓冲的复用器头部（按传输包写出时不使用 ffmpeg 的输出）。</summary>
    private static void DiscardDynamicBuffer(AVFormatContext* output)
    {
        byte* buffer = null;
        FF.avio_close_dyn_buf(output->pb, &buffer);
        FF.av_free(buffer);
        output->pb = null;
    }

    /// <summary>从首个解码起点解出第一帧（留在 frame 中），读取实际的隔行与场序标志。</summary>
    private static AVFieldOrder ProbeFieldOrder(SpliceContext context, CodecContext decoder, long decodeStart, CancellationToken token,
        Frame frame)
    {
        var input = context.Input;
        AVFormatContext* raw = input;
        var video = context.Video;
        var discards = new AVDiscard[raw->nb_streams];
        for (var i = 0; i < raw->nb_streams; i++)
        {
            discards[i] = raw->streams[i]->discard;
            if (i != video->index)
                raw->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
        }
        try
        {
            // TS 定位不精确，与导出时相同地向前预留余量，再按 PTS 找到解码起点。
            input.SeekFrame(decodeStart - (input.InputFormat?.Name == "mpegts" ? context.Ticks(1) : 0), video->index);
            var started = false;
            foreach (var packet in input.ReadPackets())
            {
                token.ThrowIfCancellationRequested();
                if (packet.StreamIndex != video->index || !started && packet.Pts != decodeStart)
                    continue;
                started = true;
                var result = FF.avcodec_send_packet(decoder, packet);
                if (result < 0 && result != FF.AVERROR_EAGAIN)
                    FFmpegException.Check(result);
                if (FF.avcodec_receive_frame(decoder, frame) >= 0)
                    return FieldOrder(frame);
            }
            FFmpegException.Check(FF.avcodec_send_packet(decoder, null));
            if (FF.avcodec_receive_frame(decoder, frame) >= 0)
                return FieldOrder(frame);
            throw new FrameAccurateExportException(FrameAccurateExportError.FrameNotFound, decodeStart.ToString());
        }
        finally
        {
            FF.avcodec_flush_buffers(decoder);
            for (var i = 0; i < discards.Length; i++)
                raw->streams[i]->discard = discards[i];
        }
    }

    internal static AVFieldOrder FieldOrder(AVFrame* frame) => (frame->flags & AV_FRAME_FLAG_INTERLACED) == 0
        ? AVFieldOrder.AV_FIELD_PROGRESSIVE
        : (frame->flags & AV_FRAME_FLAG_TOP_FIELD_FIRST) != 0 ? AVFieldOrder.AV_FIELD_TT : AVFieldOrder.AV_FIELD_BB;

    private const int AV_FRAME_FLAG_INTERLACED = 1 << 3;
    private const int AV_FRAME_FLAG_TOP_FIELD_FIRST = 1 << 4;
    private const ulong PixelFormatPlanar = 1 << 4;

    private static AVProgram* FindProgram(AVFormatContext* input, int streamIndex)
    {
        for (var i = 0; i < input->nb_programs; i++)
        {
            var program = input->programs[i];
            for (var j = 0; j < program->nb_stream_indexes; j++)
                if (program->stream_index[j] == streamIndex)
                    return program;
        }
        return null;
    }

    // ---------------- 规划 ----------------

    private sealed class SpliceContext(FormatContext input, AVStream* video, int lengthSize, bool hevc)
    {
        public FormatContext Input { get; } = input;
        public AVStream* Video { get; } = video;
        public int LengthSize { get; } = lengthSize;
        public bool Hevc { get; } = hevc;
        public bool Mpeg2 { get; } = video->codecpar->codec_id == AVCodecID.AV_CODEC_ID_MPEG2VIDEO;
        public bool Av1 { get; } = video->codecpar->codec_id == AVCodecID.AV_CODEC_ID_AV1;
        public bool Vp9 { get; } = video->codecpar->codec_id == AVCodecID.AV_CODEC_ID_VP9;
        /// <summary>H.264/HEVC：码流按 NAL 组织，有参数集与切片头处理；MPEG-2、AV1 与 VP9 不是。</summary>
        public bool Nal => !Mpeg2 && !Av1 && !Vp9;
        private byte[]? av1SequenceHeader;
        /// <summary>AV1 的原始序列头（av1C 中），复制起点的关键帧缺少时补上；首次使用时读取一次。</summary>
        public byte[] Av1SequenceHeader => av1SequenceHeader ??=
            Av1Bitstream.SequenceHeaderFrom(new ReadOnlySpan<byte>(Video->codecpar->extradata, Video->codecpar->extradata_size));
        private byte[]? mpeg2Header;
        /// <summary>MPEG-2 的原始序列头（附加数据），复制起点缺少时补在 I 帧前；首次使用时读取一次。</summary>
        public byte[] Mpeg2Header => mpeg2Header ??=
            new ReadOnlySpan<byte>(Video->codecpar->extradata, Video->codecpar->extradata_size).ToArray();

        /// <summary>
        /// 随机访问点：H.264/HEVC 为 IDR；MPEG-2 为 I 帧，GOP 头未声明 closed_gop 时为开放 GOP，
        /// 其前导 B 帧参考上一 GOP，不能随 I 帧复制或从该 I 帧开始解码。
        /// H.264 的普通 I 帧（广播常见，可能没有任何 IDR）按开放 GOP 处理，但须先验证（Candidate）：
        /// 规划选用前确认其后画面不参考 I 帧之前的画面，复制时改写为 IDR。
        /// </summary>
        public (bool RandomAccess, bool Open, bool Candidate) Classify(ReadOnlySpan<byte> data, bool key)
        {
            if (!key)
                return (false, false, false);
            // AV1 显示的关键帧重置全部参考帧，时间单元中没有前导画面，相当于闭合 GOP 的 IDR。
            if (Av1)
                return (Av1Bitstream.IsKeyFrame(data), false, false);
            // VP9 关键帧（含超帧中隐藏的关键帧）刷新全部参考帧槽；样本内的隐藏帧在显示帧之前解码，不存在前导画面。
            if (Vp9)
                return (Vp9Bitstream.IsKeyFrame(data), false, false);
            if (Mpeg2)
            {
                var picture = VideoBitstream.ReadMpeg2(data);
                return (picture.PictureType == 1, picture.ClosedGop != true, false);
            }
            if (IsIdr(data))
                return (true, false, false);
            // HEVC 的 CRA/BLA（广播素材常见，全片可能没有 IDR）：此后的普通画面按规范不参考它之前的画面，
            // 只有 RASL 前导画面参考上一 GOP。与 MPEG-2 开放 GOP 相同处理：前导画面丢弃并随片头重新编码，复制时改为 BLA。
            if (Hevc)
                return VideoBitstream.ContainsOpenIrap(data, LengthSize) ? (true, true, false) : (false, false, false);
            return H264Splice.IsIntraPicture(data, LengthSize) ? (true, true, true) : (false, false, false);
        }

        /// <summary>
        /// 切点附近原视频码率（bit/s）：各次扫描窗口内视频包字节数除以时长，取最大值。
        /// 全部重编码会话使用同一上限，保证编码器参数集一致。
        /// </summary>
        public long SourceBitrate { get; set; }
        public long MaxDtsAfterPts { get; set; }
        public long DtsCorrection { get; set; }

        /// <summary>扫描时识别场画面用的参数集（随带内参数集更新）与解析暂存。</summary>
        public H264Splice.ParameterSets H264Sets { get; } = new();
        public List<H264Splice.Mmco> Scratch { get; } = [];

        /// <summary>规划时验证 I 帧衔接点使用的解码器（与导出共用，验证后清空状态）。</summary>
        public CodecContext? Decoder { get; set; }
        /// <summary>已验证的普通 I 帧衔接点（按 PTS），结果在各片段规划间共用。</summary>
        public Dictionary<long, bool> VerifiedSplices { get; } = [];

        /// <summary>按原始参数集新建 H.264 参数集表；复制区改写和验证时按带内参数集继续更新。</summary>
        public H264Splice.ParameterSets CreateH264Sets()
        {
            var sets = new H264Splice.ParameterSets();
            sets.AddAll(OriginalParameterSets);
            return sets;
        }
        public List<byte[]> OriginalParameterSets { get; } = [];
        public List<byte[]> EncodedParameterSets { get; } = [];
        public int ParameterSetId { get; set; }
        public AVFieldOrder FieldOrder { get; set; }
        public bool HighTier { get; set; }
        public bool IsIdr(ReadOnlySpan<byte> data) =>
            Av1 ? Av1Bitstream.IsKeyFrame(data) : Vp9 ? Vp9Bitstream.IsKeyFrame(data)
                : VideoBitstream.ContainsIdr(data, LengthSize, Hevc);
        public long Ticks(double seconds) => (long)Math.Ceiling(seconds * Video->time_base.den / Video->time_base.num);
    }

    /// <summary>
    /// 视频包元数据。H.264 场编码（PAFF）的两个场是两个包，第二场（SecondField）与第一场属于同一画面，
    /// 画面的显示时间取第一场；规划中的画面边界、终点与时长都按画面计算。
    /// </summary>
    private readonly record struct VideoPacket(long Pts, long Dts, long Duration, bool RandomAccess, bool Key, bool Open,
        bool Candidate = false, bool SecondField = false, int Size = 0);

    /// <summary>衔接点：GroupStart 为该 GOP（含前导画面）最早的显示时间；开放 GOP 的复制只能从 I 帧本身的显示时间开始。</summary>
    private readonly record struct Splice(long GroupStart, bool Open, long Pts)
    {
        public long CopyDisplayStart => Open ? Pts : GroupStart;
    }

    /// <summary>
    /// 片段规划，时间戳均为源视频时间基。头段重编码 [StartPts, CopyDisplayStart)，复制 [CopyStartPts, CopyEndPts) 的包
    /// （解码顺序），尾段从 TailStartPts 解码并重编码 [TailDisplayStart, EndPts]。无复制区时整个范围从 DecodeStartPts 解码重编码。
    /// </summary>
    private sealed record SegmentPlan(long StartPts, long? EndPts, long EndExclusive, long DecodeStartPts,
        long? CopyStartPts, long CopyDisplayStart, long? CopyEndPts, bool CopyToEnd, long? TailStartPts,
        long TailDisplayStart, long HeadDelta, long TailDelta, long FirstDts, long CopyStartDts = 0)
    {
        /// <summary>开放 GOP 的复制起点：其前导画面不复制，由头段重新编码。</summary>
        public bool DropLeading { get; init; }
        /// <summary>开放 GOP 的尾段：从该包起在复制的同时送入解码器，供前导画面参考。</summary>
        public long? TailWarmupPts { get; init; }
        /// <summary>复制起点为场对时其第二场的 PTS：与 I 场一起暂存，头段解码前导画面时同样需要。</summary>
        public long? CopyStartPairPts { get; init; }
        public bool HasCopy => CopyStartPts is not null;
        public bool HasHead => !HasCopy || CopyDisplayStart > StartPts;
        // 保留到文件末尾时按视频流终点估算，仅用于进度显示。
        public double DurationSeconds(AVRational timeBase, long streamEnd) =>
            Math.Max(0, (EndExclusive == long.MaxValue ? streamEnd : EndExclusive) - StartPts) * (double)timeBase.num / timeBase.den;
    }

    private SegmentPlan Plan(SpliceContext context, long start, long? end, CancellationToken token)
    {
        var limit = context.Ticks(MaxReencodeSeconds);
        // 起点：从起点所在 GOP 的 IDR 开始解码；起点之后首个可衔接 IDR 作为复制起点。
        List<VideoPacket> head;
        int headStartIndex, startIndex, k0, k1;
        bool reachedEof;
        long copyDisplayStart;
        bool dropLeading;
        // 普通 I 帧在选用时才验证；有候选验证失败时重新扫描，已知不合格的候选不再作为扫描终点，范围随之扩大。
        for (var round = 0; ; round++)
        {
            var rejected = RejectedCount(context);
            (head, headStartIndex, startIndex, reachedEof) = ScanAround(context, start, start, token);
            k0 = -1;
            // 开放 GOP 的 I 帧之前显示的前导画面不能从该 I 帧开始解码，解码起点须不晚于起点显示。
            for (var i = startIndex; i >= headStartIndex; i--)
                if (head[i].RandomAccess && (!head[i].Open || head[i].Pts <= start) && Usable(context, head[i], token))
                {
                    k0 = i;
                    break;
                }
            k1 = -1;
            copyDisplayStart = 0;
            dropLeading = false;
            if (k0 >= 0)
                for (var i = k0; i < head.Count; i++)
                    if (SpliceAt(head, i, reachedEof) is { } splice && splice.CopyDisplayStart >= start && Usable(context, head[i], token))
                    {
                        (k1, copyDisplayStart, dropLeading) = (i, splice.CopyDisplayStart, splice.Open);
                        break;
                    }
            if (k0 >= 0 && (k1 >= 0 || reachedEof) || RejectedCount(context) == rejected || round >= 8)
                break;
        }
        // 起点前过远才有 IDR（例如只在文件开头有 IDR 的开放 GOP 码流）时拒绝，避免长距离解码。
        // 距离按实际画面计算：信号中断造成的时间戳跳变不算在内。
        if (k0 < 0 || Content(context, head, head[k0].Pts, start) > limit)
            throw new FrameAccurateExportException(FrameAccurateExportError.NoRandomAccessPoint);
        long? pairPts = k1 >= 0 && k1 + 1 < head.Count && head[k1 + 1].SecondField ? head[k1 + 1].Pts : null;
        var endExclusive = end is { } last ? NextDisplay(head, last) : long.MaxValue;
        if (k1 < 0 || end is { } bound && copyDisplayStart > bound)
        {
            // 起止之间没有可衔接的 IDR：从 k0 开始解码，整个选择范围重新编码。
            if (end is null && k1 < 0 && !reachedEof || Content(context, head, start, (end ?? head.Max(packet => packet.Pts)) + 1) > limit)
                throw new FrameAccurateExportException(FrameAccurateExportError.ReencodeTooLong);
            // 范围内有关键帧却不能衔接（开放 GOP 或非 IDR 关键帧）：不把跨 GOP 的范围整体重编码。
            if (head.Skip(k0 + 1).Any(packet => packet.Key && packet.Pts > start && packet.Pts <= (end ?? long.MaxValue)))
                throw new FrameAccurateExportException(FrameAccurateExportError.NoRandomAccessPoint, "open GOP");
            if (endExclusive == long.MinValue)
                endExclusive = EstimateEnd(context, head, end!.Value);
            return new SegmentPlan(start, end, endExclusive, head[k0].Pts, null, 0, null, false, null, 0, 0, 0, start);
        }
        if (Content(context, head, start, copyDisplayStart) > limit)
            throw new FrameAccurateExportException(FrameAccurateExportError.ReencodeTooLong);
        var copyStart = head[k1].Pts;
        // 头段不用 B 帧，DTS = PTS − Δ；Δ 取复制区首个显示时间与复制起点 IDR 解码时间之差，
        // 使头段最后一帧的 DTS 早于复制区首包。
        var headDelta = copyDisplayStart - head[k1].Dts;
        var decodeStart = copyDisplayStart == start ? copyStart : head[k0].Pts;
        var firstDts = copyDisplayStart == start ? head[k1].Dts : start - headDelta;
        if (end is not { } endPts)
            return new SegmentPlan(start, null, long.MaxValue, decodeStart, copyStart, copyDisplayStart, null, true, null,
                0, headDelta, 0, firstDts, head[k1].Dts) { DropLeading = dropLeading, CopyStartPairPts = pairPts };

        // 终点：找到显示起点不晚于终点的最后一个可衔接 IDR；该 GOP 中保留的包若恰为解码顺序前缀，直接复制。
        var tailSeek = Math.Max(copyDisplayStart, endPts);
        List<VideoPacket> tail;
        int tailStartIndex, endIndex, k2;
        bool tailEof;
        long tailDisplayStart = 0;
        long? warmup = null;
        while (true)
        {
            var rejected = RejectedCount(context);
            (tail, tailStartIndex, endIndex, tailEof) = ScanAround(context, tailSeek, endPts, token);
            k2 = -1;
            for (var i = endIndex; i >= tailStartIndex; i--)
                if (tail[i].Pts >= copyStart && SpliceAt(tail, i, tailEof) is { } splice && splice.GroupStart <= endPts &&
                    Usable(context, tail[i], token))
                {
                    (k2, tailDisplayStart) = (i, splice.GroupStart);
                    break;
                }
            if (k2 < 0 && RejectedCount(context) > rejected && tailSeek > copyStart)
            {
                tailSeek = Math.Max(copyStart, tail[tailStartIndex].Pts - context.Ticks(ScanMarginSeconds));
                continue;
            }
            if (k2 < 0)
                throw new FrameAccurateExportException(FrameAccurateExportError.NoRandomAccessPoint, "end");
            if (!tail[k2].Open || tail[k2].Pts == copyStart)
                break;
            // 开放 GOP 的尾段前导画面参考上一 GOP：找到复制区内的上一个随机访问点作为解码预热起点。
            var previous = k2 - 1;
            while (previous >= 0 && !(tail[previous].RandomAccess && Usable(context, tail[previous], token)))
                previous--;
            if (previous >= 0)
            {
                warmup = tail[previous].Pts;
                break;
            }
            if (tailSeek <= copyStart)
                throw new FrameAccurateExportException(FrameAccurateExportError.NoRandomAccessPoint, "end");
            tailSeek = Math.Max(copyStart, tail[tailStartIndex].Pts - context.Ticks(ScanMarginSeconds));
        }
        if (endExclusive == long.MinValue)
            endExclusive = NextDisplay(tail, endPts);
        if (endExclusive == long.MinValue)
            endExclusive = EstimateEnd(context, tail, endPts);
        var next = k2 + 1;
        while (next < tail.Count && !tail[next].RandomAccess)
            next++;
        var kept = 0;
        // 第二场随所属画面一起保留。
        while (k2 + kept < next && (tail[k2 + kept].SecondField || tail[k2 + kept].Pts <= endPts))
            kept++;
        long? copyEnd = k2 + kept < tail.Count ? tail[k2 + kept].Pts : null;
        var prefix = copyEnd is not null || tailEof;
        for (var i = k2 + kept; i < tail.Count; i++)
            if (tail[i].Pts <= endPts)
                prefix = false;
        if (prefix)
            return new SegmentPlan(start, end, endExclusive, decodeStart, copyStart, copyDisplayStart, copyEnd,
                copyEnd is null, null, 0, headDelta, 0, firstDts, head[k1].Dts) { DropLeading = dropLeading, CopyStartPairPts = pairPts };
        if (tail[k2].Pts == copyStart)
        {
            // 唯一可衔接的 IDR 所在 GOP 也需重编码时，没有可复制的完整区间，整个范围重新编码。
            if (Content(context, head, start, copyStart) + Content(context, tail, copyStart, endPts + 1) > limit)
                throw new FrameAccurateExportException(FrameAccurateExportError.ReencodeTooLong);
            return new SegmentPlan(start, end, endExclusive, head[k0].Pts, null, 0, null, false, null, 0, 0, 0, start);
        }
        if (Content(context, tail, tailDisplayStart, endPts + 1) > limit)
            throw new FrameAccurateExportException(FrameAccurateExportError.ReencodeTooLong);
        // 尾段首帧使用尾段 IDR 的原始 DTS，晚于复制区最后一个包。
        return new SegmentPlan(start, end, endExclusive, decodeStart, copyStart, copyDisplayStart, tail[k2].Pts, false,
            tail[k2].Pts, tailDisplayStart, headDelta, tailDisplayStart - tail[k2].Dts, firstDts, head[k1].Dts)
        {
            DropLeading = dropLeading, CopyStartPairPts = pairPts, TailWarmupPts = warmup
        };
    }

    /// <summary>按扫描窗口（至少半秒）统计视频码率，记录各窗口中的最大值。</summary>
    private static void RecordBitrate(SpliceContext context, List<VideoPacket> packets)
    {
        if (packets.Count < 2)
            return;
        long first = long.MaxValue, last = long.MinValue, bytes = 0;
        foreach (var packet in packets)
        {
            first = Math.Min(first, packet.Pts);
            last = Math.Max(last, packet.Pts);
            bytes += packet.Size;
        }
        var seconds = (last - first) * (double)context.Video->time_base.num / context.Video->time_base.den;
        if (seconds >= 0.5)
            context.SourceBitrate = Math.Max(context.SourceBitrate, (long)(bytes * 8 / seconds));
    }

    /// <summary>已验证不能作为衔接点的普通 I 帧。</summary>
    private static bool Rejected(SpliceContext context, VideoPacket packet) =>
        packet.Candidate && context.VerifiedSplices.TryGetValue(packet.Pts, out var usable) && !usable;

    private static int RejectedCount(SpliceContext context) => context.VerifiedSplices.Count(pair => !pair.Value);

    /// <summary>衔接点可用：IDR 与 MPEG-2 的 I 帧直接可用；H.264 普通 I 帧须通过验证（结果缓存）。</summary>
    private static bool Usable(SpliceContext context, VideoPacket packet, CancellationToken token)
    {
        if (!packet.Candidate)
            return true;
        if (!context.VerifiedSplices.TryGetValue(packet.Pts, out var usable))
            context.VerifiedSplices[packet.Pts] = usable = VerifyIntraSplice(context, packet.Pts, token);
        return usable;
    }

    /// <summary>
    /// 验证 H.264 普通 I 帧能否改写为 IDR 作为衔接点：
    /// I 帧（或 I 场对的第一场）为全 I 切片且作参考；该 GOP 内没有长期参考帧操作。
    /// 前导画面（解码在 I 后、显示在前）会被丢弃并重新编码；作参考的前导画面（B 帧金字塔）须紧跟 I 帧，
    /// 其后画面的编号与参考列表随之改写（见 <see cref="H264Splice.CopyRewriter.SkipLeading"/>）。
    /// 再从上一个关键帧连续解码原码流、与按复制时的方式改写后（I 帧改为 IDR、丢弃前导画面）单独解码，
    /// 逐帧比较 I 帧之后的画面，完全一致才说明它们不参考 I 帧之前的画面与被丢弃的前导画面。
    /// </summary>
    private static bool VerifyIntraSplice(SpliceContext context, long pts, CancellationToken token)
    {
        var input = context.Input;
        AVFormatContext* raw = input;
        var video = context.Video;
        var decoder = context.Decoder ?? throw new InvalidOperationException("Verification decoder is not ready.");
        var discards = new AVDiscard[raw->nb_streams];
        for (var i = 0; i < raw->nb_streams; i++)
        {
            discards[i] = raw->streams[i]->discard;
            if (i != video->index)
                raw->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
        }
        var packets = new List<Packet>();
        try
        {
            var startTime = video->start_time == FF.AV_NOPTS_VALUE ? 0 : video->start_time;
            var limit = context.Ticks(MaxReencodeSeconds + ScanMarginSeconds);
            int splice = -1, end = -1;
            var endPts = long.MaxValue;
            var frame = FrameDuration(context);
            // 范围按读到的画面时长（包时长之和）计算，不按时间戳之差：信号中断时时间戳会跳过一段。
            for (var margin = context.Ticks(ScanMarginSeconds); ; margin *= 2)
            {
                DisposePackets(packets);
                var fromStart = pts - margin <= startTime;
                input.SeekFrame(fromStart ? startTime - context.Ticks(ScanMarginSeconds) : pts - margin, video->index);
                splice = end = -1;
                var keyBefore = false;
                long before = 0, after = 0;
                foreach (var packet in input.ReadPackets())
                {
                    token.ThrowIfCancellationRequested();
                    AVPacket* p = packet;
                    if (p->stream_index != video->index || p->pts == FF.AV_NOPTS_VALUE)
                        continue;
                    var key = (p->flags & 1) != 0;
                    var duration = p->duration > 0 ? p->duration : frame;
                    if (splice < 0)
                    {
                        before += duration;
                        if (p->pts == pts)
                            splice = packets.Count;
                        else if (key)
                        {
                            // 只保留 I 帧之前最近一个关键帧起的包，连续解码从这里开始。
                            DisposePackets(packets);
                            keyBefore = true;
                        }
                        else if (!keyBefore)
                            continue;
                    }
                    else if (key)
                    {
                        end = packets.Count;
                        endPts = p->pts;
                        break;
                    }
                    else if ((after += duration) > limit)
                        return false;
                    var copy = new Packet();
                    packets.Add(copy);
                    FFmpegException.Check(FF.av_packet_ref(copy, p));
                }
                if (splice < 0)
                    return false;
                if (keyBefore || fromStart)
                    break;
                if (before > limit)
                    return false;
            }
            if (end < 0)
                end = packets.Count;
            if (!CheckIntraSpliceSyntax(context, packets, splice, end))
                return false;
            // 连续解码与单独解码的 I 帧之后画面（显示不早于 I、早于下一关键帧）。
            // 场编码时两个场合成一帧，按解码输出的画面比较：数量相同、包含 I 帧画面、逐帧哈希一致。
            var continuous = DecodeHashes(decoder, packets, 0, end, pts, endPts, token);
            var standalone = DecodeRewritten(context, packets, splice, end, pts, endPts, token);
            return continuous is not null && standalone is not null && standalone.ContainsKey(pts) &&
                   standalone.Count == continuous.Count &&
                   standalone.All(pair => continuous.TryGetValue(pair.Key, out var hash) && hash == pair.Value);
        }
        finally
        {
            DisposePackets(packets);
            FF.avcodec_flush_buffers(decoder);
            for (var i = 0; i < discards.Length; i++)
                raw->streams[i]->discard = discards[i];
        }

        static void DisposePackets(List<Packet> list)
        {
            foreach (var packet in list)
                packet.Dispose();
            list.Clear();
        }
    }

    /// <summary>
    /// 按复制区的方式改写 I 帧及其后的包（I 帧改为 IDR、前导画面丢弃并重编号，首包前带原始参数集），
    /// 用单独的解码器从改写后的码流解码，返回显示时间在 [lower, upper) 内各画面的哈希。
    /// 改写不支持该码流结构（<see cref="InvalidDataException"/>）或解码出错时返回 null，即该 I 帧不能作为衔接点。
    /// </summary>
    private static Dictionary<long, ulong>? DecodeRewritten(SpliceContext context, List<Packet> packets, int splice, int end,
        long lower, long upper, CancellationToken token)
    {
        var rewriter = new H264Splice.CopyRewriter(context.CreateH264Sets());
        var rewritten = new List<Packet>();
        try
        {
            for (var i = splice; i < end; i++)
            {
                token.ThrowIfCancellationRequested();
                AVPacket* source = packets[i];
                var data = new ReadOnlySpan<byte>(source->data, source->size);
                try
                {
                    if (i > splice && source->pts < lower)
                    {
                        rewriter.SkipLeading(data, context.LengthSize);
                        continue;
                    }
                    var prefix = i == splice ? context.OriginalParameterSets : null;
                    var buffer = ArrayPool<byte>.Shared.Rent(H264Splice.CopyRewriter.OutputBound(data, context.LengthSize, prefix));
                    try
                    {
                        var length = rewriter.Rewrite(data, context.LengthSize, i == splice, 1, prefix, buffer);
                        var packet = new Packet();
                        rewritten.Add(packet);
                        AVPacket* target = packet;
                        FFmpegException.Check(FF.av_new_packet(target, length));
                        buffer.AsSpan(0, length).CopyTo(new Span<byte>(target->data, length));
                        target->pts = source->pts;
                        target->dts = source->dts;
                        target->duration = source->duration;
                        target->flags = source->flags;
                    }
                    finally { ArrayPool<byte>.Shared.Return(buffer); }
                }
                catch (InvalidDataException)
                {
                    // 改写不支持该结构（例如作参考的前导场、前导画面带 MMCO）：结论是不能在此衔接，不是解码错误。
                    return null;
                }
            }
            // 改写后为 Annex B 且首包带参数集，用不带附加数据的解码器按带内参数集解码。
            using var decoder = new CodecContext(Codec.FindDecoderById(AVCodecID.AV_CODEC_ID_H264));
            decoder.PacketTimeBase = context.Video->time_base;
            decoder.ThreadCount = 0;
            // 沿用源流的重排序深度：新解码器从 IDR 开始时按 0 帧延迟输出，会丢掉 I 帧之后先解码、后显示的 B 帧。
            ((AVCodecContext*)decoder)->has_b_frames = context.Video->codecpar->video_delay;
            FFmpegException.Check(FF.avcodec_open2(decoder, FF.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264), null));
            return DecodeHashes(decoder, rewritten, 0, rewritten.Count, lower, upper, token);
        }
        finally
        {
            foreach (var packet in rewritten)
                packet.Dispose();
        }
    }

    /// <summary>检查 I 帧与其 GOP 的切片头是否允许改写为 IDR 衔接（见 <see cref="VerifyIntraSplice"/>）。</summary>
    private static bool CheckIntraSpliceSyntax(SpliceContext context, List<Packet> packets, int splice, int end)
    {
        var sets = context.CreateH264Sets();
        var mmco = new List<H264Splice.Mmco>();
        var rbsp = new byte[512];
        for (var i = 0; i < end; i++)
        {
            AVPacket* packet = packets[i];
            var data = new ReadOnlySpan<byte>(packet->data, packet->size);
            try
            {
                foreach (var range in VideoBitstream.EnumerateNals(data, context.LengthSize))
                {
                    var nal = data[range];
                    var type = nal[0] & 0x1F;
                    if (type is 7 or 8)
                        sets.Add(nal);
                    if (i < splice || type is not (1 or 5))
                        continue;
                    var header = nal[1..Math.Min(nal.Length, rbsp.Length)];
                    var length = H264Splice.Unescape(header, rbsp);
                    // 切片头超出读取范围时视为不支持（例如极长的加权预测表）。
                    if (!H264Splice.TryParseSlice(rbsp.AsSpan(0, length), nal[0], sets, mmco, out var slice) ||
                        mmco.Any(op => op.Op is not (1 or 5)))
                        return false;
                    if (i == splice && (!slice.Intra || slice.NalRefIdc == 0 || slice.NalType != 1))
                        return false;
                }
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>从 from 起送入解码器，返回显示时间在 [lower, upper) 内各画面的哈希；解码出错返回 null。</summary>
    private static Dictionary<long, ulong>? DecodeHashes(CodecContext decoder, List<Packet> packets, int from, int end,
        long lower, long upper, CancellationToken token)
    {
        FF.avcodec_flush_buffers(decoder);
        var hashes = new Dictionary<long, ulong>();
        using var frame = new Frame();
        for (var i = from; i <= end; i++)
        {
            token.ThrowIfCancellationRequested();
            var result = FF.avcodec_send_packet(decoder, i < end ? packets[i] : null);
            if (result < 0 && result != FF.AVERROR_EAGAIN && result != FF.AVERROR_EOF)
                return null;
            while (FF.avcodec_receive_frame(decoder, frame) >= 0)
            {
                try
                {
                    AVFrame* raw = frame;
                    var pts = raw->pts != FF.AV_NOPTS_VALUE ? raw->pts : raw->best_effort_timestamp;
                    if (pts >= lower && pts < upper)
                        hashes[pts] = HashPicture(raw);
                }
                finally { frame.Unref(); }
            }
        }
        FF.avcodec_flush_buffers(decoder);
        return hashes;
    }

    /// <summary>画面内容的 64 位哈希（软件解码的平面 YUV：亮度与两个色度平面），只用于比较两次解码是否一致。</summary>
    private static ulong HashPicture(AVFrame* frame)
    {
        var hash = 14695981039346656037UL;
        var descriptor = FF.av_pix_fmt_desc_get((AVPixelFormat)frame->format);
        if (descriptor == null || descriptor->nb_components < 3 || (descriptor->flags & PixelFormatPlanar) == 0)
            throw new FrameAccurateExportException(FrameAccurateExportError.IncompatibleStream, ((AVPixelFormat)frame->format).ToString());
        // 每个样本的字节数（8 位为 1，10 位为 2），色度平面按格式的水平/垂直二次采样缩小。
        var bytes = descriptor->comp[0u].step;
        for (var plane = 0; plane < 3; plane++)
        {
            var width = (plane == 0 ? frame->width : -(-frame->width >> descriptor->log2_chroma_w)) * bytes;
            var height = plane == 0 ? frame->height : -(-frame->height >> descriptor->log2_chroma_h);
            for (var y = 0; y < height; y++)
            {
                var row = new ReadOnlySpan<byte>(frame->data[(uint)plane] + (long)y * frame->linesize[(uint)plane], width);
                var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(row);
                foreach (var word in words)
                    hash = (hash ^ word) * 1099511628211UL;
                foreach (var value in row[(words.Length * 8)..])
                    hash = (hash ^ value) * 1099511628211UL;
            }
        }
        return hash;
    }

    /// <summary>
    /// Matroska 等容器定位后开头几个包可能没有 DTS：按后续已知 DTS 减去包时长回推，结尾缺失时按前一包顺推。
    /// </summary>
    private static void FillMissingDts(List<VideoPacket> packets, long frameDuration)
    {
        long Duration(VideoPacket packet) => packet.Duration > 0 ? packet.Duration : frameDuration;
        for (var i = packets.Count - 2; i >= 0; i--)
            if (packets[i].Dts == FF.AV_NOPTS_VALUE && packets[i + 1].Dts != FF.AV_NOPTS_VALUE)
                packets[i] = packets[i] with { Dts = packets[i + 1].Dts - Duration(packets[i]) };
        for (var i = 1; i < packets.Count; i++)
            if (packets[i].Dts == FF.AV_NOPTS_VALUE && packets[i - 1].Dts != FF.AV_NOPTS_VALUE)
                packets[i] = packets[i] with { Dts = packets[i - 1].Dts + Duration(packets[i - 1]) };
        if (packets.Any(packet => packet.Dts == FF.AV_NOPTS_VALUE))
            throw new FrameAccurateExportException(FrameAccurateExportError.NonMonotonicTimestamp, "dts");
    }

    private static long FrameDuration(SpliceContext context) => EstimateEnd(context, [], 0);

    /// <summary>
    /// 显示时间在 [from, to) 内的画面时长（画面数 × 帧时长）。录制中信号中断时时间戳会跳过一段，
    /// 跳变两侧的画面在码流中相邻，按时间戳相减会把中断的时长当作需要解码或重编码的内容。
    /// </summary>
    private static long Content(SpliceContext context, List<VideoPacket> packets, long from, long to) =>
        packets.Count(packet => !packet.SecondField && packet.Pts >= from && packet.Pts < to) * FrameDuration(context);

    private static long NextDisplay(List<VideoPacket> packets, long pts)
    {
        var next = long.MaxValue;
        foreach (var packet in packets)
            if (!packet.SecondField && packet.Pts > pts && packet.Pts < next)
                next = packet.Pts;
        return next == long.MaxValue ? long.MinValue : next;
    }

    // 文件最后一帧没有后继画面时，使用包时长；缺失时按平均帧率推算。
    private static long EstimateEnd(SpliceContext context, List<VideoPacket> packets, long pts)
    {
        var index = packets.FindIndex(packet => packet.Pts == pts && !packet.SecondField);
        // 场对的终点取第二场的结束时间。
        if (index >= 0 && index + 1 < packets.Count && packets[index + 1].SecondField && packets[index + 1].Duration > 0)
            return packets[index + 1].Pts + packets[index + 1].Duration;
        if (index >= 0 && packets[index].Duration > 0 && (index + 1 >= packets.Count || !packets[index + 1].SecondField))
            return pts + packets[index].Duration;
        var rate = FrameAccurateVideoEncoder.FrameRate(context.Video);
        var timeBase = context.Video->time_base;
        return pts + Math.Max(1, (long)Math.Round((double)rate.den * timeBase.den / ((double)rate.num * timeBase.num)));
    }

    /// <summary>
    /// 随机访问点可作为衔接点时返回其 GOP 的最早显示时间。闭合 GOP 的前导画面（解码在后、显示在前）只参考本 GOP，
    /// 随随机访问点一起复制或解码；开放 GOP 另行标记。要求上一个随机访问点之后、本点之前的包显示都早于该时间；
    /// 读到下一个随机访问点或文件尾才能确认。
    /// </summary>
    private static Splice? SpliceAt(List<VideoPacket> packets, int index, bool reachedEof)
    {
        if (!packets[index].RandomAccess)
            return null;
        var display = packets[index].Pts;
        var confirmed = reachedEof;
        for (var i = index + 1; i < packets.Count; i++)
        {
            if (packets[i].RandomAccess)
            {
                confirmed = true;
                break;
            }
            display = Math.Min(display, packets[i].Pts);
        }
        if (!confirmed)
            return null;
        for (var i = index - 1; i >= 0 && !packets[i].RandomAccess; i--)
            if (packets[i].Pts >= display)
                return null;
        return new Splice(display, packets[index].Open, packets[index].Pts);
    }

    /// <summary>
    /// 显示顺序中紧邻 pts 之前的画面（场对取第一场），把“终点不含该帧”换算为包含的终点；pts 之前没有画面时报错。
    /// </summary>
    private long PreviousFramePts(SpliceContext context, long pts, CancellationToken token)
    {
        var (packets, _, _, _) = ScanAround(context, pts, pts, token);
        var previous = packets.Where(packet => !packet.SecondField && packet.Pts < pts)
            .Select(packet => packet.Pts).DefaultIfEmpty(long.MinValue).Max();
        return previous != long.MinValue ? previous
            : throw new FrameAccurateExportException(FrameAccurateExportError.FrameNotFound, pts.ToString());
    }

    /// <summary>
    /// 从目标之前定位读取视频包元数据，直到目标之后出现已确认闭合的 IDR、文件结束或超过重编码上限。
    /// 返回列表、首个 IDR 的位置、目标帧位置。
    /// </summary>
    private (List<VideoPacket> Packets, int FirstIdr, int TargetIndex, bool ReachedEof) ScanAround(
        SpliceContext context, long seekTarget, long target, CancellationToken token)
    {
        var input = context.Input;
        AVFormatContext* raw = input;
        var video = context.Video;
        var margin = context.Ticks(ScanMarginSeconds);
        var limit = context.Ticks(MaxReencodeSeconds + ScanMarginSeconds);
        var discards = new AVDiscard[raw->nb_streams];
        for (var i = 0; i < raw->nb_streams; i++)
        {
            discards[i] = raw->streams[i]->discard;
            if (i != video->index)
                raw->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
        }
        try
        {
            var startTime = video->start_time == FF.AV_NOPTS_VALUE ? 0 : video->start_time;
            var frame = FrameDuration(context);
            // 向前定位的余量按时间戳逐次加倍；信号中断时目标之前的时间戳有跳变，余量落在中断内时定位会停在中断之后，
            // 因此以实际读到的目标前画面时长（而不是余量本身）判断是否已超出范围。
            for (; ; margin *= 2)
            {
                var seek = seekTarget - margin;
                var fromStart = seek <= startTime;
                // 带 B 帧的 TS 在首帧附近定位可能越过首个 GOP，从开头读取时再向前预留。
                input.SeekFrame(fromStart ? startTime - context.Ticks(ScanMarginSeconds) : seek, video->index);
                var packets = new List<VideoPacket>();
                var reachedEof = true;
                var firstIdr = -1;
                var after = 0;
                H264Splice.PictureInfo? firstField = null;
                foreach (var packet in input.ReadPackets())
                {
                    token.ThrowIfCancellationRequested();
                    AVPacket* p = packet;
                    if (p->stream_index != video->index || p->pts == FF.AV_NOPTS_VALUE)
                        continue;
                    var data = new ReadOnlySpan<byte>(p->data, p->size);
                    var (idr, open, candidate) = context.Classify(data, (p->flags & 1) != 0);
                    if (idr)
                    {
                        // 记录带内参数集；广播 TS 通常只在码流中携带 SPS/PPS。
                        if (context.Nal)
                            foreach (var range in VideoBitstream.SplitNals(data, context.LengthSize))
                                VideoBitstream.AddParameterSet(data[range], context.Hevc, context.OriginalParameterSets);
                        if (firstIdr < 0)
                            firstIdr = packets.Count;
                    }
                    var secondField = false;
                    if (context.Nal && !context.Hevc &&
                        H264Splice.ReadPicture(data, context.LengthSize, context.H264Sets, context.Scratch) is { } picture)
                    {
                        secondField = picture.Field && firstField is { } first && first.FrameNum == picture.FrameNum &&
                                      first.Bottom != picture.Bottom;
                        firstField = picture.Field && !secondField ? picture : null;
                    }
                    packets.Add(new VideoPacket(p->pts, p->dts, p->duration, idr, (p->flags & 1) != 0, open, candidate,
                        secondField, p->size));
                    if (!secondField && p->pts > target)
                        after++;
                    if (p->pts > target && Confirmed(context, packets, target) || after * frame > limit)
                    {
                        reachedEof = false;
                        break;
                    }
                }
                FillMissingDts(packets, FrameDuration(context));
                if (input.InputFormat?.Name == "mpegts")
                    for (var i = 0; i < packets.Count; i++)
                    {
                        var packet = packets[i];
                        context.MaxDtsAfterPts = Math.Max(context.MaxDtsAfterPts, packet.Dts - packet.Pts);
                        if (context.DtsCorrection > 0)
                            packets[i] = packet with { Dts = packet.Dts - context.DtsCorrection };
                    }
                RecordBitrate(context, packets);
                var targetIndex = packets.FindIndex(packet => packet.Pts == target);
                if (targetIndex >= 0 && firstIdr >= 0 && firstIdr <= targetIndex &&
                    packets.Take(targetIndex + 1).Any(packet => packet.RandomAccess && !Rejected(context, packet) &&
                        (!packet.Open || packet.Pts <= target)))
                {
                    if (context.OriginalParameterSets.Count == 0 && context.Nal)
                        VideoBitstream.ReadParameterSets(new ReadOnlySpan<byte>(video->codecpar->extradata,
                            video->codecpar->extradata_size), context.Hevc, context.OriginalParameterSets);
                    return (packets, firstIdr, targetIndex, reachedEof);
                }
                // 未找到目标帧时，若已从文件开头读取且目标不在其中，说明所选画面不是该流的帧。
                if (fromStart)
                {
                    if (targetIndex < 0)
                        throw new FrameAccurateExportException(FrameAccurateExportError.FrameNotFound, target.ToString());
                    throw new FrameAccurateExportException(FrameAccurateExportError.NoRandomAccessPoint);
                }
                if (Content(context, packets, long.MinValue, target) > 2 * limit)
                    throw new FrameAccurateExportException(FrameAccurateExportError.NoRandomAccessPoint);
            }
        }
        finally
        {
            // 读取中可能新增 EPG 等轨道；只恢复本轮实际修改过的轨道。
            for (var i = 0; i < discards.Length; i++)
                raw->streams[i]->discard = discards[i];
        }
    }

    // 目标之后已有闭合 IDR，并且读到了它之后的下一个 IDR（用于确认闭合）。验证不合格的普通 I 帧仍分隔 GOP，但不作为终点。
    private static bool Confirmed(SpliceContext context, List<VideoPacket> packets, long target)
    {
        if (!packets[^1].RandomAccess)
            return false;
        for (var i = packets.Count - 2; i >= 0; i--)
            if (packets[i].RandomAccess && !Rejected(context, packets[i]))
                return SpliceAt(packets, i, false)?.CopyDisplayStart > target;
        return false;
    }

    // ---------------- 输出 ----------------

    /// <summary>
    /// 解码输出与编码之间的小重排序缓冲。正常码流的解码输出已按显示时间排列；接收中断后参考关系与 POC 不再连续，
    /// 解码器可能把中断前最后的画面晚于中断后的画面输出。按 PTS 暂存最多 Depth 帧再送入编码器，
    /// 编码顺序与显示时间一致，与播放器按时间戳显示的画面相同；更晚到达的画面按时间戳错误报出。
    /// </summary>
    private sealed class ReorderedFrames : IDisposable
    {
        internal const int Depth = 4;
        private readonly SortedList<long, Frame> frames = [];
        public int Count => frames.Count;
        public bool Contains(long pts) => frames.ContainsKey(pts);
        public void Add(long pts, Frame frame) => frames.Add(pts, frame.CloneReference());

        public (long Pts, Frame Frame) TakeFirst()
        {
            var first = (frames.Keys[0], frames.Values[0]);
            frames.RemoveAt(0);
            return first;
        }

        public void Clear()
        {
            foreach (var frame in frames.Values)
                frame.Dispose();
            frames.Clear();
        }

        public void Dispose() => Clear();
    }

    private sealed class StreamMap(int[] output, int videoOutput)
    {
        public int[] Output { get; } = output;
        public int VideoOutput { get; } = videoOutput;
    }

    private static StreamMap CreateStreams(AVFormatContext* output, FormatContext input, FrameAccurateExportSettings settings,
        SpliceContext context)
    {
        AVFormatContext* raw = input;
        var map = Enumerable.Repeat(-1, checked((int)raw->nb_streams)).ToArray();
        var videoOutput = -1;
        FFmpegException.Check(FF.av_dict_copy(&output->metadata, raw->metadata, 0));
        foreach (var index in settings.StreamIndices)
        {
            var source = raw->streams[index];
            var type = source->codecpar->codec_type;
            var cover = (source->disposition & 1024) != 0;
            var supported = type switch
            {
                AVMediaType.AVMEDIA_TYPE_VIDEO => index == settings.VideoIndex || cover && settings.Container is "mp4" or "mkv",
                AVMediaType.AVMEDIA_TYPE_AUDIO => true,
                AVMediaType.AVMEDIA_TYPE_SUBTITLE => IsTextSubtitle(source->codecpar->codec_id) ||
                    settings.Container is "ts" or "m2ts" && source->codecpar->codec_id is
                        AVCodecID.AV_CODEC_ID_DVB_SUBTITLE or AVCodecID.AV_CODEC_ID_DVB_TELETEXT,
                AVMediaType.AVMEDIA_TYPE_ATTACHMENT => settings.Container == "mkv",
                _ => false
            };
            // mpegts 复用器不提供编码查询，由写文件头时校验。
            if (!supported || type != AVMediaType.AVMEDIA_TYPE_ATTACHMENT && settings.Container is not ("ts" or "m2ts") &&
                FF.avformat_query_codec(output->oformat, source->codecpar->codec_id, 0) <= 0)
                throw new FrameAccurateExportException(FrameAccurateExportError.UnsupportedTrack,
                    $"#{index}: {input.Streams[index].Codecpar?.CodecName}");
            var stream = FF.avformat_new_stream(output, null);
            if (stream == null)
                throw new OutOfMemoryException();
            FFmpegException.Check(FF.avcodec_parameters_copy(stream->codecpar, source->codecpar));
            stream->codecpar->codec_tag = 0;
            RemuxCodecTag.FillChannelLayout(stream->codecpar);
            stream->time_base = source->time_base;
            stream->avg_frame_rate = source->avg_frame_rate;
            stream->sample_aspect_ratio = source->sample_aspect_ratio;
            stream->disposition = source->disposition;
            // TS 输出沿用原始 PID，便于与源节目对照。
            if (settings.Container is "ts" or "m2ts" && source->id is > 0x0F and < 0x1FFF)
                stream->id = source->id;
            FFmpegException.Check(FF.av_dict_copy(&stream->metadata, source->metadata, 0));
            if (cover)
                FFmpegException.Check(FF.av_packet_ref(&stream->attached_pic, &source->attached_pic));
            map[index] = stream->index;
            if (index == settings.VideoIndex)
                videoOutput = stream->index;
            // MPEG-2 保留原始序列头作为附加数据；重编码段的序列头随其首个 I 帧在码流中输出。
            // AV1 保留原始 av1C；重编码段的序列头在其关键帧的时间单元中。VP9 保留原始 vpcC，每个关键帧自带完整帧头。
            if (index == settings.VideoIndex && context.Nal)
            {
                // 视频统一以 Annex B 写入。H.264 附加数据同时包含原始与重编码参数集，MP4/MKV 由封装器生成
                // 含多组参数集的 avcC。HEVC 的重编码参数集与原始参数集编号相同，不能放进同一个 hvcC：
                // MP4 使用 hvc1，在切换处以新附加数据让封装器写入第二个样本描述，并从样本中去掉参数集；
                // TS/MKV 仍在每个衔接 IDR 前带内重发参数集。
                var parameterSets = new List<byte[]>(context.OriginalParameterSets);
                foreach (var set in context.EncodedParameterSets)
                    VideoBitstream.AddParameterSet(set, context.Hevc, parameterSets);
                ReplaceExtradata(stream->codecpar, VideoBitstream.ToAnnexB([], 0, context.Hevc, parameterSets));
                // 杜比视界为 dvh1（参数集同样只在样本描述中），其余 HEVC 为 hvc1。
                if (context.Hevc && settings.Container == "mp4")
                    stream->codecpar->codec_tag = RemuxCodecTag.Select("mp4", stream->codecpar);
            }
        }
        if (videoOutput < 0)
            throw new FrameAccurateExportException(FrameAccurateExportError.InvalidRequest);
        return new StreamMap(map, videoOutput);
    }

    private static void ReplaceExtradata(AVCodecParameters* parameters, byte[] data)
    {
        var buffer = (byte*)FF.av_mallocz(checked((ulong)data.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
        if (buffer == null)
            throw new OutOfMemoryException();
        data.CopyTo(new Span<byte>(buffer, data.Length));
        FF.av_free(parameters->extradata);
        parameters->extradata = buffer;
        parameters->extradata_size = data.Length;
    }

    private static void WriteAttachedPictures(AVFormatContext* output, FormatContext input, FrameAccurateExportSettings settings,
        StreamMap map)
    {
        foreach (var index in settings.StreamIndices)
        {
            AVStream* stream = input.Streams[index];
            if ((stream->disposition & 1024) == 0 || stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO)
                continue;
            using var packet = new Packet();
            AVPacket* raw = packet;
            FFmpegException.Check(FF.av_packet_ref(raw, &stream->attached_pic));
            raw->stream_index = map.Output[index];
            raw->pts = raw->dts = 0;
            raw->pos = -1;
            FFmpegException.Check(FF.av_interleaved_write_frame(output, raw));
        }
    }

    internal static bool IsTextSubtitle(AVCodecID codec) => codec is AVCodecID.AV_CODEC_ID_MOV_TEXT or
        AVCodecID.AV_CODEC_ID_ASS or AVCodecID.AV_CODEC_ID_SSA or AVCodecID.AV_CODEC_ID_SUBRIP or
        AVCodecID.AV_CODEC_ID_TEXT or AVCodecID.AV_CODEC_ID_WEBVTT;

    /// <summary>负责时间戳映射、拼接点 DTS 单调与写包；所有片段共用一个实例。</summary>
    private sealed class SpliceWriter(AVFormatContext* output, StreamMap map, bool annexBParameterSets, SpliceContext context)
        : IDisposable
    {
        private readonly long[] lastDts = Enumerable.Repeat(FF.AV_NOPTS_VALUE, (int)output->nb_streams).ToArray();
        private readonly Packet scratch = new();
        public long Bytes { get; private set; }
        /// <summary>HEVC MP4：参数集切换处给包附加新附加数据，由封装器选择或新建 hvc1 样本描述。</summary>
        public bool SampleEntries { get; init; }
        /// <summary>TS → TS 按源传输包写出时的输出；为 null 时经 ffmpeg 复用器写出。</summary>
        public FrameAccurateTsOutput? Ts { get; init; }
        public AVFormatContext* Output => output;
        public StreamMap Map => map;
        public bool InBandParameterSets => annexBParameterSets;
        public SpliceContext Context => context;
        // 当前片段在输出时间轴上的起点（源视频时间基）与源起点。
        public long Offset { get; private set; }
        public long SourceStart { get; private set; }
        public long SegmentLength { get; set; }
        private long spliceFirstDts = FF.AV_NOPTS_VALUE;
        private long splicePreviousDts = FF.AV_NOPTS_VALUE;
        // H.264 复制区从普通 I 帧开始时的改写状态（改为 IDR、frame_num 重编号），每个片段重新开始。
        private H264Splice.CopyRewriter? h264Rewriter;
        // 改写出的 IDR 在 1、2 间交替，与 x264 首个 IDR（0）及相邻的改写 IDR 都不同。
        private int idrPicId = 2;

        public long LastVideoDts => lastDts[map.VideoOutput];

        public void BeginSegment(long sourceStart, long offset, long firstSourceDts)
        {
            SourceStart = sourceStart;
            Offset = offset;
            var timeBase = output->streams[map.VideoOutput]->time_base;
            spliceFirstDts = Rescale(firstSourceDts - sourceStart + offset, context.Video->time_base, timeBase);
            splicePreviousDts = lastDts[map.VideoOutput];
            h264Rewriter = null;
        }

        public long MapTime(long value, AVRational from, int outputIndex)
        {
            var target = output->streams[outputIndex]->time_base;
            return Rescale(value, from, target) - Rescale(SourceStart - Offset, context.Video->time_base, target);
        }

        public void WriteVideo(AVPacket* packet, long sourcePts, long sourceDts, long sourceDuration,
            TsVideoPiece kind = TsVideoPiece.Source)
        {
            var index = map.VideoOutput;
            var sourcePosition = packet->pos;
            var timeBase = output->streams[index]->time_base;
            var source = context.Video->time_base;
            packet->stream_index = index;
            packet->pts = MapTime(sourcePts, source, index);
            packet->dts = MapTime(sourceDts, source, index);
            packet->duration = Rescale(sourceDuration, source, timeBase);
            packet->pos = -1;
            var splice = Rescale(Offset, source, timeBase);
            if (splicePreviousDts != FF.AV_NOPTS_VALUE && spliceFirstDts <= splicePreviousDts && packet->dts < splice)
            {
                // 片段开头的解码预滚与上一片段的解码时间重叠：把预滚压入拼接点前的可用间隔，
                // 只改 DTS，保留 PTS 与压缩数据。
                var firstAvailable = splicePreviousDts + 1;
                if (firstAvailable >= splice)
                    throw new FrameAccurateExportException(FrameAccurateExportError.NonMonotonicTimestamp, "splice");
                packet->dts = Math.Max(lastDts[index] + 1, firstAvailable + (long)Math.Floor(
                    (packet->dts - spliceFirstDts) / (double)(splice - spliceFirstDts) * (splice - firstAvailable)));
                if (packet->dts >= splice || packet->dts > packet->pts)
                    throw new FrameAccurateExportException(FrameAccurateExportError.NonMonotonicTimestamp, "splice");
            }
            if (context.DtsCorrection > 0 && packet->dts > packet->pts)
                throw new FrameAccurateExportException(FrameAccurateExportError.NonMonotonicTimestamp, "video DTS correction");
            CheckDts(packet, index, strict: true);
            if (Ts is not null)
            {
                Ts.Video(kind, sourcePosition, new ReadOnlySpan<byte>(packet->data, packet->size), packet->pts, packet->dts,
                    (packet->flags & 1) != 0);
                Bytes = Ts.BytesWritten;
                FF.av_packet_unref(packet);
                return;
            }
            Bytes += packet->size;
            FFmpegException.Check(FF.av_interleaved_write_frame(output, packet));
        }

        /// <summary>写出其他轨道（音频、字幕）的包；sourcePosition 为源 PES 的位置，按传输包写出时用。</summary>
        public void Write(AVPacket* packet, int index, bool strict, long sourcePosition)
        {
            // DVB 字幕的显示集可能有重叠时间；按源传输包写出时保留原顺序与时间戳。
            // 其他轨道及经复用器写出的路径仍检查 DTS，避免放过音视频时钟异常。
            if (Ts is null || output->streams[index]->codecpar->codec_id != AVCodecID.AV_CODEC_ID_DVB_SUBTITLE)
                CheckDts(packet, index, strict);
            if (Ts is not null)
            {
                Ts.Other(sourcePosition, new ReadOnlySpan<byte>(packet->data, packet->size), packet->pts, packet->dts);
                Bytes = Ts.BytesWritten;
                FF.av_packet_unref(packet);
                return;
            }
            Bytes += packet->size;
            FFmpegException.Check(FF.av_interleaved_write_frame(output, packet));
        }

        private void CheckDts(AVPacket* packet, int index, bool strict)
        {
            if (lastDts[index] != FF.AV_NOPTS_VALUE && (packet->dts < lastDts[index] || strict && packet->dts == lastDts[index]))
                throw new FrameAccurateExportException(FrameAccurateExportError.NonMonotonicTimestamp,
                    $"#{index}: DTS {packet->dts} <= {lastDts[index]}");
            lastDts[index] = packet->dts;
        }

        /// <summary>复制源压缩数据，按输出需要转换为 Annex B 并补带内参数集；NAL 与图像数据不变。</summary>
        public void WriteCopied(AVPacket* source, long sourceDts, bool insertParameterSets, bool closeGop = false)
        {
            var data = new ReadOnlySpan<byte>(source->data, source->size);
            // VP9 样本（含超帧与隐藏帧）原样写出：关键帧自带画面尺寸、位深与色彩空间，不需要补任何头。
            if (context.Vp9)
            {
                WriteVideo(source, source->pts, sourceDts, source->duration);
                return;
            }
            if (context.Av1)
            {
                // 重编码段使用编码器自己的序列头；复制起点的关键帧不带序列头时补上原始序列头（新的编码视频序列）。
                if (insertParameterSets && !Av1Bitstream.HasSequenceHeader(data) && context.Av1SequenceHeader.Length > 0)
                    WriteData(source, [.. context.Av1SequenceHeader, .. data], source->pts, sourceDts, source->duration, source->flags);
                else
                    WriteVideo(source, source->pts, sourceDts, source->duration);
                return;
            }
            if (context.Mpeg2)
            {
                byte[]? bytes = null;
                // 复制区从重编码段之后开始：重编码段的序列头参数不同，先结束上一序列再开始新序列。
                ReadOnlySpan<byte> restart = insertParameterSets && LastVideoDts != FF.AV_NOPTS_VALUE ? SequenceEndCode : [];
                ReadOnlySpan<byte> header = insertParameterSets && !VideoBitstream.ReadMpeg2(data).SequenceHeader
                    ? context.Mpeg2Header : [];
                if (restart.Length + header.Length > 0)
                    bytes = [.. restart, .. header, .. data];
                if (closeGop)
                {
                    bytes ??= data.ToArray();
                    if (!VideoBitstream.CloseMpeg2Gop(bytes))
                        throw new FrameAccurateExportException(FrameAccurateExportError.IncompatibleStream, "GOP header");
                }
                if (bytes is null)
                    WriteVideo(source, source->pts, sourceDts, source->duration);
                else
                    WriteData(source, bytes, source->pts, sourceDts, source->duration, source->flags);
                return;
            }
            List<byte[]>? prefix = null;
            if (insertParameterSets && !VideoBitstream.ContainsSps(data, context.LengthSize, context.Hevc))
                prefix = context.OriginalParameterSets;
            // 复制区首包切回原始参数集所在的样本描述。
            var sampleEntry = SampleEntries && insertParameterSets ? context.OriginalParameterSets : null;
            // HEVC 复制区从 CRA 开始：RASL 前导画面已丢弃并随片头重新编码，CRA 改为 BLA_W_LP，
            // 解码器在此开始新的编码视频序列，POC 不再接续之前重新编码的画面。数据已改写，按改写过的 PES 写出。
            if (closeGop && context.Hevc)
            {
                FFmpegException.Check(FF.av_packet_make_writable(source));
                var writable = new Span<byte>(source->data, source->size);
                VideoBitstream.ConvertCraToBla(writable, context.LengthSize);
                WriteAnnexB(source, writable, context.LengthSize, prefix, source->pts, sourceDts, source->duration,
                    source->flags, sampleEntry);
                return;
            }
            // H.264 复制区从普通 I 帧开始：该 I 帧改写为 IDR，此后的切片重编号，直到码流自身出现 IDR。
            if (closeGop)
                h264Rewriter = new H264Splice.CopyRewriter(context.CreateH264Sets());
            if (h264Rewriter is { Active: true } rewriter)
            {
                WriteRewritten(source, rewriter, data, sourceDts, closeGop, prefix);
                return;
            }
            if (prefix is null && sampleEntry is null && context.LengthSize is 0 or 4)
            {
                // TS 源本就是 Annex B；MP4/MKV 的四字节长度前缀与起始码等长，在源包上原地改写，不复制压缩数据。
                // 缓冲区被解码器等共享时由 av_packet_make_writable 复制，不影响其他引用。
                if (context.LengthSize == 4)
                {
                    FFmpegException.Check(FF.av_packet_make_writable(source));
                    VideoBitstream.ConvertToAnnexBInPlace(new Span<byte>(source->data, source->size));
                }
                WriteVideo(source, source->pts, sourceDts, source->duration);
                return;
            }
            WriteAnnexB(source, data, context.LengthSize, prefix, source->pts, sourceDts, source->duration,
                source->flags, sampleEntry);
        }

        /// <summary>转为 Annex B（可插入参数集、附加新的样本描述）后直接写入原生包，不经过托管数组。</summary>
        public void WriteAnnexB(AVPacket* properties, ReadOnlySpan<byte> data, int lengthSize, IReadOnlyList<byte[]>? prefix,
            long pts, long dts, long duration, int flags, IReadOnlyList<byte[]>? sampleEntry)
        {
            scratch.Unref();
            AVPacket* packet = scratch;
            try
            {
                var size = VideoBitstream.AnnexBSize(data, lengthSize, prefix);
                FFmpegException.Check(FF.av_new_packet(packet, size));
                if (properties != null)
                    FFmpegException.Check(FF.av_packet_copy_props(packet, properties));
                VideoBitstream.WriteAnnexB(data, lengthSize, context.Hevc, prefix, new Span<byte>(packet->data, size));
                packet->flags = flags;
                AddSampleEntry(packet, sampleEntry);
                WriteVideo(packet, pts, dts, duration, TsVideoPiece.Replaced);
            }
            finally { scratch.Unref(); }
        }

        /// <summary>复制区首个 I 帧之后不复制的前导画面：交给改写器记录，作参考时此后画面的编号前移。</summary>
        public void SkipLeading(ReadOnlySpan<byte> data)
        {
            if (h264Rewriter is not { Active: true } rewriter)
                return;
            try { rewriter.SkipLeading(data, context.LengthSize); }
            catch (InvalidDataException exception)
            {
                throw new FrameAccurateExportException(FrameAccurateExportError.IncompatibleStream, exception.Message);
            }
        }

        private void WriteRewritten(AVPacket* source, H264Splice.CopyRewriter rewriter, ReadOnlySpan<byte> data, long sourceDts,
            bool convertToIdr, IReadOnlyList<byte[]>? prefix)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(H264Splice.CopyRewriter.OutputBound(data, context.LengthSize, prefix));
            try
            {
                int length;
                try
                {
                    if (convertToIdr)
                        idrPicId = idrPicId == 1 ? 2 : 1;
                    length = rewriter.Rewrite(data, context.LengthSize, convertToIdr, idrPicId, prefix, buffer);
                }
                catch (InvalidDataException exception)
                {
                    throw new FrameAccurateExportException(FrameAccurateExportError.IncompatibleStream, exception.Message);
                }
                WriteData(source, buffer.AsSpan(0, length), source->pts, sourceDts, source->duration,
                    convertToIdr ? source->flags | 1 : source->flags);
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        /// <summary>写入改写后的数据（MPEG-2 补序列头、H.264 改写切片头等）。</summary>
        public void WriteData(AVPacket* properties, ReadOnlySpan<byte> data, long pts, long dts, long duration, int flags)
        {
            scratch.Unref();
            AVPacket* packet = scratch;
            try
            {
                FFmpegException.Check(FF.av_new_packet(packet, data.Length));
                if (properties != null)
                    FFmpegException.Check(FF.av_packet_copy_props(packet, properties));
                data.CopyTo(new Span<byte>(packet->data, data.Length));
                packet->flags = flags;
                WriteVideo(packet, pts, dts, duration, TsVideoPiece.Replaced);
            }
            finally { scratch.Unref(); }
        }

        /// <summary>写出调用方持有的原生包（如排队的编码输出），写出后包为空，由调用方释放。</summary>
        public void WriteOwned(AVPacket* packet, long pts, long dts, long duration, IReadOnlyList<byte[]>? sampleEntry)
        {
            AddSampleEntry(packet, sampleEntry);
            WriteVideo(packet, pts, dts, duration, TsVideoPiece.Encoded);
        }

        private void AddSampleEntry(AVPacket* packet, IReadOnlyList<byte[]>? sampleEntry)
        {
            if (sampleEntry is not { Count: > 0 })
                return;
            var size = VideoBitstream.AnnexBSize([], 0, sampleEntry);
            var side = FF.av_packet_new_side_data(packet, AVPacketSideDataType.AV_PKT_DATA_NEW_EXTRADATA, (ulong)size);
            if (side == null)
                throw new OutOfMemoryException();
            VideoBitstream.WriteAnnexB([], 0, context.Hevc, sampleEntry, new Span<byte>(side, size));
        }

        public void Dispose() => scratch.Dispose();
    }

    /// <summary>MPEG-2 sequence_end_code：重编码段与复制段的序列头参数（码率、VBV）不同，切换处结束上一序列。</summary>
    private static ReadOnlySpan<byte> SequenceEndCode => [0, 0, 1, 0xB7];

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteArrayComparer Instance = new();
        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);
        public int GetHashCode(byte[] obj) => obj.Length;
    }

    private static readonly AVRational Ms = new() { num = 1, den = 1000 };

    private static long Rescale(long value, AVRational from, AVRational to)
    {
        // 精确有理数换算，四舍五入；避免分数帧率累计误差。
        var numerator = (Int128)value * from.num * to.den;
        var denominator = (Int128)from.den * to.num;
        var half = denominator / 2;
        return (long)(numerator >= 0 ? (numerator + half) / denominator : -((-numerator + half) / denominator));
    }

    private static int CompareTime(long a, AVRational ta, long b, AVRational tb) =>
        ((Int128)a * ta.num * tb.den).CompareTo((Int128)b * tb.num * ta.den);

    // ---------------- 单个片段导出 ----------------

    // Leading：开放 GOP 复制起点的 I 帧已暂存，其前导画面仍送入头段解码器。
    private enum Phase { WaitStart, Head, Leading, Copy, Tail, Done }

    /// <summary>片段内进度：已读到的视频位置（秒，相对片段起点）、阶段、本片段已重编码画面数与已复制视频包数。</summary>
    private delegate void SegmentProgress(double seconds, FrameAccuratePhase phase, int encodedFrames, int copiedPackets);

    private static FrameAccuratePhase PublicPhase(Phase phase) => phase switch
    {
        Phase.WaitStart => FrameAccuratePhase.Preparing,
        Phase.Head or Phase.Leading => FrameAccuratePhase.EncodingHead,
        Phase.Copy => FrameAccuratePhase.Copying,
        Phase.Tail => FrameAccuratePhase.EncodingTail,
        _ => FrameAccuratePhase.Finishing
    };

    /// <summary>
    /// 把各片段的规划换算到输出时间轴（秒）：片头重编码到复制区显示起点，复制到片尾重编码的显示起点，其余为片尾重编码。
    /// 没有可复制关键帧的片段整段重编码；保留到文件末尾的片段按视频流终点估算。
    /// </summary>
    private static List<FrameAccuratePlanPart> PlanParts(List<SegmentPlan> plans, AVRational timeBase, long streamEnd)
    {
        var parts = new List<FrameAccuratePlanPart>();
        var scale = (double)timeBase.num / timeBase.den;
        double offset = 0;
        for (var index = 0; index < plans.Count; index++)
        {
            var plan = plans[index];
            var end = Math.Max(plan.StartPts, plan.EndExclusive == long.MaxValue ? streamEnd : plan.EndExclusive);
            var copyStart = plan.HasCopy ? Math.Clamp(plan.CopyDisplayStart, plan.StartPts, end) : end;
            var copyEnd = !plan.HasCopy ? end : plan.TailStartPts is null ? end : Math.Clamp(plan.TailDisplayStart, copyStart, end);
            void Add(FrameAccuratePartKind kind, long from, long to)
            {
                if (to > from)
                    parts.Add(new FrameAccuratePlanPart(index, kind, offset + (from - plan.StartPts) * scale, (to - from) * scale));
            }
            Add(FrameAccuratePartKind.Encode, plan.StartPts, copyStart);
            Add(FrameAccuratePartKind.Copy, copyStart, copyEnd);
            Add(FrameAccuratePartKind.Encode, copyEnd, end);
            offset += (end - plan.StartPts) * scale;
        }
        return parts;
    }

    private FrameAccurateSegmentReport ExportSegment(SpliceContext context, CodecContext decoder, SpliceWriter writer,
        SegmentPlan plan, FrameAccurateExportSettings settings, CancellationToken token, SegmentProgress report)
    {
        var input = context.Input;
        AVFormatContext* rawInput = input;
        var video = context.Video;
        var videoTimeBase = video->time_base;
        var isTs = input.InputFormat?.Name == "mpegts";
        long audioDelay = settings.AudioDelayMs;
        // 下一片段从上一片段的显示终点继续。按传输包写出时第一个片段保持源时间戳（与关键帧剪辑一致）。
        var offset = writer.Ts is not null && writer.LastVideoDts == FF.AV_NOPTS_VALUE
            ? plan.StartPts : writer.Offset + writer.SegmentLength;
        writer.BeginSegment(plan.StartPts, offset, plan.FirstDts);
        writer.Ts?.BeginSegment(plan.StartPts, plan.EndPts,
            Rescale(offset - plan.StartPts, videoTimeBase, new AVRational { num = 1, den = 90000 }));
        // 解析器把同一 PES 中的后续访问单元（场编码的第二场、多帧音频 PES）标为无位置，沿用该 PES 的位置。
        var lastPositions = new Dictionary<int, long>();
        var discards = new AVDiscard[rawInput->nb_streams];
        for (var i = 0; i < rawInput->nb_streams; i++)
        {
            discards[i] = rawInput->streams[i]->discard;
            rawInput->streams[i]->discard = i < writer.Map.Output.Length && writer.Map.Output[i] >= 0
                ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;
        }
        // TS 的音频可能先于视频写入文件，定位时向前预留余量，早于起点的包会被丢弃。
        input.SeekFrame(plan.DecodeStartPts - (isTs ? context.Ticks(1) : 0), video->index);
        FF.avcodec_flush_buffers(decoder);
        var phase = Phase.WaitStart;
        FrameAccurateVideoEncoder? encoder = null;
        var encoderActive = false;
        var encodedFrames = 0;
        var copiedPackets = 0;
        var pendingDurations = new Dictionary<long, long>();
        // 排队的编码输出保存为原生包引用，不复制成托管数组。
        var encodedQueue = new Queue<(Packet Packet, long Pts, IReadOnlyList<byte[]>? SampleEntry)>();
        var lastVideoEnd = long.MinValue;
        var nextCopyDts = FF.AV_NOPTS_VALUE;
        var readLimit = plan.EndExclusive == long.MaxValue ? long.MaxValue : plan.EndExclusive + context.Ticks(5);
        var encodedPts = new List<long>();
        var encodedOutput = 0;
        using var held = new Packet();
        using var heldPair = new Packet();
        // 头段解码时经过的前导画面：不复制，I 帧写出后交给改写器记录（作参考时此后画面的编号前移）。
        var heldLeading = new List<byte[]>();
        var warming = false;
        long encodeDelta = 0;
        var firstEncodedPacket = true;
        var finishedStreams = new HashSet<int>();
        var otherStreams = settings.StreamIndices.Where(index => index != settings.VideoIndex &&
            (rawInput->streams[index]->disposition & 1024) == 0 &&
            rawInput->streams[index]->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_ATTACHMENT).ToHashSet();
        using var frame = new Frame();
        using var reordered = new ReorderedFrames();
        try
        {
            using var packets = input.ReadPackets().GetEnumerator();
            while (phase != Phase.Done || finishedStreams.Count < otherStreams.Count)
            {
                if (!packets.MoveNext())
                    break;
                token.ThrowIfCancellationRequested();
                AVPacket* packet = packets.Current;
                var streamIndex = packet->stream_index;
                if (writer.Ts is { } ts && (streamIndex == video->index || otherStreams.Contains(streamIndex)))
                {
                    if (packet->pos >= 0)
                        lastPositions[streamIndex] = packet->pos;
                    else if (lastPositions.TryGetValue(streamIndex, out var inherited))
                        packet->pos = inherited;
                    ts.Demuxed(rawInput->streams[streamIndex]->id, packet->pos);
                }
                if (streamIndex == video->index)
                {
                    if (packet->pts == FF.AV_NOPTS_VALUE)
                    {
                        if (phase is Phase.WaitStart or Phase.Done)
                            continue;
                        throw new FrameAccurateExportException(FrameAccurateExportError.NonMonotonicTimestamp, "video pts");
                    }
                    // 视频完成后只为其他轨道再读取有限范围，稀疏字幕不会拖到文件末尾。
                    if (phase == Phase.Done && packet->pts > readLimit)
                        break;
                    // 复制时源包会直接交给封装器写出（时间戳被改写、包被清空），进度位置须在处理前取得。
                    var readPts = packet->pts;
                    if (context.DtsCorrection > 0 && packet->dts != FF.AV_NOPTS_VALUE)
                        packet->dts -= context.DtsCorrection;
                    HandleVideo(packet);
                    report(Math.Max(0, (readPts - plan.StartPts) * (double)videoTimeBase.num / videoTimeBase.den),
                        PublicPhase(phase), encodedFrames, copiedPackets);
                }
                else if (otherStreams.Contains(streamIndex) && !finishedStreams.Contains(streamIndex))
                {
                    HandleOther(packet);
                }
            }
            // 文件结束：排空解码器与编码器。
            if (phase == Phase.Leading)
            {
                CloseHead();
                phase = Phase.Copy;
            }
            if (phase is Phase.Head or Phase.Tail)
            {
                DecodeAndEncode(null, phase == Phase.Head ? plan.StartPts : plan.TailDisplayStart);
                FinishEncoder(plan.EndExclusive == long.MaxValue ? LastEncodedEnd() : plan.EndExclusive);
                phase = Phase.Done;
            }
            if (phase == Phase.Copy && !plan.CopyToEnd)
                throw new FrameAccurateExportException(FrameAccurateExportError.SourceChanged, "copy end");
            if (phase == Phase.WaitStart)
                throw new FrameAccurateExportException(FrameAccurateExportError.FrameNotFound, plan.StartPts.ToString());
            writer.Ts?.EndSegment();
        }
        finally
        {
            encoder?.Dispose();
            while (encodedQueue.TryDequeue(out var pending))
                pending.Packet.Dispose();
            for (var i = 0; i < discards.Length; i++)
                rawInput->streams[i]->discard = discards[i];
        }
        writer.SegmentLength = (plan.EndExclusive == long.MaxValue ? Math.Max(lastVideoEnd, plan.StartPts) : plan.EndExclusive)
            - plan.StartPts;
        return new FrameAccurateSegmentReport(encodedFrames, copiedPackets, plan.CopyStartPts, plan.CopyEndPts);

        void HandleVideo(AVPacket* packet)
        {
            var pts = packet->pts;
            if (phase == Phase.Done)
                return;
            if (phase == Phase.WaitStart)
            {
                if (pts != plan.DecodeStartPts)
                    return;
                phase = plan.HasHead ? Phase.Head : Phase.Copy;
                if (phase == Phase.Head)
                    StartEncoder(plan.HeadDelta);
            }
            if (phase == Phase.Head)
            {
                if (plan.HasCopy && pts == plan.CopyStartPts)
                {
                    if (!plan.DropLeading)
                    {
                        CloseHead();
                        phase = Phase.Copy;
                    }
                    else
                    {
                        // 开放 GOP：前导画面要在头段中重新编码，I 帧与前导画面继续送入头段解码器；
                        // I 帧暂存到头段写完后再复制，保持解码顺序。
                        DecodeAndEncode(packet, plan.StartPts);
                        FFmpegException.Check(FF.av_packet_ref(held, packet));
                        writer.Ts?.Hold(packet->pos);
                        phase = Phase.Leading;
                        return;
                    }
                }
                else
                {
                    DecodeAndEncode(packet, plan.StartPts);
                    return;
                }
            }
            if (phase == Phase.Leading)
            {
                // 前导画面与 I 场对的第二场继续送入头段解码器；第二场同样暂存，随 I 帧按解码顺序复制。
                if (pts < plan.CopyStartPts || pts == plan.CopyStartPairPts)
                {
                    DecodeAndEncode(packet, plan.StartPts);
                    if (pts == plan.CopyStartPairPts)
                    {
                        FFmpegException.Check(FF.av_packet_ref(heldPair, packet));
                        writer.Ts?.Hold(packet->pos);
                    }
                    else
                        heldLeading.Add(new ReadOnlySpan<byte>(packet->data, packet->size).ToArray());
                    return;
                }
                CloseHead();
                AVPacket* pair = heldPair;
                var hasPair = pair->data != null;
                warming |= plan.TailWarmupPts == plan.CopyStartPts;
                if (warming)
                {
                    DecodeAndEncode(held, plan.TailDisplayStart);
                    if (hasPair)
                        DecodeAndEncode(heldPair, plan.TailDisplayStart);
                }
                CopyPacket(held);
                held.Unref();
                if (hasPair)
                {
                    CopyPacket(heldPair);
                    heldPair.Unref();
                }
                foreach (var leading in heldLeading)
                    writer.SkipLeading(leading);
                heldLeading.Clear();
                phase = Phase.Copy;
            }
            if (phase == Phase.Copy)
            {
                if (!plan.CopyToEnd && pts == plan.CopyEndPts)
                {
                    if (plan.TailStartPts is null)
                    {
                        phase = Phase.Done;
                        return;
                    }
                    phase = Phase.Tail;
                    StartEncoder(plan.TailDelta);
                }
                else
                {
                    // 开放 GOP 复制起点的前导画面（显示早于复制起点）不复制：有头段时已重新编码，没有头段时不在选择范围内。
                    if (plan.DropLeading && pts < plan.CopyStartPts)
                    {
                        writer.SkipLeading(new ReadOnlySpan<byte>(packet->data, packet->size));
                        return;
                    }
                    // 开放 GOP 的尾段前导画面参考复制区最后一个 GOP，复制时同步送入解码器预热。
                    warming |= pts == plan.TailWarmupPts;
                    if (warming)
                        DecodeAndEncode(packet, plan.TailDisplayStart);
                    CopyPacket(packet);
                    return;
                }
            }
            if (phase == Phase.Tail)
                DecodeAndEncode(packet, plan.TailDisplayStart);
        }

        void CloseHead()
        {
            DecodeAndEncode(null, plan.StartPts);
            FinishEncoder(plan.CopyDisplayStart);
            FF.avcodec_flush_buffers(decoder);
        }

        void CopyPacket(AVPacket* packet)
        {
            var pts = packet->pts;
            var start = pts == plan.CopyStartPts;
            var (randomAccess, _, candidate) = context.Classify(new ReadOnlySpan<byte>(packet->data, packet->size), (packet->flags & 1) != 0);
            // 普通 I 帧只在作为复制起点（改写为 IDR）时视为随机访问点，其余仍按原样复制。
            randomAccess &= !candidate || start;
            // 定位后缺少 DTS 的包按上一包顺推；复制区首包使用规划时回推得到的 DTS。
            var dts = packet->dts != FF.AV_NOPTS_VALUE ? packet->dts
                : nextCopyDts != FF.AV_NOPTS_VALUE ? nextCopyDts
                : start ? plan.CopyStartDts
                : throw new FrameAccurateExportException(FrameAccurateExportError.NonMonotonicTimestamp, "dts");
            nextCopyDts = dts + (packet->duration > 0 ? packet->duration : FrameDuration(context));
            lastVideoEnd = Math.Max(lastVideoEnd, pts + Math.Max(0, packet->duration));
            // H.264 的 TS 输出在每个复制的 IDR 前确保带内参数集；HEVC 在复制区首包前重发原始参数集，
            // 恢复被重编码段带内重新定义的 0 号参数集；MPEG-2 在复制区首包前补原始序列头（量化矩阵等）。
            var insert = !context.Nal ? start
                : randomAccess && (writer.InBandParameterSets || context.Hevc && start);
            writer.WriteCopied(packet, dts, insert, closeGop: start && plan.DropLeading);
            copiedPackets++;
        }

        void HandleOther(AVPacket* packet)
        {
            var stream = rawInput->streams[packet->stream_index];
            var timeBase = stream->time_base;
            var outputIndex = writer.Map.Output[packet->stream_index];
            if (packet->pts == FF.AV_NOPTS_VALUE)
            {
                if (packet->dts == FF.AV_NOPTS_VALUE)
                    return;
                packet->pts = packet->dts;
            }
            if (packet->dts == FF.AV_NOPTS_VALUE)
                packet->dts = packet->pts;
            var subtitle = stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_SUBTITLE;
            if (plan.EndExclusive != long.MaxValue && CompareTime(packet->pts, timeBase, plan.EndExclusive, videoTimeBase) >= 0)
            {
                finishedStreams.Add(packet->stream_index);
                return;
            }
            var startsBefore = CompareTime(packet->pts, timeBase, plan.StartPts, videoTimeBase) < 0;
            if (subtitle && IsTextSubtitle(stream->codecpar->codec_id))
            {
                // 文字字幕裁剪到选择范围，规则与封装导出相同。
                var endTicks = packet->pts + Math.Max(0, packet->duration);
                if (CompareTime(endTicks, timeBase, plan.StartPts, videoTimeBase) <= 0)
                    return;
                var clipStart = startsBefore ? Rescale(plan.StartPts, videoTimeBase, timeBase) : packet->pts;
                var clipEnd = plan.EndExclusive == long.MaxValue ? endTicks
                    : Math.Min(endTicks, Rescale(plan.EndExclusive, videoTimeBase, timeBase));
                if (stream->codecpar->codec_id is AVCodecID.AV_CODEC_ID_ASS or AVCodecID.AV_CODEC_ID_SSA &&
                    (clipStart > packet->pts || clipEnd < endTicks) && RemuxExportService.HasTimedAssEffects(packet))
                    throw new FrameAccurateExportException(FrameAccurateExportError.UnsupportedTrack, $"#{packet->stream_index}");
                packet->pts = packet->dts = clipStart;
                packet->duration = Math.Max(0, clipEnd - clipStart);
            }
            else if (startsBefore)
                return;
            var outputTimeBase = writer.Output->streams[outputIndex]->time_base;
            packet->pts = writer.MapTime(packet->pts, timeBase, outputIndex);
            packet->dts = writer.MapTime(packet->dts, timeBase, outputIndex);
            if (stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && audioDelay != 0)
            {
                // 音频延迟：各片段的音轨统一平移；提前到输出开头之前的声音不输出。
                var shift = Rescale(audioDelay, Ms, outputTimeBase);
                if (packet->dts + shift < 0)
                    return;
                packet->pts += shift;
                packet->dts += shift;
            }
            packet->duration = Rescale(packet->duration, timeBase, outputTimeBase);
            packet->stream_index = outputIndex;
            var sourcePosition = packet->pos;
            packet->pos = -1;
            writer.Write(packet, outputIndex, strict: !subtitle, sourcePosition);
        }

        // 编码器在收到首帧时打开，HEVC 的 HDR10 静态元数据可能只在画面的 SEI 中。
        void StartEncoder(long delta)
        {
            encoderActive = true;
            encodeDelta = delta;
            firstEncodedPacket = true;
            pendingDurations.Clear();
            encodedPts.Clear();
            encodedOutput = 0;
        }

        void DecodeAndEncode(AVPacket* packet, long lower)
        {
            var result = FF.avcodec_send_packet(decoder, packet);
            if (result < 0 && result != FF.AVERROR_EOF)
                throw new FrameAccurateExportException(FrameAccurateExportError.IncompatibleStream,
                    FFmpegException.FromErrorCode(result).Message);
            while (true)
            {
                result = FF.avcodec_receive_frame(decoder, frame);
                if (result == FF.AVERROR_EAGAIN)
                    return;
                if (result == FF.AVERROR_EOF)
                {
                    while (reordered.Count > 0)
                        EncodeNext(lower);
                    return;
                }
                FFmpegException.Check(result);
                try
                {
                    AVFrame* raw = frame;
                    var pts = raw->pts != FF.AV_NOPTS_VALUE ? raw->pts : raw->best_effort_timestamp;
                    if (pts < lower)
                        continue;
                    if (phase == Phase.Done)
                    {
                        // 已写完终点：终点之前的画面此时才输出，说明晚于重排序缓冲，不能静默丢弃。
                        if (plan.EndPts is { } finalPts && pts <= finalPts)
                            throw new FrameAccurateExportException(FrameAccurateExportError.NonMonotonicTimestamp, "decoded");
                        continue;
                    }
                    if (reordered.Contains(pts) || encodedPts.Count > 0 && pts <= encodedPts[^1])
                        throw new FrameAccurateExportException(FrameAccurateExportError.NonMonotonicTimestamp, "decoded");
                    reordered.Add(pts, frame);
                }
                finally { frame.Unref(); }
                while (reordered.Count > ReorderedFrames.Depth && phase != Phase.Done)
                    EncodeNext(lower);
            }
        }

        // 按显示时间取出最早的解码画面送入编码器。
        void EncodeNext(long lower)
        {
            var (pts, next) = reordered.TakeFirst();
            using (next)
            {
                if (phase == Phase.Done)
                    return;
                AVFrame* raw = next;
                var upperExclusive = phase is Phase.Head or Phase.Leading && plan.HasCopy ? plan.CopyDisplayStart
                    : plan.EndPts is { } endPts ? endPts + 1 : long.MaxValue;
                if (pts >= upperExclusive)
                {
                    // 按显示时间取出：出现晚于终点的画面后，终点之前已到达的画面已全部编码。
                    if (phase == Phase.Tail || phase == Phase.Head && !plan.HasCopy)
                    {
                        FinishEncoder(plan.EndExclusive);
                        phase = Phase.Done;
                        reordered.Clear();
                    }
                    return;
                }
                if (encodedPts.Count > 0)
                {
                    pendingDurations[encodedPts[^1]] = pts - encodedPts[^1];
                    DrainEncoded();
                }
                else if (pts != lower)
                    throw new FrameAccurateExportException(FrameAccurateExportError.FrameNotFound, lower.ToString());
                encodedPts.Add(pts);
                // MPEG-2 编码器只接受标准帧率的时间基，按帧序号送入；输出包按顺序对应源 PTS。
                raw->pts = context.Mpeg2 ? encodedPts.Count - 1 : pts;
                if (encoder is null)
                {
                    encoder = FrameAccurateVideoEncoder.Open(video, context.ParameterSetId, context.Hevc, context.HighTier,
                        context.FieldOrder, context.SourceBitrate, raw);
                    if (!encoder.ParameterSets.SequenceEqual(context.EncodedParameterSets, ByteArrayComparer.Instance))
                        throw new FrameAccurateExportException(FrameAccurateExportError.IncompatibleStream, "encoder parameter sets");
                }
                encoder.Send(raw, WriteEncoded);
                encodedFrames++;
            }
        }

        void FinishEncoder(long endExclusive)
        {
            if (!encoderActive)
                return;
            if (encoder is null || encodedPts.Count == 0)
                throw new FrameAccurateExportException(FrameAccurateExportError.FrameNotFound, plan.StartPts.ToString());
            pendingDurations[encodedPts[^1]] = Math.Max(1, endExclusive - encodedPts[^1]);
            encoder.Send(null, WriteEncoded);
            encoder.Dispose();
            encoder = null;
            encoderActive = false;
            DrainEncoded();
            if (encodedQueue.Count > 0)
                throw new FrameAccurateExportException(FrameAccurateExportError.EncodeFailed, "pending packets");
        }

        long LastEncodedEnd() => encodedPts.Count == 0 ? plan.StartPts
            : encodedPts[^1] + Math.Max(1, EstimateEnd(context, [], encodedPts[^1]) - encodedPts[^1]);

        // 编码输出可能早于下一帧到达，此时显示时长未知；排队到时长确定后再写出。
        // 不使用 B 帧，输出顺序等于输入顺序，按序取回源 PTS。
        void WriteEncoded(AVPacket* packet)
        {
            if (encodedOutput >= encodedPts.Count)
                throw new FrameAccurateExportException(FrameAccurateExportError.EncodeFailed, "unexpected packet");
            var sourcePts = encodedPts[encodedOutput++];
            var data = new ReadOnlySpan<byte>(packet->data, packet->size);
            // H.264 的重编码参数集在全局头中，TS 需带内补到首个 IDR；HEVC 编码器已在码流中输出。
            List<byte[]>? prefix = writer.InBandParameterSets && firstEncodedPacket && context.Nal && !context.Hevc
                ? context.EncodedParameterSets : null;
            // MPEG-2 重编码段接在已写出的画面之后：先结束上一序列，新序列从编码器输出的序列头开始。
            var restart = context.Mpeg2 && firstEncodedPacket && writer.LastVideoDts != FF.AV_NOPTS_VALUE;
            // HEVC MP4：重编码段首包带内的 VPS/SPS/PPS 作为新的样本描述。
            List<byte[]>? sampleEntry = null;
            if (writer.SampleEntries && firstEncodedPacket)
            {
                sampleEntry = [];
                foreach (var range in VideoBitstream.SplitNals(data, 0))
                    VideoBitstream.AddParameterSet(data[range], true, sampleEntry);
                if (!sampleEntry.Any(set => VideoBitstream.IsSps(VideoBitstream.NalType(set, true), true)))
                    throw new FrameAccurateExportException(FrameAccurateExportError.EncodeFailed, "parameter sets");
            }
            firstEncodedPacket = false;
            var queued = new Packet();
            try
            {
                AVPacket* raw = queued;
                if (restart)
                {
                    FFmpegException.Check(FF.av_new_packet(raw, data.Length + SequenceEndCode.Length));
                    var target = new Span<byte>(raw->data, raw->size);
                    SequenceEndCode.CopyTo(target);
                    data.CopyTo(target[SequenceEndCode.Length..]);
                    raw->flags = packet->flags;
                }
                else if (prefix is null)
                {
                    FFmpegException.Check(FF.av_packet_ref(raw, packet));
                    // 只保留压缩数据与标志，编码器附带的统计等侧数据不写入输出。
                    FF.av_packet_side_data_free(&raw->side_data, &raw->side_data_elems);
                }
                else
                {
                    var size = VideoBitstream.AnnexBSize(data, 0, prefix);
                    FFmpegException.Check(FF.av_new_packet(raw, size));
                    VideoBitstream.WriteAnnexB(data, 0, false, prefix, new Span<byte>(raw->data, size));
                    raw->flags = packet->flags;
                }
                encodedQueue.Enqueue((queued, sourcePts, sampleEntry));
            }
            catch
            {
                queued.Dispose();
                throw;
            }
            DrainEncoded();
        }

        void DrainEncoded()
        {
            while (encodedQueue.TryPeek(out var item) && pendingDurations.Remove(item.Pts, out var duration))
            {
                encodedQueue.Dequeue();
                using (item.Packet)
                    writer.WriteOwned(item.Packet, item.Pts, item.Pts - encodeDelta, duration, item.SampleEntry);
                lastVideoEnd = Math.Max(lastVideoEnd, item.Pts + duration);
            }
        }
    }
}
