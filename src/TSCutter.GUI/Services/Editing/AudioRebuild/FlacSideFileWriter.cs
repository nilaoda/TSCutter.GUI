using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Services.Editing.AudioRebuild;

/// <summary>
/// FLAC 不能放进 TS，重建的音轨另存为独立的 .flac 文件：48 kHz、24 位，结束时由封装器写回总长度与校验。
/// </summary>
internal sealed unsafe class FlacSideFileWriter : IDisposable
{
    private readonly int channels;
    private readonly List<float>[] buffer;
    private AVCodecContext* encoder;
    private AVFormatContext* output;
    private AVStream* stream;
    private readonly Packet packet = new();
    private long samplesEncoded;
    private bool completed;

    public FlacSideFileWriter(string path, int channels, ulong mask)
    {
        this.channels = channels;
        buffer = new List<float>[channels];
        for (var i = 0; i < channels; i++)
            buffer[i] = [];
        try
        {
            var codec = FF.avcodec_find_encoder_by_name("flac");
            if (codec == null)
                throw new InvalidOperationException("Encoder unavailable: flac");
            encoder = FF.avcodec_alloc_context3(codec);
            if (encoder == null)
                throw new OutOfMemoryException();
            encoder->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_S32;
            encoder->bits_per_raw_sample = 24;
            encoder->sample_rate = AudioOutputChain.OutputRate;
            encoder->ch_layout.order = AVChannelOrder.AV_CHANNEL_ORDER_NATIVE;
            encoder->ch_layout.nb_channels = channels;
            encoder->ch_layout.u.mask = mask;
            encoder->time_base = new AVRational { num = 1, den = AudioOutputChain.OutputRate };
            FFmpegException.Check(FF.avcodec_open2(encoder, codec, null));

            AVFormatContext* context = null;
            FFmpegException.Check(FF.avformat_alloc_output_context2(&context, null, "flac", path));
            output = context;
            stream = FF.avformat_new_stream(output, null);
            if (stream == null)
                throw new OutOfMemoryException();
            FFmpegException.Check(FF.avcodec_parameters_from_context(stream->codecpar, encoder));
            stream->time_base = encoder->time_base;
            FFmpegException.Check(FF.avio_open2(&output->pb, path, 2, null, null));
            FFmpegException.Check(FF.avformat_write_header(output, null));
        }
        catch
        {
            Release();
            throw;
        }
    }

    public void Write(List<float>[] source, int count)
    {
        for (var c = 0; c < channels; c++)
            buffer[c].AddRange(CollectionsMarshal.AsSpan(source[c])[..count]);
        var frameSize = Math.Max(1, encoder->frame_size == 0 ? 4608 : encoder->frame_size);
        while (buffer[0].Count >= frameSize)
            EncodeFrame(frameSize);
    }

    public void Complete()
    {
        if (completed)
            return;
        if (buffer[0].Count > 0)
            EncodeFrame(buffer[0].Count);
        FFmpegException.Check(FF.avcodec_send_frame(encoder, null));
        Receive();
        FFmpegException.Check(FF.av_write_trailer(output));
        completed = true;
        Release();
    }

    private void EncodeFrame(int samples)
    {
        using var frame = new Frame();
        AVFrame* raw = frame;
        raw->format = (int)AVSampleFormat.AV_SAMPLE_FMT_S32;
        raw->nb_samples = samples;
        raw->sample_rate = AudioOutputChain.OutputRate;
        raw->ch_layout = encoder->ch_layout;
        FFmpegException.Check(FF.av_frame_get_buffer(raw, 0));
        var data = (int*)raw->data[0];
        for (var i = 0; i < samples; i++)
            for (var c = 0; c < channels; c++)
                data[i * channels + c] = (int)Math.Round(Math.Clamp(buffer[c][i], -1f, 1f) * 8388607f) << 8;
        raw->pts = samplesEncoded;
        samplesEncoded += samples;
        foreach (var list in buffer)
            list.RemoveRange(0, samples);
        FFmpegException.Check(FF.avcodec_send_frame(encoder, raw));
        Receive();
    }

    private void Receive()
    {
        AVPacket* raw = packet;
        while (true)
        {
            var result = FF.avcodec_receive_packet(encoder, raw);
            if (result == FF.AVERROR_EAGAIN || result == FF.AVERROR_EOF)
                return;
            FFmpegException.Check(result);
            raw->stream_index = stream->index;
            raw->pts = Rescale(raw->pts, encoder->time_base, stream->time_base);
            raw->dts = Rescale(raw->dts, encoder->time_base, stream->time_base);
            raw->duration = Rescale(raw->duration, encoder->time_base, stream->time_base);
            var written = FF.av_interleaved_write_frame(output, raw);
            FF.av_packet_unref(raw);
            FFmpegException.Check(written);
        }
    }

    private static long Rescale(long value, AVRational from, AVRational to) => value == FF.AV_NOPTS_VALUE
        ? value
        : (long)Math.Round(value * ((double)from.num * to.den) / ((double)from.den * to.num));

    private void Release()
    {
        if (output != null)
        {
            if (output->pb != null)
                FF.avio_closep(&output->pb);
            FF.avformat_free_context(output);
            output = null;
        }
        if (encoder != null)
        {
            var context = encoder;
            FF.avcodec_free_context(&context);
            encoder = null;
        }
    }

    public void Dispose()
    {
        Release();
        packet.Dispose();
    }
}
