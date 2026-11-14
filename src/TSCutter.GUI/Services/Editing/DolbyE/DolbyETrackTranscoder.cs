using System;
using System.Collections.Generic;
using System.Linq;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Services.Editing.AudioRebuild;
using TSCutter.GUI.Utils.Media.DolbyE;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Services.Editing.DolbyE;

/// <summary>
/// 一条杜比 E 轨的转码：逐个送入 302M 的 PES 负载，解码后按各路输出的声道组取出声道交给输出链。
/// 杜比 E 轨自身就是锚定 PID，编码帧放在 PTS 不晚于它的最近一个杜比 E PES 的位置。
/// </summary>
internal sealed unsafe class DolbyETrackTranscoder : IAudioRebuildTranscoder
{
    private static readonly AVRational TimeBase90k = new() { num = 1, den = 90_000 };

    private readonly DolbyEDecoder decoder;
    // 各路输出与它所取的声道组；声道组帧每次送入后释放引用。
    private readonly IReadOnlyList<(AudioOutputChain Chain, int Program, Frame ProgramFrame)> outputs;
    private readonly Packet input = new();
    private readonly AnchorSlotMap slots = new();
    private long expectedPts = FF.AV_NOPTS_VALUE;
    private Action<EncodedAudioUnit, int>? sink;

    public DolbyETrackTranscoder(IReadOnlyList<(AudioOutputChain Chain, int Program)> outputs)
    {
        this.outputs = outputs.Select(output => (output.Chain, output.Program, new Frame())).ToArray();
        decoder = DolbyEDecoder.TryCreate(null, TimeBase90k)
                  ?? throw new InvalidOperationException("Dolby E decoder unavailable.");
    }

    public int DecodedFrames { get; private set; }

    public int ProcessedAnchorIndex { get; private set; } = -1;

    public void Feed(int pid, ReadOnlySpan<byte> payload, long pts90k, int pesIndex, Action<EncodedAudioUnit, int> emit)
    {
        slots.Add(pesIndex, pts90k);
        ProcessedAnchorIndex = pesIndex;
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

    public void Skip(int pid, int pesIndex)
    {
        slots.Touch(pesIndex);
        ProcessedAnchorIndex = pesIndex;
    }

    public void Finish(Action<EncodedAudioUnit, int> emit)
    {
        sink = emit;
        try
        {
            decoder.Decode(null, OnFrame);
            foreach (var (chain, _, _) in outputs)
                chain.Finish(unit => emit(unit, slots.SlotFor(unit.Pts90k)));
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
        // 声道组在这一帧中不存在时按帧长补静音。
        var silentSamples = (int)Math.Round(1792.0 * AudioOutputChain.OutputRate / Math.Max(1, header.SampleRate));
        foreach (var (chain, program, programFrame) in outputs)
        {
            var source = DolbyEDecoder.TryExtractProgram(frame, header, program, programFrame) ? programFrame : null;
            chain.Push(source, silentSamples, pts, restart, unit => emit(unit, slots.SlotFor(unit.Pts90k)));
            programFrame.Unref();
        }
    }

    public void Dispose()
    {
        foreach (var (chain, _, programFrame) in outputs)
        {
            chain.Dispose();
            programFrame.Dispose();
        }
        decoder.Dispose();
        input.Dispose();
    }
}
