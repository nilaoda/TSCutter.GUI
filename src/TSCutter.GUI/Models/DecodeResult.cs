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
    public PixelSize SourcePixelSize { get; init; }
    public TimeSpan FrameTimestamp { get; init; }
    public VideoPresentationMode PresentationMode { get; init; } = VideoPresentationMode.SoftwareBitmap;
}
