using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TSCutter.GUI.FFmpeg;

public static unsafe partial class NativeMethods
{
    [LibraryImport("tscutter.avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void av_log_set_callback(delegate* unmanaged[Cdecl]<void*, int, byte*, byte*, void> callback);

    [LibraryImport("tscutter.avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void av_log_default_callback(void* context, int level, byte* format, byte* arguments);

    [LibraryImport("tscutter.avutil")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int av_log_format_line2(void* context, int level, byte* format, byte* arguments,
        byte* buffer, int size, int* printPrefix);
}
