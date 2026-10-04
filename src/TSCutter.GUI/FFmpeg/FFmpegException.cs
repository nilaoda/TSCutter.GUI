using System;
using System.Runtime.InteropServices;

namespace TSCutter.GUI.FFmpeg;

public sealed class FFmpegException(string message, int errorCode) : Exception(message)
{
    public int ErrorCode { get; } = errorCode;

    public static unsafe FFmpegException FromErrorCode(int code, string? message = null)
    {
        byte* buffer = stackalloc byte[256];
        NativeMethods.av_strerror(code, buffer, 256);
        return new FFmpegException($"{message} {Marshal.PtrToStringUTF8((IntPtr)buffer)} ({code})".Trim(), code);
    }

    internal static int Check(int code)
    {
        if (code < 0) throw FromErrorCode(code);
        return code;
    }
}
