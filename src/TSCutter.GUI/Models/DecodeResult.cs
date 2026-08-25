using System;
using Avalonia;
using Avalonia.Media.Imaging;

namespace TSCutter.GUI.Models;

public class DecodeResult
{
    public Bitmap Bitmap { get; init; }
    public PixelSize SourcePixelSize { get; init; }
    public TimeSpan FrameTimestamp { get; init; }
}
