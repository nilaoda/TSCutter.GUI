// swresample 绑定：声明仅覆盖播放重采样实际使用的接口。
// 模式与 NativeMethods.cs 的 swscale 声明一致，走 Bootstrapper 加载的共享库。
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;

namespace TSCutter.GUI.FFmpeg;

public static unsafe partial class NativeMethods
{
    [LibraryImport("tscutter.swresample")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial SwrContext* swr_alloc();

    [LibraryImport("tscutter.swresample")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int swr_alloc_set_opts2(SwrContext** swr,
        AVChannelLayout* outChLayout, AVSampleFormat outSampleFmt, int outSampleRate,
        AVChannelLayout* inChLayout, AVSampleFormat inSampleFmt, int inSampleRate,
        int logOffset, void* logCtx);

    [LibraryImport("tscutter.swresample")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int swr_init(SwrContext* swr);

    [LibraryImport("tscutter.swresample")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void swr_free(SwrContext** swr);

    [LibraryImport("tscutter.swresample")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial long swr_get_delay(SwrContext* swr, long sampleRate);

    [LibraryImport("tscutter.swresample")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int swr_convert(SwrContext* swr,
        byte** @out, int outCount,
        byte** @in, int inCount);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_channel_layout_default")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void av_channel_layout_default(AVChannelLayout* chLayout, int nbChannels);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_channel_layout_copy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_channel_layout_copy(AVChannelLayout* dst, AVChannelLayout* src);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_channel_layout_uninit")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void av_channel_layout_uninit(AVChannelLayout* chLayout);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_sample_fmt_is_planar")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_sample_fmt_is_planar(AVSampleFormat sampleFmt);
}
