using System;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.Extensions;
using TSCutter.GUI.FFmpeg;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Utils.Media.DolbyE;

/// <summary>收到一帧杜比 E 解码输出：所有声道，fltp，采样率随帧率，时间戳为音轨时基。</summary>
internal delegate void DolbyEFrameHandler(Frame frame, DolbyEFrameHeader header);

/// <summary>
/// 解码 SMPTE 302M 中的杜比 E：302M 解码器以 copy 模式原样输出 AES 字，按 337M 拆出突发，
/// 读帧头识别声道组配置后交给 dolby_e 解码器。突发时间戳 = 所在 302M 包的 PTS + 突发起点的样本偏移。
/// 302M 包时间不连续（定位或丢包）时丢弃未完成的突发。
/// </summary>
internal sealed unsafe class DolbyEDecoder : IDisposable
{
    private const int AesSampleRate = 48_000;

    private readonly CodecContext pcm;
    private readonly CodecContext? dolby;
    private readonly AVRational timeBase;
    private readonly Frame pcmFrame = new();
    private readonly Frame dolbyFrame = new();
    private readonly Packet burstPacket = new();
    private S337mDeframer[] deframers = [];
    private int lockedPair = -1;
    private long expectedPts = FF.AV_NOPTS_VALUE;

    private DolbyEDecoder(CodecContext pcm, CodecContext? dolby, AVRational timeBase)
    {
        this.pcm = pcm;
        this.dolby = dolby;
        this.timeBase = timeBase;
    }

    /// <summary>最近一个可读帧头；探测和界面用它显示声道组配置。</summary>
    public DolbyEFrameHeader? LastHeader { get; private set; }

    /// <summary>已读到的可解析帧头数。</summary>
    public int HeaderCount { get; private set; }

    /// <summary>承载杜比 E 的 AES 对序号（从 0 开始）；尚未找到时为 -1。</summary>
    public int AesPair => lockedPair;

    public static bool IsCarrier(MediaStream stream) =>
        stream.Codecpar is { CodecType: AVMediaType.AVMEDIA_TYPE_AUDIO, CodecId: AVCodecID.AV_CODEC_ID_S302M };

    public static bool IsDecoderAvailable => FF.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_DOLBY_E) != null &&
                                             FF.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_S302M) != null;

    /// <param name="headersOnly">只拆帧和读帧头，不创建 dolby_e 解码器（探测用）。</param>
    public static DolbyEDecoder? TryCreate(MediaStream stream, bool headersOnly = false) =>
        IsCarrier(stream) ? TryCreate(stream.Codecpar, stream.TimeBase, headersOnly) : null;

    /// <summary>不经解复用器使用：直接送入 302M 的 PES 负载，时间戳的时基为 <paramref name="timeBase"/>。</summary>
    public static DolbyEDecoder? TryCreate(CodecParameters? parameters, AVRational timeBase, bool headersOnly = false)
    {
        if (!IsDecoderAvailable)
            return null;
        CodecContext? pcm = null;
        CodecContext? dolby = null;
        try
        {
            pcm = new CodecContext(Codec.FindDecoderById(AVCodecID.AV_CODEC_ID_S302M));
            if (parameters is not null)
                pcm.FillParameters(parameters);
            pcm.PacketTimeBase = timeBase;
            AVCodecContext* raw = pcm;
            // copy：非 PCM 数据（杜比 E 突发）原样输出，不丢弃也不报错。
            FFmpegException.Check(FF.av_opt_set_int(raw->priv_data, "non_pcm_mode", 0, 0));
            pcm.Open();
            if (!headersOnly)
            {
                dolby = new CodecContext(Codec.FindDecoderById(AVCodecID.AV_CODEC_ID_DOLBY_E));
                dolby.PacketTimeBase = timeBase;
                dolby.Open();
            }
            return new DolbyEDecoder(pcm, dolby, timeBase);
        }
        catch
        {
            pcm?.Dispose();
            dolby?.Dispose();
            return null;
        }
    }

    public void Flush()
    {
        pcm.FlushBuffers();
        dolby?.FlushBuffers();
        foreach (var deframer in deframers)
            deframer.Reset();
        expectedPts = FF.AV_NOPTS_VALUE;
    }

    /// <summary>送入一个 302M 包（null 表示结束），每解出一帧回调一次。损坏的突发跳过，不抛出。</summary>
    public void Decode(Packet? packet, DolbyEFrameHandler? handler)
    {
        var result = FF.avcodec_send_packet(pcm, packet);
        if (result < 0 && result != FF.AVERROR_EOF)
            return;
        while (true)
        {
            pcmFrame.Unref();
            result = FF.avcodec_receive_frame(pcm, pcmFrame);
            if (result < 0)
                break;
            Consume(pcmFrame, handler);
        }
        pcmFrame.Unref();
    }

    private void Consume(Frame frame, DolbyEFrameHandler? handler)
    {
        AVFrame* raw = frame;
        var channels = raw->ch_layout.nb_channels;
        var samples = raw->nb_samples;
        if (channels < 2 || samples <= 0 || raw->data[0] == null)
            return;
        var pairs = channels / 2;
        if (deframers.Length != pairs)
        {
            deframers = new S337mDeframer[pairs];
            for (var i = 0; i < pairs; i++)
                deframers[i] = new S337mDeframer();
            lockedPair = -1;
        }

        var pts = raw->pts != FF.AV_NOPTS_VALUE ? raw->pts : raw->best_effort_timestamp;
        // 时间不连续（定位、丢包）时，跨包的残缺突发不能与新数据拼接。
        if (pts != FF.AV_NOPTS_VALUE && expectedPts != FF.AV_NOPTS_VALUE &&
            Math.Abs(pts - expectedPts) > Ticks(48))
            foreach (var deframer in deframers)
                deframer.Reset();
        expectedPts = pts == FF.AV_NOPTS_VALUE ? FF.AV_NOPTS_VALUE : pts + Ticks(samples);

        var is16 = (AVSampleFormat)raw->format == AVSampleFormat.AV_SAMPLE_FMT_S16;
        var data = raw->data[0];
        for (var pair = 0; pair < pairs; pair++)
        {
            if (lockedPair >= 0 && pair != lockedPair)
                continue;
            var deframer = deframers[pair];
            for (var i = 0; i < samples; i++)
            {
                var index = i * channels + pair * 2;
                uint left, right;
                if (is16)
                {
                    left = (uint)((ushort*)data)[index] << 16;
                    right = (uint)((ushort*)data)[index + 1] << 16;
                }
                else
                {
                    left = ((uint*)data)[index];
                    right = ((uint*)data)[index + 1];
                }
                var burst = deframer.Push(left, right, pts == FF.AV_NOPTS_VALUE ? FF.AV_NOPTS_VALUE : pts + Ticks(i));
                if (burst is null || burst.DataType != S337mDeframer.DolbyEDataType ||
                    !DolbyEFrameHeader.TryParse(burst.Payload, out var header))
                    continue;
                lockedPair = pair;
                LastHeader = header;
                HeaderCount++;
                DecodeBurst(burst, header, handler);
            }
        }
    }

    private void DecodeBurst(S337mBurst burst, DolbyEFrameHeader header, DolbyEFrameHandler? handler)
    {
        if (dolby is null || handler is null)
            return;
        AVPacket* packet = burstPacket;
        FF.av_packet_unref(packet);
        if (FF.av_new_packet(packet, burst.Payload.Length) < 0)
            return;
        burst.Payload.AsSpan().CopyTo(new Span<byte>(packet->data, packet->size));
        packet->pts = packet->dts = burst.Timestamp;
        var result = FF.avcodec_send_packet(dolby, packet);
        FF.av_packet_unref(packet);
        if (result < 0)
            return;
        while (true)
        {
            dolbyFrame.Unref();
            if (FF.avcodec_receive_frame(dolby, dolbyFrame) < 0)
                break;
            handler(dolbyFrame, header);
        }
        dolbyFrame.Unref();
    }

    private long Ticks(int samples) =>
        (long)Math.Round(samples * (double)timeBase.den / ((double)timeBase.num * AesSampleRate));

    /// <summary>
    /// 把解码器整帧中的一个声道组复制为独立的帧（fltp，原生声道顺序）；该帧的声道组配置没有这个声道组时返回 false。
    /// </summary>
    public static bool TryExtractProgram(Frame source, DolbyEFrameHeader header, int programNumber, Frame destination)
    {
        var programs = DolbyEConfiguration.GetPrograms(header.ProgramConfig);
        if (programNumber < 1 || programNumber > programs.Count)
            return false;
        var program = programs[programNumber - 1];
        AVFrame* input = source;
        AVFrame* output = destination;
        if ((AVSampleFormat)input->format != AVSampleFormat.AV_SAMPLE_FMT_FLTP ||
            input->ch_layout.nb_channels < program.LastChannel)
            return false;
        destination.Unref();
        output->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        output->nb_samples = input->nb_samples;
        output->sample_rate = input->sample_rate;
        output->ch_layout.order = AVChannelOrder.AV_CHANNEL_ORDER_NATIVE;
        output->ch_layout.nb_channels = program.ChannelCount;
        output->ch_layout.u.mask = program.Mask;
        if (FF.av_frame_get_buffer(output, 0) < 0)
            return false;
        output->pts = input->pts;
        output->best_effort_timestamp = input->best_effort_timestamp;
        output->time_base = input->time_base;
        var bytes = input->nb_samples * sizeof(float);
        var offsets = program.NativeOffsets;
        for (var i = 0; i < offsets.Count; i++)
        {
            var channel = program.FirstChannel + offsets[i];
            var plane = input->extended_data[DolbyEConfiguration.DecoderIndex(header.ProgramConfig, channel)];
            Buffer.MemoryCopy(plane, output->extended_data[i], bytes, bytes);
        }
        return true;
    }

    public void Dispose()
    {
        pcmFrame.Dispose();
        dolbyFrame.Dispose();
        burstPacket.Dispose();
        pcm.Dispose();
        dolby?.Dispose();
    }
}
