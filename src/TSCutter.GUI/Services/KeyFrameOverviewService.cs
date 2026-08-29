using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Services;

internal sealed class KeyFrameOverviewService : IAsyncDisposable
{
    private VideoInstance? decoder;

    public async Task<IReadOnlyList<KeyFrameIndexEntry>> OpenAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await DisposeAsync().ConfigureAwait(false);
        // 总览只使用软件解码。缩略图尺寸很小，而硬件帧在回读到主机内存前
        // 仍需要完整的 4K/8K surface。保持软件解码可以限制 native surface
        // 的占用，同时不影响主预览使用硬件解码。
        decoder = new VideoInstance(filePath, enableHardwareDecoding: false);
        try
        {
            await decoder.InitVideoAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return decoder.CreateEstimatedKeyFrameIndex();
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<Bitmap?> DecodeThumbnailAsync(
        KeyFrameIndexEntry entry,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        var activeDecoder = decoder ?? throw new InvalidOperationException("Overview decoder is not initialized.");
        var result = await activeDecoder.DecodeAtTimeAsync(
            entry.Timestamp,
            width,
            height,
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (result.Bitmap is null)
                return null;
            return ImageUtil.CreateThumbnail(result.Bitmap, width, height);
        }
        finally
        {
            result.BitmapLease?.Dispose();
            if (result.BitmapLease is null)
                result.Bitmap?.Dispose();
            result.GpuFrame?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var activeDecoder = Interlocked.Exchange(ref decoder, null);
        if (activeDecoder is not null)
            activeDecoder.Dispose();
        await ValueTask.CompletedTask;
    }
}
