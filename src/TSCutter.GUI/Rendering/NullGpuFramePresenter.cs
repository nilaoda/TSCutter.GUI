using System;
using Avalonia;
using Sdcb.FFmpeg.Utils;

namespace TSCutter.GUI.Rendering;

/// <summary>
/// Conservative default until a platform-specific YUV import bridge is
/// available. Keeping this as a real presenter makes the fallback explicit
/// and prevents decode code from depending on a particular UI backend.
/// </summary>
public sealed class NullGpuFramePresenter : IGpuFramePresenter
{
    public NullGpuFramePresenter(
        string? reason = null,
        bool isPlatformSupported = false,
        bool isRendererSupported = false,
        string? backend = null)
    {
        Capabilities = new GpuFramePresenterCapabilities(
            IsPlatformSupported: isPlatformSupported,
            IsRendererSupported: isRendererSupported,
            Backend: backend ?? (OperatingSystem.IsWindows()
                ? "D3D11"
                : OperatingSystem.IsMacOS()
                    ? "Metal"
                    : OperatingSystem.IsLinux()
                        ? "Vulkan/DMABUF"
                        : "Unknown"),
            UnavailableReason: reason ?? "No compatible hardware-surface importer is registered.");
    }

    public GpuFramePresenterCapabilities Capabilities { get; }

    public bool TryPresent(Frame frame, out IGpuFrameLease? presentation)
    {
        presentation = null;
        return false;
    }

    public void Dispose()
    {
    }
}
