using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;

namespace TSCutter.GUI.FFmpeg;

public static unsafe partial class NativeMethods
{
    [LibraryImport("tscutter.avutil", EntryPoint = "av_dynamic_hdr_plus_alloc")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial AVDynamicHDRPlus* av_dynamic_hdr_plus_alloc(nuint* size);

    [LibraryImport("tscutter.avutil", EntryPoint = "av_dynamic_hdr_plus_from_t35")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int av_dynamic_hdr_plus_from_t35(AVDynamicHDRPlus* metadata, byte* data, nuint size);
}
