#if TSCUTTER_FFMPEG_STATIC
using System;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Application;

namespace TSCutter.GUI.Utils.Media;

// Static publishing has no FFmpeg runtime directory or dynamically loaded handles.
public static class FFmpegNativeBootstrapper
{
    private static readonly object SyncRoot = new();
    private static bool initialized;
    private const string Summary = "FFmpeg 9 is statically linked into this executable.";

    public static void Configure(AppConfig config) { }

    public static void Initialize(AppConfig? config = null)
    {
        lock (SyncRoot)
        {
            if (initialized) return;
            Validate("avutil", NativeMethods.avutil_version(), 61);
            Validate("avcodec", NativeMethods.avcodec_version(), 63);
            Validate("avformat", NativeMethods.avformat_version(), 63);
            Validate("avfilter", NativeMethods.avfilter_version(), 12);
            Validate("avdevice", NativeMethods.avdevice_version(), 63);
            Validate("swscale", NativeMethods.swscale_version(), 10);
            Validate("swresample", NativeMethods.swresample_version(), 7);
            var ass = NativeMethods.ass_library_version();
            if (ass < 0x01704000 || ass >= 0x10000000)
                throw new InvalidOperationException($"Statically linked libass has incompatible version 0x{ass:X8}.");
            initialized = true;
        }
    }

    private static void Validate(string component, uint version, int major)
    {
        if (version >> 16 != major)
            throw new InvalidOperationException($"Statically linked {component}: expected ABI {major}, got {version >> 16}.");
    }

    public static string BuildLoadFailureMessage(Exception exception) => $"{exception.Message}{Environment.NewLine}{Summary}";
    public static string GetDiagnosticSummary() => Summary;
}
#endif
