using System;
using System.Buffers;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Media;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Services.Media;

/// <summary>将文字字幕解码成 ASS 事件，再编码为目标封装支持的文字字幕。</summary>
internal sealed unsafe class MediaMuxSubtitleTranscoder : IDisposable
{
    private static readonly AVRational Microseconds = new() { num = 1, den = FF.AV_TIME_BASE };
    private static readonly AVRational Milliseconds = new() { num = 1, den = 1000 };
    private AVCodecContext* decoder;
    private AVCodecContext* encoder;
    private readonly Packet encoded = new();
    private const int EncodingBufferSize = 1024 * 1024;
    private readonly AVRational inputTimeBase;
    private readonly bool trimAtZero;
    private long lastDts = FF.AV_NOPTS_VALUE;

    public MediaMuxSubtitleTranscoder(AVStream* source, AVStream* destination, AVCodecID target)
    {
        inputTimeBase = source->time_base;
        trimAtZero = target == AVCodecID.AV_CODEC_ID_MOV_TEXT;
        try
        {
            decoder = FF.avcodec_alloc_context3(FF.avcodec_find_decoder(source->codecpar->codec_id));
            encoder = FF.avcodec_alloc_context3(FF.avcodec_find_encoder(target));
            if (decoder == null || encoder == null)
                throw new OutOfMemoryException();
            FFmpegException.Check(FF.avcodec_parameters_to_context(decoder, source->codecpar));
            decoder->pkt_timebase = inputTimeBase;
            FFmpegException.Check(FF.avcodec_open2(decoder, null, null));
            encoder->time_base = Microseconds;
            encoder->width = decoder->width;
            encoder->height = decoder->height;
            // 字幕编码器需要 ASS 样式头；复制并补零，所有权交给编码器上下文。
            if (decoder->subtitle_header_size > 0)
            {
                var size = decoder->subtitle_header_size;
                encoder->subtitle_header = (byte*)FF.av_mallocz(checked((ulong)size + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
                if (encoder->subtitle_header == null)
                    throw new OutOfMemoryException();
                Buffer.MemoryCopy(decoder->subtitle_header, encoder->subtitle_header, size, size);
                encoder->subtitle_header_size = size;
            }
            FFmpegException.Check(FF.avcodec_open2(encoder, null, null));
            FFmpegException.Check(FF.avcodec_parameters_from_context(destination->codecpar, encoder));
            destination->time_base = Microseconds;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Write(AVPacket* packet, AVFormatContext* output, int streamIndex, long delta, long end)
    {
        var remaining = *packet;
        var count = 0;
        while (true)
        {
            AVSubtitle subtitle = default;
            subtitle.pts = FF.AV_NOPTS_VALUE;
            var got = 0;
            int consumed;
            try
            {
                consumed = FF.avcodec_decode_subtitle2(decoder, &subtitle, &got, &remaining);
                FFmpegException.Check(consumed);
                if (got != 0 && subtitle.num_rects > 0)
                {
                    var start = subtitle.start_display_time;
                    var duration = subtitle.end_display_time != uint.MaxValue && subtitle.end_display_time > start
                        ? (long)subtitle.end_display_time - start
                        : MediaMuxService.Rescale(packet->duration, inputTimeBase, Milliseconds) - start;
                    if (duration <= 0)
                        throw new MediaMuxException(MediaMuxError.MissingTimestamp);
                    var pts = subtitle.pts == FF.AV_NOPTS_VALUE
                        ? MediaMuxService.Rescale(packet->pts, inputTimeBase, Microseconds) : subtitle.pts;
                    pts = checked(pts + start * 1000L);
                    // 字幕编码 API 无延迟输出，SRT／mov_text 会将当前事件的矩形合并为同一条提示。
                    var local = subtitle;
                    local.pts = pts;
                    local.start_display_time = 0;
                    local.end_display_time = checked((uint)duration);
                    encoded.Unref();
                    AVPacket* raw = encoded;
                    int size;
                    // 轨道共用池化暂存区，稀疏字幕不各自持有 1 MiB；写出包拥有独立的实际大小缓冲。
                    var buffer = ArrayPool<byte>.Shared.Rent(EncodingBufferSize);
                    try
                    {
                        fixed (byte* data = buffer)
                        {
                            size = FF.avcodec_encode_subtitle(encoder, data, EncodingBufferSize, &local);
                            FFmpegException.Check(size);
                            if (size > 0)
                            {
                                FFmpegException.Check(FF.av_new_packet(raw, size));
                                Buffer.MemoryCopy(data, raw->data, size, size);
                            }
                        }
                    }
                    finally { ArrayPool<byte>.Shared.Return(buffer); }
                    if (size > 0)
                    {
                        var timeBase = output->streams[streamIndex]->time_base;
                        raw->pts = raw->dts = checked(MediaMuxService.Rescale(pts, Microseconds, timeBase) + delta);
                        raw->duration = MediaMuxService.Rescale(duration, Milliseconds, timeBase);
                        raw->flags = 1;
                        raw->stream_index = streamIndex;
                        raw->pos = -1;
                        if (MediaMuxService.TrimSubtitleToOutputRange(raw, trimAtZero, end))
                        {
                            if (lastDts != FF.AV_NOPTS_VALUE && raw->dts < lastDts)
                                throw new MediaMuxException(MediaMuxError.NonMonotonicTimestamp);
                            lastDts = raw->dts;
                            FFmpegException.Check(FF.av_interleaved_write_frame(output, raw));
                            count++;
                        }
                    }
                }
            }
            finally { FF.avsubtitle_free(&subtitle); }
            if (consumed <= 0 || consumed >= remaining.size)
                return count;
            remaining.data += consumed;
            remaining.size -= consumed;
        }
    }

    public void Dispose()
    {
        fixed (AVCodecContext** pointer = &decoder)
            FF.avcodec_free_context(pointer);
        fixed (AVCodecContext** pointer = &encoder)
            FF.avcodec_free_context(pointer);
        encoded.Dispose();
    }
}
