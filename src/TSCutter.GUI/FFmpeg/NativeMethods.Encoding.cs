using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;

namespace TSCutter.GUI.FFmpeg;

// 帧精确剪辑重编码边界画面所需的编码接口。
public static unsafe partial class NativeMethods
{
    public const int AV_CODEC_FLAG_GLOBAL_HEADER = 1 << 22;

    [LibraryImport("tscutter.avcodec", EntryPoint = "avcodec_find_encoder_by_name", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVCodec* avcodec_find_encoder_by_name(string name);

    /// <summary>config 为 AVCodecConfig；0 表示像素格式，列表以 AV_PIX_FMT_NONE 结尾，返回 null 表示不限制。</summary>
    [LibraryImport("tscutter.avcodec", EntryPoint = "avcodec_get_supported_config")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int avcodec_get_supported_config(AVCodecContext* context, AVCodec* codec, int config, uint flags,
        void** values, int* count);

    [LibraryImport("tscutter.avcodec", EntryPoint = "avcodec_send_frame")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int avcodec_send_frame(AVCodecContext* context, AVFrame* frame);

    [LibraryImport("tscutter.avcodec", EntryPoint = "avcodec_receive_packet")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int avcodec_receive_packet(AVCodecContext* context, AVPacket* packet);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_opt_set", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_opt_set(void* obj, string name, string value, int searchFlags);

    /// <summary>flags 为 AV_FRAME_CROP_UNALIGNED（1）时允许裁剪后的数据指针不对齐。</summary>
    [LibraryImport("tscutter.avutil", EntryPoint = "av_frame_apply_cropping")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_frame_apply_cropping(AVFrame* frame, int flags);

    [LibraryImport("tscutter.avcodec", EntryPoint = "av_packet_new_side_data")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial byte* av_packet_new_side_data(AVPacket* packet, AVPacketSideDataType type, ulong size);

    [LibraryImport("tscutter.avcodec", EntryPoint = "av_packet_side_data_new")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVPacketSideData* av_packet_side_data_new(AVPacketSideData** sideData, int* count,
        AVPacketSideDataType type, ulong size, int flags);

    [LibraryImport("tscutter.avcodec", EntryPoint = "av_packet_side_data_free")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void av_packet_side_data_free(AVPacketSideData** sideData, int* count);

    [LibraryImport("tscutter.avcodec", EntryPoint = "av_packet_make_writable")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_packet_make_writable(AVPacket* packet);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_frame_side_data_clone")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_frame_side_data_clone(AVFrameSideData*** sideData, int* count, AVFrameSideData* source, uint flags);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_malloc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void* av_malloc(ulong size);
}
