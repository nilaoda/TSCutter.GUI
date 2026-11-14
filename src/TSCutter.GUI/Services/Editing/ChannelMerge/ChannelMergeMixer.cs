using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Services.Editing.ChannelMerge;

/// <summary>
/// 多轨合并的交织：三条来源（前置、中置与低音、环绕）的解码画面重采样到 48 kHz，按 PTS 对齐后交织成 5.1
/// （L R C LFE Ls Rs，侧环绕布局），导出与预览共用。
/// <list type="bullet">
/// <item>前置来源是锚定来源：合并结果的时间与分段以它为准，它的帧不衔接时输出重新开始一段（与杜比 E 的规则相同）。</item>
/// <item>其他来源按各自的时间戳放进同一时间轴：比前置来源晚开始、中途缺帧或时间戳跳变的部分补静音，早于前置来源的部分丢弃。</item>
/// <item>某个来源落后前置来源太多（没有数据或长时间中断）时不再等它，相应时段补静音。</item>
/// </list>
/// </summary>
internal sealed unsafe class ChannelMergeMixer : IDisposable
{
    public const int OutputRate = 48_000;
    public const int SlotCount = 3;
    // 其他来源落后前置来源超过这么多样本时不再等待，按静音合并。
    private const long WaitLimit = OutputRate * 2;
    // 已解码但还没合并的数据上限，超过时丢弃最早的部分（前置来源长时间中断）。
    private const long BufferLimit = OutputRate * 30;
    private const ulong Layout51 = 0x60F;

    private sealed class Source(bool swap)
    {
        public bool Swap { get; } = swap;
        public StereoResampler Resampler { get; private set; } = new();
        // 按时间排列的连续片段（48 kHz 样本位置）。
        public List<Segment> Segments { get; } = [];

        public void Reset()
        {
            Resampler.Dispose();
            Resampler = new StereoResampler();
            Segments.Clear();
        }
    }

    private sealed class Segment(long start)
    {
        // 头部已合并、不再需要的样本只记偏移，攒到一半以上时再一次性移除，避免每合并一帧都搬动整段暂存。
        private const int CompactThreshold = 4096;
        private readonly List<float> left = [];
        private readonly List<float> right = [];
        private int offset;

        public long Start { get; private set; } = start;
        public int Count => left.Count - offset;
        public long End => Start + Count;
        public ReadOnlySpan<float> Left => CollectionsMarshal.AsSpan(left)[offset..];
        public ReadOnlySpan<float> Right => CollectionsMarshal.AsSpan(right)[offset..];

        public void Append(float[] l, float[] r)
        {
            left.AddRange(l);
            right.AddRange(r);
        }

        /// <summary>丢弃开头的 <paramref name="count"/> 个样本。</summary>
        public void DropFront(int count)
        {
            offset += count;
            Start += count;
            if (offset < CompactThreshold || offset < left.Count / 2)
                return;
            left.RemoveRange(0, offset);
            right.RemoveRange(0, offset);
            offset = 0;
        }
    }

    private sealed record AnchorFrame(long Start, long Pts90k, float[] Left, float[] Right, bool Restart);

    /// <summary>合并结果：5.1 画面（只在回调期间有效）、样本数、90 kHz 时间戳与是否不连续。</summary>
    public delegate void Output(Frame frame, int samples, long pts90k, bool restart);

    private readonly Source[] sources;
    private readonly Output output;
    private readonly Queue<AnchorFrame> anchorFrames = new();
    private readonly Frame merged = new();
    private long anchorExpected = long.MinValue;
    private long anchorLatestEnd = long.MinValue;

    /// <param name="swaps">按槽位顺序，各来源左右是否互换。</param>
    public ChannelMergeMixer(IReadOnlyList<bool> swaps, Output output)
    {
        if (swaps.Count != SlotCount)
            throw new ArgumentException("Channel merge needs three sources.", nameof(swaps));
        sources = swaps.Select(swap => new Source(swap)).ToArray();
        this.output = output;
    }

    /// <summary>已合并输出的帧数（以前置来源的帧计）。</summary>
    public int MergedFrames { get; private set; }

    /// <summary>已合并到的时刻（90 kHz）；还没有输出时为 null。</summary>
    public long? MergedUntil90k { get; private set; }

    /// <summary>送入一个来源的解码画面（时间戳为 90 kHz、已展开回绕），可能触发合并输出。</summary>
    public void Accept(int slot, Frame frame, long pts90k)
    {
        var source = sources[slot];
        var (left, right) = source.Resampler.Convert(frame);
        if (left.Length == 0)
            return;
        var start = Rescale90kTo48k(pts90k);
        if (slot == 0)
        {
            // 与上一帧相差不到半帧时视为连续（时间戳取整误差），否则重新开始一段。
            var restart = anchorExpected != long.MinValue && Math.Abs(start - anchorExpected) > left.Length / 2;
            if (anchorExpected != long.MinValue && !restart)
                start = anchorExpected;
            // 时间不连续：之前的帧不再等待其他来源，先合并完再开始新的一段。
            if (restart)
                Produce(final: true);
            anchorExpected = start + left.Length;
            anchorLatestEnd = anchorExpected;
            anchorFrames.Enqueue(new AnchorFrame(start, pts90k, left, right, restart));
            // 前置来源一直在等其他来源时也不让暂存无限增长：最早的帧不再等待。
            while (anchorFrames.Count > 0 && anchorLatestEnd - anchorFrames.Peek().Start > BufferLimit)
                Merge(anchorFrames.Dequeue());
        }
        else
        {
            var last = source.Segments.Count > 0 ? source.Segments[^1] : null;
            if (last is null || Math.Abs(start - last.End) > left.Length / 2)
            {
                // 时间戳回退：之前的数据属于另一段时间，作废。
                if (last is not null && start < last.Start)
                    source.Segments.Clear();
                source.Segments.Add(last = new Segment(start));
            }
            last.Append(left, right);
            TrimSource(source, last.End - BufferLimit);
        }
        Produce(final: false);
    }

    /// <summary>
    /// 合并前置来源中其他来源已经到齐（或等待超时）的帧；<paramref name="final"/> 为 true 时不再等待，全部合并。
    /// </summary>
    public void Produce(bool final)
    {
        while (anchorFrames.Count > 0)
        {
            var frame = anchorFrames.Peek();
            var end = frame.Start + frame.Left.Length;
            if (!final && anchorLatestEnd - end < WaitLimit && sources.Skip(1).Any(source => !Reached(source, end)))
                return;
            Merge(anchorFrames.Dequeue());
        }
    }

    /// <summary>定位后重新开始：丢弃全部暂存的数据。</summary>
    public void Reset()
    {
        anchorFrames.Clear();
        foreach (var source in sources)
            source.Reset();
        anchorExpected = anchorLatestEnd = long.MinValue;
    }

    /// <summary>
    /// 来源的数据已经到达 <paramref name="end"/>：最近一段解出的数据越过了这个时刻，而且这一段与它在同一时间范围
    /// （时间戳跳变后，旧时间范围的数据不算）。来源晚开始时，最近一段从稍后开始也算到达，前面补静音。
    /// </summary>
    private static bool Reached(Source source, long end)
    {
        if (source.Segments.Count == 0)
            return false;
        var last = source.Segments[^1];
        return last.End >= end && last.Start - end < WaitLimit;
    }

    private void Merge(AnchorFrame frame)
    {
        var count = frame.Left.Length;
        var end = frame.Start + count;
        AVFrame* raw = merged;
        merged.Unref();
        raw->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        raw->nb_samples = count;
        raw->sample_rate = OutputRate;
        raw->ch_layout.order = AVChannelOrder.AV_CHANNEL_ORDER_NATIVE;
        raw->ch_layout.nb_channels = 6;
        raw->ch_layout.u.mask = Layout51;
        FFmpegException.Check(FF.av_frame_get_buffer(raw, 0));
        for (var slot = 0; slot < sources.Length; slot++)
        {
            var first = new Span<float>(raw->extended_data[slot * 2], count);
            var second = new Span<float>(raw->extended_data[slot * 2 + 1], count);
            first.Clear();
            second.Clear();
            var source = sources[slot];
            if (slot == 0)
            {
                frame.Left.CopyTo(first);
                frame.Right.CopyTo(second);
            }
            else
                CopyRange(source, frame.Start, first, second);
            if (source.Swap)
                Swap(first, second);
        }
        raw->pts = frame.Pts90k;
        try
        {
            output(merged, count, frame.Pts90k, frame.Restart);
        }
        finally
        {
            merged.Unref();
        }
        MergedFrames++;
        MergedUntil90k = frame.Pts90k + Rescale48kTo90k(count);
        foreach (var source in sources.Skip(1))
            TrimSource(source, end);
    }

    private static void CopyRange(Source source, long start, Span<float> first, Span<float> second)
    {
        var end = start + first.Length;
        foreach (var segment in source.Segments)
        {
            var from = Math.Max(start, segment.Start);
            var to = Math.Min(end, segment.End);
            if (to <= from)
                continue;
            var length = (int)(to - from);
            var sourceOffset = (int)(from - segment.Start);
            var targetOffset = (int)(from - start);
            segment.Left.Slice(sourceOffset, length).CopyTo(first.Slice(targetOffset, length));
            segment.Right.Slice(sourceOffset, length).CopyTo(second.Slice(targetOffset, length));
        }
    }

    private static void Swap(Span<float> first, Span<float> second)
    {
        for (var i = 0; i < first.Length; i++)
            (first[i], second[i]) = (second[i], first[i]);
    }

    /// <summary>丢弃 <paramref name="before"/> 之前的数据。</summary>
    private static void TrimSource(Source source, long before)
    {
        while (source.Segments.Count > 0)
        {
            var segment = source.Segments[0];
            if (segment.End <= before)
            {
                source.Segments.RemoveAt(0);
                continue;
            }
            if (segment.Start < before)
                segment.DropFront((int)(before - segment.Start));
            return;
        }
    }

    internal static long Rescale90kTo48k(long pts) => (long)Math.Round(pts * (OutputRate / 90000.0));
    internal static long Rescale48kTo90k(long samples) => (long)Math.Round(samples * (90000.0 / OutputRate));

    public void Dispose()
    {
        foreach (var source in sources)
            source.Resampler.Dispose();
        merged.Dispose();
    }
}

/// <summary>把解码画面重采样为 48 kHz 浮点的左右两路；单声道复制到两路，多于两路时下混为立体声。</summary>
internal sealed unsafe class StereoResampler : IDisposable
{
    private SwrContext* swr;
    private ulong inputMask;
    private int inputChannels;
    private int inputRate;
    private int inputFormat;
    private int outputChannels;
    private float[] scratch = [];

    public (float[] Left, float[] Right) Convert(Frame frame)
    {
        AVFrame* input = frame;
        if (input->nb_samples <= 0 || input->ch_layout.nb_channels <= 0)
            return ([], []);
        Ensure(input);
        var capacity = (int)(FF.swr_get_delay(swr, ChannelMergeMixer.OutputRate) +
                             (long)input->nb_samples * ChannelMergeMixer.OutputRate / input->sample_rate + 64);
        if (scratch.Length < capacity * outputChannels)
            scratch = new float[capacity * outputChannels];
        var pointers = stackalloc byte*[outputChannels];
        int produced;
        fixed (float* data = scratch)
        {
            for (var i = 0; i < outputChannels; i++)
                pointers[i] = (byte*)(data + i * capacity);
            produced = FF.swr_convert(swr, pointers, capacity, input->extended_data, input->nb_samples);
        }
        FFmpegException.Check(produced);
        var left = scratch.AsSpan(0, produced).ToArray();
        var right = outputChannels == 2 ? scratch.AsSpan(capacity, produced).ToArray() : left.ToArray();
        return (left, right);
    }

    private void Ensure(AVFrame* input)
    {
        var mask = input->ch_layout.u.mask;
        if (swr != null && mask == inputMask && input->ch_layout.nb_channels == inputChannels &&
            input->sample_rate == inputRate && input->format == inputFormat)
            return;
        Free();
        outputChannels = input->ch_layout.nb_channels == 1 ? 1 : 2;
        AVChannelLayout inLayout = input->ch_layout;
        AVChannelLayout outLayout = default;
        outLayout.order = AVChannelOrder.AV_CHANNEL_ORDER_NATIVE;
        outLayout.nb_channels = outputChannels;
        outLayout.u.mask = outputChannels == 1 ? 0x4UL : 0x3UL;
        SwrContext* context = null;
        FFmpegException.Check(FF.swr_alloc_set_opts2(&context, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLTP,
            ChannelMergeMixer.OutputRate, &inLayout, (AVSampleFormat)input->format, input->sample_rate, 0, null));
        var result = FF.swr_init(context);
        if (result < 0)
        {
            FF.swr_free(&context);
            FFmpegException.Check(result);
        }
        swr = context;
        inputMask = mask;
        inputChannels = input->ch_layout.nb_channels;
        inputRate = input->sample_rate;
        inputFormat = input->format;
    }

    private void Free()
    {
        if (swr == null)
            return;
        var context = swr;
        FF.swr_free(&context);
        swr = null;
    }

    public void Dispose() => Free();
}
