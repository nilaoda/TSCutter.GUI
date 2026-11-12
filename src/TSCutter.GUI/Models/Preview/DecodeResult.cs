using System;
using Avalonia;
using Avalonia.Media.Imaging;
using TSCutter.GUI.Rendering;
using TSCutter.GUI.Utils.Editing;

namespace TSCutter.GUI.Models.Preview;

public class DecodeResult
{
    public Bitmap? Bitmap { get; init; }
    public IBitmapFrameLease? BitmapLease { get; init; }
    public IGpuFrameLease? GpuFrame { get; init; }
    /// <summary>
    /// 按方形显示像素计算的画面尺寸；非方形采样时可能与位图或 GPU 表面尺寸不同。
    /// </summary>
    public PixelSize SourcePixelSize { get; init; }
    /// <summary>
    /// 解码帧具有有效的非方形采样宽高比时为 true，用于避免对方形像素重复重采样。
    /// </summary>
    public bool RequiresSampleAspectRatioCorrection { get; init; }
    public VideoDynamicRange VideoDynamicRange { get; init; }
    public TimeSpan FrameTimestamp { get; init; }
    // 画面和来源信息一起发布，不能在标记时读取可能已被后台预览推进的解码器状态。
    public long FramePts { get; init; } = long.MinValue;
    public string? KeyFrameType { get; init; }
    public long FramePts90k { get; init; } = long.MinValue;
    public long FramePosition { get; init; } = -1;
    internal TSCutter.GUI.Utils.Editing.PacketEndSignature FrameEndSignature { get; init; }
    /// <summary>DVD 画面的定位锚点（附近关键帧签名与相对偏移）；其他来源无效。</summary>
    internal TSCutter.GUI.Utils.Discs.DvdFrameAnchor FrameAnchor { get; init; }
    private VideoPresentationMode presentationMode;
    public VideoPresentationMode PresentationMode
    {
        get
        {
            if (GpuFrame is not Rendering.Gpu.SkiaVideoFrame { HasBeenRendered: true } frame)
                return presentationMode;
            if (presentationMode is VideoPresentationMode.HardwareCpuTransfer or VideoPresentationMode.HardwareGpuUpload)
                return frame.LastPresentationMode switch
                {
                    VideoPresentationMode.SoftwareGpu => VideoPresentationMode.HardwareGpuUpload,
                    VideoPresentationMode.SoftwareBitmap => VideoPresentationMode.HardwareCpuTransfer,
                    _ => frame.LastPresentationMode
                };
            return frame.LastPresentationMode;
        }
        init => presentationMode = value;
    }
}
