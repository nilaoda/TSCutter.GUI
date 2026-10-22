using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Rendering;
using TSCutter.GUI.Utils.Media;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Models.Preview;

public partial class VideoInstance
{
    private Frame? retainedPreviewFrame;
    private DecodeResult? retainedPreviewInfo;
    private int retainedPreviewWidth, retainedPreviewHeight;

    // 仅主预览保留一帧 CPU 数据引用，开关映射时不重新 seek，也不改变标记位置。
    private unsafe void RetainPreviewFrame(Frame source, DecodeResult result)
    {
        if (!retainPreviewFrame || isSearchScan) return;
        if (result.VideoDynamicRange == VideoDynamicRange.Standard)
        {
            retainedPreviewFrame?.Unref();
            retainedPreviewInfo = CopyPreviewInfo(result);
            return;
        }
        retainedPreviewFrame ??= new Frame();
        retainedPreviewFrame.Unref();
        FFmpegException.Check(FF.av_frame_ref(retainedPreviewFrame, source));
        retainedPreviewInfo = CopyPreviewInfo(result);
        retainedPreviewWidth = result.Bitmap!.PixelSize.Width;
        retainedPreviewHeight = result.Bitmap.PixelSize.Height;
    }

    // TS 的定位结果可能略晚于请求点，提前喂入关键帧再核对原始 PTS。
    internal async Task<DecodeResult?> DecodePreviewFrameAtPtsAsync(long targetPts, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Run(() => Seek(Math.Max(firstFrameTimestamp, targetPts - keyFrameGap * 2), AVSEEK_FLAG.Backward), cancellationToken);
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var result = await DecodeNextFrameAsync(cancellationToken: cancellationToken);
            if (result.FramePts == targetPts) return result;
            result.BitmapLease?.Dispose();
            if (result.BitmapLease is null) result.Bitmap?.Dispose();
            result.GpuFrame?.Dispose();
            if (result.FramePts > targetPts) break;
        }
        return null;
    }

    public Task<DecodeResult?> RefreshCurrentFrameAsync(CancellationToken cancellationToken = default, long? expectedPts = null)
    {
        // 被取消的拖动或失败的导航可能已推进缓存，以界面实际发布的 PTS 为准。
        if (retainPreviewFrame && expectedPts is { } pts && retainedPreviewInfo?.FramePts != pts)
            return DecodePreviewFrameAtPtsAsync(pts, cancellationToken);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (retainedPreviewFrame is null || retainedPreviewInfo is null
                || retainedPreviewInfo.VideoDynamicRange == VideoDynamicRange.Standard) return null;
            var bitmap = ConvertPreviewFrame(retainedPreviewFrame, retainedPreviewWidth, retainedPreviewHeight, out var lease);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                return CopyPreviewInfo(retainedPreviewInfo, bitmap, lease);
            }
            catch
            {
                if (lease is not null) lease.Dispose(); else bitmap.Dispose();
                throw;
            }
        }, cancellationToken);
    }

    private Bitmap ConvertPreviewFrame(Frame source, int maxWidth, int maxHeight, out IBitmapFrameLease? lease)
    {
        lease = null;
        var enabled = enableSdrToneMapping?.Invoke() ?? true;
        try { hostFramePresenter.TryConvert(source, maxWidth, maxHeight, out lease, enabled); }
        catch (Exception exception) { Console.WriteLine($"Host bitmap reuse unavailable; using allocation fallback: {exception.Message}"); }
        if (lease is not null) return lease.Bitmap;
        return maxWidth > 0 && maxHeight > 0
            ? ImageUtil.CreateScaledBitmapFromFrame(source, maxWidth, maxHeight, enableSdrToneMapping: enabled)
            : ImageUtil.CreateBitmapFromFrame(source, enableSdrToneMapping: enabled);
    }

    // 缓存只保存来源信息，不持有界面的位图租约。
    private static DecodeResult CopyPreviewInfo(DecodeResult source, Bitmap? bitmap = null, IBitmapFrameLease? lease = null) => new()
    {
        Bitmap = bitmap, BitmapLease = lease, SourcePixelSize = source.SourcePixelSize,
        RequiresSampleAspectRatioCorrection = source.RequiresSampleAspectRatioCorrection,
        VideoDynamicRange = source.VideoDynamicRange, FrameTimestamp = source.FrameTimestamp,
        FramePts = source.FramePts, FramePts90k = source.FramePts90k, FramePosition = source.FramePosition,
        FrameEndSignature = source.FrameEndSignature, PresentationMode = source.PresentationMode
    };
}
