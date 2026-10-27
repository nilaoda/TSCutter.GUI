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

internal sealed record KeyFrameStripSource(string Path, PreviewStreamSelection? Selection, bool ToneMapping,
    DiscInput? Disc = null);
internal sealed record KeyFrameStripFrame(long Pts, TimeSpan Timestamp, Bitmap? Bitmap, string? KeyFrameType = null);
internal sealed record KeyFrameStripRange(IReadOnlyList<KeyFrameStripFrame> Frames, bool AtStart, bool AtEnd);

internal interface IKeyFrameStripService : IAsyncDisposable
{
    Task OpenAsync(KeyFrameStripSource source, CancellationToken token);
    Task<KeyFrameStripRange> ReadAsync(double target, Func<long, bool> isCached,
        Action<KeyFrameStripFrame> received, CancellationToken token, Action<KeyFrameStripRange>? progress = null, int frameCount = 9);
    Task<KeyFrameStripFrame?> ReadNeighborAsync(KeyFrameStripFrame anchor, int direction,
        Func<long, bool> isCached, CancellationToken token);
}

/// <summary>只扫描指针附近的真实关键帧，不创建全片索引，也不占用主预览解码器。</summary>
internal sealed class KeyFrameStripService : IKeyFrameStripService
{
    private VideoInstance? decoder;
    private long? cursorPts;

    public async Task OpenAsync(KeyFrameStripSource source, CancellationToken token)
    {
        decoder = new VideoInstance(source.Path, enableHardwareDecoding: false,
            source.Selection, () => source.ToneMapping, discInput: source.Disc);
        await decoder.InitVideoAsync(token);
    }

    public async Task<KeyFrameStripRange> ReadAsync(double target, Func<long, bool> isCached,
        Action<KeyFrameStripFrame> received, CancellationToken token, Action<KeyFrameStripRange>? progress = null, int frameCount = 9)
    {
        var video = decoder ?? throw new InvalidOperationException("Strip decoder is not initialized.");
        frameCount = Math.Clamp(frameCount, 1, 48);
        var leftCount = (frameCount - 1) / 2;
        var rightCount = frameCount - 1 - leftCount;
        target = Math.Clamp(target, 0, Math.Max(0, video.GetVideoDurationInSeconds()));
        var interval = Math.Clamp(video.EstimatedKeyFrameIntervalSeconds, 0.04, 30);
        // TS seek 可能落到请求点之后的关键包，多预读一个间隔，减少因前帧不足而重复扫描。
        var start = Math.Max(0, target - interval * (leftCount + 1.5));
        // 估算间隔仅用于选择扫描起点；显示和跳转始终使用解码得到的实际时间。
        // GOP 突然变密时限制预读量，再从目标处向后定位，避免扫描整段视频。
        for (var attempt = 0; attempt < 3; attempt++)
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
                var result = await video.DecodeNextSearchFrameAsync(320, 180, token);
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
                    if (reachedTarget && frames.Count >= frameCount && frames.Count(item => item.Timestamp.TotalSeconds > target) >= rightCount)
                        break;
                }
                finally
                {
                    DisposeResult(result);
                }
            }
            if (frames.Count >= 2)
            {
                var observedInterval = (frames[^1].Timestamp - frames[^2].Timestamp).TotalSeconds;
                if (observedInterval > 0)
                    interval = observedInterval;
            }
            if (attempt == 2)
            {
                // 不可靠的索引可能让 seek 多次落到远处；预算耗尽时保留原图，
                // 不把远离指针的扫描结果作为新的邻域发布。
                if (!reachedTarget && !atEnd)
                    throw new InvalidOperationException("Unable to locate nearby keyframes within the scan budget.");
                return CreateRange(frames, target, start == 0, firstPts, atEnd, frameCount);
            }
            if (reachedTarget || atEnd)
            {
                if (start == 0 || (frames.Count >= frameCount &&
                    frames.Count(frame => frame.Timestamp.TotalSeconds <= target) >= leftCount + 1))
                    return CreateRange(frames, target, start == 0, firstPts, atEnd, frameCount);
                // 可变 GOP 或接近结尾导致前帧不足时，扩大局部窗口补齐图片。
                var missing = Math.Max(1, leftCount + 1 - frames.Count(frame => frame.Timestamp.TotalSeconds <= target));
                if (atEnd)
                    missing = Math.Max(missing, frameCount - frames.Count);
                // 密集 GOP 后接长间隔时，仅用最后一次短间隔会反复定位到同一末帧。
                var distance = frames.Count > 0 ? Math.Max(0, target - frames[0].Timestamp.TotalSeconds) : interval;
                start = Math.Max(0, start - Math.Max(interval, distance) * (missing + 1.5));
            }
            else
            {
                // GOP 变密导致预读预算耗尽，按刚读到的真实间隔重新定位到目标附近。
                var gap = frames.Count >= 2
                    ? (frames[^1].Timestamp - frames[^2].Timestamp).TotalSeconds : 0;
                start = attempt == 1 ? target : Math.Max(0, target - Math.Max(0.001, gap) * (leftCount + 1.5));
            }
        }
        throw new InvalidOperationException();
    }

    public async Task<KeyFrameStripFrame?> ReadNeighborAsync(KeyFrameStripFrame anchor, int direction,
        Func<long, bool> isCached, CancellationToken token)
    {
        var video = decoder ?? throw new InvalidOperationException("Strip decoder is not initialized.");
        if (direction > 0)
        {
            // 顺序前进时保留解码器游标，通常只需再解码一张图片。
            if (cursorPts != anchor.Pts)
            {
                cursorPts = null;
                // 重新定位仍使用缩略图扫描路径，避免恢复游标时切回完整分辨率和完整 GOP 解码。
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var preroll = attempt == 0 ? 0 : Math.Max(0.04, video.EstimatedKeyFrameIntervalSeconds) * 2;
                    await video.SeekBeforeTimeAsync(
                        TimeSpan.FromSeconds(Math.Max(0, anchor.Timestamp.TotalSeconds - preroll)), token);
                    for (var count = 0; count < 48; count++)
                    {
                        var positioned = await video.DecodeNextSearchFrameAsync(320, 180, token);
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
                var result = await video.DecodeNextSearchFrameAsync(320, 180, token);
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
                        var result = await video.DecodeNextSearchFrameAsync(320, 180, token);
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
                    preroll = Math.Clamp(video.EstimatedKeyFrameIntervalSeconds * 2, 0.04, 60);
                else
                    preroll *= 2;
            }
        }
        throw new InvalidOperationException("Unable to locate the adjacent keyframe within the scan budget.");
    }

    private static KeyFrameStripFrame CreateFrame(DecodeResult result, Func<long, bool> isCached)
    {
        Bitmap? bitmap = null;
        if (!isCached(result.FramePts) && result.Bitmap is not null)
        {
            var aspect = result.RequiresSampleAspectRatioCorrection && result.SourcePixelSize.Height > 0
                ? result.SourcePixelSize.Width / (double)result.SourcePixelSize.Height : 0;
            bitmap = ImageUtil.CreateThumbnail(result.Bitmap, 320, 180, aspect);
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
        return new KeyFrameStripRange(visible,
            scannedFromStart && visible.Length > 0 && visible[0].Pts == firstPts, atEnd);
    }

    public async ValueTask DisposeAsync()
    {
        var previous = decoder;
        decoder = null;
        if (previous is not null)
            await Task.Run(previous.Dispose);
    }
}
