using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.VisualTree;
using Sdcb.FFmpeg.Utils;

namespace TSCutter.GUI.Rendering;

/// <summary>
/// Imports FFmpeg hardware surfaces into the UI renderer without a
/// hwframe-to-system-memory transfer. Implementations must return false for
/// unsupported formats or when the UI device is not compatible with the
/// decoder device; callers then use the existing software bitmap fallback.
/// </summary>
public interface IGpuFramePresenter : IDisposable
{
    GpuFramePresenterCapabilities Capabilities { get; }

    bool TryPresent(Frame frame, out IGpuFrameLease? presentation);
}

public interface IGpuFrameLease : IDisposable
{
    PixelSize PixelSize { get; }

    string Backend { get; }

    /// <summary>Attaches the native visual to the preview control.</summary>
    bool TryAttach(Visual host);

    /// <summary>Updates the destination rectangle after zoom/pan/layout changes.</summary>
    void UpdateViewport(Rect destination);
}

public sealed record GpuFramePresenterCapabilities(
    bool IsPlatformSupported,
    bool IsRendererSupported,
    string Backend,
    string? UnavailableReason)
{
    public bool IsAvailable => IsPlatformSupported && IsRendererSupported;
}
