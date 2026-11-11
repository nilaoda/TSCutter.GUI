using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using TSCutter.GUI.Models.Discs;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Utils.Media;

namespace TSCutter.GUI.Services.Preview;

// EveryFrame 为帧精确模式：导航条显示每一帧及其画面类型，而不只是关键帧。
internal sealed record KeyFrameStripSource(string Path, PreviewStreamSelection? Selection, bool ToneMapping,
    DiscInput? Disc = null, bool EveryFrame = false,
    KeyFrameStripThumbnailSize ThumbnailSize = KeyFrameStripThumbnailSize.Medium);
internal sealed record KeyFrameStripFrame(long Pts, TimeSpan Timestamp, Bitmap? Bitmap, string? KeyFrameType = null);
internal sealed record KeyFrameStripRange(IReadOnlyList<KeyFrameStripFrame> Frames, bool AtStart, bool AtEnd);

internal interface IKeyFrameStripService : IAsyncDisposable
{
    Task OpenAsync(KeyFrameStripSource source, CancellationToken token);
    Task<KeyFrameStripRange> ReadAsync(double target, Func<long, bool> isCached,
        Action<KeyFrameStripFrame> received, CancellationToken token, Action<KeyFrameStripRange>? progress = null, int frameCount = 9);
    Task<KeyFrameStripFrame?> ReadNeighborAsync(KeyFrameStripFrame anchor, int direction,
        Func<long, bool> isCached, CancellationToken token);

    /// <summary>
    /// 向 direction 方向读取最多 count 帧，按与锚点的距离排序（最近的在前）；空列表表示已到片头或片尾。
    /// 默认逐帧读取一张，能一次解码取回多帧的实现可覆盖。
    /// </summary>
    async Task<IReadOnlyList<KeyFrameStripFrame>> ReadNeighborsAsync(KeyFrameStripFrame anchor, int direction, int count,
        Func<long, bool> isCached, CancellationToken token) =>
        await ReadNeighborAsync(anchor, direction, isCached, token) is { } frame ? [frame] : [];
}

/// <summary>只扫描指针附近的真实关键帧（或帧精确模式下的每一帧），不创建全片索引，也不占用主预览解码器。</summary>
internal sealed partial class KeyFrameStripService : IKeyFrameStripService
{
    private const int MaximumWindowAttempts = 4;
    private VideoInstance? decoder;
    private long? cursorPts;
    private bool everyFrame;
    private double? observedInterval;
    private double? leftWindowSpan;
    private int leftWindowCount;
    // 解码与缩略图的尺寸，打开时按导航条缩略图大小确定；改变大小会换用新的来源与服务。
    private int thumbnailWidth, thumbnailHeight;

    public async Task OpenAsync(KeyFrameStripSource source, CancellationToken token)
    {
        everyFrame = source.EveryFrame;
        var layout = KeyFrameStripLayout.For(source.ThumbnailSize);
        thumbnailWidth = layout.DecodeWidth;
        thumbnailHeight = layout.DecodeHeight;
        cursorPts = null;
        observedInterval = leftWindowSpan = null;
        leftWindowCount = 0;
        // 始终软解：不与主预览争用硬件解码器，也不额外占用显存。
        decoder = new VideoInstance(source.Path, enableHardwareDecoding: false,
            source.Selection, () => source.ToneMapping, discInput: source.Disc) { ThumbnailDecoding = true };
        await decoder.InitVideoAsync(token);
        await OpenHelpersAsync(source, token);
    }

    public async Task<KeyFrameStripRange> ReadAsync(double target, Func<long, bool> isCached,
        Action<KeyFrameStripFrame> received, CancellationToken token, Action<KeyFrameStripRange>? progress = null, int frameCount = 9)
    {
        var video = decoder ?? throw new InvalidOperationException("Strip decoder is not initialized.");
        frameCount = Math.Clamp(frameCount, 1, 48);
        var leftCount = (frameCount - 1) / 2;
        var rightCount = frameCount - 1 - leftCount;
        if (everyFrame)
            return await ReadEveryFrameAsync(video, target, leftCount, rightCount, isCached, received, token);
        target = Math.Clamp(target, 0, Math.Max(0, video.GetVideoDurationInSeconds()));
        if (video.SupportsParallelKeyFrameScan && await ReadParallelAsync(video, target, leftCount, rightCount, frameCount,
                isCached, received, progress, token) is { } parallel)
            return parallel;
        var interval = Math.Clamp(observedInterval ?? video.EstimatedKeyFrameIntervalSeconds, 0.04, 30);
        // 复用上一屏实际的左侧跨度；宽度改变时按槽位数估算，再预留 TS seek 的落点偏差。
        var leftSpan = leftWindowSpan is { } span && leftWindowCount > 0
            ? span * leftCount / leftWindowCount : interval * leftCount;
        var start = Math.Max(0, target - Math.Max(leftSpan, interval * leftCount) - interval * 1.5);
        // 估算间隔仅用于选择扫描起点；显示和跳转始终使用解码得到的实际时间。
        // GOP 突然变密时限制预读量，再从目标处向后定位，避免扫描整段视频。
        for (var attempt = 0; attempt < MaximumWindowAttempts; attempt++)
        {
            cursorPts = null;
            await video.SeekBeforeTimeAsync(TimeSpan.FromSeconds(start), token);
            var frames = new List<KeyFrameStripFrame>();
            var seen = new HashSet<long>();
            var atEnd = false;
            long? firstPts = null;
            var reachedTarget = false;
            for (var count = 0; count < Math.Max(48, frameCount * 4) * (attempt + 1); count++)
            {
                token.ThrowIfCancellationRequested();
                cursorPts = null;
                var result = await video.DecodeNextSearchFrameAsync(thumbnailWidth, thumbnailHeight, token);
                if (result is null)
                {
                    atEnd = true;
                    break;
                }
                try
                {
                    if (!seen.Add(result.FramePts) || result.FrameTimestamp.TotalSeconds < 0)
                        continue;
                    cursorPts = result.FramePts;
                    firstPts ??= result.FramePts;
                    var frame = CreateFrame(result, isCached);
                    // 回调接管位图；取消前已完成的图仍可留在有界缓存中。
                    received(frame);
                    frames.Add(frame with { Bitmap = null });
                    frames.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
                    var before = frames.Count(item => item.Timestamp.TotalSeconds <= target);
                    // 保留足够的前帧，让文件末尾也能向前补满整条预览。
                    if (before > frameCount)
                        frames.RemoveRange(0, before - frameCount);
                    reachedTarget |= frame.Timestamp.TotalSeconds >= target;
                    if (start == 0 || reachedTarget)
                        progress?.Invoke(CreateRange(frames, target, start == 0, firstPts, atEnd: false, frameCount: frameCount));
                    // 开头没有前帧时继续向后补图，避免只取到半条预览。
                    if (reachedTarget && frames.Count(item => item.Timestamp.TotalSeconds > target) >= rightCount &&
                        (frames.Count >= frameCount || start > 0 && before < leftCount + 1))
                        break;
                }
                finally
                {
                    DisposeResult(result);
                }
            }
            if (frames.Count >= 2)
            {
                // CRA 与 IDR 可能相隔仅一帧；用整段平均间隔，避免将这种短间隔误当成常规 GOP。
                var gap = (frames[^1].Timestamp - frames[0].Timestamp).TotalSeconds / (frames.Count - 1);
                if (gap > 0)
                    interval = Math.Clamp(gap, 0.04, 30);
            }
            if (attempt == MaximumWindowAttempts - 1)
            {
                // 不可靠的索引可能让 seek 多次落到远处；预算耗尽时保留原图，
                // 不把远离指针的扫描结果作为新的邻域发布。
                if (!reachedTarget && !atEnd)
                    throw new InvalidOperationException("Unable to locate nearby keyframes within the scan budget.");
                return RememberWindow(CreateRange(frames, target, start == 0, firstPts, atEnd, frameCount), target, leftCount);
            }
            if (reachedTarget || atEnd)
            {
                if (start == 0 || (frames.Count >= frameCount &&
                    frames.Count(frame => frame.Timestamp.TotalSeconds <= target) >= leftCount + 1))
                    return RememberWindow(CreateRange(frames, target, start == 0, firstPts, atEnd, frameCount), target, leftCount);
                // 可变 GOP 或接近结尾导致前帧不足时，扩大局部窗口补齐图片。
                var missing = Math.Max(1, leftCount + 1 - frames.Count(frame => frame.Timestamp.TotalSeconds <= target));
                if (atEnd)
                    missing = Math.Max(missing, frameCount - frames.Count);
                var rewind = interval * (missing + 1.5);
                if (frames.Count > 0)
                {
                    // 从实际最早帧之前补缺口；seek 越过左边界时也计入落点偏差。
                    var first = frames[0].Timestamp.TotalSeconds;
                    rewind = Math.Max(rewind, (first - start) * (missing + 1.5));
                    start = Math.Min(start, first);
                }
                start = Math.Max(0, start - rewind);
            }
            else
            {
                // GOP 变密导致预读预算耗尽，按刚读到的真实间隔重新定位到目标附近。
                start = Math.Max(0, target - interval * (leftCount + 1.5));
            }
        }
        throw new InvalidOperationException();
    }

    private KeyFrameStripRange RememberWindow(KeyFrameStripRange range, double target, int leftCount)
    {
        if (range.Frames.Count >= 2)
            observedInterval = Math.Clamp((range.Frames[^1].Timestamp - range.Frames[0].Timestamp).TotalSeconds /
                (range.Frames.Count - 1), 0.04, 30);
        var current = range.Frames.TakeWhile(frame => frame.Timestamp.TotalSeconds <= target).Count() - 1;
        if (leftCount > 0 && current >= leftCount)
        {
            leftWindowSpan = (range.Frames[current].Timestamp - range.Frames[current - leftCount].Timestamp).TotalSeconds;
            leftWindowCount = leftCount;
        }
        return range;
    }

    private async Task<KeyFrameStripRange> ReadEveryFrameAsync(VideoInstance video, double target, int leftCount,
        int rightCount, Func<long, bool> isCached, Action<KeyFrameStripFrame> received, CancellationToken token)
    {
        target = Math.Clamp(target, 0, Math.Max(0, video.GetVideoDurationInSeconds()));
        var window = await video.DecodeFrameWindowAsync(video.SecondsToPts(target), leftCount, rightCount,
            thumbnailWidth, thumbnailHeight, token);
        var frames = new List<KeyFrameStripFrame>(window.Frames.Count);
        try
        {
            foreach (var result in window.Frames)
            {
                var frame = CreateFrame(result, isCached);
                received(frame);
                frames.Add(frame with { Bitmap = null });
            }
        }
        finally
        {
            foreach (var result in window.Frames)
                DisposeResult(result);
        }
        return new KeyFrameStripRange(frames, window.AtStart, window.AtEnd);
    }

    /// <summary>
    /// 帧精确模式下补齐导航条两侧：一次解码取回多帧。向前补图若逐张读取，每张都要从关键帧重新解码整个 GOP，
    /// 4K 软解时每张约需一秒。关键帧模式仍逐张读取。
    /// </summary>
    public async Task<IReadOnlyList<KeyFrameStripFrame>> ReadNeighborsAsync(KeyFrameStripFrame anchor, int direction, int count,
        Func<long, bool> isCached, CancellationToken token)
    {
        var video = decoder ?? throw new InvalidOperationException("Strip decoder is not initialized.");
        if (!everyFrame || count <= 1)
            return await ReadNeighborAsync(anchor, direction, isCached, token) is { } frame ? [frame] : [];
        count = Math.Min(count, 48);
        var results = direction > 0
            ? await video.DecodeFollowingFramesAsync(anchor.Pts, count, thumbnailWidth, thumbnailHeight, token)
            : await video.DecodePrecedingFramesAsync(anchor.Pts, count, thumbnailWidth, thumbnailHeight, token);
        var frames = new List<KeyFrameStripFrame>(results.Count);
        try
        {
            foreach (var result in results)
                frames.Add(CreateFrame(result, isCached));
        }
        catch
        {
            foreach (var frame in frames)
                frame.Bitmap?.Dispose();
            throw;
        }
        finally
        {
            foreach (var result in results)
                DisposeResult(result);
        }
        return frames;
    }

    public async Task<KeyFrameStripFrame?> ReadNeighborAsync(KeyFrameStripFrame anchor, int direction,
        Func<long, bool> isCached, CancellationToken token)
    {
        var video = decoder ?? throw new InvalidOperationException("Strip decoder is not initialized.");
        if (everyFrame)
        {
            var result = direction > 0
                ? await video.DecodeFollowingFrameAsync(anchor.Pts, thumbnailWidth, thumbnailHeight, token)
                : await video.DecodePrecedingFrameAsync(anchor.Pts, thumbnailWidth, thumbnailHeight, token);
            if (result is null)
                return null;
            try { return CreateFrame(result, isCached); }
            finally { DisposeResult(result); }
        }
        if (direction > 0)
        {
            // 顺序前进时保留解码器游标，通常只需再解码一张图片。
            if (cursorPts != anchor.Pts)
            {
                cursorPts = null;
                // 重新定位仍使用缩略图扫描路径，避免恢复游标时切回完整分辨率和完整 GOP 解码。
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var preroll = attempt == 0 ? 0 : Math.Max(0.04, observedInterval ?? video.EstimatedKeyFrameIntervalSeconds) * 2;
                    await video.SeekBeforeTimeAsync(
                        TimeSpan.FromSeconds(Math.Max(0, anchor.Timestamp.TotalSeconds - preroll)), token);
                    for (var count = 0; count < 48; count++)
                    {
                        var positioned = await video.DecodeNextSearchFrameAsync(thumbnailWidth, thumbnailHeight, token);
                        if (positioned is null)
                            break;
                        var pts = positioned.FramePts;
                        DisposeResult(positioned);
                        if (pts == anchor.Pts)
                        {
                            cursorPts = pts;
                            break;
                        }
                        if (pts > anchor.Pts)
                            break;
                    }
                    if (cursorPts == anchor.Pts)
                        break;
                }
                if (cursorPts != anchor.Pts)
                    throw new InvalidOperationException("Unable to position the strip decoder.");
            }
            for (var count = 0; count < 48; count++)
            {
                cursorPts = null;
                var result = await video.DecodeNextSearchFrameAsync(thumbnailWidth, thumbnailHeight, token);
                if (result is null)
                    return null;
                try
                {
                    cursorPts = result.FramePts;
                    if (result.FrameTimestamp > anchor.Timestamp)
                        return CreateFrame(result, isCached);
                }
                finally { DisposeResult(result); }
            }
        }
        else
        {
            var preroll = 0.000001;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var start = Math.Max(0, anchor.Timestamp.TotalSeconds - preroll);
                cursorPts = null;
                await video.SeekBeforeTimeAsync(TimeSpan.FromSeconds(start), token);
                DecodeResult? previous = null;
                try
                {
                    for (var count = 0; count < 48; count++)
                    {
                        cursorPts = null;
                        var result = await video.DecodeNextSearchFrameAsync(thumbnailWidth, thumbnailHeight, token);
                        if (result is null)
                            break;
                        cursorPts = result.FramePts;
                        if (result.FrameTimestamp >= anchor.Timestamp)
                        {
                            DisposeResult(result);
                            if (previous is not null)
                                return CreateFrame(previous, isCached);
                            if (start == 0)
                                return null;
                            break;
                        }
                        if (previous is not null)
                        {
                            var gap = (result.FrameTimestamp - previous.FrameTimestamp).TotalSeconds;
                            if (gap > 0)
                                preroll = gap * 4;
                            DisposeResult(previous);
                        }
                        previous = result;
                    }
                }
                finally
                {
                    if (previous is not null)
                        DisposeResult(previous);
                }
                if (preroll < 0.001)
                    preroll = Math.Clamp((observedInterval ?? video.EstimatedKeyFrameIntervalSeconds) * 2, 0.04, 60);
                else
                    preroll *= 2;
            }
        }
        throw new InvalidOperationException("Unable to locate the adjacent keyframe within the scan budget.");
    }

    private KeyFrameStripFrame CreateFrame(DecodeResult result, Func<long, bool> isCached)
    {
        Bitmap? bitmap = null;
        if (!isCached(result.FramePts) && result.Bitmap is not null)
        {
            var aspect = result.RequiresSampleAspectRatioCorrection && result.SourcePixelSize.Height > 0
                ? result.SourcePixelSize.Width / (double)result.SourcePixelSize.Height : 0;
            bitmap = ImageUtil.CreateThumbnail(result.Bitmap, thumbnailWidth, thumbnailHeight, aspect);
        }
        return new KeyFrameStripFrame(result.FramePts, result.FrameTimestamp, bitmap, result.KeyFrameType);
    }

    private static void DisposeResult(DecodeResult result)
    {
        result.BitmapLease?.Dispose();
        if (result.BitmapLease is null)
            result.Bitmap?.Dispose();
        result.GpuFrame?.Dispose();
    }

    private static KeyFrameStripRange CreateRange(List<KeyFrameStripFrame> frames, double target,
        bool scannedFromStart, long? firstPts, bool atEnd, int frameCount)
    {
        var current = Math.Max(0, frames.FindLastIndex(frame => frame.Timestamp.TotalSeconds <= target));
        var start = Math.Clamp(current - (frameCount - 1) / 2, 0, Math.Max(0, frames.Count - frameCount));
        var visible = frames.Skip(start).Take(frameCount).ToArray();
        // 读到的画面可能比显示的多，只有显示到最后一张时才算到达开头或结尾。
        return new KeyFrameStripRange(visible,
            scannedFromStart && visible.Length > 0 && visible[0].Pts == firstPts,
            atEnd && visible.Length > 0 && visible[^1].Pts == frames[^1].Pts);
    }

    public async ValueTask DisposeAsync()
    {
        var previous = decoder;
        decoder = null;
        if (previous is not null)
            await Task.Run(previous.Dispose);
        await DisposeHelpersAsync();
    }
}
