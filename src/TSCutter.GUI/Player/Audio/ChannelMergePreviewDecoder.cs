using System;
using System.Collections.Generic;
using System.Linq;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.Extensions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Services.Editing.ChannelMerge;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Player.Audio;

/// <summary>
/// 播放与波形用：把三条来源音轨的包分别解码，按与导出相同的规则（<see cref="ChannelMergeMixer"/>）交织成 5.1，
/// 由播放的重采样统一下混为立体声。输出帧的时间戳换算到前置来源的时基。
/// </summary>
internal sealed class ChannelMergePreviewDecoder : IDisposable
{
    private readonly MediaStream[] streams;
    private readonly CodecContext[] decoders;
    private readonly ChannelMergeMixer mixer;
    private readonly Frame frame = new();
    private Action<Frame>? sink;

    private ChannelMergePreviewDecoder(MediaStream[] streams, CodecContext[] decoders, IReadOnlyList<bool> swaps)
    {
        this.streams = streams;
        this.decoders = decoders;
        mixer = new ChannelMergeMixer(swaps, OnMerged);
    }

    /// <summary>流序号是否是三条来源之一（逐包调用，不分配）。</summary>
    public bool IsSource(int streamIndex)
    {
        foreach (var stream in streams)
            if (stream.Index == streamIndex)
                return true;
        return false;
    }

    /// <summary>三条来源都能打开解码器时创建；否则返回 null。</summary>
    public static ChannelMergePreviewDecoder? TryCreate(IReadOnlyList<(MediaStream Stream, bool Swap)> sources)
    {
        if (sources.Count != ChannelMergeMixer.SlotCount)
            return null;
        var decoders = new List<CodecContext>();
        foreach (var (stream, _) in sources)
        {
            if (AudioPlaybackPipeline.TryOpenDecoder(stream) is not { } decoder)
            {
                foreach (var opened in decoders)
                    opened.Dispose();
                return null;
            }
            decoders.Add(decoder);
        }
        return new ChannelMergePreviewDecoder(sources.Select(source => source.Stream).ToArray(), decoders.ToArray(),
            sources.Select(source => source.Swap).ToArray());
    }

    /// <summary>定位后重新开始。</summary>
    public void Flush()
    {
        foreach (var decoder in decoders)
            decoder.FlushBuffers();
        mixer.Reset();
    }

    /// <summary>送入一个来源的包（null 表示结束：冲出解码器并不再等待）；回调中的帧只在回调期间有效。</summary>
    public void Decode(Packet? packet, Action<Frame> onFrame)
    {
        sink = onFrame;
        try
        {
            if (packet is null)
            {
                for (var slot = 0; slot < decoders.Length; slot++)
                    DecodeSlot(slot, null);
                mixer.Produce(final: true);
                return;
            }
            var index = Array.FindIndex(streams, stream => stream.Index == packet.StreamIndex);
            if (index >= 0)
                DecodeSlot(index, packet);
        }
        finally
        {
            sink = null;
        }
    }

    private void DecodeSlot(int slot, Packet? packet)
    {
        var timeBase = streams[slot].TimeBase;
        foreach (var decoded in decoders[slot].DecodePacket(packet, frame, preserveRemainingOutput: true))
        {
            var pts = decoded.BestEffortTimestamp;
            if (pts == FF.AV_NOPTS_VALUE)
                pts = decoded.Pts;
            if (pts == FF.AV_NOPTS_VALUE)
                continue;
            mixer.Accept(slot, decoded, Rescale(pts, timeBase, TimeBase90k));
        }
    }

    private unsafe void OnMerged(Frame merged, int samples, long pts90k, bool restart)
    {
        AVFrame* raw = merged;
        raw->pts = Rescale(pts90k, TimeBase90k, streams[0].TimeBase);
        raw->best_effort_timestamp = raw->pts;
        sink?.Invoke(merged);
    }

    private static readonly AVRational TimeBase90k = new() { num = 1, den = 90_000 };

    private static long Rescale(long value, AVRational from, AVRational to) =>
        (long)Math.Round(value * (double)from.num * to.den / ((double)from.den * to.num));

    public void Dispose()
    {
        foreach (var decoder in decoders)
            decoder.Dispose();
        mixer.Dispose();
        frame.Dispose();
    }
}
