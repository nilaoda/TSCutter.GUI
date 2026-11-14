using System;
using System.Collections.Generic;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Utils.Media.DolbyE;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Services.Editing.DolbyE;

/// <summary>
/// 一条杜比 E 轨的转码：逐个送入 302M 的 PES 负载（90 kHz、已展开回绕的 PTS），解码后交给各路输出。
/// 每个编码帧按时间戳归入“槽位”：PTS 不晚于它的最近一个杜比 E PES 的序号，写出时放在该 PES 的位置。
/// </summary>
internal sealed unsafe class DolbyETrackTranscoder : IDisposable
{
    private static readonly AVRational TimeBase90k = new() { num = 1, den = 90_000 };
    private const int RingSize = 128;

    private readonly DolbyEDecoder decoder;
    private readonly IReadOnlyList<DolbyEOutputChain> chains;
    private readonly Packet input = new();
    private readonly (int Index, long Pts)[] ring = new (int, long)[RingSize];
    private int ringCount;
    private int ringNext;
    private long expectedPts = FF.AV_NOPTS_VALUE;
    private int currentIndex;
    private Action<DolbyEEncodedUnit, int>? sink;

    public DolbyETrackTranscoder(IReadOnlyList<DolbyEOutputChain> chains)
    {
        this.chains = chains;
        decoder = DolbyEDecoder.TryCreate(null, TimeBase90k)
                  ?? throw new InvalidOperationException("Dolby E decoder unavailable.");
    }

    /// <summary>成功解码的杜比 E 帧数。</summary>
    public int DecodedFrames { get; private set; }

    public void Feed(ReadOnlySpan<byte> payload, long pts90k, int pesIndex, Action<DolbyEEncodedUnit, int> emit)
    {
        currentIndex = pesIndex;
        if (pts90k != FF.AV_NOPTS_VALUE)
        {
            ring[ringNext] = (pesIndex, pts90k);
            ringNext = (ringNext + 1) % RingSize;
            ringCount = Math.Min(ringCount + 1, RingSize);
        }
        AVPacket* raw = input;
        FF.av_packet_unref(raw);
        if (payload.Length == 0 || FF.av_new_packet(raw, payload.Length) < 0)
            return;
        payload.CopyTo(new Span<byte>(raw->data, raw->size));
        raw->pts = raw->dts = pts90k;
        sink = emit;
        try
        {
            decoder.Decode(input, OnFrame);
        }
        finally
        {
            sink = null;
            FF.av_packet_unref(raw);
        }
    }

    public void Finish(Action<DolbyEEncodedUnit, int> emit)
    {
        sink = emit;
        try
        {
            decoder.Decode(null, OnFrame);
            foreach (var chain in chains)
                chain.Finish(unit => emit(unit, SlotFor(unit.Pts90k)));
        }
        finally
        {
            sink = null;
        }
    }

    private void OnFrame(Frame frame, DolbyEFrameHeader header)
    {
        AVFrame* raw = frame;
        var pts = raw->pts != FF.AV_NOPTS_VALUE ? raw->pts : expectedPts;
        if (pts == FF.AV_NOPTS_VALUE)
            return;
        var duration = (long)Math.Round(raw->nb_samples * 90_000.0 / Math.Max(1, raw->sample_rate));
        // 与上一帧不衔接（丢包、损坏的帧被跳过、合并接缝的跳变）超过半帧时重新开始一段。
        var restart = expectedPts != FF.AV_NOPTS_VALUE && Math.Abs(pts - expectedPts) > duration / 2;
        expectedPts = pts + duration;
        DecodedFrames++;
        var emit = sink!;
        foreach (var chain in chains)
        {
            var program = DolbyEDecoder.TryExtractProgram(frame, header, chain.Output.Program, chain.ProgramFrame)
                ? chain.ProgramFrame : null;
            chain.Push(program, header, pts, restart, unit => emit(unit, SlotFor(unit.Pts90k)));
            chain.ProgramFrame.Unref();
        }
    }

    private int SlotFor(long pts)
    {
        if (ringCount == 0)
            return currentIndex;
        var oldest = int.MaxValue;
        var best = -1;
        var bestPts = long.MinValue;
        for (var i = 0; i < ringCount; i++)
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

    public void Dispose()
    {
        foreach (var chain in chains)
            chain.Dispose();
        decoder.Dispose();
        input.Dispose();
    }
}
