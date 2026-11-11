using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.Extensions;
using TSCutter.GUI.FFmpeg;

namespace TSCutter.GUI.Models.Preview;

internal sealed record FrameWindow(IReadOnlyList<DecodeResult> Frames, bool AtStart, bool AtEnd);

// 帧精确模式的导航条：从目标之前的关键帧完整解码，按显示顺序取目标附近的每一帧缩略图及画面类型。
// 只供导航条专用的解码实例使用，不与主预览共享解码器。
public partial class VideoInstance
{
    // 解码器已输出到该帧，下一次顺序读取可直接继续，避免逐帧前进时重复解码整个 GOP。
    private long? frameCursorPts;
    private bool frameDecoderDrained;

    /// <summary>精确解码指定 PTS 的画面（导航条点击任意帧时使用）。</summary>
    internal Task<DecodeResult?> DecodeFrameAtPtsAsync(long pts, CancellationToken cancellationToken = default) =>
        Task.Run(() => RunReadOperation(() => DecodeMatchingFrame(pts, value => value == pts, pts, cancellationToken),
            cancellationToken), cancellationToken);

    internal long SecondsToPts(double seconds) =>
        (long)Math.Round(seconds * timeBase.den / timeBase.num) + firstFrameTimestamp;

    /// <summary>目标帧（不晚于 targetPts 的最后一帧）之前取 before 帧、之后取 after 帧。</summary>
    internal Task<FrameWindow> DecodeFrameWindowAsync(long targetPts, int before, int after, int maxWidth, int maxHeight,
        CancellationToken cancellationToken) =>
        Task.Run(() => RunReadOperation(() => WithEveryFrame(() =>
            DecodeFrameWindow(targetPts, before, after, maxWidth, maxHeight, cancellationToken)), cancellationToken), cancellationToken);

    /// <summary>显示顺序中紧随 anchorPts 的帧；文件结束时返回 null。</summary>
    internal Task<DecodeResult?> DecodeFollowingFrameAsync(long anchorPts, int maxWidth, int maxHeight,
        CancellationToken cancellationToken) =>
        Task.Run(() => RunReadOperation(() => WithEveryFrame(() =>
        {
            if (frameCursorPts == anchorPts)
            {
                var next = DecodeNextAnyFrame(maxWidth, maxHeight, cancellationToken);
                frameCursorPts = next?.FramePts;
                return next;
            }
            var window = DecodeFrameWindow(anchorPts, 0, 1, maxWidth, maxHeight, cancellationToken);
            return KeepOnly(window.Frames, window.Frames.FirstOrDefault(frame => frame.FramePts > anchorPts));
        }), cancellationToken), cancellationToken);

    /// <summary>显示顺序中紧邻 anchorPts 之前的帧；片头返回 null。</summary>
    internal Task<DecodeResult?> DecodePrecedingFrameAsync(long anchorPts, int maxWidth, int maxHeight,
        CancellationToken cancellationToken) =>
        Task.Run(() => RunReadOperation(() => WithEveryFrame(() =>
        {
            var window = DecodeFrameWindow(anchorPts - 1, 0, 0, maxWidth, maxHeight, cancellationToken);
            // 锚点是首帧时窗口被限制到锚点本身，此时没有上一帧，取到的画面同样要释放。
            return KeepOnly(window.Frames, window.Frames.LastOrDefault(frame => frame.FramePts < anchorPts));
        }), cancellationToken), cancellationToken);

    /// <summary>
    /// 锚点之前最多 count 帧，按与锚点的距离排序（最近的在前）；片头时可能少于 count。
    /// 一次定位、一次解码取回全部画面，避免每补一张都从关键帧重新解码整个 GOP。
    /// </summary>
    internal Task<IReadOnlyList<DecodeResult>> DecodePrecedingFramesAsync(long anchorPts, int count, int maxWidth, int maxHeight,
        CancellationToken cancellationToken) =>
        Task.Run(() => RunReadOperation(() => WithEveryFrame<IReadOnlyList<DecodeResult>>(() =>
        {
            var window = DecodeFrameWindow(anchorPts - 1, Math.Max(0, count - 1), 0, maxWidth, maxHeight, cancellationToken);
            var kept = window.Frames.Where(frame => frame.FramePts < anchorPts).TakeLast(count).ToList();
            foreach (var frame in window.Frames.Except(kept))
                DisposeFrame(frame);
            kept.Reverse();
            return kept;
        }), cancellationToken), cancellationToken);

    /// <summary>
    /// 锚点之后最多 count 帧，按显示顺序排列；文件结束时可能少于 count。解码器已停在锚点时顺序继续，否则重新定位一次。
    /// </summary>
    internal Task<IReadOnlyList<DecodeResult>> DecodeFollowingFramesAsync(long anchorPts, int count, int maxWidth, int maxHeight,
        CancellationToken cancellationToken) =>
        Task.Run(() => RunReadOperation(() => WithEveryFrame<IReadOnlyList<DecodeResult>>(() =>
        {
            if (frameCursorPts != anchorPts)
            {
                var window = DecodeFrameWindow(anchorPts, 0, count, maxWidth, maxHeight, cancellationToken);
                var later = window.Frames.Where(frame => frame.FramePts > anchorPts).Take(count).ToList();
                foreach (var frame in window.Frames.Except(later))
                    DisposeFrame(frame);
                return later;
            }
            var frames = new List<DecodeResult>(count);
            try
            {
                while (frames.Count < count)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var next = DecodeNextAnyFrame(maxWidth, maxHeight, cancellationToken);
                    frameCursorPts = next?.FramePts;
                    if (next is null)
                        break;
                    frames.Add(next);
                }
            }
            catch
            {
                frameCursorPts = null;
                foreach (var frame in frames)
                    DisposeFrame(frame);
                throw;
            }
            return frames;
        }), cancellationToken), cancellationToken);

    /// <summary>释放窗口中除 keep 之外的画面，返回 keep。</summary>
    private static DecodeResult? KeepOnly(IReadOnlyList<DecodeResult> frames, DecodeResult? keep)
    {
        foreach (var frame in frames)
            if (!ReferenceEquals(frame, keep))
                DisposeFrame(frame);
        return keep;
    }

    // 送入完整码流并输出每一帧；画面类型按导航条的规则标注（关键包的 IDR/CRA 等，其余为 I/P/B）。
    private T WithEveryFrame<T>(Func<T> action) => WithFullDecoder(() => WithEveryFrameCore(action));

    private T WithEveryFrameCore<T>(Func<T> action)
    {
        var scanMode = isSearchScan;
        isSearchScan = true;
        exactFrameFilter = static _ => true;
        SetKeyFramesOnly(false);
        try { return action(); }
        finally
        {
            exactFrameFilter = null;
            isSearchScan = scanMode;
            SetKeyFramesOnly(true);
        }
    }

    private FrameWindow DecodeFrameWindow(long targetPts, int before, int after, int maxWidth, int maxHeight,
        CancellationToken token)
    {
        targetPts = Math.Clamp(targetPts, firstFrameTimestamp, Math.Max(firstFrameTimestamp, maxPts));
        var frameTicks = Math.Max(1, inVideoStream.AvgFrameRate is { num: > 0, den: > 0 } rate
            ? (long)Math.Round((double)rate.den * timeBase.den / ((double)rate.num * timeBase.num)) : keyFrameGap / 25);
        var total = before + 1 + after;
        // 关键帧到窗口之间的画面只用于推进解码，不转换缩略图。按平均帧率留出余量；
        // 帧率不均导致跳过后窗口前的画面不足时，改为全部转换重试同一位置。
        var skipBefore = targetPts - frameTicks * (total + 2);
        var distance = Math.Max(1, keyFrameGap);
        for (var attempt = 0; attempt < 6; attempt++, distance *= 2)
        {
            var start = targetPts - distance - frameTicks * (before + 1);
            var window = TryDecodeFrameWindow(start, targetPts, before, after, skipBefore, maxWidth, maxHeight, token,
                out var skippedTooMany);
            if (skippedTooMany)
            {
                skipBefore = long.MinValue;
                window = TryDecodeFrameWindow(start, targetPts, before, after, skipBefore, maxWidth, maxHeight, token, out _);
            }
            if (window is not null)
                return window;
        }
        throw new InvalidOperationException("Unable to decode the frames near the requested position.");
    }

    /// <summary>
    /// 从 start 定位解码一次窗口；定位落在目标之后或前面的画面不足时返回 null，由调用方扩大预读。
    /// 早于 skipBefore 的画面不转换缩略图；因此缺少窗口所需的前面画面时 skippedTooMany 为 true。
    /// 任何异常（含取消）都会释放已取得的画面。
    /// </summary>
    private FrameWindow? TryDecodeFrameWindow(long start, long targetPts, int before, int after, long skipBefore,
        int maxWidth, int maxHeight, CancellationToken token, out bool skippedTooMany)
    {
        skippedTooMany = false;
        var fromStart = start <= firstFrameTimestamp;
        // 片头处 TS 带 B 帧时须从首帧之前预读，与关键帧导航相同。
        Seek(fromStart ? firstFrameTimestamp - Math.Max(1, keyFrameGap * 2) : start, AVSEEK_FLAG.Backward);
        frameDecoderDrained = false;
        frameCursorPts = null;
        // 片头前面的画面不足时向后多取，片尾后面的画面不足时保留更多前面的画面，使窗口总数一致。
        var total = before + 1 + after;
        var kept = new List<DecodeResult>();
        var earlier = 0;
        var later = 0;
        var wanted = after;
        var late = false;
        var dropped = false;
        var skipped = false;
        var atEnd = false;
        var first = true;
        var previousFilter = exactFrameFilter;
        // 时间轴从首个关键帧开始；片头关键帧之前显示的前导画面不在可编辑范围内，同样不转换。
        exactFrameFilter = pts =>
        {
            if (pts < firstFrameTimestamp)
                return false;
            if (pts >= skipBefore)
                return true;
            skipped = true;
            return false;
        };
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var frame = DecodeNextAnyFrame(maxWidth, maxHeight, token);
                if (frame is null)
                {
                    atEnd = true;
                    break;
                }
                // 定位落在目标之后时，前面的画面无法取得，扩大预读重试。
                if (first && frame.FramePts > targetPts && !fromStart)
                {
                    DisposeFrame(frame);
                    late = true;
                    break;
                }
                first = false;
                if (frame.FramePts <= targetPts)
                {
                    kept.Add(frame);
                    if (++earlier > total)
                    {
                        DisposeFrame(kept[0]);
                        kept.RemoveAt(0);
                        earlier--;
                        dropped = true;
                    }
                    continue;
                }
                if (later == 0)
                    wanted = Math.Max(after, total - Math.Min(earlier, before + 1));
                if (later >= wanted)
                {
                    DisposeFrame(frame);
                    break;
                }
                kept.Add(frame);
                if (++later == wanted)
                {
                    frameCursorPts = frame.FramePts;
                    break;
                }
            }
        }
        catch
        {
            foreach (var frame in kept)
                DisposeFrame(frame);
            throw;
        }
        finally { exactFrameFilter = previousFilter; }
        var needed = Math.Max(before + 1, total - later);
        // 跳过了转换却缺少前面的画面（包括定位并未落后、只是余量内没有画面）：需全部转换重试。
        if (skipped && (late || earlier < needed))
        {
            skippedTooMany = true;
            foreach (var frame in kept)
                DisposeFrame(frame);
            return null;
        }
        if (!late && (dropped || skipped || fromStart || earlier > before))
        {
            // 只保留窗口所需的前面画面；后面画面不足时多保留前面的画面。
            var trim = Math.Max(0, earlier - needed);
            foreach (var frame in kept.Take(trim))
                DisposeFrame(frame);
            kept.RemoveRange(0, trim);
            return new FrameWindow(kept, fromStart && !dropped && !skipped && trim == 0, atEnd);
        }
        foreach (var frame in kept)
            DisposeFrame(frame);
        return null;
    }

    /// <summary>按显示顺序输出下一帧：先取解码器中已有的输出，再读取新包，文件结束时排空解码器。</summary>
    private DecodeResult? DecodeNextAnyFrame(int maxWidth, int maxHeight, CancellationToken token)
    {
        var pending = DecodePacket(null, token, maxWidth, maxHeight, receiveOnly: true, preserveRemainingOutput: true);
        if (pending is not null)
            return pending;
        if (!frameDecoderDrained)
        {
            foreach (var packet in inFc.ReadPackets())
            {
                token.ThrowIfCancellationRequested();
                activeReadDeadline?.ThrowIfInterrupted();
                if (packet.StreamIndex != videoStreamIndex)
                    continue;
                var result = DecodePacket(packet, token, maxWidth, maxHeight, preserveRemainingOutput: true);
                if (result is not null)
                    return result;
            }
            frameDecoderDrained = true;
            var flushed = DecodePacket(null, token, maxWidth, maxHeight, preserveRemainingOutput: true);
            if (flushed is not null)
                return flushed;
        }
        return DecodePacket(null, token, maxWidth, maxHeight, receiveOnly: true, preserveRemainingOutput: true);
    }

    private static void DisposeFrame(DecodeResult result)
    {
        result.BitmapLease?.Dispose();
        if (result.BitmapLease is null)
            result.Bitmap?.Dispose();
        result.GpuFrame?.Dispose();
    }
}
