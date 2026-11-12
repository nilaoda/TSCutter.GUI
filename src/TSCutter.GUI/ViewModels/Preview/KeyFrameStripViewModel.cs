using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Services.Preview;

namespace TSCutter.GUI.ViewModels.Preview;

public sealed record KeyFrameStripItem(long Pts, TimeSpan Timestamp, Bitmap? Bitmap, bool IsCurrent, string? KeyFrameType = null)
{
    public bool IsPlaceholder => Pts == long.MinValue;
}

/// <summary>在 UI 线程复用相邻帧，只调度一个后台解码工作流。</summary>
public sealed class KeyFrameStripViewModel : ObservableObject
{
    private const int CacheLimit = 64;
    internal const int MaximumVisibleCount = 48;

    /// <summary>
    /// 非立即的请求（拖动、逐个跳转）停下多久才开始解码：停住后很快开始加载，连续操作时又不会反复启动和取消解码。
    /// </summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(100);
    public int VisibleCount { get; private set; } = 9;
    private double? targetTime;
    private readonly Func<IKeyFrameStripService> createService;
    private readonly Dictionary<long, KeyFrameStripFrame> cache = new();
    private readonly LinkedList<long> cacheOrder = new();
    private readonly Dictionary<long, long> previousFrames = new();
    private readonly Dictionary<long, long> nextFrames = new();
    private long? firstPts, lastPts;
    private long? windowAnchorPts;
    private KeyFrameStripSource? source;
    private KeyFrameStripSource? serviceSource;
    private IKeyFrameStripService? service;
    private CancellationTokenSource? active;
    private Request? pending;
    private Request? requested;
    private bool running;
    private long sourceGeneration;
    private long requestGeneration;
    private IReadOnlyList<KeyFrameStripItem> items = [];

    public KeyFrameStripViewModel() : this(() => new KeyFrameStripService()) { }
    internal KeyFrameStripViewModel(Func<IKeyFrameStripService> createService) => this.createService = createService;
    public IReadOnlyList<KeyFrameStripItem> Items
    {
        get => items;
        private set => SetProperty(ref items, value);
    }
    internal Task IdleTask { get; private set; } = Task.CompletedTask;
    internal int CachedCount => cache.Count;
    internal bool IsSuspended { get; private set; }

    internal void SetSource(KeyFrameStripSource? value)
    {
        if (source == value)
            return;
        Cancel();
        source = value;
        sourceGeneration++;
        targetTime = null;
        windowAnchorPts = null;
        Items = [];
        if (IsSuspended && source is not null)
            PublishEmpty();
        firstPts = lastPts = null;
        previousFrames.Clear();
        nextFrames.Clear();
        foreach (var frame in cache.Values)
            frame.Bitmap?.Dispose();
        cache.Clear();
        cacheOrder.Clear();
        StartWorker();
    }

    public void SetVisibleCount(int count)
    {
        count = Math.Clamp(count, 1, MaximumVisibleCount);
        if (VisibleCount == count)
            return;
        VisibleCount = count;
        windowAnchorPts = null;
        if (IsSuspended)
        {
            PublishEmpty();
            return;
        }
        if (source is null || targetTime is not { } time)
            return;
        var current = FindCachedTarget(time, requested?.Pts);
        if (current is { } pts)
            PublishCached(pts);
        // 缩放时先复用已有图片，停住后再补齐，避免每跨过一个槽位就重启解码。
        requested = null;
        RequestAt(time);
    }

    // 播放时保留缓存，但只显示占位并停止补图；恢复位置由主窗口在暂停时指定。
    internal void SetSuspended(bool value)
    {
        if (IsSuspended == value)
            return;
        IsSuspended = value;
        if (value)
        {
            Cancel();
            PublishEmpty();
        }
    }

    public void Cancel()
    {
        requestGeneration++;
        requested = null;
        pending = null;
        active?.Cancel();
    }

    // 按下导航按钮即可移动缓存图片，无需等待主预览完成解码。
    public bool PreviewStep(int direction)
    {
        var current = Items.FirstOrDefault(item => item.IsCurrent && !item.IsPlaceholder);
        var neighbors = direction > 0 ? nextFrames : previousFrames;
        if (current is not null && neighbors.TryGetValue(current.Pts, out var pts) && cache.TryGetValue(pts, out var frame))
        {
            RequestAt(frame.Timestamp.TotalSeconds, currentPts: pts);
            return true;
        }
        // 连续单步快于补图时仍可移动已有槽位；未知位置只显示占位，不伪造时间或 PTS。
        var index = Items.ToList().FindIndex(item => item.IsCurrent);
        var target = index + direction;
        if (index < 0 || target < 0 || target >= Items.Count || !Items[target].IsPlaceholder)
            return false;
        Cancel();
        var shifted = Items.Select(item => item with { IsCurrent = false }).ToList();
        shifted[target] = shifted[target] with { IsCurrent = true };
        Items = shifted;
        return true;
    }

    public void RequestAt(double time, bool immediate = false, long? currentPts = null)
    {
        if (IsSuspended || source is null || !double.IsFinite(time))
            return;
        time = Math.Max(0, time);
        var direction = targetTime is { } previousTime ? time.CompareTo(previousTime) : 0;
        targetTime = time;
        var pts = FindCachedTarget(time, currentPts);
        var loadNow = immediate || currentPts is not null;
        // 主预览确认同一位置时继续已有加载，避免刚填好的图片被重复清空。
        if (requested is { } previous && previous.Time == time &&
            (previous.Pts is null || currentPts is null || previous.Pts == currentPts) &&
            (previous.Started == 0 || !loadNow))
            return;
        Cancel();
        if (pts is null)
            PublishEmpty();
        else if (loadNow)
            PublishCached(pts.Value);
        pending = requested = new Request(time, loadNow ? 0 : Stopwatch.GetTimestamp(), requestGeneration,
            currentPts ?? pts, VisibleCount, direction);
        StartWorker();
    }

    private void PublishEmpty()
    {
        windowAnchorPts = null;
        var empty = Enumerable.Range(0, VisibleCount)
            .Select(_ => new KeyFrameStripItem(long.MinValue, default, null, false)).ToArray();
        if (!Items.SequenceEqual(empty))
            Items = empty;
    }

    private long? FindCachedTarget(double time, long? pts)
    {
        if (pts is { } exact)
            return cache.ContainsKey(exact) ? exact : null;
        var matching = cache.Values.FirstOrDefault(frame => frame.Timestamp.TotalSeconds == time);
        if (matching is not null)
            return matching.Pts;
        var preceding = cache.Values.Where(frame => frame.Timestamp.TotalSeconds <= time)
            .MaxBy(frame => frame.Timestamp);
        if (preceding is not null && (lastPts == preceding.Pts ||
            nextFrames.TryGetValue(preceding.Pts, out var next) && cache.TryGetValue(next, out var following) &&
            following.Timestamp.TotalSeconds > time))
            return preceding.Pts;
        return null;
    }

    private void StartWorker()
    {
        if (running)
            return;
        running = true;
        IdleTask = ProcessAsync();
    }

    private async Task ProcessAsync()
    {
        try
        {
            while (true)
            {
                if (serviceSource != source)
                {
                    if (service is not null)
                        await service.DisposeAsync();
                    service = null;
                    serviceSource = null;
                }
                if (pending is not { } request || source is not { } requestedSource)
                    break;
                pending = null;
                using var cancellation = new CancellationTokenSource();
                active = cancellation;
                var generation = sourceGeneration;
                try
                {
                    if (request.Started != 0)
                    {
                        var delay = SettleDelay - Stopwatch.GetElapsedTime(request.Started);
                        if (delay > TimeSpan.Zero)
                            await Task.Delay(delay, cancellation.Token);
                    }
                    cancellation.Token.ThrowIfCancellationRequested();
                    var knownPts = FindCachedTarget(request.Time, request.Pts);
                    if (knownPts is { } cachedPts)
                    {
                        PublishCached(cachedPts);
                        if (Items.All(item => !item.IsPlaceholder) && !NeedsPrefetch(request, cachedPts))
                            continue;
                    }
                    if (knownPts is null)
                        PublishEmpty();
                    if (service is null)
                    {
                        var opening = createService();
                        try
                        {
                            await opening.OpenAsync(requestedSource, cancellation.Token);
                        }
                        catch
                        {
                            // 打开中途取消或失败：释放已打开的解码实例，否则源文件要等垃圾回收才关闭（Windows 上无法删除或改名）。
                            await opening.DisposeAsync();
                            throw;
                        }
                        service = opening;
                        serviceSource = requestedSource;
                    }
                    // 缓存只覆盖目标及右侧时，从整屏左边界一次向右读取，避免逐张向前 seek。
                    if (request.Started == 0 && !requestedSource.EveryFrame && knownPts is { } partial &&
                        BuildWindow(partial).Items.TakeWhile(item => item.IsPlaceholder).Count() > 1)
                    {
                        await ReadWindowAsync(request, generation, cancellation.Token);
                    }
                    else if (knownPts is { } current)
                    {
                        try
                        {
                            await FillEdgesAsync(request, current, generation, cancellation.Token);
                        }
                        catch (InvalidOperationException exception) when (IsCurrent(request, generation, cancellation.Token))
                        {
                            // 相邻帧定位的预算不足时重读当前局部窗口，不让少量缓存图片一直停在未补齐状态。
                            Console.Error.WriteLine($"Keyframe strip neighbor lookup failed; reloading nearby frames: {exception.Message}");
                            await ReadWindowAsync(request, generation, cancellation.Token);
                        }
                    }
                    else
                    {
                        await ReadWindowAsync(request, generation, cancellation.Token);
                    }
                    if (IsCurrent(request, generation, cancellation.Token) &&
                        Items.FirstOrDefault(item => item.IsCurrent && !item.IsPlaceholder) is { } selected)
                        await PrefetchEdgeAsync(request, selected.Pts, generation, cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"Keyframe strip: {exception}");
                    if (service is not null)
                        await service.DisposeAsync();
                    service = null;
                    serviceSource = null;
                }
                finally
                {
                    active = null;
                    if (request.Generation == requestGeneration)
                        requested = null;
                }
            }
        }
        finally { running = false; }
    }

    private async Task ReadWindowAsync(Request request, long generation, CancellationToken token)
    {
        var range = await service!.ReadAsync(request.Time, cache.ContainsKey,
            frame => Receive(frame, generation), token,
            range => PublishProgress(range, request, generation, token), request.Count);
        if (IsCurrent(request, generation, token))
        {
            PublishProgress(range, request, generation, token);
            // 扫描窗口以新目标居中，界面可能仍沿用之前的窗口锚点，边缘因此还差一张。
            // 按实际显示的窗口补齐，保留窗口内移动时只切换高亮的行为。
            if (Items.FirstOrDefault(item => item.IsCurrent && !item.IsPlaceholder) is { } current &&
                windowAnchorPts != current.Pts && Items.Any(item => item.IsPlaceholder))
                await FillEdgesAsync(request, current.Pts, generation, token);
        }
    }

    private void PublishProgress(KeyFrameStripRange range, Request request, long generation, CancellationToken token)
    {
        if (generation != sourceGeneration || range.Frames.Count == 0)
            return;
        RegisterRange(range);
        if (!IsCurrent(request, generation, token))
            return;
        if (range.AtEnd || range.Frames[^1].Timestamp.TotalSeconds >= request.Time)
        {
            var selected = range.Frames.LastOrDefault(frame => frame.Timestamp.TotalSeconds <= request.Time)
                ?? range.Frames[0];
            if (cache.ContainsKey(selected.Pts))
                PublishCached(selected.Pts);
            return;
        }
        // 从片头顺序填充时，目标帧尚未到达也先显示已完成的图片，不提前高亮错误帧。
        var partial = range.Frames.Where(frame => cache.ContainsKey(frame.Pts))
            .Select(frame => new KeyFrameStripItem(frame.Pts, frame.Timestamp, cache[frame.Pts].Bitmap, false, cache[frame.Pts].KeyFrameType)).Take(VisibleCount).ToList();
        while (partial.Count < VisibleCount)
            partial.Add(new(long.MinValue, default, null, false));
        Items = partial;
    }

    private async Task FillEdgesAsync(Request request, long current, long generation, CancellationToken token)
    {
        for (var count = 0; count < request.Count + 1; count++)
        {
            token.ThrowIfCancellationRequested();
            var window = BuildWindow(current).Items;
            if (window.All(item => !item.IsPlaceholder))
                return;
            var direction = window[0].IsPlaceholder ? -1 : 1;
            var anchor = direction < 0 ? window.First(item => !item.IsPlaceholder) : window.Last(item => !item.IsPlaceholder);
            // 一次请求该侧缺少的全部画面；服务能批量解码时（帧精确模式）只需定位、解码一次。
            var missing = direction < 0
                ? window.TakeWhile(item => item.IsPlaceholder).Count()
                : window.Reverse().TakeWhile(item => item.IsPlaceholder).Count();
            // 向前补图须从关键帧解码整个 GOP，顺带多取一屏放入缓存，连续向前单步时可直接复用；
            // 向后可沿解码器游标顺序继续，只取缺少的部分。
            // 多取的数量受缓存余量限制，避免刚取到的画面又被挤出缓存。
            var requestCount = direction < 0
                ? Math.Max(missing, Math.Min(VisibleCount, CacheLimit - VisibleCount))
                : missing;
            await ReadNeighborsAsync(anchor.Pts, direction, requestCount, generation, token);
            if (IsCurrent(request, generation, token))
                PublishCached(current);
            else
                return;
        }
    }

    private int PrefetchCount => Math.Min((VisibleCount + 1) / 2, CacheLimit - VisibleCount);

    private bool NeedsPrefetch(Request request, long current)
    {
        if (request.Direction == 0 || Items.Any(item => item.IsPlaceholder))
            return false;
        var index = Items.ToList().FindIndex(item => item.Pts == current);
        var margin = Math.Min(2, (VisibleCount - 1) / 2);
        if (index < 0 || (request.Direction < 0 ? index > margin : index < Items.Count - 1 - margin))
            return false;
        var cursor = request.Direction < 0 ? Items[0].Pts : Items[^1].Pts;
        var links = request.Direction < 0 ? previousFrames : nextFrames;
        for (var count = 0; count < PrefetchCount; count++)
        {
            if (request.Direction < 0 ? firstPts == cursor : lastPts == cursor)
                return false;
            if (!links.TryGetValue(cursor, out var next) || !cache.ContainsKey(next))
                return true;
            cursor = next;
        }
        return false;
    }

    private async Task PrefetchEdgeAsync(Request request, long current, long generation, CancellationToken token)
    {
        if (!NeedsPrefetch(request, current))
            return;
        var direction = request.Direction;
        var cursor = direction < 0 ? Items[0].Pts : Items[^1].Pts;
        var links = direction < 0 ? previousFrames : nextFrames;
        for (var count = 0; count < PrefetchCount && IsCurrent(request, generation, token);)
        {
            if (direction < 0 ? firstPts == cursor : lastPts == cursor)
                break;
            if (links.TryGetValue(cursor, out var next) && cache.ContainsKey(next))
            {
                // 预取锚点可能在可视范围之外，保持它的缓存活跃，避免补图时淘汰连接点。
                cacheOrder.Remove(next);
                cacheOrder.AddLast(next);
                cursor = next;
                count++;
                continue;
            }
            if (await ReadNeighborsAsync(cursor, direction, PrefetchCount - count, generation, token) == 0)
                break;
        }
    }

    private async Task<int> ReadNeighborsAsync(long anchor, int direction, int count, long generation, CancellationToken token)
    {
        var frames = await service!.ReadNeighborsAsync(cache[anchor], direction, count, cache.ContainsKey, token);
        if (generation != sourceGeneration)
        {
            foreach (var frame in frames)
                frame.Bitmap?.Dispose();
            return 0;
        }
        if (frames.Count == 0)
        {
            if (direction < 0)
                firstPts = anchor;
            else
                lastPts = anchor;
        }
        var linked = anchor;
        foreach (var frame in frames)
        {
            Receive(frame, generation);
            Link(direction < 0 ? frame.Pts : linked, direction < 0 ? linked : frame.Pts);
            linked = frame.Pts;
        }
        return frames.Count;
    }

    private bool IsCurrent(Request request, long generation, CancellationToken token) =>
        generation == sourceGeneration && request.Generation == requestGeneration && !token.IsCancellationRequested;

    private void Receive(KeyFrameStripFrame frame, long generation)
    {
        if (generation != sourceGeneration)
        {
            frame.Bitmap?.Dispose();
            return;
        }
        if (frame.Bitmap is not null)
        {
            if (cache.Remove(frame.Pts, out var old))
                old.Bitmap?.Dispose();
            cache[frame.Pts] = frame;
        }
        if (cache.ContainsKey(frame.Pts))
        {
            cacheOrder.Remove(frame.Pts);
            cacheOrder.AddLast(frame.Pts);
        }
        TrimCache();
    }

    private void RegisterRange(KeyFrameStripRange range)
    {
        for (var index = 1; index < range.Frames.Count; index++)
            Link(range.Frames[index - 1].Pts, range.Frames[index].Pts);
        if (range.AtStart && range.Frames.Count > 0)
            firstPts = range.Frames[0].Pts;
        if (range.AtEnd && range.Frames.Count > 0)
            lastPts = range.Frames[^1].Pts;
    }

    private void Link(long previous, long next)
    {
        if (!cache.ContainsKey(previous) || !cache.ContainsKey(next))
            return;
        nextFrames[previous] = next;
        previousFrames[next] = previous;
    }

    private (IReadOnlyList<KeyFrameStripItem> Items, long Anchor) BuildWindow(long current)
    {
        var anchor = windowAnchorPts ?? current;
        var window = cache.ContainsKey(anchor) ? BuildCenteredWindow(anchor) : [];
        // 当前帧仍在原窗口内时只移动高亮；越界或远距离跳转才重新居中，形成半屏左右的平移。
        if (!window.Any(item => !item.IsPlaceholder && item.Pts == current))
        {
            anchor = current;
            window = BuildCenteredWindow(current);
        }
        return (window.Select(item => item with { IsCurrent = !item.IsPlaceholder && item.Pts == current }).ToArray(), anchor);
    }

    private IReadOnlyList<KeyFrameStripItem> BuildCenteredWindow(long current)
    {
        var before = new List<long>();
        var after = new List<long>();
        void Extend(List<long> frames, Dictionary<long, long> links, int limit)
        {
            var cursor = frames.Count > 0 ? frames[^1] : current;
            while (frames.Count < limit && links.TryGetValue(cursor, out var next) && cache.ContainsKey(next))
            {
                frames.Add(next);
                cursor = next;
            }
        }
        var leftCount = (VisibleCount - 1) / 2;
        Extend(before, previousFrames, leftCount);
        Extend(after, nextFrames, VisibleCount - 1 - leftCount);
        bool AtStart() => firstPts == (before.Count > 0 ? before[^1] : current);
        bool AtEnd() => lastPts == (after.Count > 0 ? after[^1] : current);
        if (AtStart())
            Extend(after, nextFrames, VisibleCount - 1 - before.Count);
        if (AtEnd())
            Extend(before, previousFrames, VisibleCount - 1 - after.Count);
        var leftPadding = AtStart() ? 0 : (AtEnd() ? VisibleCount - 1 - after.Count : leftCount) - before.Count;
        var rightPadding = AtStart() && AtEnd() ? 0 : VisibleCount - 1 - before.Count - after.Count - leftPadding;
        var result = new List<KeyFrameStripItem>();
        void Placeholder() => result.Add(new(long.MinValue, default, null, false));
        for (var index = 0; index < leftPadding; index++) Placeholder();
        foreach (var pts in before.AsEnumerable().Reverse().Append(current).Concat(after))
        {
            var frame = cache[pts];
            result.Add(new(pts, frame.Timestamp, frame.Bitmap, pts == current, frame.KeyFrameType));
        }
        for (var index = 0; index < rightPadding; index++) Placeholder();
        return result;
    }

    private void PublishCached(long current)
    {
        var (updated, anchor) = BuildWindow(current);
        windowAnchorPts = anchor;
        if (!Items.SequenceEqual(updated))
            Items = updated;
        foreach (var item in Items.Where(item => !item.IsPlaceholder))
        {
            cacheOrder.Remove(item.Pts);
            cacheOrder.AddLast(item.Pts);
        }
        TrimCache();
    }

    private void TrimCache()
    {
        var node = cacheOrder.First;
        while (cache.Count > CacheLimit && node is not null)
        {
            var next = node.Next;
            if (!Items.Any(item => !item.IsPlaceholder && item.Pts == node.Value))
            {
                var pts = node.Value;
                cache.Remove(pts, out var frame);
                frame?.Bitmap?.Dispose();
                if (previousFrames.Remove(pts, out var previous))
                    nextFrames.Remove(previous);
                if (nextFrames.Remove(pts, out var following))
                    previousFrames.Remove(following);
                cacheOrder.Remove(node);
            }
            node = next;
        }
    }

    private sealed record Request(double Time, long Started, long Generation, long? Pts, int Count, int Direction);
}
