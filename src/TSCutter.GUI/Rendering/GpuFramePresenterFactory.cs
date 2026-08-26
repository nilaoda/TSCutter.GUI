using System;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;

namespace TSCutter.GUI.Rendering;

/// <summary>
/// Detects whether Avalonia can import external images on the current
/// compositor. Import support alone is not enough to enable the path: the
/// FFmpeg surface format (currently NV12/YUV for all built-in decoders) must
/// also have a platform YUV shader bridge.
/// </summary>
public static class GpuFramePresenterFactory
{
    public static IGpuFramePresenter CreateDefault()
    {
        var backend = OperatingSystem.IsWindows()
            ? "D3D11"
            : OperatingSystem.IsMacOS()
                ? "Metal"
                : OperatingSystem.IsLinux()
                    ? "Vulkan/DMABUF"
                    : "Unknown";

        if (OperatingSystem.IsLinux())
            return new NullGpuFramePresenter("The bundled FFmpeg libraries do not enable Linux hardware decoding.", backend: backend);

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            return new NullGpuFramePresenter("The current operating system has no GPU video presenter.");

        try
        {
            var compositor = Compositor.TryGetDefaultCompositor();
            if (compositor is null)
                return new NullGpuFramePresenter("Avalonia has not created a compositor yet.");

            var interop = compositor.TryGetCompositionGpuInterop().AsTask().GetAwaiter().GetResult();
            if (interop is null)
                return new NullGpuFramePresenter($"Avalonia compositor does not expose external GPU image import ({backend}).");

            var supported = string.Join(", ", interop.SupportedImageHandleTypes);
            return new NullGpuFramePresenter(
                $"Avalonia imports [{supported}], but FFmpeg hardware frames require a native YUV shader bridge on {backend}.",
                isPlatformSupported: true,
                isRendererSupported: false,
                backend: backend);
        }
        catch (Exception exception)
        {
            return new NullGpuFramePresenter($"Avalonia GPU interop probe failed: {exception.Message}");
        }
    }
}
