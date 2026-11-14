using System;
using System.Collections.Generic;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Media;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Services.Editing.AudioRebuild;

/// <summary>一个编码后的音频帧；时间戳为 90 kHz、未回绕。</summary>
internal sealed record EncodedAudioUnit(int OutputIndex, byte[] Data, long Pts90k);

/// <summary>
/// 送入输出链的来源声道布局：声道数与 FFmpeg 原生顺序的布局掩码。<paramref name="IsSurround51"/> 表示 L R C LFE Ls Rs，
/// AAC 按 ADTS 声道配置改用后环绕布局。
/// </summary>
internal readonly record struct AudioSourceLayout(int ChannelCount, ulong Mask, bool IsSurround51)
{
    /// <summary>5.1（L R C LFE Ls Rs，侧环绕）。</summary>
    public static AudioSourceLayout Surround51 => new(6, 0x60F, true);
}

/// <summary>
/// 一路重建输出（杜比 E 声道组、多轨合并结果）：把来源的解码帧重采样到 48 kHz 并转换声道，再编码。时间戳按样本计数推算：
/// 一段连续的输入（run）从首帧的时间戳开始，第 n 个 48 kHz 样本的时间为起点 + n / 48000 秒。
/// 输入不连续时结束本段（冲出编码器）并重新开始，输出保留同样的间隙，不补静音。
/// FLAC 另存为独立文件，没有时间戳：间隙补静音、重叠部分丢弃，使文件与视频起点保持同步。
/// </summary>
internal sealed unsafe class AudioOutputChain : IDisposable
{
    public const int OutputRate = 48_000;
    private const int PcmFrameSamples = 1920;
    // 独立 FLAC 文件中最多补这么长的静音；更大的时间差视为时间戳跳变。
    private const long MaxFlacGap = OutputRate * 600L;

    private readonly AudioOutput output;
    private readonly int outputIndex;
    private readonly int channels;
    private readonly ulong mask;
    private readonly FlacSideFileWriter? flac;
    private SwrContext* swr;
    private ulong swrMask;
    private int swrChannels;
    private int swrRate;
    private AVCodecContext* encoder;
    private readonly Packet packet = new();
    private readonly Frame encoderFrame = new();
    private readonly List<float>[] fifo;
    // 重采样输出缓冲（各声道依次排列），按需增长后复用。
    private float[] scratch = [];
    private bool running;
    private long runStart48;
    private long samplesQueued;
    private long samplesEncoded;
    private int pcmFraming;

    public AudioOutputChain(AudioOutput output, int outputIndex, AudioSourceLayout source, string? flacPath)
    {
        this.output = output;
        this.outputIndex = outputIndex;
        channels = AudioOutputCodecInfo.OutputChannels(output.Codec, output.Layout, source.ChannelCount);
        mask = OutputMask(output, source, channels);
        fifo = new List<float>[channels];
        for (var i = 0; i < channels; i++)
            fifo[i] = [];
        if (output.Codec == AudioOutputCodec.Flac)
            flac = new FlacSideFileWriter(flacPath ?? throw new ArgumentNullException(nameof(flacPath)), channels, mask);
    }

    public AudioOutput Output => output;

    private static ulong OutputMask(AudioOutput output, AudioSourceLayout source, int channels)
    {
        if (channels == source.ChannelCount && output.Layout == AudioOutputLayout.Original)
        {
            // AAC 按 ADTS 声道配置写入：5.1 为 C L R Ls Rs LFE，用 FFmpeg 的 5.1（后环绕）布局。
            if (output.Codec == AudioOutputCodec.Aac && source.IsSurround51)
                return 0x3F;
            return source.Mask;
        }
        return channels switch
        {
            1 => 0x4,
            2 => 0x3,
            _ => output.Codec == AudioOutputCodec.Aac ? 0x3FUL : 0x60FUL
        };
    }

    /// <summary>
    /// 送入一帧。来源在这一帧中不存在时 <paramref name="source"/> 为 null，按 <paramref name="silentSamples"/>
    /// （48 kHz 样本数）输出等长的静音。<paramref name="restart"/> 表示时间不连续：先结束上一段再从 <paramref name="pts90k"/> 重新开始。
    /// </summary>
    public void Push(Frame? source, int silentSamples, long pts90k, bool restart, Action<EncodedAudioUnit> emit)
    {
        if (flac is not null)
        {
            PushFlac(source, silentSamples, pts90k);
            return;
        }
        if (restart && running)
            EndRun(emit);
        if (!running)
            StartRun(pts90k);
        Append(source, silentSamples);
        Encode(emit, final: false);
    }

    /// <summary>结束：冲出重采样与编码器中剩余的数据。</summary>
    public void Finish(Action<EncodedAudioUnit> emit)
    {
        if (flac is not null)
        {
            if (running)
            {
                FlushResampler();
                flac.Write(fifo, fifo[0].Count);
                ClearFifo();
            }
            flac.Complete();
            return;
        }
        if (running)
            EndRun(emit);
    }

    private void PushFlac(Frame? source, int silentSamples, long pts90k)
    {
        var start48 = Rescale90kTo48k(pts90k);
        if (!running)
        {
            running = true;
            runStart48 = start48;
            samplesQueued = 0;
        }
        else
        {
            // 独立文件没有时间戳：缺失的时段补静音，重叠的部分丢弃。重采样器中尚未输出的样本也算已写入。
            // 相差超过上限的是时间戳跳变（拼接的文件，或被当作回绕的跳变），不是缺失：接着写，时间以新位置为准。
            var pending = swr == null ? 0 : FF.swr_get_delay(swr, OutputRate);
            var gap = start48 - (runStart48 + samplesQueued + pending);
            if (Math.Abs(gap) > MaxFlacGap)
                runStart48 += gap;
            else if (gap > OutputRate / 100)
            {
                FlushResampler();
                AppendSilence((int)(gap - pending));
            }
            else if (gap < -OutputRate / 100)
                return;
        }
        Append(source, silentSamples);
        var count = fifo[0].Count;
        flac!.Write(fifo, count);
        samplesQueued += count;
        ClearFifo();
    }

    private void StartRun(long pts90k)
    {
        running = true;
        runStart48 = Rescale90kTo48k(pts90k);
        samplesQueued = samplesEncoded = 0;
        ClearFifo();
        if (output.Codec != AudioOutputCodec.Pcm)
            OpenEncoder();
    }

    private void EndRun(Action<EncodedAudioUnit> emit)
    {
        FlushResampler();
        Encode(emit, final: true);
        if (encoder != null)
        {
            Drain(emit);
            var context = encoder;
            FF.avcodec_free_context(&context);
            encoder = null;
        }
        FreeResampler();
        running = false;
    }

    private void Append(Frame? source, int silentSamples)
    {
        if (source is null)
        {
            // 来源缺失：按该帧的时长补静音，保持后续时间连续。
            FlushResampler();
            AppendSilence(silentSamples);
            return;
        }
        AVFrame* input = source;
        EnsureResampler(input);
        var capacity = (int)(FF.swr_get_delay(swr, OutputRate) + (long)input->nb_samples * OutputRate / input->sample_rate + 64);
        Convert(input->extended_data, input->nb_samples, capacity);
    }

    private void EnsureResampler(AVFrame* input)
    {
        var inputMask = input->ch_layout.u.mask;
        if (swr != null && inputMask == swrMask && input->ch_layout.nb_channels == swrChannels && input->sample_rate == swrRate)
            return;
        // 配置变化（声道数或帧率）时冲出旧重采样器的余量再重建。
        FlushResampler();
        FreeResampler();
        AVChannelLayout inLayout = input->ch_layout;
        AVChannelLayout outLayout = default;
        outLayout.order = AVChannelOrder.AV_CHANNEL_ORDER_NATIVE;
        outLayout.nb_channels = channels;
        outLayout.u.mask = mask;
        SwrContext* context = null;
        FFmpegException.Check(FF.swr_alloc_set_opts2(&context, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLTP, OutputRate,
            &inLayout, (AVSampleFormat)input->format, input->sample_rate, 0, null));
        var result = FF.swr_init(context);
        if (result < 0)
        {
            FF.swr_free(&context);
            FFmpegException.Check(result);
        }
        swr = context;
        swrMask = inputMask;
        swrChannels = input->ch_layout.nb_channels;
        swrRate = input->sample_rate;
    }

    private void Convert(byte** input, int count, int capacity)
    {
        capacity = Math.Max(capacity, 64);
        if (scratch.Length < capacity * channels)
            scratch = new float[capacity * channels];
        var pointers = stackalloc byte*[channels];
        int produced;
        fixed (float* data = scratch)
        {
            for (var i = 0; i < channels; i++)
                pointers[i] = (byte*)(data + i * capacity);
            produced = FF.swr_convert(swr, pointers, capacity, input, count);
        }
        FFmpegException.Check(produced);
        for (var i = 0; i < channels; i++)
            fifo[i].AddRange(scratch.AsSpan(i * capacity, produced));
    }

    private void FlushResampler()
    {
        if (swr == null)
            return;
        // 冲出缓冲的样本后释放；之后的输入重新创建重采样器。
        var delay = (int)FF.swr_get_delay(swr, OutputRate) + 64;
        Convert(null, 0, delay);
        FreeResampler();
    }

    private void FreeResampler()
    {
        if (swr == null)
            return;
        var context = swr;
        FF.swr_free(&context);
        swr = null;
    }

    private void AppendSilence(int samples)
    {
        if (samples <= 0)
            return;
        foreach (var list in fifo)
        {
            var start = list.Count;
            System.Runtime.InteropServices.CollectionsMarshal.SetCount(list, start + samples);
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list)[start..].Clear();
        }
    }

    private void ClearFifo()
    {
        foreach (var list in fifo)
            list.Clear();
    }

    private void OpenEncoder()
    {
        var name = output.Codec switch
        {
            AudioOutputCodec.Ac3 => "ac3",
            AudioOutputCodec.Eac3 => "eac3",
            _ => "aac"
        };
        var codec = FF.avcodec_find_encoder_by_name(name);
        if (codec == null)
            throw new InvalidOperationException($"Encoder unavailable: {name}");
        var context = FF.avcodec_alloc_context3(codec);
        if (context == null)
            throw new OutOfMemoryException();
        context->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        context->sample_rate = OutputRate;
        context->ch_layout.order = AVChannelOrder.AV_CHANNEL_ORDER_NATIVE;
        context->ch_layout.nb_channels = channels;
        context->ch_layout.u.mask = mask;
        context->bit_rate = output.BitrateKbps * 1000L;
        context->time_base = new AVRational { num = 1, den = OutputRate };
        var result = FF.avcodec_open2(context, codec, null);
        if (result < 0)
        {
            FF.avcodec_free_context(&context);
            FFmpegException.Check(result);
        }
        encoder = context;
    }

    private void Encode(Action<EncodedAudioUnit> emit, bool final)
    {
        var frameSize = output.Codec == AudioOutputCodec.Pcm ? PcmFrameSamples : Math.Max(1, encoder->frame_size);
        while (fifo[0].Count >= frameSize || final && fifo[0].Count > 0)
        {
            var available = Math.Min(frameSize, fifo[0].Count);
            if (output.Codec == AudioOutputCodec.Pcm)
            {
                var pts = runStart48 + samplesEncoded;
                emit(new EncodedAudioUnit(outputIndex, PackS302m(available), Rescale48kTo90k(pts)));
            }
            else
                SendFrame(available, frameSize, emit);
            foreach (var list in fifo)
                list.RemoveRange(0, available);
            samplesEncoded += available;
        }
    }

    private void SendFrame(int available, int frameSize, Action<EncodedAudioUnit> emit)
    {
        encoderFrame.Unref();
        AVFrame* raw = encoderFrame;
        raw->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        // AC3 只接受固定帧长：最后不足一帧时补零。
        raw->nb_samples = frameSize;
        raw->sample_rate = OutputRate;
        raw->ch_layout.order = AVChannelOrder.AV_CHANNEL_ORDER_NATIVE;
        raw->ch_layout.nb_channels = channels;
        raw->ch_layout.u.mask = mask;
        FFmpegException.Check(FF.av_frame_get_buffer(raw, 0));
        for (var c = 0; c < channels; c++)
        {
            var destination = new Span<float>(raw->extended_data[c], frameSize);
            destination.Clear();
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(fifo[c])[..available].CopyTo(destination);
        }
        raw->pts = runStart48 + samplesEncoded;
        FFmpegException.Check(FF.avcodec_send_frame(encoder, raw));
        Receive(emit);
    }

    private void Drain(Action<EncodedAudioUnit> emit)
    {
        var result = FF.avcodec_send_frame(encoder, null);
        if (result < 0 && result != FF.AVERROR_EOF)
            FFmpegException.Check(result);
        Receive(emit);
    }

    private void Receive(Action<EncodedAudioUnit> emit)
    {
        AVPacket* raw = packet;
        while (true)
        {
            var result = FF.avcodec_receive_packet(encoder, raw);
            if (result == FF.AVERROR_EAGAIN || result == FF.AVERROR_EOF)
                return;
            FFmpegException.Check(result);
            try
            {
                var data = new ReadOnlySpan<byte>(raw->data, raw->size);
                var payload = output.Codec == AudioOutputCodec.Aac ? WithAdtsHeader(data, channels) : data.ToArray();
                var pts = raw->pts != FF.AV_NOPTS_VALUE ? raw->pts : runStart48 + samplesEncoded;
                emit(new EncodedAudioUnit(outputIndex, payload, Rescale48kTo90k(pts)));
            }
            finally
            {
                FF.av_packet_unref(raw);
            }
        }
    }

    /// <summary>为原始 AAC 帧加上 ADTS 头（LC、48 kHz）。</summary>
    internal static byte[] WithAdtsHeader(ReadOnlySpan<byte> frame, int channels)
    {
        const int profile = 1; // AAC LC
        const int frequencyIndex = 3; // 48 kHz
        var configuration = channels switch { 6 => 6, 4 => 4, 2 => 2, _ => 1 };
        var length = frame.Length + 7;
        var result = new byte[length];
        result[0] = 0xFF;
        result[1] = 0xF1;
        result[2] = (byte)((profile << 6) | (frequencyIndex << 2) | (configuration >> 2));
        result[3] = (byte)(((configuration & 3) << 6) | (length >> 11));
        result[4] = (byte)((length >> 3) & 0xFF);
        result[5] = (byte)(((length & 7) << 5) | 0x1F);
        result[6] = 0xFC;
        frame.CopyTo(result.AsSpan(7));
        return result;
    }

    /// <summary>按 SMPTE 302M 打包 24 位 PCM：4 字节 AES3 头后每个声道对占 7 字节，位序反转。</summary>
    private byte[] PackS302m(int samples)
    {
        var size = samples * channels * 28 / 8;
        var result = new byte[4 + size];
        // 头：16 位负载长度、2 位声道数（(n-2)/2）、8 位通道标识、2 位位深（2 = 24 位）、4 位对齐。
        var header = ((uint)size << 16) | ((uint)((channels - 2) >> 1) << 14) | (2u << 4);
        result[0] = (byte)(header >> 24);
        result[1] = (byte)(header >> 16);
        result[2] = (byte)(header >> 8);
        result[3] = (byte)header;
        var o = 4;
        for (var i = 0; i < samples; i++)
        {
            var vucf = pcmFraming == 0 ? (byte)0x10 : (byte)0;
            for (var c = 0; c < channels; c += 2)
            {
                var left = ToInt24(fifo[c][i]);
                var right = ToInt24(fifo[c + 1][i]);
                result[o] = Reverse((byte)(left >> 8));
                result[o + 1] = Reverse((byte)(left >> 16));
                result[o + 2] = Reverse((byte)(left >> 24));
                result[o + 3] = (byte)(Reverse((byte)((right & 0x00000F00) >> 4)) | vucf);
                result[o + 4] = Reverse((byte)((right & 0x000FF000) >> 12));
                result[o + 5] = Reverse((byte)((right & 0x0FF00000) >> 20));
                result[o + 6] = Reverse((byte)((right & 0xF0000000) >> 28));
                o += 7;
            }
            if (++pcmFraming >= 192)
                pcmFraming = 0;
        }
        return result;
    }

    // 24 位有效位放在 32 位的高位。
    private static uint ToInt24(float value)
    {
        var scaled = (int)Math.Round(Math.Clamp(value, -1f, 1f) * 8388607f);
        return (uint)(scaled << 8);
    }

    private static byte Reverse(byte value)
    {
        value = (byte)(((value & 0xF0) >> 4) | ((value & 0x0F) << 4));
        value = (byte)(((value & 0xCC) >> 2) | ((value & 0x33) << 2));
        return (byte)(((value & 0xAA) >> 1) | ((value & 0x55) << 1));
    }

    internal static long Rescale90kTo48k(long pts90k) => (long)Math.Round(pts90k * (OutputRate / 90000.0));
    internal static long Rescale48kTo90k(long pts48k) => (long)Math.Round(pts48k * (90000.0 / OutputRate));

    public void Dispose()
    {
        FreeResampler();
        if (encoder != null)
        {
            var context = encoder;
            FF.avcodec_free_context(&context);
            encoder = null;
        }
        packet.Dispose();
        encoderFrame.Dispose();
        flac?.Dispose();
    }
}
