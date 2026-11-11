using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models.Preview;

namespace TSCutter.GUI.Services.Preview;

// 关键帧模式的并行扫描：把导航条需要覆盖的时间范围切成几段，每段由一个单线程软解码实例同时读取。
// 只解关键帧时单个解码器的多线程几乎不提速（每个关键帧独立解码），多实例并行才能缩短 4K HEVC 等的加载时间。
internal sealed partial class KeyFrameStripService
{
    /// <summary>
    /// 同时扫描的解码实例数（含主实例）。只用少量并行：每路是一个单线程关键帧解码器，内存只多一两个解码器的量。最多占用四分之一的逻辑核心，
    /// 让主预览的解码和界面始终有空闲的核心（Apple 芯片约一半是能效核心）。
    /// </summary>
    internal static int ParallelScanCount => Math.Clamp(Environment.ProcessorCount / 4, 1, 3);

    /// <summary>本服务同时扫描的实例数；为 1 时只用主实例顺序扫描。</summary>
    internal int ScanInstanceCount { get; init; } = ParallelScanCount;

    private readonly List<VideoInstance> helpers = [];

    /// <summary>
    /// 一次读取的扫描状态。每个关键包在解码前由某一段“认领”：更早的段已认领则跳过，更晚的段已认领则停止，
    /// 同一关键包不会重复解码；某段因预算用尽留下的空隙由之后的补读填上。相邻关系只按实际读到的前后关键包登记，画面乱序到达也能确定是否连续；
    /// 认领后解不出画面的关键包记为空包，拼接时跨过。
    /// </summary>
    private sealed class ScanState
    {
        public readonly object Sync = new();
        public readonly Dictionary<long, KeyFrameStripFrame> Frames = [];
        public readonly ConcurrentDictionary<long, int> Owners = new();
        public readonly Dictionary<long, long> Next = [];
        public readonly Dictionary<long, long> Previous = [];
        public readonly HashSet<long> Empty = [];
        public long? FileStartPts;
        public long? FileEndPts;
    }

    private sealed record ScanRun(List<KeyFrameStripFrame> Frames, bool AtStart, bool AtEnd);

    /// <summary>一段读取的结果。<see cref="Exhausted"/> 表示预算用尽而结束，可从 <see cref="LastPacketPts"/> 之后接着读。</summary>
    private readonly record struct SliceResult(long? CursorPts, long? LastPacketPts = null, bool Exhausted = false);

    private async Task OpenHelpersAsync(KeyFrameStripSource source, CancellationToken token)
    {
        var count = ScanInstanceCount - 1;
        if (source.EveryFrame || count <= 0 || decoder is not { SupportsParallelKeyFrameScan: true })
            return;
        // 每个实例打开后立即登记：中途取消时，已打开的实例随服务一起释放，不会遗留打开的源文件。
        await Task.WhenAll(Enumerable.Range(0, count).Select(async _ =>
        {
            var helper = new VideoInstance(source.Path, enableHardwareDecoding: false, source.Selection,
                () => source.ToneMapping, discInput: source.Disc) { ThumbnailDecoding = true };
            try
            {
                await helper.InitVideoAsync(token);
                if (helper.SupportsParallelKeyFrameScan)
                {
                    lock (helpers)
                        helpers.Add(helper);
                    return;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // 辅助实例只用于加速；打开失败时仍由主实例完成读取。
                Console.Error.WriteLine($"Keyframe strip helper decoder unavailable: {exception.Message}");
            }
            catch (OperationCanceledException)
            {
                await Task.Run(helper.Dispose);
                throw;
            }
            await Task.Run(helper.Dispose);
        }));
    }

    private async Task DisposeHelpersAsync()
    {
        VideoInstance[] previous;
        lock (helpers)
        {
            previous = helpers.ToArray();
            helpers.Clear();
        }
        foreach (var helper in previous)
            await Task.Run(helper.Dispose);
    }

    /// <summary>
    /// 并行读取目标附近的关键帧窗口。无法确认画面连续（例如解码器中途改为完整解码）时返回 null，
    /// 由调用方按原来的顺序扫描读取。
    /// </summary>
    private async Task<KeyFrameStripRange?> ReadParallelAsync(VideoInstance primary, double target, int leftCount,
        int rightCount, int frameCount, Func<long, bool> isCached, Action<KeyFrameStripFrame> received,
        Action<KeyFrameStripRange>? progress, CancellationToken token)
    {
        var interval = Math.Clamp(observedInterval ?? primary.EstimatedKeyFrameIntervalSeconds, 0.04, 30);
        var leftSpan = leftWindowSpan is { } span && leftWindowCount > 0 ? span * leftCount / leftWindowCount : interval * leftCount;
        var from = Math.Max(0, target - Math.Max(leftSpan, interval * leftCount) - interval * 1.5);
        var to = target + interval * (rightCount + 1.5);
        var state = new ScanState();
        var budget = Math.Max(48, frameCount * 4);
        VideoInstance[] usable;
        lock (helpers)
            usable = helpers.Where(helper => helper.SupportsParallelKeyFrameScan).ToArray();
        // 没有可用的辅助实例时按原来的顺序扫描读取。
        if (usable.Length == 0)
            return null;
        // 每段至少约 3 个关键帧，段太短时定位和段间重叠的开销超过并行收益。
        var count = (int)Math.Clamp(Math.Floor((to - from) / interval / 3), 1, usable.Length + 1);
        var slices = new Task<SliceResult>[count];
        for (var index = 0; index < count; index++)
        {
            var start = from + (to - from) * index / count;
            // 最后一段由主实例读取：它按时间结束，之后可以接着向右补图，也保持相邻关键帧导航的游标。
            var last = index == count - 1;
            slices[index] = ScanSliceAsync(last ? primary : usable[index], index, start, last ? to : null, budget,
                state, target, frameCount, isCached, received, progress, token);
        }
        var results = await Task.WhenAll(slices);
        cursorPts = results[^1].CursorPts;
        var nextRank = count;
        var leftReach = 1;
        var rightReach = 1;
        SliceResult? leftSlice = null;
        var leftRank = 0;
        for (var attempt = 0; attempt < MaximumWindowAttempts; attempt++)
        {
            if (BuildRun(state, target) is not { } scanned)
                return null;
            var (run, atStart, atEnd) = scanned;
            var before = run.Count(frame => frame.Timestamp.TotalSeconds <= target);
            var after = run.Count - before;
            // 与顺序扫描相同：一侧到达文件开头或结尾时，由另一侧多取画面补满整条预览。
            var wantBefore = atEnd ? Math.Max(leftCount + 1, frameCount - after) : leftCount + 1;
            var wantAfter = atStart ? Math.Max(rightCount, frameCount - before) : rightCount;
            var leftDone = atStart || before >= wantBefore;
            var rightDone = atEnd || after >= wantAfter;
            if (leftDone && rightDone || attempt == MaximumWindowAttempts - 1)
                return RememberWindow(CreateRange(run, target, atStart, run[0].Pts, atEnd, frameCount), target, leftCount);
            // 只补缺少的一侧，已经读过的画面不再解码。某段可能因预算用尽没有读到下一段，空隙可能紧挨着连续段，
            // 所以补读都以最高的序号重新定位：已认领的关键包跳过，只解出空缺的画面。
            // 向右由主实例从连续段末尾附近读取，越过段末后补够缺少的张数，或遇到已认领的关键包（与之后读过的部分接上）即止。
            // TS 定位可能越过最后一个关键帧，起点再往前留出余量，仍未读到段末时每次加倍。
            if (!rightDone)
            {
                var end = run[^1].Timestamp.TotalSeconds;
                var later = run.SkipWhile(frame => frame.Timestamp.TotalSeconds <= target).ToList();
                var gap = later.Count >= 2
                    ? (later[^1].Timestamp - later[0].Timestamp).TotalSeconds / (later.Count - 1) : 0;
                var back = (gap > 0 ? Math.Clamp(gap, 0.04, 30) : interval) * 1.5 * rightReach;
                rightReach *= 2;
                var result = await ScanSliceAsync(primary, nextRank++, Math.Max(0, end - back), null, budget, state,
                    target, frameCount, isCached, received, progress, token, skipThroughPts: run[^1].Pts,
                    newFrames: wantAfter - after);
                cursorPts = result.CursorPts;
            }
            // 向左从更早处读到连续段的第一张为止。可变 GOP 下整段的平均间隔可能远小于左侧的实际间隔，
            // 按左侧画面的间隔估算起点，仍不够时每次加倍。上一次向左读取因预算用尽而没有读到连续段时，
            // 空隙就在它停下的位置之后，接着读而不是再往前定位。
            if (!leftDone)
            {
                if (leftSlice is { Exhausted: true } resume)
                    leftSlice = await ScanSliceAsync(usable[0], leftRank, null, null, budget, state, target, frameCount,
                        isCached, received, progress, token, skipThroughPts: run[0].Pts - 1, previousPacket: resume.LastPacketPts);
                else
                {
                    var earlier = run.TakeWhile(frame => frame.Timestamp.TotalSeconds <= target).ToList();
                    var gap = earlier.Count >= 2
                        ? (earlier[^1].Timestamp - earlier[0].Timestamp).TotalSeconds / (earlier.Count - 1) : 0;
                    var reach = (gap > 0 ? Math.Clamp(gap, 0.04, 30) : interval) * (wantBefore - before + 1.5) * leftReach;
                    leftReach *= 2;
                    leftRank = nextRank++;
                    leftSlice = await ScanSliceAsync(usable[0], leftRank, Math.Max(0, run[0].Timestamp.TotalSeconds - reach),
                        null, budget, state, target, frameCount, isCached, received, progress, token,
                        skipThroughPts: run[0].Pts - 1);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 读取一段：定位到 <paramref name="start"/> 之前的关键帧后顺序解码（为 null 时接着上次的位置，上一个关键包为
    /// <paramref name="previousPacket"/>）。遇到更晚的段已认领的关键包时停止，
    /// 遇到更早的段已认领的跳过；指定 <paramref name="skipThroughPts"/> 时，PTS 大于它的已认领关键包也停止。
    /// 指定 <paramref name="stopAt"/> 时读到其后的第一张画面为止，指定 <paramref name="newFrames"/> 时新增这么多张画面即止。
    /// 结束时取出解码器中仍缓存的画面；认领后始终没有画面的关键包记为空包。
    /// </summary>
    private async Task<SliceResult> ScanSliceAsync(VideoInstance video, int rank, double? start, double? stopAt,
        int budget, ScanState state, double target, int frameCount, Func<long, bool> isCached,
        Action<KeyFrameStripFrame> received, Action<KeyFrameStripRange>? progress, CancellationToken token,
        long? skipThroughPts = null, int? newFrames = null, long? previousPacket = null)
    {
        if (start is { } seconds)
            await video.SeekBeforeTimeAsync(TimeSpan.FromSeconds(seconds), token);
        var previous = start is null ? previousPacket : null;
        var stopped = false;
        var fromFileStart = start == 0;
        var claims = new List<long>();
        var fresh = 0;
        KeyPacketDecision Filter(long pts)
        {
            lock (state.Sync)
            {
                if (previous is { } last && last != pts)
                {
                    state.Next[last] = pts;
                    state.Previous[pts] = last;
                }
                else if (previous is null && fromFileStart)
                    state.FileStartPts ??= pts;
                previous = pts;
            }
            if (state.Owners.TryAdd(pts, rank) || state.Owners[pts] == rank)
            {
                lock (state.Sync)
                    claims.Add(pts);
                return KeyPacketDecision.Decode;
            }
            if (state.Owners[pts] > rank || pts > skipThroughPts)
            {
                stopped = true;
                return KeyPacketDecision.Stop;
            }
            return KeyPacketDecision.Skip;
        }

        long? cursor = null;
        try
        {
            for (var count = 0; count < budget; count++)
            {
                var result = await video.DecodeNextSearchFrameAsync(thumbnailWidth, thumbnailHeight, token, Filter);
                if (result is null)
                {
                    if (!stopped)
                    {
                        lock (state.Sync)
                            state.FileEndPts = previous;
                        return new SliceResult(cursor, previous);
                    }
                    // 停止前已送入解码器的关键帧可能仍在重排序缓存中。
                    await DrainAsync();
                    return new SliceResult(null, previous);
                }
                if (Accept(result) is { } timestamp)
                {
                    cursor = Math.Max(cursor ?? long.MinValue, result.FramePts);
                    if (stopAt is { } end && timestamp >= end || fresh >= newFrames)
                    {
                        await DrainAsync();
                        return new SliceResult(cursor, previous);
                    }
                }
            }
            await DrainAsync();
            return new SliceResult(cursor, previous, Exhausted: true);
        }
        finally
        {
            // 这一段已经结束并取出了缓存的画面，认领后仍没有画面的关键包确实解不出画面。
            lock (state.Sync)
                foreach (var pts in claims.Where(pts => !state.Frames.ContainsKey(pts)))
                    state.Empty.Add(pts);
        }

        async Task DrainAsync()
        {
            foreach (var remaining in await video.DrainSearchFramesAsync(thumbnailWidth, thumbnailHeight, token))
                if (Accept(remaining) is not null)
                    cursor = Math.Max(cursor ?? long.MinValue, remaining.FramePts);
        }

        double? Accept(DecodeResult result)
        {
            try
            {
                if (result.FrameTimestamp.TotalSeconds < 0)
                    return null;
                var frame = CreateFrame(result, isCached);
                bool added;
                lock (state.Sync)
                {
                    added = state.Frames.TryAdd(frame.Pts, frame with { Bitmap = null });
                    // 关键帧按顺序输出：这张画面之前认领、却没有输出画面的关键包是空包。
                    var index = claims.IndexOf(frame.Pts);
                    for (var earlier = 0; earlier < index; earlier++)
                        if (!state.Frames.ContainsKey(claims[earlier]))
                            state.Empty.Add(claims[earlier]);
                }
                if (!added)
                {
                    frame.Bitmap?.Dispose();
                    return frame.Timestamp.TotalSeconds;
                }
                fresh++;
                // 回调接管位图；随后发布已确认连续、包含目标的部分窗口。
                received(frame);
                if (progress is not null && BuildRun(state, target) is { } run)
                    progress(CreateRange(run.Frames, target, run.AtStart, run.Frames[0].Pts, run.AtEnd, frameCount));
                return frame.Timestamp.TotalSeconds;
            }
            finally
            {
                DisposeResult(result);
            }
        }
    }

    /// <summary>
    /// 取包含目标的连续关键帧：从目标所在画面（不晚于目标的最后一张，且已确认下一张晚于目标或已到文件结尾）
    /// 沿相邻关系向两侧延伸，跨过空包，遇到尚未解出的画面即止。目标所在画面还不能确认时返回 null。
    /// </summary>
    private static ScanRun? BuildRun(ScanState state, double target)
    {
        lock (state.Sync)
        {
            if (state.Frames.Count == 0)
                return null;
            var anchor = state.Frames.Values.Where(frame => frame.Timestamp.TotalSeconds <= target)
                .MaxBy(frame => frame.Timestamp);
            if (anchor is null)
            {
                // 目标早于第一个关键帧：从文件开头读到的第一张画面即为当前画面。
                if (state.FileStartPts is not { } first || !state.Frames.TryGetValue(first, out anchor))
                    return null;
            }
            // 画面恰好在目标处时，之后不可能再有不晚于目标的画面，无需等下一张确认。
            else if (anchor.Timestamp.TotalSeconds < target)
            {
                if (Step(state, anchor.Pts, forward: true, out var ended) is { } following)
                {
                    if (following.Timestamp.TotalSeconds <= target)
                        return null;
                }
                else if (!ended)
                    return null;
            }
            var run = new List<KeyFrameStripFrame> { anchor };
            var atStart = false;
            var atEnd = false;
            for (var frame = anchor; run.Count < 256;)
            {
                if (Step(state, frame.Pts, forward: false, out var reached) is not { } earlier)
                {
                    atStart = reached;
                    break;
                }
                run.Insert(0, earlier);
                frame = earlier;
            }
            for (var frame = anchor; run.Count < 512;)
            {
                if (Step(state, frame.Pts, forward: true, out var reached) is not { } later)
                {
                    atEnd = reached;
                    break;
                }
                run.Add(later);
                frame = later;
            }
            return new ScanRun(run, atStart, atEnd);
        }
    }

    /// <summary>
    /// 沿相邻关系跳过空包，返回下一张（或上一张）有画面的关键帧；还不确定时返回 null。
    /// <paramref name="boundary"/> 表示已到文件开头或结尾。调用方持有 <see cref="ScanState.Sync"/>。
    /// </summary>
    private static KeyFrameStripFrame? Step(ScanState state, long pts, bool forward, out bool boundary)
    {
        var links = forward ? state.Next : state.Previous;
        boundary = false;
        for (var guard = 0; guard < 64; guard++)
        {
            if (!links.TryGetValue(pts, out var neighbor))
            {
                boundary = forward ? pts == state.FileEndPts : pts == state.FileStartPts;
                return null;
            }
            if (state.Frames.TryGetValue(neighbor, out var frame))
                return frame;
            if (!state.Empty.Contains(neighbor))
                return null;
            pts = neighbor;
        }
        return null;
    }
}
