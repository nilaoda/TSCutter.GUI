using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Services.Preview;

namespace TSCutter.GUI.ViewModels.Preview;

/// <summary>
/// 导航条下方的音频波形：读取导航条可见的完整时间范围，每段最多 <see cref="ReadChunkSeconds"/> 秒，
/// 从当前位置起逐秒读取，按秒缓存峰值；后台只保留一个读取任务，范围变化时取消旧任务。
/// 音轨不能解码时 <see cref="IsAvailable"/> 为 false，整条波形不显示。
/// </summary>
public sealed class StripWaveformViewModel : ObservableObject
{
    private const int ReadChunkSeconds = 30;
    // 每秒峰值 4 KB，保留约十分钟的历史缓存；当前可见范围在移出窗口前不淘汰。
    private const int CacheSeconds = 600;

    private readonly Dictionary<int, float[]> cache = [];
    private readonly LinkedList<int> cacheOrder = [];
    private readonly Dictionary<int, LinkedListNode<int>> cacheNodes = [];
    private AudioWaveformSource? source;
    private bool unsupported;
    private long generation;
    private CancellationTokenSource? active;
    private (int First, int End) activeRange = (0, 0);
    private (int First, int End) visibleRange = (0, 0);
    private Task worker = Task.CompletedTask;
    private AudioWaveformReader? reader;

    /// <summary>有可解码的音轨，显示波形条。</summary>
    public bool IsAvailable => source is not null && !unsupported;

    /// <summary>每读完一秒递增，通知导航条重绘。</summary>
    public int Version { get; private set; }

    /// <summary>音频延迟（秒，正数推后）：波形按延迟平移显示，读取范围也随之平移，已读到的数据不变。</summary>
    internal double OffsetSeconds { get; private set; }

    internal void SetOffset(double seconds)
    {
        if (OffsetSeconds == seconds)
            return;
        OffsetSeconds = seconds;
        BumpVersion();
    }

    internal Task IdleTask => worker;

    internal void SetSource(AudioWaveformSource? value)
    {
        if (source == value)
            return;
        Cancel();
        // 旧来源的读取器在当前任务结束后释放，不在没有视频时继续占用文件。
        worker = worker.ContinueWith(_ => ReleaseReader(), TaskScheduler.Default);
        source = value;
        unsupported = false;
        generation++;
        cache.Clear();
        cacheOrder.Clear();
        cacheNodes.Clear();
        visibleRange = (0, 0);
        OnPropertyChanged(nameof(IsAvailable));
        BumpVersion();
    }

    /// <summary>请求导航条可见的范围；已缓存的秒不再读取，正在读取的范围已覆盖时不重新开始。</summary>
    internal void Request(double start, double end, double center)
    {
        if (!IsAvailable || end <= start)
            return;
        // 画面时间换成音频自身的时间：声音推后时，画面上这一刻对应更早的声音。
        start -= OffsetSeconds;
        end -= OffsetSeconds;
        center -= OffsetSeconds;
        start = Math.Max(0, start);
        if (end <= start)
            return;
        var first = (int)Math.Floor(start);
        var last = (int)Math.Ceiling(end);
        visibleRange = (first, last);
        TrimCache();
        var missing = Enumerable.Range(first, Math.Max(0, last - first)).Where(second => !cache.ContainsKey(second))
            .ToArray();
        if (missing.Length == 0)
        {
            // 新窗口已经全部缓存，停止旧窗口尚未完成的读取。
            Cancel();
            return;
        }
        if (active is { IsCancellationRequested: false }
            && missing.All(second => second >= activeRange.First && second < activeRange.End))
            return;
        Cancel();
        var cancellation = new CancellationTokenSource();
        active = cancellation;
        activeRange = (missing[0], missing[^1] + 1);
        var requested = source!;
        var requestGeneration = generation;
        var centerSecond = (int)Math.Floor(center);
        var previous = worker;
        worker = Task.Run(async () =>
        {
            // 同一时刻只有一个任务使用读取器：等上一个任务结束后再开始。
            try { await previous.ConfigureAwait(false); }
            catch { /* 上一个任务的失败已经处理。 */ }
            ReadMissing(requested, requestGeneration, missing, centerSecond, cancellation);
        });
    }

    private void ReadMissing(AudioWaveformSource requested, long requestGeneration, int[] missing, int centerSecond,
        CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        try
        {
            if (reader is null)
            {
                token.ThrowIfCancellationRequested();
                reader = AudioWaveformReader.TryOpen(requested);
                if (reader is null)
                {
                    Publish(requestGeneration, () => unsupported = true, availabilityChanged: true);
                    return;
                }
            }
            foreach (var (first, end) in Runs(missing, centerSecond))
            {
                token.ThrowIfCancellationRequested();
                reader.Read(first, end, (second, peaks) => Publish(requestGeneration, () => Store(second, peaks)), token);
            }
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // 取消时的任何中断都不代表音轨不能解码。
        }
        catch (Exception exception)
        {
            // 读取失败只影响波形，不影响剪辑；解码器打不开同样视为不能解码。
            Console.WriteLine($"Waveform unavailable: {exception.Message}");
            ReleaseReader();
            Publish(requestGeneration, () => unsupported = true, availabilityChanged: true);
        }
        finally
        {
            // 字段只在界面线程上读写：在界面线程上摘下后再释放。
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(active, cancellation))
                    active = null;
                cancellation.Dispose();
            });
        }
    }

    private void ReleaseReader()
    {
        reader?.Dispose();
        reader = null;
    }

    /// <summary>
    /// 读取顺序：把缺少的秒合并成连续段并在当前位置处断开，先读当前位置及之后的段，再由近及远读之前的段，
    /// 当前画面附近的波形最先出现；每段不超过 <see cref="ReadChunkSeconds"/> 秒，只需一次定位。
    /// </summary>
    private static IReadOnlyList<(int First, int End)> Runs(IEnumerable<int> seconds, int centerSecond)
    {
        var sorted = seconds.Order().ToArray();
        var runs = new List<(int First, int End)>();
        for (var i = 0; i < sorted.Length;)
        {
            var j = i;
            while (j + 1 < sorted.Length && sorted[j + 1] == sorted[j] + 1 && sorted[j + 1] != centerSecond
                   && sorted[j + 1] - sorted[i] < ReadChunkSeconds)
                j++;
            runs.Add((sorted[i], sorted[j] + 1));
            i = j + 1;
        }
        return runs.OrderBy(run => run.First >= centerSecond ? run.First - centerSecond : centerSecond - run.End + 0.5)
            .ToArray();
    }

    private void Publish(long requestGeneration, Action apply, bool availabilityChanged = false)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (requestGeneration != generation)
                return;
            apply();
            if (availabilityChanged)
                OnPropertyChanged(nameof(IsAvailable));
            BumpVersion();
        });
    }

    private void Store(int second, float[] peaks)
    {
        if (cacheNodes.Remove(second, out var existing))
            cacheOrder.Remove(existing);
        cache[second] = peaks;
        cacheNodes[second] = cacheOrder.AddLast(second);
        TrimCache();
    }

    private void TrimCache()
    {
        var limit = Math.Max(CacheSeconds, visibleRange.End - visibleRange.First);
        var oldest = cacheOrder.First;
        while (cache.Count > limit && oldest is not null)
        {
            var next = oldest.Next;
            if (oldest.Value < visibleRange.First || oldest.Value >= visibleRange.End)
            {
                cacheOrder.Remove(oldest);
                cacheNodes.Remove(oldest.Value);
                cache.Remove(oldest.Value);
            }
            oldest = next;
        }
    }

    private void BumpVersion()
    {
        Version++;
        OnPropertyChanged(nameof(Version));
    }

    /// <summary>画面时间 [start, end) 对应声音的峰值（0–1，已按延迟平移）；已读取的采样空隙为 0，尚未读取时返回 false。</summary>
    internal bool TryGetPeak(double start, double end, out float peak)
    {
        peak = 0;
        var found = false;
        var first = (long)Math.Floor((start - OffsetSeconds) * AudioWaveformReader.BucketsPerSecond);
        var last = Math.Max(first + 1, (long)Math.Ceiling((end - OffsetSeconds) * AudioWaveformReader.BucketsPerSecond));
        float[]? chunk = null;
        var chunkSecond = long.MinValue;
        for (var bucket = Math.Max(0, first); bucket < last; bucket++)
        {
            var second = bucket / AudioWaveformReader.BucketsPerSecond;
            if (second != chunkSecond)
            {
                chunkSecond = second;
                cache.TryGetValue((int)second, out chunk);
            }
            if (chunk is null)
                return false;
            // 已完成读取的一秒中，没有采样的位置画在底线；不与尚未加载的范围混为一谈。
            found = true;
            var value = chunk[bucket % AudioWaveformReader.BucketsPerSecond];
            if (float.IsNaN(value))
                continue;
            peak = Math.Max(peak, value);
        }
        return found;
    }

    private void Cancel()
    {
        active?.Cancel();
        active = null;
        activeRange = (0, 0);
    }
}
