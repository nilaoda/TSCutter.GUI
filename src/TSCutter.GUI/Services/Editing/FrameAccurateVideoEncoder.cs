using System;
using System.Collections.Generic;
using System.Globalization;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Utils.Media;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Services.Editing;

internal unsafe delegate void EncodedPacketHandler(AVPacket* packet);

/// <summary>杜比视界配置记录中与帧精确剪辑有关的字段。</summary>
internal readonly record struct DolbyVisionRecord(byte Profile, bool Rpu, bool EnhancementLayer, bool BaseLayer,
    byte Compatibility, byte Compression);

/// <summary>
/// 一段边界画面的编码会话，所有会话使用同一配置。不使用 B 帧，编码顺序等于显示顺序，
/// 解码时间戳由调用方按相邻复制区推导。
/// H.264：参数集编号避开原始码流已用的编号，两组参数集可同时放入 avcC 或带内传输。
/// HEVC：x265 只能使用 0 号参数集，改为在每个 IDR 前带内重发参数集（新的编码视频序列可重新定义参数集）。
/// </summary>
internal sealed unsafe class FrameAccurateVideoEncoder : IDisposable
{
    private const int AV_CODEC_FLAG_INTERLACED_DCT = 1 << 18;
    private const int AV_CODEC_FLAG_CLOSED_GOP = 1 << 31;
    private const int AV_CODEC_FLAG_INTERLACED_ME = 1 << 29;
    private AVCodecContext* context;
    private AVFieldOrder fieldOrder;
    private bool dolbyVision;
    private readonly Packet output = new();

    /// <summary>H.264 编码器全局头中的参数集；HEVC 为空，参数集在码流中。</summary>
    public List<byte[]> ParameterSets { get; } = [];

    private FrameAccurateVideoEncoder(AVCodecContext* context) => this.context = context;

    private static string EncoderName(AVCodecID codec) => codec switch
    {
        AVCodecID.AV_CODEC_ID_HEVC => "libx265",
        AVCodecID.AV_CODEC_ID_MPEG2VIDEO => "mpeg2video",
        // AV1 优先用较快的 SVT-AV1，运行库没有时用 libaom。
        AVCodecID.AV_CODEC_ID_AV1 => FF.avcodec_find_encoder_by_name("libsvtav1") != null ? "libsvtav1" : "libaom-av1",
        AVCodecID.AV_CODEC_ID_VP9 => "libvpx-vp9",
        _ => "libx264"
    };

    /// <summary>编码器能否按原像素格式编码（例如 x264 是否带 10 位支持）；编码器不存在时为 false。</summary>
    public static bool SupportsPixelFormat(AVCodecID codecId, AVPixelFormat format)
    {
        var codec = FF.avcodec_find_encoder_by_name(EncoderName(codecId));
        if (codec == null)
            return false;
        void* values = null;
        var count = 0;
        if (FF.avcodec_get_supported_config(null, codec, 0, 0, &values, &count) < 0)
            return false;
        // 返回空列表表示编码器不限制像素格式。
        return values == null || new ReadOnlySpan<AVPixelFormat>(values, count).Contains(format);
    }

    /// <summary>
    /// 逐行刷新衔接的 H.264 头段：编码输出要改写到源参数集上，编码器按源 PPS 中影响解码的设置编码。
    /// </summary>
    internal sealed record H264RefreshTarget(H264RefreshSplice.SequenceSyntax Sps, H264RefreshSplice.PictureSyntax Pps);

    /// <summary>
    /// 按源视频参数打开编码器；源码流参数不在编码器支持范围时抛出。
    /// refresh 不为空时按逐行刷新衔接的要求编码 H.264 头段，并核对编码器的参数集与源在解码上等价
    /// （x264 的心理视觉优化会改动色度量化偏移，不一致时按差值重新打开一次）。
    /// </summary>
    /// <param name="sourceBitrate">切点附近原视频的码率（bit/s，0 表示未知），用于限定重编码码率上限。</param>
    /// <param name="mpeg2Sequence">MPEG-2 源的序列头，low_delay 等按它设置；为空时取流参数中的附加数据。</param>
    public static FrameAccurateVideoEncoder Open(AVStream* stream, int parameterSetId, bool hevc, bool highTier,
        AVFieldOrder fieldOrder, long sourceBitrate, AVFrame* firstFrame = null, H264RefreshTarget? refresh = null,
        VideoBitstream.Mpeg2SequenceUnits? mpeg2Sequence = null)
    {
        if (refresh is null)
            return OpenCore(stream, parameterSetId, hevc, highTier, fieldOrder, sourceBitrate, firstFrame, null, 0,
                mpeg2Sequence);
        var chromaOffset = refresh.Pps.ChromaQpOffset;
        for (var attempt = 0; ; attempt++)
        {
            var encoder = OpenCore(stream, parameterSetId, hevc, highTier, fieldOrder, sourceBitrate, firstFrame, refresh,
                chromaOffset, mpeg2Sequence);
            var sps = H264RefreshSplice.ParseSps(encoder.ParameterSets.Find(set => (set[0] & 0x1F) == 7)!);
            var pps = H264RefreshSplice.ParsePps(encoder.ParameterSets.Find(set => (set[0] & 0x1F) == 8)!);
            var mismatch = sps is null || pps is null ? "parameter sets"
                : H264RefreshSplice.Mismatch(refresh.Sps, refresh.Pps, sps, pps);
            if (mismatch is null)
            {
                encoder.RefreshPps = pps;
                return encoder;
            }
            encoder.Dispose();
            if (attempt > 0 || pps is null || mismatch != "chroma QP offset")
                throw new FrameAccurateExportException(FrameAccurateExportError.Unsupported, mismatch,
                    FrameAccurateSupport.GradualRefresh);
            chromaOffset += refresh.Pps.ChromaQpOffset - pps.ChromaQpOffset;
        }
    }

    /// <summary>逐行刷新衔接的头段编码器实际使用的 PPS（切片头改写时换算量化参数）。</summary>
    public H264RefreshSplice.PictureSyntax? RefreshPps { get; private set; }

    private static FrameAccurateVideoEncoder OpenCore(AVStream* stream, int parameterSetId, bool hevc, bool highTier,
        AVFieldOrder fieldOrder, long sourceBitrate, AVFrame* firstFrame, H264RefreshTarget? refresh, int chromaOffset,
        VideoBitstream.Mpeg2SequenceUnits? mpeg2Sequence)
    {
        var codecId = stream->codecpar->codec_id;
        var codec = FF.avcodec_find_encoder_by_name(EncoderName(codecId));
        if (codec == null)
            throw new FrameAccurateExportException(FrameAccurateExportError.Unsupported, EncoderName(codecId), FrameAccurateSupport.Encoder);
        var context = FF.avcodec_alloc_context3(codec);
        if (context == null)
            throw new OutOfMemoryException();
        var encoder = new FrameAccurateVideoEncoder(context);
        try
        {
            var source = stream->codecpar;
            context->width = source->width;
            context->height = source->height;
            // 逐行刷新衔接的 H.264 头段按完整的编码画面（含裁剪区）编码，切片头改写后沿用源 SPS 的裁剪。
            if (refresh is not null && firstFrame != null)
            {
                context->width = firstFrame->width;
                context->height = firstFrame->height;
            }
            context->pix_fmt = (AVPixelFormat)source->format;
            context->time_base = stream->time_base;
            context->pkt_timebase = stream->time_base;
            context->framerate = FrameRate(stream);
            context->sample_aspect_ratio = source->sample_aspect_ratio;
            context->color_range = source->color_range;
            context->color_primaries = source->color_primaries;
            context->color_trc = source->color_trc;
            context->colorspace = source->color_space;
            context->chroma_sample_location = source->chroma_location;
            context->field_order = fieldOrder;
            encoder.fieldOrder = fieldOrder;
            context->max_b_frames = 0;
            var options = context->priv_data;
            if (codecId == AVCodecID.AV_CODEC_ID_MPEG2VIDEO)
                ConfigureMpeg2(context, source, fieldOrder, sourceBitrate, mpeg2Sequence);
            else if (codecId == AVCodecID.AV_CODEC_ID_AV1)
                ConfigureAv1(context, options, EncoderName(codecId));
            else if (codecId == AVCodecID.AV_CODEC_ID_VP9)
                ConfigureVp9(context, options);
            else
            {
                // 重编码只在切点附近，时长很短：按接近无损的画质编码，码率受原码流级别上限约束。
                // H.264 用 slow 预设；4K HEVC 用 slow 太慢，保持 medium。
                Set(options, "preset", hevc ? "medium" : "slow");
                Set(options, "crf", hevc ? "14" : "10");
                if (hevc)
                    ConfigureHevc(options, source, highTier, firstFrame, RelativeCap(stream, sourceBitrate, 4));
                else
                    ConfigureH264(context, options, source, parameterSetId, fieldOrder, RelativeCap(stream, sourceBitrate, 4),
                        refresh, chromaOffset);
            }
            // 杜比视界：x265 由每帧解码出的杜比元数据重新生成 RPU。首帧元数据交给编码器推断配置，
            // 打开后核对推断出的配置与源一致。
            var sourceDovi = DolbyVisionConfiguration(source);
            if (sourceDovi != null)
            {
                Set(options, "dolbyvision", "1");
                encoder.dolbyVision = true;
                if (firstFrame != null &&
                    FF.av_frame_get_side_data(firstFrame, AVFrameSideDataType.AV_FRAME_DATA_DOVI_METADATA) is var metadata &&
                    metadata != null)
                    FFmpegException.Check(FF.av_frame_side_data_clone(&context->decoded_side_data,
                        &context->nb_decoded_side_data, metadata, 0));
            }
            var result = FF.avcodec_open2(context, codec, null);
            if (result < 0)
                throw new FrameAccurateExportException(FrameAccurateExportError.Unsupported,
                    FFmpegException.FromErrorCode(result).Message, FrameAccurateSupport.Encoder);
            if (sourceDovi is { } expected)
            {
                var encoded = DolbyVisionConfiguration(context->coded_side_data, context->nb_coded_side_data);
                if (encoded is not { } actual || actual.Profile != expected.Profile ||
                    actual.Compatibility != expected.Compatibility || actual.EnhancementLayer || !actual.Rpu)
                    throw new FrameAccurateExportException(FrameAccurateExportError.Unsupported,
                        encoded is { } value ? $"profile {value.Profile}.{value.Compatibility}" : "no DOVI configuration",
                        FrameAccurateSupport.DolbyVision);
            }
            if (codecId == AVCodecID.AV_CODEC_ID_H264)
            {
                VideoBitstream.ReadParameterSets(new ReadOnlySpan<byte>(context->extradata, context->extradata_size),
                    false, encoder.ParameterSets);
                if (encoder.ParameterSets.Count != 2)
                    throw new FrameAccurateExportException(FrameAccurateExportError.EncodeFailed, "parameter sets");
                // 重编码段不用 B 帧，但沿用原码流的重排序深度声明：解码器从重编码段进入带 B 帧的复制区时
                // 已有足够的重排序缓冲，不会丢掉第一个乱序输出的画面（广播编码器的 SPS 常不声明该深度）。
                var spsIndex = encoder.ParameterSets.FindIndex(set => (set[0] & 0x1F) == 7);
                try
                {
                    encoder.ParameterSets[spsIndex] = H264Splice.WithReorderDepth(encoder.ParameterSets[spsIndex],
                        stream->codecpar->video_delay);
                }
                catch (System.IO.InvalidDataException exception)
                {
                    throw new FrameAccurateExportException(FrameAccurateExportError.EncodeFailed, "SPS: " + exception.Message);
                }
            }
            return encoder;
        }
        catch
        {
            encoder.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 重编码码率上限（kbit/s）：切点附近原视频码率的若干倍，避免低码率片源的重编码段码率突增；
    /// 不低于按像素率估计的下限（每像素 0.15 bit），以免静止画面统计出的低码率把开头的 I 帧压得过狠。原码率未知时为 null。
    /// </summary>
    private static int? RelativeCap(AVStream* stream, long sourceBitrate, double factor)
    {
        if (sourceBitrate <= 0)
            return null;
        var rate = FrameRate(stream);
        var pixelsPerSecond = (double)stream->codecpar->width * stream->codecpar->height * rate.num / rate.den;
        return (int)Math.Min(int.MaxValue, Math.Ceiling(Math.Max(sourceBitrate * factor, pixelsPerSecond * 0.15) / 1000));
    }

    private static void ConfigureH264(AVCodecContext* context, void* options, AVCodecParameters* source, int parameterSetId,
        AVFieldOrder fieldOrder, int? cap, H264RefreshTarget? refresh, int chromaOffset)
    {
        context->flags |= FF.AV_CODEC_FLAG_GLOBAL_HEADER;
        if (H264ProfileName(source->profile) is { } profile)
            Set(options, "profile", profile);
        var parameters = $"sps-id={parameterSetId}:bframes=0:keyint=infinite:scenecut=0:repeat-headers=0";
        if (refresh is { } target)
        {
            // 逐行刷新衔接的头段沿用源参数集：熵编码、8x8 变换、色度量化偏移、受限帧内预测与源 PPS 一致，
            // 参考帧数不超过源 SPS（滑动窗口一致），不用加权预测。
            parameters += string.Create(CultureInfo.InvariantCulture,
                $":ref={Math.Clamp(target.Sps.MaxNumRefFrames, 1, 16)}:weightp=0:cabac={(target.Pps.Cabac ? 1 : 0)}" +
                $":8x8dct={(target.Pps.Transform8x8 ? 1 : 0)}:chroma-qp-offset={chromaOffset}");
            if (target.Pps.ConstrainedIntraPred)
                parameters += ":constrained-intra=1";
        }
        // 隔行素材以 MBAFF 帧编码，保持原场序；Baseline 不支持隔行。逐行刷新衔接时只有源本身是 MBAFF 才用
        // （源是场编码而不用 MBAFF 时，帧画面按逐行方式编码，解码方式与源 SPS 一致）。
        if (fieldOrder != AVFieldOrder.AV_FIELD_PROGRESSIVE && (refresh is null || refresh.Sps.Mbaff))
        {
            if (source->profile is 66 or 578)
                throw new FrameAccurateExportException(FrameAccurateExportError.Unsupported, "", FrameAccurateSupport.Interlaced);
            context->flags |= AV_CODEC_FLAG_INTERLACED_DCT | AV_CODEC_FLAG_INTERLACED_ME;
            parameters += fieldOrder == AVFieldOrder.AV_FIELD_TT ? ":tff=1" : ":bff=1";
        }
        if (source->level is 9 or >= 10 and <= 62)
        {
            Set(options, "level", source->level == 9 ? "1b" : Level(source->level / 10, source->level % 10));
            // 边界画面不能超出原码流声明的级别，否则同一轨道内的解码缓冲要求会变化。
            if (H264LevelLimits(source->level) is { } limits)
            {
                // 表 A-2 的 cpbBrVclFactor 与 Baseline/Main 之比：High 1.25、High 10 为 3、High 4:2:2 为 4。
                var factor = source->profile switch { 122 => 4.0, 110 => 3.0, 100 => 1.25, _ => 1.0 };
                var maxRate = (int)(limits.MaxBitrate * factor);
                var buffer = (int)(limits.MaxBuffer * factor);
                parameters += cap is { } value ? Vbv(Math.Min(maxRate, value), Math.Min(buffer, value)) : Vbv(maxRate, buffer);
            }
            else if (cap is { } value)
                parameters += Vbv(value, value);
        }
        else if (cap is { } value)
            parameters += Vbv(value, value);
        Set(options, "x264-params", parameters);
    }

    private static void ConfigureHevc(void* options, AVCodecParameters* source, bool highTier, AVFrame* frame, int? cap)
    {
        // 4:2:2（格式范围扩展）8 位与 10 位都以 Main 4:2:2 10 档次编码。
        var chroma422 = (AVPixelFormat)source->format is AVPixelFormat.AV_PIX_FMT_YUV422P or AVPixelFormat.AV_PIX_FMT_YUVJ422P
            or AVPixelFormat.AV_PIX_FMT_YUV422P10LE;
        var tenBit = (AVPixelFormat)source->format == AVPixelFormat.AV_PIX_FMT_YUV420P10LE;
        Set(options, "profile", chroma422 ? "main422-10" : tenBit ? "main10" : "main");
        // 关闭 SAO：高画质下 SAO 会抹平细节与颗粒。
        var parameters = "bframes=0:keyint=-1:min-keyint=1:scenecut=0:open-gop=0:repeat-headers=1:info=0:log-level=error:no-sao=1";
        if (source->level > 0 && HevcLevelLimits(source->level, highTier) is { } baseLimits)
        {
            // 表 A.9 的 CpbBrVclFactor：Main/Main 10 为 1000，Main 4:2:2 10 为 1667。
            var limits = chroma422 ? (int)Math.Min(int.MaxValue, baseLimits * 1667L / 1000) : baseLimits;
            parameters += $":level-idc={Level(source->level / 30, source->level % 30 / 3)}:high-tier={(highTier ? 1 : 0)}"
                          + (cap is { } value ? Vbv(Math.Min(limits, value), Math.Min(limits, value)) : Vbv(limits, limits));
        }
        else if (cap is { } value)
            parameters += Vbv(value, value);
        // HDR10 静态元数据随参数写入 SEI：优先取流参数，缺失时取首帧解码出的 SEI；颜色描述由编码上下文传入。
        AVMasteringDisplayMetadata* mastering = null;
        AVContentLightMetadata* light = null;
        for (var i = 0; i < source->nb_coded_side_data; i++)
        {
            var side = source->coded_side_data[i];
            if (side.type == AVPacketSideDataType.AV_PKT_DATA_MASTERING_DISPLAY_METADATA &&
                side.size >= (ulong)sizeof(AVMasteringDisplayMetadata))
                mastering = (AVMasteringDisplayMetadata*)side.data;
            else if (side.type == AVPacketSideDataType.AV_PKT_DATA_CONTENT_LIGHT_LEVEL &&
                     side.size >= (ulong)sizeof(AVContentLightMetadata))
                light = (AVContentLightMetadata*)side.data;
        }
        if (frame != null && mastering == null &&
            FF.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA) is var frameMastering &&
            frameMastering != null)
            mastering = (AVMasteringDisplayMetadata*)frameMastering->data;
        if (frame != null && light == null &&
            FF.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL) is var frameLight &&
            frameLight != null)
            light = (AVContentLightMetadata*)frameLight->data;
        if (mastering != null && mastering->has_primaries != 0 && mastering->has_luminance != 0)
        {
            long Chroma(AVRational value) => (long)Math.Round(value.num * 50000.0 / value.den);
            long Luma(AVRational value) => (long)Math.Round(value.num * 10000.0 / value.den);
            var p = mastering->display_primaries;
            parameters += string.Create(CultureInfo.InvariantCulture,
                $":hdr10=1:master-display=G({Chroma(p[1][0])},{Chroma(p[1][1])})B({Chroma(p[2][0])},{Chroma(p[2][1])})" +
                $"R({Chroma(p[0][0])},{Chroma(p[0][1])})WP({Chroma(mastering->white_point[0])},{Chroma(mastering->white_point[1])})" +
                $"L({Luma(mastering->max_luminance)},{Luma(mastering->min_luminance)})");
        }
        if (light != null)
            parameters += string.Create(CultureInfo.InvariantCulture, $":max-cll={light->MaxCLL},{light->MaxFALL}");
        Set(options, "x265-params", parameters);
    }

    /// <summary>
    /// AV1：恒定画质、不插入后续关键帧（重编码段只在开头有一个关键帧，衔接处由复制区的关键帧开始新的序列）。
    /// 不设全局头：序列头随关键帧写在码流中，复制区与重编码段的序列头各自生效。
    /// libaom 用实时模式（4K 时质量模式约每秒 3 帧，实时模式约 20 帧），按分辨率分块并行；SVT-AV1 用预设 8。
    /// </summary>
    private static void ConfigureAv1(AVCodecContext* context, void* options, string encoder)
    {
        context->gop_size = 1_000_000;
        context->keyint_min = 1_000_000;
        context->bit_rate = 0;
        context->thread_count = 0;
        if (encoder == "libsvtav1")
        {
            Set(options, "preset", "8");
            Set(options, "crf", "16");
            Set(options, "svtav1-params", "scd=0");
            return;
        }
        Set(options, "usage", "realtime");
        Set(options, "cpu-used", "7");
        Set(options, "row-mt", "1");
        Set(options, "crf", "12");
        var columns = context->width >= 2560 ? 2 : context->width >= 1280 ? 1 : 0;
        Set(options, "tile-columns", columns.ToString(CultureInfo.InvariantCulture));
        Set(options, "tile-rows", (columns > 1 ? 1 : 0).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// VP9：恒定画质、只在开头有一个关键帧（衔接处由复制区的关键帧开始），不使用隐藏的替代参考帧（不产生超帧）。
    /// libvpx 实时模式 cpu-used 7：4K 约每秒 50 帧，画质（crf 12）PSNR 约 52 dB；质量模式 4K 每秒只有几帧。
    /// 按宽度分块（每块不窄于 256 像素）并行，行级多线程。
    /// </summary>
    private static void ConfigureVp9(AVCodecContext* context, void* options)
    {
        context->gop_size = 1_000_000;
        context->keyint_min = 1_000_000;
        context->bit_rate = 0;
        context->thread_count = 0;
        Set(options, "deadline", "realtime");
        Set(options, "cpu-used", "7");
        Set(options, "crf", "12");
        Set(options, "lag-in-frames", "0");
        Set(options, "auto-alt-ref", "0");
        Set(options, "row-mt", "1");
        var columns = context->width >= 3840 ? 3 : context->width >= 1920 ? 2 : context->width >= 1280 ? 1 : 0;
        Set(options, "tile-columns", columns.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// MPEG-2 只有标准帧率，时间基取帧率倒数（调用方按帧序号送入）。单个闭合 GOP，序列头随首个 I 帧输出。
    /// FFmpeg 的 MPEG-2 编码器只编 I/P 帧，在原码流声明的码率下画质明显不如广播编码器，因此按固定量化（qscale 3）编码，
    /// 码率上限取切点附近实际码率的 2 倍与原码流声明上限中的较大者（DVD 等低码率片源基本停在原声明上限），且不超过档次/级别上限；
    /// VBV 缓冲沿用原码流声明值。重编码段的序列头参数与复制段不同，写出时在切换处插入 sequence_end_code 开始新序列。
    /// 级别未知时退回原码流声明的 VBV 参数。
    /// </summary>
    private static void ConfigureMpeg2(AVCodecContext* context, AVCodecParameters* source, AVFieldOrder fieldOrder,
        long sourceBitrate, VideoBitstream.Mpeg2SequenceUnits? sequence)
    {
        // 源声明 low_delay（没有 B 帧）时重编码段同样声明，解码器在重编码段与复制区之间按同一方式输出画面。
        sequence ??= source->extradata == null ? null
            : VideoBitstream.ReadMpeg2Sequence(new ReadOnlySpan<byte>(source->extradata, source->extradata_size));
        if (sequence?.LowDelay == true)
            context->flags |= AV_CODEC_FLAG_LOW_DELAY;
        var rate = context->framerate;
        context->time_base = new AVRational { num = rate.den, den = rate.num };
        // mpeg2video 的 GOP 上限为 600 帧；超过时插入新的闭合 GOP。
        context->gop_size = 600;
        context->flags |= AV_CODEC_FLAG_CLOSED_GOP;
        context->profile = source->profile;
        context->level = source->level;
        if (fieldOrder != AVFieldOrder.AV_FIELD_PROGRESSIVE)
            context->flags |= AV_CODEC_FLAG_INTERLACED_DCT | AV_CODEC_FLAG_INTERLACED_ME;
        long maxRate = 0, buffer = 0;
        for (var i = 0; i < source->nb_coded_side_data; i++)
        {
            var side = source->coded_side_data[i];
            if (side.type == AVPacketSideDataType.AV_PKT_DATA_CPB_PROPERTIES && side.size >= (ulong)sizeof(AVCPBProperties))
            {
                var cpb = (AVCPBProperties*)side.data;
                (maxRate, buffer) = (cpb->max_bitrate, cpb->buffer_size);
            }
        }
        if (Mpeg2LevelLimits(source->profile, source->level) is { } limits)
        {
            context->flags |= AV_CODEC_FLAG_QSCALE;
            context->global_quality = 3 * FF_QP2LAMBDA;
            var cap = sourceBitrate > 0 ? Math.Max(2 * sourceBitrate, maxRate) : maxRate > 0 ? 2 * maxRate : limits.MaxRate;
            context->rc_max_rate = Math.Min(limits.MaxRate, cap);
            context->rc_buffer_size = buffer > 0 ? (int)Math.Min(limits.Buffer, buffer) : limits.Buffer;
        }
        else
        {
            if (maxRate <= 0)
                maxRate = source->bit_rate;
            if (maxRate <= 0 || buffer <= 0)
                throw new FrameAccurateExportException(FrameAccurateExportError.Unsupported, "VBV", FrameAccurateSupport.Codec);
            context->bit_rate = maxRate;
            context->rc_max_rate = maxRate;
            context->rc_buffer_size = (int)Math.Min(int.MaxValue, buffer);
        }
        context->rc_initial_buffer_occupancy = context->rc_buffer_size * 3 / 4;
        // 闭合 GOP 要求关闭场景切换检测；重编码段本就只需一个 I 帧。
        Set(context->priv_data, "sc_threshold", "1000000000");
        // 非线性量化表（qmax 上限 28）在高码率下保留更多细节，仍可量化到足够粗以满足 VBV。
        context->qmax = 28;
        Set(context->priv_data, "intra_vlc", "1");
        Set(context->priv_data, "non_linear_quant", "1");
        // 率失真宏块决策、trellis 量化、SATD 运动搜索比较、10 位帧内 DC 精度（Main 档次允许）。
        Set(context, "mbd", "rd");
        Set(context, "trellis", "2");
        Set(context, "cmp", "satd");
        Set(context, "subcmp", "satd");
        Set(context->priv_data, "intra_dc_precision", "2");
    }

    private const int AV_CODEC_FLAG_QSCALE = 1 << 1;
    private const int AV_CODEC_FLAG_LOW_DELAY = 1 << 19;
    private const int FF_QP2LAMBDA = 118;

    /// <summary>
    /// MPEG-2 各档次、级别的最大码率（bit/s）与 VBV 缓冲（bit）。Main/Simple 档次的级别 4/6/8/10 为 High/High 1440/Main/Low；
    /// 4:2:2 档次只有 High 与 Main 两级（级别 2/5）。
    /// </summary>
    private static (long MaxRate, int Buffer)? Mpeg2LevelLimits(int profile, int level) => profile == 0
        ? level switch
        {
            2 => (300_000_000, 47_185_920),
            5 => (50_000_000, 9_437_184),
            _ => null
        }
        : level switch
        {
            4 => (80_000_000, 9_781_248),
            6 => (60_000_000, 7_340_032),
            8 => (15_000_000, 1_835_008),
            10 => (4_000_000, 475_136),
            _ => null
        };

    /// <summary>
    /// 是否为 MPEG-2 4:2:2 档次。档次与级别字节带转义位，解码器去掉转义位后与多视点档次同为 profile 0，
    /// 只能按级别区分：4:2:2 档次为 2（High）与 5（Main），多视点档次为 10/11/13/14。
    /// </summary>
    internal static bool IsMpeg2Profile422(AVCodecParameters* parameters) =>
        parameters->profile == 0 && parameters->level is 2 or 5;

    /// <summary>流参数中的杜比视界配置记录；没有时返回 null。</summary>
    internal static DolbyVisionRecord? DolbyVisionConfiguration(AVCodecParameters* parameters) =>
        DolbyVisionConfiguration(parameters->coded_side_data, parameters->nb_coded_side_data);

    // FFmpeg 9 的 AVDOVIDecoderConfigurationRecord 由独立字节字段组成：版本主/次、profile、level、
    // rpu/el/bl 标志、基础层兼容编号，以及新版本增加的元数据压缩方式。
    private static DolbyVisionRecord? DolbyVisionConfiguration(AVPacketSideData* sideData, int count)
    {
        for (var i = 0; sideData != null && i < count; i++)
        {
            var side = sideData[i];
            if (side.type != AVPacketSideDataType.AV_PKT_DATA_DOVI_CONF || side.data == null || side.size < 8)
                continue;
            var data = side.data;
            return new DolbyVisionRecord(data[2], data[4] != 0, data[5] != 0, data[6] != 0, data[7],
                side.size >= 9 ? data[8] : (byte)0);
        }
        return null;
    }

    private static string Level(int major, int minor) =>
        major.ToString(CultureInfo.InvariantCulture) + "." + minor.ToString(CultureInfo.InvariantCulture);

    private static string Vbv(int maxrate, int bufsize) =>
        string.Create(CultureInfo.InvariantCulture, $":vbv-maxrate={maxrate}:vbv-bufsize={bufsize}");

    internal static AVRational FrameRate(AVStream* stream)
    {
        var rate = stream->avg_frame_rate.num > 0 && stream->avg_frame_rate.den > 0
            ? stream->avg_frame_rate : stream->r_frame_rate;
        return rate.num > 0 && rate.den > 0 ? rate : new AVRational { num = 25, den = 1 };
    }

    private static void Set(void* options, string name, string value)
    {
        var result = FF.av_opt_set(options, name, value, 0);
        if (result < 0)
            throw new FrameAccurateExportException(FrameAccurateExportError.Unsupported,
                $"{name}={value}: {FFmpegException.FromErrorCode(result).Message}", FrameAccurateSupport.Encoder);
    }

    internal static string? H264ProfileName(int profile) => profile switch
    {
        66 or 578 => "baseline",
        77 => "main",
        100 => "high",
        122 => "high422",
        _ => null
    };

    // H.264 表 A-1 的 MaxBR（kbit/s）与 MaxCPB（kbit），为 Baseline/Main 的值。
    private static (int MaxBitrate, int MaxBuffer)? H264LevelLimits(int level) => level switch
    {
        9 => (128, 350), 10 => (64, 175), 11 => (192, 500), 12 => (384, 1000), 13 => (768, 2000),
        20 => (2000, 2000), 21 => (4000, 4000), 22 => (4000, 4000),
        30 => (10000, 10000), 31 => (14000, 14000), 32 => (20000, 20000),
        40 => (20000, 25000), 41 => (50000, 62500), 42 => (50000, 62500),
        50 => (135000, 135000), 51 => (240000, 240000), 52 => (240000, 240000),
        60 => (240000, 240000), 61 => (480000, 480000), 62 => (800000, 800000),
        _ => null
    };

    // HEVC 表 A.8 的 MaxBR（kbit/s，Main/Main 10 的 CpbBrVclFactor）；MaxCPB 数值与之相同。
    private static int? HevcLevelLimits(int level, bool highTier) => (level, highTier) switch
    {
        (30, _) => 128, (60, _) => 1500, (63, _) => 3000, (90, _) => 6000, (93, _) => 10000,
        (120, false) => 12000, (123, false) => 20000, (150, false) => 25000, (153, false) => 40000,
        (156, false) => 60000, (180, false) => 60000, (183, false) => 120000, (186, false) => 240000,
        (120, true) => 30000, (123, true) => 50000, (150, true) => 100000, (153, true) => 160000,
        (156, true) => 240000, (180, true) => 240000, (183, true) => 480000, (186, true) => 800000,
        _ => null
    };

    /// <summary>送入解码帧；帧的显示时间戳保持源时间基。</summary>
    public void Send(AVFrame* frame, EncodedPacketHandler write)
    {
        if (frame != null)
        {
            if (frame->width != context->width || frame->height != context->height || frame->format != (int)context->pix_fmt)
                throw new FrameAccurateExportException(FrameAccurateExportError.IncompatibleStream,
                    $"{frame->width}x{frame->height} {(AVPixelFormat)frame->format}");
            // 隔行画面的场序必须与编码设置一致；同一码流中途改变场序时不能保证输出场序不变。
            if (fieldOrder != AVFieldOrder.AV_FIELD_PROGRESSIVE &&
                FrameAccurateExportService.FieldOrder(frame) is var order && order != AVFieldOrder.AV_FIELD_PROGRESSIVE && order != fieldOrder)
                throw new FrameAccurateExportException(FrameAccurateExportError.IncompatibleStream, "field order");
            // 软 3:2 下拉（重复场）的显示时长无法在重编码画面中保持。
            if (frame->repeat_pict != 0)
                throw new FrameAccurateExportException(FrameAccurateExportError.Unsupported, "", FrameAccurateSupport.Pulldown);
            // 杜比视界素材的每一帧都必须带有可重新生成 RPU 的元数据；非杜比素材出现杜比元数据时不能静默丢弃。
            var hasDolbyMetadata = FF.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DOVI_METADATA) != null;
            if (dolbyVision ? !hasDolbyMetadata
                    : hasDolbyMetadata || FF.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DOVI_RPU_BUFFER) != null)
                throw new FrameAccurateExportException(FrameAccurateExportError.Unsupported, "", FrameAccurateSupport.DolbyVision);
            if (FF.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_PLUS) != null)
                throw new FrameAccurateExportException(FrameAccurateExportError.Unsupported, "", FrameAccurateSupport.DynamicHdr);
            // 解码帧带有原始画面类型，必须清除，否则编码器会照搬原 GOP 结构。
            frame->pict_type = AVPictureType.AV_PICTURE_TYPE_NONE;
            frame->flags &= ~FF.AV_FRAME_FLAG_KEY;
        }
        var result = FF.avcodec_send_frame(context, frame);
        if (result < 0)
            throw new FrameAccurateExportException(FrameAccurateExportError.EncodeFailed, FFmpegException.FromErrorCode(result).Message);
        while (true)
        {
            AVPacket* packet = output;
            result = FF.avcodec_receive_packet(context, packet);
            if (result == FF.AVERROR_EAGAIN || result == FF.AVERROR_EOF)
                return;
            if (result < 0)
                throw new FrameAccurateExportException(FrameAccurateExportError.EncodeFailed, FFmpegException.FromErrorCode(result).Message);
            try { write(packet); }
            finally { FF.av_packet_unref(packet); }
        }
    }

    public void Dispose()
    {
        output.Dispose();
        if (context != null)
        {
            var raw = context;
            FF.avcodec_free_context(&raw);
            context = null;
        }
    }
}
