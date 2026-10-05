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
    // 画面和来源信息一起发布，不能在标记时读取可能已被后台预览推进的解码器状态。
    public long FramePts { get; init; } = long.MinValue;
    public long FramePts90k { get; init; } = long.MinValue;
    public long FramePosition { get; init; } = -1;
    internal TSCutter.GUI.Utils.PacketEndSignature FrameEndSignature { get; init; }
    public VideoPresentationMode PresentationMode { get; init; } = VideoPresentationMode.SoftwareBitmap;
}
