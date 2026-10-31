using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;

namespace TSCutter.GUI.FFmpeg;

public static unsafe partial class NativeMethods
{
    public const int AV_FRAME_FLAG_INTERLACED = 1 << 3;
    public const int AV_FRAME_FLAG_TOP_FIELD_FIRST = 1 << 4;
    public const int AV_BUFFERSRC_FLAG_KEEP_REF = 8;

    [LibraryImport("tscutter.avfilter")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVFilterGraph* avfilter_graph_alloc();

    [LibraryImport("tscutter.avfilter")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void avfilter_graph_free(AVFilterGraph** graph);

    [LibraryImport("tscutter.avfilter", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVFilter* avfilter_get_by_name(string name);

    [LibraryImport("tscutter.avfilter", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int avfilter_graph_create_filter(AVFilterContext** context, AVFilter* filter,
        string name, string? args, void* opaque, AVFilterGraph* graph);

    [LibraryImport("tscutter.avfilter")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int avfilter_link(AVFilterContext* source, uint sourcePad, AVFilterContext* destination, uint destinationPad);

    [LibraryImport("tscutter.avfilter")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int avfilter_graph_config(AVFilterGraph* graph, void* logContext);

    [LibraryImport("tscutter.avfilter")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_buffersrc_add_frame_flags(AVFilterContext* context, AVFrame* frame, int flags);

    [LibraryImport("tscutter.avfilter")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_buffersink_get_frame(AVFilterContext* context, AVFrame* frame);

    [LibraryImport("tscutter.avfilter")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial FilterTimeBase av_buffersink_get_time_base(AVFilterContext* context);

    [StructLayout(LayoutKind.Sequential)]
    internal struct FilterTimeBase
    {
        public int Numerator;
        public int Denominator;
    }
}
