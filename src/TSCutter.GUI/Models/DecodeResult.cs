using System;
using Avalonia;
using Avalonia.Media.Imaging;
using TSCutter.GUI.Rendering;

namespace TSCutter.GUI.Models;

public class DecodeResult
{
    public Bitmap? Bitmap { get; init; }
    public IBitmapFrameLease? BitmapLease { get; init; }
    public IGpuFrameLease? GpuFrame { get; init; }
    /// <summary>
    /// Frame geometry in square display pixels. This may differ from the decoded
    /// bitmap or GPU surface size when the video uses non-square samples.
    /// </summary>
    public PixelSize SourcePixelSize { get; init; }
    /// <summary>
    /// True only when the decoded frame has a valid non-square sample aspect ratio.
    /// Consumers can use this to avoid an unnecessary resample for square pixels.
    /// </summary>
    public bool RequiresSampleAspectRatioCorrection { get; init; }
    public VideoDynamicRange VideoDynamicRange { get; init; }
    public TimeSpan FrameTimestamp { get; init; }
    public VideoPresentationMode PresentationMode { get; init; } = VideoPresentationMode.SoftwareBitmap;
}
