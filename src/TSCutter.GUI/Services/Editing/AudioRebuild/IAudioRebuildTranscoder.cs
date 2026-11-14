using System;

namespace TSCutter.GUI.Services.Editing.AudioRebuild;

/// <summary>
/// 重组流中一条重建音轨的转码。重组流逐个送入来源 PID 的 PES 负载（90 kHz、已展开回绕的 PTS），
/// 转码器解码、编码后交出编码帧，并为每个编码帧标出槽位：放在哪个锚定 PES（新音轨所跟随的来源 PID 的 PES）的位置。
/// </summary>
internal interface IAudioRebuildTranscoder : IDisposable
{
    /// <summary>成功解码的来源帧数。</summary>
    int DecodedFrames { get; }

    /// <summary>
    /// 已处理到的锚定 PES 序号：这个序号及之前的时段，除编码器延迟内的帧外，编码帧都已产生。
    /// 重组流据此决定暂存的包何时可以写出。
    /// </summary>
    int ProcessedAnchorIndex { get; }

    /// <summary>送入一个来源 PES 的负载。<paramref name="pesIndex"/> 为该 PID 上的 PES 序号。</summary>
    void Feed(int pid, ReadOnlySpan<byte> payload, long pts90k, int pesIndex, Action<EncodedAudioUnit, int> emit);

    /// <summary>某个来源 PES 没有可用的负载（结构损坏）：只推进进度。</summary>
    void Skip(int pid, int pesIndex);

    /// <summary>写出结束：冲出解码器与各路编码器。</summary>
    void Finish(Action<EncodedAudioUnit, int> emit);
}

/// <summary>
/// 编码帧到槽位的映射：记录最近若干个锚定 PES 的序号与 PTS，编码帧归入 PTS 不晚于它的最近一个 PES。
/// 比所有记录都早的帧归入最早的记录。
/// </summary>
internal sealed class AnchorSlotMap
{
    private const int RingSize = 128;
    private readonly (int Index, long Pts)[] ring = new (int, long)[RingSize];
    private int count;
    private int next;

    /// <summary>最近一次送入的锚定 PES 序号；没有记录时也作为槽位使用。</summary>
    public int Current { get; private set; }

    public void Add(int index, long pts)
    {
        Current = index;
        if (pts == FFmpeg.NativeMethods.AV_NOPTS_VALUE)
            return;
        ring[next] = (index, pts);
        next = (next + 1) % RingSize;
        count = Math.Min(count + 1, RingSize);
    }

    public void Touch(int index) => Current = index;

    public int SlotFor(long pts)
    {
        if (count == 0)
            return Current;
        var oldest = int.MaxValue;
        var best = -1;
        var bestPts = long.MinValue;
        for (var i = 0; i < count; i++)
        {
            var (index, entryPts) = ring[i];
            oldest = Math.Min(oldest, index);
            if (entryPts <= pts && (entryPts > bestPts || entryPts == bestPts && index > best))
            {
                bestPts = entryPts;
                best = index;
            }
        }
        return best >= 0 ? best : oldest;
    }

    /// <summary>PTS 不晚于 <paramref name="pts"/> 的最大锚定 PES 序号；没有时为 -1。</summary>
    public int LastIndexAtOrBefore(long pts)
    {
        var best = -1;
        for (var i = 0; i < count; i++)
            if (ring[i].Pts <= pts)
                best = Math.Max(best, ring[i].Index);
        return best;
    }
}
