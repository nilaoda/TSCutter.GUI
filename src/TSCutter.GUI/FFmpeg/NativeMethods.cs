// 接口签名参考 FFmpeg.AutoGen（MIT）；版权和修改说明见 THIRD_PARTY_NOTICES.md。
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.FFmpeg;

// 只声明项目实际使用的接口。LibraryImport 提前生成 UTF-8 编组代码，
// 支持裁剪后的 NativeAOT 发布，避免运行时生成绑定或初始化无关函数。
public static unsafe partial class NativeMethods
{
    public const long AV_NOPTS_VALUE = long.MinValue;
    public const int AV_TIME_BASE = 1_000_000;
    public const int AVERROR_EXIT = -1414092869;
    public const int AVERROR_EOF = -541478725;
    public const int AVERROR_OPTION_NOT_FOUND = -1414549496;
    // EAGAIN 来自平台 errno；macOS 为 35，Windows/Linux 为 11。
    public static int AVERROR_EAGAIN => OperatingSystem.IsMacOS() ? -35 : -11;
    public const int AV_FRAME_FLAG_KEY = 1 << 1;
    public const int AV_CODEC_FLAG_COPY_OPAQUE = 1 << 7;

    static NativeMethods() => NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly,
        (name, assembly, path) => name.StartsWith("tscutter.", StringComparison.Ordinal)
            ? FFmpegNativeBootstrapper.ResolveLibrary(name[9..]) : IntPtr.Zero);

    public static double av_q2d(AVRational value) => value.num / (double)value.den;

    [LibraryImport("tscutter.avformat", EntryPoint = "avformat_alloc_context", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVFormatContext* avformat_alloc_context();

    [LibraryImport("tscutter.avformat", EntryPoint = "av_find_input_format", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVInputFormat* av_find_input_format(string shortName);

    [LibraryImport("tscutter.avformat", EntryPoint = "avformat_open_input", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int avformat_open_input(AVFormatContext** @ps, string @url, AVInputFormat* @fmt, AVDictionary** @options);

    [LibraryImport("tscutter.avformat", EntryPoint = "avformat_close_input", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void avformat_close_input(AVFormatContext** @s);

    [LibraryImport("tscutter.avformat", EntryPoint = "avformat_find_stream_info", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int avformat_find_stream_info(AVFormatContext* @ic, AVDictionary** @options);

    [LibraryImport("tscutter.avformat", EntryPoint = "av_read_frame", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_read_frame(AVFormatContext* @s, AVPacket* @pkt);

    [LibraryImport("tscutter.avformat", EntryPoint = "av_seek_frame", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_seek_frame(AVFormatContext* @s, int @stream_index, long @timestamp, int @flags);

    [LibraryImport("tscutter.avformat", EntryPoint = "avformat_seek_file", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int avformat_seek_file(AVFormatContext* @s, int @stream_index, long @min_ts, long @ts, long @max_ts, int @flags);

    [LibraryImport("tscutter.avformat", EntryPoint = "avio_size", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial long avio_size(AVIOContext* @s);

    [LibraryImport("tscutter.avcodec", EntryPoint = "avcodec_alloc_context3", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVCodecContext* avcodec_alloc_context3(AVCodec* @codec);

    [LibraryImport("tscutter.avcodec", EntryPoint = "avcodec_free_context", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void avcodec_free_context(AVCodecContext** @avctx);

    [LibraryImport("tscutter.avcodec", EntryPoint = "avcodec_parameters_to_context", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int avcodec_parameters_to_context(AVCodecContext* @codec, AVCodecParameters* @par);

    [LibraryImport("tscutter.avcodec", EntryPoint = "avcodec_open2", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int avcodec_open2(AVCodecContext* @avctx, AVCodec* @codec, AVDictionary** @options);

    [LibraryImport("tscutter.avcodec", EntryPoint = "avcodec_flush_buffers", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void avcodec_flush_buffers(AVCodecContext* @avctx);

    [LibraryImport("tscutter.avcodec", EntryPoint = "avcodec_send_packet", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int avcodec_send_packet(AVCodecContext* @avctx, AVPacket* @avpkt);

    [LibraryImport("tscutter.avcodec", EntryPoint = "avcodec_receive_frame", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int avcodec_receive_frame(AVCodecContext* @avctx, AVFrame* @frame);

    [LibraryImport("tscutter.avcodec", EntryPoint = "av_codec_iterate", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVCodec* av_codec_iterate(void** @opaque);

    [LibraryImport("tscutter.avcodec", EntryPoint = "av_codec_is_decoder", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_codec_is_decoder(AVCodec* @codec);

    [LibraryImport("tscutter.avcodec", EntryPoint = "avcodec_find_decoder", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVCodec* avcodec_find_decoder(AVCodecID @id);

    [LibraryImport("tscutter.avcodec", EntryPoint = "avcodec_get_hw_config", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVCodecHWConfig* avcodec_get_hw_config(AVCodec* @codec, int @index);

    [LibraryImport("tscutter.avcodec", EntryPoint = "avcodec_descriptor_get", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVCodecDescriptor* avcodec_descriptor_get(AVCodecID @id);

    [LibraryImport("tscutter.avcodec", EntryPoint = "av_packet_alloc", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVPacket* av_packet_alloc();

    [LibraryImport("tscutter.avcodec", EntryPoint = "av_packet_free", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void av_packet_free(AVPacket** @pkt);

    [LibraryImport("tscutter.avcodec", EntryPoint = "av_packet_unref", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void av_packet_unref(AVPacket* @pkt);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_frame_alloc", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVFrame* av_frame_alloc();

    [LibraryImport("tscutter.avutil", EntryPoint = "av_frame_free", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void av_frame_free(AVFrame** @frame);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_frame_unref", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void av_frame_unref(AVFrame* @frame);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_frame_get_buffer", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_frame_get_buffer(AVFrame* @frame, int @align);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_frame_get_side_data", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVFrameSideData* av_frame_get_side_data(AVFrame* @frame, AVFrameSideDataType @type);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_hwdevice_ctx_create", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_hwdevice_ctx_create(AVBufferRef** @device_ctx, AVHWDeviceType @type, string? @device, AVDictionary* @opts, int @flags);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_hwframe_transfer_data", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_hwframe_transfer_data(AVFrame* @dst, AVFrame* @src, int @flags);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_dict_set", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_dict_set(AVDictionary** @pm, string @key, string @value, int @flags);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_dict_get", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVDictionaryEntry* av_dict_get(AVDictionary* @m, string @key, AVDictionaryEntry* @prev, int @flags);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_dict_iterate", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVDictionaryEntry* av_dict_iterate(AVDictionary* @m, AVDictionaryEntry* @prev);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_dict_free", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void av_dict_free(AVDictionary** @m);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_strerror", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_strerror(int @errnum, byte* @errbuf, ulong @errbuf_size);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_reduce", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_reduce(int* @dst_num, int* @dst_den, long @num, long @den, long @max);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_channel_layout_describe", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_channel_layout_describe(AVChannelLayout* @channel_layout, byte* @buf, ulong @buf_size);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_pix_fmt_desc_get", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVPixFmtDescriptor* av_pix_fmt_desc_get(AVPixelFormat @pix_fmt);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_get_bits_per_sample", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_get_bits_per_sample(AVCodecID @codec_id);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_opt_set_int", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_opt_set_int(void* @obj, string @name, long @val, int @search_flags);

    [LibraryImport("tscutter.swscale", EntryPoint = "sws_getCachedContext", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SwsContext* sws_getCachedContext(SwsContext* @context, int @srcW, int @srcH, AVPixelFormat @srcFormat, int @dstW, int @dstH, AVPixelFormat @dstFormat, int @flags, SwsFilter* @srcFilter, SwsFilter* @dstFilter, double* @param);

    [LibraryImport("tscutter.swscale", EntryPoint = "sws_scale", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int sws_scale(SwsContext* @c, byte** @srcSlice, int* @srcStride, int @srcSliceY, int @srcSliceH, byte** @dst, int* @dstStride);

    [LibraryImport("tscutter.swscale", EntryPoint = "sws_freeContext", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void sws_freeContext(SwsContext* @swsContext);
}
