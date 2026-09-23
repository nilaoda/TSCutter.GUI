using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Services;

/// <summary>
/// 缩略图总览的抽帧服务。与 <see cref="KeyFrameOverviewService"/> 一样只使用软件解码，
/// 并在每次抽帧后按严格顺序释放 native 帧与位图租约。
///
/// 与关键帧总览的区别：总览按可视范围惰性取帧，这里是一次性按采样计划取满 N×M 个帧点。
/// </summary>
internal sealed class ThumbnailSheetService : IAsyncDisposable
{
    private VideoInstance? decoder;
    private TimeSpan duration;

    public TimeSpan Duration => duration;

    /// <summary>
    /// 打开输入并解析时长。返回值表示是否拿到了有效的视频时长
    /// （音频模式或时长缺失时返回 false，此时抽帧会得到占位图）。
    /// </summary>
    public async Task<bool> OpenAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await DisposeAsync().ConfigureAwait(false);

        // 与总览一致：只走软件解码。缩略图很小，而硬件帧回读到主机内存前
        // 仍需要完整的 native surface，保持软件解码可以限制显存/纹理占用。
        decoder = new VideoInstance(filePath, enableHardwareDecoding: false);
        try
        {
            await decoder.InitVideoAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            duration = TimeSpan.FromSeconds(decoder.GetVideoDurationInSeconds());
            // 音频模式下 DecodeAtTimeAsync 仍会返回占位位图，这里只报告时长是否可用。
            return duration > TimeSpan.Zero;
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 按采样策略生成帧点列表。返回的数量最多为 <paramref name="count"/>。
    /// </summary>
    public IReadOnlyList<TimeSpan> BuildSamplePoints(
        ThumbnailSheetSampling sampling,
        int count,
        double intervalSeconds)
    {
        count = Math.Max(0, count);
        if (count == 0)
            return [];

        var active = decoder;
        var totalSeconds = active is null ? duration.TotalSeconds : active.GetVideoDurationInSeconds();
        if (totalSeconds <= 0)
        {
            // 时长未知时退化为取首帧，至少让用户看到内容而不是空白。
            return [TimeSpan.Zero];
        }

        switch (sampling)
        {
            case ThumbnailSheetSampling.KeyFrame:
                return BuildKeyFramePoints(active, count, totalSeconds);

            case ThumbnailSheetSampling.Interval:
            {
                var step = intervalSeconds > 0 ? intervalSeconds : 10;
                var points = new List<TimeSpan>();
                for (var i = 0; i < count; i++)
                {
                    var seconds = i * step;
                    if (seconds >= totalSeconds)
                        break;
                    points.Add(TimeSpan.FromSeconds(seconds));
                }

                if (points.Count == 0)
                    points.Add(TimeSpan.Zero);
                return points;
            }

            default:
            {
                // 均匀分帧：把整段时长等分为 count 份，取每份中点，
                // 避免首点固定落在 0 秒导致开头重复。
                var points = new List<TimeSpan>(count);
                var step = totalSeconds / count;
                for (var i = 0; i < count; i++)
                {
                    var seconds = step * (i + 0.5);
                    if (seconds > totalSeconds)
                        seconds = totalSeconds;
                    points.Add(TimeSpan.FromSeconds(seconds));
                }
                return points;
            }
        }
    }

    private static IReadOnlyList<TimeSpan> BuildKeyFramePoints(VideoInstance? active, int count, double totalSeconds)
    {
        if (active is null)
            return [];

        var index = active.CreateEstimatedKeyFrameIndex();
        if (index.Count == 0)
        {
            // 音频模式或时长缺失时退回均匀分帧。
            var fallback = new List<TimeSpan>(count);
            var step = totalSeconds / count;
            for (var i = 0; i < count; i++)
                fallback.Add(TimeSpan.FromSeconds(step * (i + 0.5)));
            return fallback;
        }

        if (index.Count <= count)
        {
            var all = new List<TimeSpan>(index.Count);
            foreach (var entry in index)
                all.Add(entry.Timestamp);
            return all;
        }

        // 从估算索引里等距挑选 count 个点。
        var picked = new List<TimeSpan>(count);
        var stride = index.Count / (double)count;
        for (var i = 0; i < count; i++)
        {
            var position = (int)(i * stride);
            if (position >= index.Count)
                position = index.Count - 1;
            picked.Add(index[position].Timestamp);
        }
        return picked;
    }

    /// <summary>
    /// 抽取单个帧点，返回已按目标尺寸 letterbox 好的单元格位图。
    /// </summary>
    public async Task<Bitmap?> DecodeCellAsync(
        TimeSpan timestamp,
        PixelSize cellSize,
        double sourceDisplayAspectRatio,
        CancellationToken cancellationToken = default)
    {
        var activeDecoder = decoder ?? throw new InvalidOperationException("Thumbnail sheet decoder is not initialized.");

        // 解码器在目标边界内等比缩小，随后 CreateThumbnail 按显示比例完成最终绘制。
        var result = await activeDecoder.DecodeAtTimeAsync(
            timestamp,
            cellSize.Width,
            cellSize.Height,
            cancellationToken).ConfigureAwait(false);

        try
        {
            if (result.Bitmap is null)
                return null;

            // 统一走 CreateThumbnail：它内部已处理 letterbox 与尺寸相等时的快速路径。
            var displayAspectRatio = result.RequiresSampleAspectRatioCorrection
                ? sourceDisplayAspectRatio
                : 0;
            return ImageUtil.CreateThumbnail(
                result.Bitmap,
                cellSize.Width,
                cellSize.Height,
                displayAspectRatio);
        }
        finally
        {
            // 释放顺序必须与 KeyFrameOverviewService 保持一致：
            // 先归还租约，未走租约的位图才由自己释放，最后释放 GPU 帧。
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
        duration = TimeSpan.Zero;
        await ValueTask.CompletedTask;
    }
}
