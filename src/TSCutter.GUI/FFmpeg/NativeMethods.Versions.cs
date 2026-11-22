#if TSCUTTER_FFMPEG_STATIC
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TSCutter.GUI.FFmpeg;

public static unsafe partial class NativeMethods
{
    [LibraryImport("tscutter.avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint avutil_version();
    [LibraryImport("tscutter.avcodec")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint avcodec_version();
    [LibraryImport("tscutter.avformat")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint avformat_version();
    [LibraryImport("tscutter.avfilter")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint avfilter_version();
    [LibraryImport("tscutter.avdevice")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint avdevice_version();
    [LibraryImport("tscutter.swscale")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint swscale_version();
    [LibraryImport("tscutter.swresample")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint swresample_version();
    [LibraryImport("tscutter.ass")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint ass_library_version();
}
#endif
