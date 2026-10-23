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
    public int VisibleCount { get; private set; } = 9;
    private double? targetTime;
    private readonly Func<IKeyFrameStripService> createService;
    private readonly Dictionary<long, KeyFrameStripFrame> cache = new();
    private readonly LinkedList<long> cacheOrder = new();
    private readonly Dictionary<long, long> previousFrames = new();
    private readonly Dictionary<long, long> nextFrames = new();
    private long? firstPts, lastPts;
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

    internal void SetSource(KeyFrameStripSource? value)
    {
        if (source == value)
            return;
        Cancel();
        source = value;
        sourceGeneration++;
        targetTime = null;
        Items = [];
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
        if (source is null || targetTime is not { } time)
            return;
        var current = FindCachedTarget(time, requested?.Pts);
        if (current is { } pts)
            PublishCached(pts);
        // 缩放时先复用已有图片，停住后再补齐，避免每跨过一个槽位就重启解码。
        requested = null;
        RequestAt(time);
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
        if (direction > 0 && index >= (VisibleCount - 1) / 2)
        {
            shifted.RemoveAt(0);
            shifted.Add(new(long.MinValue, default, null, false));
            target--;
        }
        else if (direction < 0 && index <= (VisibleCount - 1) / 2)
        {
            shifted.RemoveAt(shifted.Count - 1);
            shifted.Insert(0, new(long.MinValue, default, null, false));
            target++;
        }
        shifted[target] = shifted[target] with { IsCurrent = true };
        Items = shifted;
        return true;
    }

    public void RequestAt(double time, bool immediate = false, long? currentPts = null)
    {
        if (source is null || !double.IsFinite(time))
            return;
        time = Math.Max(0, time);
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
            currentPts ?? pts, VisibleCount);
        StartWorker();
    }

    private void PublishEmpty()
    {
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
                        var delay = TimeSpan.FromMilliseconds(200) - Stopwatch.GetElapsedTime(request.Started);
                        if (delay > TimeSpan.Zero)
                            await Task.Delay(delay, cancellation.Token);
                    }
                    cancellation.Token.ThrowIfCancellationRequested();
                    var knownPts = FindCachedTarget(request.Time, request.Pts);
                    if (knownPts is { } cachedPts)
                    {
                        PublishCached(cachedPts);
                        if (Items.All(item => !item.IsPlaceholder))
                            continue;
                    }
                    if (knownPts is null)
                        PublishEmpty();
                    if (service is null)
                    {
                        service = createService();
                        await service.OpenAsync(requestedSource, cancellation.Token);
                        serviceSource = requestedSource;
                    }
                    if (knownPts is { } current)
                    {
                        await FillEdgesAsync(request, current, generation, cancellation.Token);
                    }
                    else
                    {
                        var range = await service.ReadAsync(request.Time, cache.ContainsKey,
                            frame => Receive(frame, generation), cancellation.Token,
                            range => PublishProgress(range, request, generation, cancellation.Token), request.Count);
                        if (IsCurrent(request, generation, cancellation.Token))
                        {
                            PublishProgress(range, request, generation, cancellation.Token);
                        }
                    }
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
            var window = BuildWindow(current);
            if (window.All(item => !item.IsPlaceholder))
                return;
            var direction = window[0].IsPlaceholder ? -1 : 1;
            var anchor = direction < 0 ? window.First(item => !item.IsPlaceholder) : window.Last(item => !item.IsPlaceholder);
            var frame = await service!.ReadNeighborAsync(cache[anchor.Pts], direction, cache.ContainsKey, token);
            if (generation != sourceGeneration)
            {
                frame?.Bitmap?.Dispose();
                return;
            }
            if (frame is null)
            {
                if (direction < 0)
                    firstPts = anchor.Pts;
                else
                    lastPts = anchor.Pts;
            }
            else
            {
                Receive(frame, generation);
                Link(direction < 0 ? frame.Pts : anchor.Pts, direction < 0 ? anchor.Pts : frame.Pts);
            }
            if (IsCurrent(request, generation, token))
                PublishCached(current);
            else
                return;
        }
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

    private IReadOnlyList<KeyFrameStripItem> BuildWindow(long current)
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
        var updated = BuildWindow(current);
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

    private sealed record Request(double Time, long Started, long Generation, long? Pts, int Count);
}
