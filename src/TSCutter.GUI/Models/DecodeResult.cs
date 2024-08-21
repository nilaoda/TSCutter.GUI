using System;
using Avalonia.Media.Imaging;

namespace TSCutter.GUI.Models;

public class DecodeResult
{
    public Bitmap Bitmap { get; init; }
    public TimeSpan FrameTimestamp { get; init; }
}