using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TSCutter.GUI.FFmpeg;

public static unsafe partial class NativeMethods
{
    [LibraryImport("tscutter.avutil", EntryPoint = "av_usleep")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int av_usleep(uint microseconds);
}
