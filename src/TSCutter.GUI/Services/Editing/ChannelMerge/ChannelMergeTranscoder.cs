using System;
using System.Collections.Generic;
using System.Linq;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Services.Editing.AudioRebuild;

namespace TSCutter.GUI.Services.Editing.ChannelMerge;

/// <summary>
/// 多轨合并的转码：三条来源的 PES 分别解码，由 <see cref="ChannelMergeMixer"/> 对齐交织成 5.1，交给各路输出。
/// 前置来源是锚定 PID：编码帧放在 PTS 不晚于它的最近一个前置来源 PES 的位置。
/// </summary>
internal sealed class ChannelMergeTranscoder : IAudioRebuildTranscoder
{
    public const int OutputRate = ChannelMergeMixer.OutputRate;

    private readonly IReadOnlyList<AudioOutputChain> chains;
    private readonly int[] pids;
    private readonly PesAudioDecoder?[] decoders;
    private readonly ChannelMergeMixer mixer;
    private readonly AnchorSlotMap slots = new();
    private Action<EncodedAudioUnit, int>? sink;

    /// <param name="sources">按槽位顺序（前置、中置与低音、环绕）的来源与左右互换设置。</param>
    public ChannelMergeTranscoder(IReadOnlyList<AudioOutputChain> chains,
        IReadOnlyList<(AudioRebuildSource Source, bool Swap)> sources)
    {
        if (sources.Count != ChannelMergeMixer.SlotCount)
            throw new ArgumentException("Channel merge needs three sources.", nameof(sources));
        this.chains = chains;
        pids = sources.Select(item => item.Source.Pid).ToArray();
        decoders = sources.Select(item => PesAudioDecoder.TryCreate(item.Source.StreamType, item.Source.Descriptors)).ToArray();
        if (decoders[0] is null)
        {
            // 输出链由重组流负责释放（转码器没有建成）。
            foreach (var decoder in decoders)
                decoder?.Dispose();
            throw new InvalidOperationException($"No decoder for PID 0x{pids[0]:X4}.");
        }
        mixer = new ChannelMergeMixer(sources.Select(item => item.Swap).ToArray(), OnMerged);
    }

    /// <summary>合并输出的帧数（以前置来源的帧计）。</summary>
    public int DecodedFrames => mixer.MergedFrames;

    public int ProcessedAnchorIndex { get; private set; } = -1;

    public void Feed(int pid, ReadOnlySpan<byte> payload, long pts90k, int pesIndex, Action<EncodedAudioUnit, int> emit)
    {
        var index = Array.IndexOf(pids, pid);
        if (index < 0)
            return;
        if (index == 0)
            slots.Add(pesIndex, pts90k);
        sink = emit;
        try
        {
            decoders[index]?.Decode(payload, pts90k, (frame, pts) => mixer.Accept(index, frame, pts));
        }
        finally
        {
            sink = null;
        }
    }

    public void Skip(int pid, int pesIndex)
    {
        if (pids[0] == pid)
            slots.Touch(pesIndex);
    }

    public void Finish(Action<EncodedAudioUnit, int> emit)
    {
        sink = emit;
        try
        {
            for (var i = 0; i < decoders.Length; i++)
            {
                var index = i;
                decoders[i]?.Flush((frame, pts) => mixer.Accept(index, frame, pts));
            }
            mixer.Produce(final: true);
            foreach (var chain in chains)
                chain.Finish(unit => emit(unit, slots.SlotFor(unit.Pts90k)));
        }
        finally
        {
            sink = null;
        }
    }

    private void OnMerged(Frame frame, int samples, long pts90k, bool restart)
    {
        var emit = sink;
        foreach (var chain in chains)
            chain.Push(frame, samples, pts90k, restart, unit => emit?.Invoke(unit, slots.SlotFor(unit.Pts90k)));
        // 这个时刻之前开始的锚定 PES 都已合并（最后一个可能还有尾部，留一个余量）。
        var until = pts90k + ChannelMergeMixer.Rescale48kTo90k(samples);
        ProcessedAnchorIndex = Math.Max(ProcessedAnchorIndex, slots.LastIndexAtOrBefore(until) - 1);
    }

    public void Dispose()
    {
        foreach (var chain in chains)
            chain.Dispose();
        foreach (var decoder in decoders)
            decoder?.Dispose();
        mixer.Dispose();
    }
}
