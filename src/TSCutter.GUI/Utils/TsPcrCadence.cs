using System;

namespace TSCutter.GUI.Utils;

// 包间距随 VBR 和复用调度变化，不能作为节目时钟损坏的证据。
// 仅在 PCR 自身有稳定的短采样周期时估算正常间隔；不确定时放弃自动校正。
internal sealed class TsPcrCadence
{
    internal const long BoundaryThreshold90k = 22_500;
    private const int WindowSize = 31;
    private readonly long[] _intervals = new long[WindowSize];
    private int _count;
    private int _next;

    public void Reset() => _count = _next = 0;

    public void Observe(long interval90k)
    {
        if (interval90k <= 0 || interval90k >= 45_000)
            return;
        _intervals[_next] = interval90k;
        _next = (_next + 1) % WindowSize;
        _count = Math.Min(_count + 1, WindowSize);
    }

    public bool TryGetInterval(out long interval90k)
    {
        Span<long> values = stackalloc long[WindowSize];
        _intervals.AsSpan(0, _count).CopyTo(values);
        values[.._count].Sort();
        return TryGetInterval(values[.._count], out interval90k);
    }

    // 调用方传入已排序的正向短间隔。
    internal static bool TryGetInterval(ReadOnlySpan<long> sortedIntervals, out long interval90k)
    {
        interval90k = 0;
        if (sortedIntervals.Length < 5)
            return false;
        var median = sortedIntervals[sortedIntervals.Length / 2];
        var tolerance = Math.Max(90, median / 20);
        var stable = 0;
        foreach (var interval in sortedIntervals)
            if (Math.Abs(interval - median) <= tolerance)
                stable++;
        if ((long)stable * 5 < (long)sortedIntervals.Length * 3)
            return false;
        interval90k = median;
        return true;
    }
}
