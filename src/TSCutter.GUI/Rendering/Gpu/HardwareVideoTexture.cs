using System;
using FFmpeg.AutoGen.Abstractions;
using SkiaSharp;
using TSCutter.GUI.FFmpeg;

namespace TSCutter.GUI.Rendering.Gpu;

internal sealed class HardwareVideoTexture : IDisposable
{
    private readonly MacOSVideoTexture? mac;
    private readonly WindowsVideoTexture? windows;
    private HardwareVideoTexture(MacOSVideoTexture texture) => mac = texture;
    private HardwareVideoTexture(WindowsVideoTexture texture) => windows = texture;
    internal SKImage Y => windows?.Y ?? mac!.Y;
    internal SKImage Uv => windows?.Uv ?? mac!.Uv;
    internal bool Direct => windows?.Direct ?? true;
    internal static void Probe(GRContext? context)
    {
        MacOSVideoTexture.Probe(context);
        WindowsVideoTexture.Probe(context);
    }
    internal static bool CanImport(Frame frame) => OperatingSystem.IsWindows()
        ? WindowsVideoTexture.CanImport(frame) : MacOSVideoTexture.CanImport(frame);
    internal static bool TryDescribe(Frame source, out AVPixelFormat format, out AVColorRange range)
    {
        format = AVPixelFormat.AV_PIX_FMT_NONE;
        range = AVColorRange.AVCOL_RANGE_UNSPECIFIED;
        return OperatingSystem.IsWindows()
            ? WindowsVideoTexture.IsAvailable && WindowsVideoTexture.TryDescribe(source, out format, out range)
            : MacOSVideoTexture.IsAvailable && MacOSVideoTexture.TryDescribe(source, out format, out range);
    }
    internal static HardwareVideoTexture Import(GRContext context, Frame source, WindowsVideoTexture.Owner owner, bool allowDirect = true)
        => OperatingSystem.IsWindows() ? new(owner.Import(context, source, allowDirect)) : new(MacOSVideoTexture.Import(context, source));
    internal static void Disable(Frame source, string reason)
    {
        if (OperatingSystem.IsWindows())
            WindowsVideoTexture.Disable(reason);
        else
            MacOSVideoTexture.Disable(reason);
    }
    internal void DisableDirect(string reason) => windows?.DisableDirect(reason);
    public void Dispose()
    {
        windows?.Dispose();
        mac?.Dispose();
    }
}
