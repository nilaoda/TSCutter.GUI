using System;
using System.Collections.Generic;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Services.Editing.AudioRebuild;

/// <summary>
/// 从 PES 负载解码普通音轨（MPEG 音频、AAC、AC3、E-AC3）：先把负载切成完整的编码帧（帧可以跨越 PES），
/// 解码后交出带 90 kHz 时间戳的画面。PES 的 PTS 属于负载中开始的第一帧；同一 PES 中后续帧的时间按样本数推算。
/// MPEG 音频与 ADTS AAC 按帧头中的帧长自行切分（FFmpeg 的解析器会丢掉 MPEG 音频的第一帧），其他编码用 FFmpeg 的解析器。
/// </summary>
internal sealed unsafe class PesAudioDecoder : IDisposable
{
    private AVCodecContext* context;
    private AVCodecParserContext* parser;
    private readonly HeaderFrameSplitter? splitter;
    private readonly Packet packet = new();
    private readonly Frame frame = new();
    private long expectedPts = FF.AV_NOPTS_VALUE;

    private PesAudioDecoder(AVCodecContext* context, AVCodecParserContext* parser, HeaderFrameSplitter? splitter)
    {
        this.context = context;
        this.parser = parser;
        this.splitter = splitter;
    }

    /// <summary>按节目表的流类型与描述符确定编码；不是能解码的普通音轨时返回 NONE。</summary>
    public static AVCodecID CodecFor(int streamType, ReadOnlySpan<byte> descriptors)
    {
        switch (streamType)
        {
            case 0x03 or 0x04:
                return AVCodecID.AV_CODEC_ID_MP2;
            case 0x0F:
                return AVCodecID.AV_CODEC_ID_AAC;
            case 0x11:
                return AVCodecID.AV_CODEC_ID_AAC_LATM;
            case 0x81:
                return AVCodecID.AV_CODEC_ID_AC3;
            case 0x87:
                return AVCodecID.AV_CODEC_ID_EAC3;
            case 0x06:
                for (var offset = 0; offset + 2 <= descriptors.Length;)
                {
                    var tag = descriptors[offset];
                    var length = descriptors[offset + 1];
                    var body = descriptors.Slice(offset + 2, Math.Min(length, descriptors.Length - offset - 2));
                    if (tag == 0x6A)
                        return AVCodecID.AV_CODEC_ID_AC3;
                    if (tag == 0x7A)
                        return AVCodecID.AV_CODEC_ID_EAC3;
                    if (tag == 0x05 && body.Length >= 4)
                    {
                        if (body[..4].SequenceEqual("AC-3"u8))
                            return AVCodecID.AV_CODEC_ID_AC3;
                        if (body[..4].SequenceEqual("EAC3"u8))
                            return AVCodecID.AV_CODEC_ID_EAC3;
                    }
                    offset += 2 + length;
                }
                return AVCodecID.AV_CODEC_ID_NONE;
            default:
                return AVCodecID.AV_CODEC_ID_NONE;
        }
    }

    public static PesAudioDecoder? TryCreate(int streamType, ReadOnlySpan<byte> descriptors)
    {
        var id = CodecFor(streamType, descriptors);
        if (id == AVCodecID.AV_CODEC_ID_NONE)
            return null;
        var codec = FF.avcodec_find_decoder(id);
        if (codec == null)
            return null;
        var context = FF.avcodec_alloc_context3(codec);
        if (context == null)
            return null;
        context->pkt_timebase = new AVRational { num = 1, den = 90_000 };
        if (FF.avcodec_open2(context, codec, null) < 0)
        {
            FF.avcodec_free_context(&context);
            return null;
        }
        var splitter = HeaderFrameSplitter.TryCreate(id);
        AVCodecParserContext* parser = null;
        if (splitter is null)
        {
            parser = FF.av_parser_init((int)id);
            if (parser == null)
            {
                FF.avcodec_free_context(&context);
                return null;
            }
        }
        return new PesAudioDecoder(context, parser, splitter);
    }

    /// <summary>送入一个 PES 的负载，按顺序交出解出的画面与时间戳（画面在回调返回后释放）。</summary>
    public void Decode(ReadOnlySpan<byte> payload, long pts90k, Action<Frame, long> onFrame)
    {
        if (splitter is not null)
        {
            splitter.Append(payload, pts90k);
            while (splitter.TryTake(out var frameData, out var framePts))
                fixed (byte* data = frameData)
                    SendFrame(data, frameData.Length, framePts, onFrame);
            return;
        }
        fixed (byte* start = payload)
        {
            var data = start;
            var remaining = payload.Length;
            // 时间戳只随负载的开头送入一次，解析器把它交给在这之后开始的第一帧。
            var pts = pts90k;
            while (remaining > 0)
            {
                byte* output = null;
                var outputSize = 0;
                var used = FF.av_parser_parse2(parser, context, &output, &outputSize, data, remaining, pts, pts, -1);
                if (used < 0)
                    return;
                data += used;
                remaining -= used;
                pts = FF.AV_NOPTS_VALUE;
                if (outputSize > 0)
                    SendFrame(output, outputSize, parser->pts, onFrame);
                if (used == 0 && outputSize == 0)
                    return;
            }
        }
    }

    /// <summary>结束：冲出解析器与解码器中剩余的帧。</summary>
    public void Flush(Action<Frame, long> onFrame)
    {
        if (parser != null)
        {
            byte* output = null;
            var outputSize = 0;
            FF.av_parser_parse2(parser, context, &output, &outputSize, null, 0, FF.AV_NOPTS_VALUE, FF.AV_NOPTS_VALUE, -1);
            if (outputSize > 0)
                SendFrame(output, outputSize, parser->pts, onFrame);
        }
        FF.avcodec_send_packet(context, null);
        Receive(onFrame);
    }

    private void SendFrame(byte* data, int size, long pts, Action<Frame, long> onFrame)
    {
        AVPacket* raw = packet;
        FF.av_packet_unref(raw);
        if (FF.av_new_packet(raw, size) < 0)
            return;
        Buffer.MemoryCopy(data, raw->data, size, size);
        raw->pts = raw->dts = pts;
        // 损坏的帧由解码器报错：跳过这一帧，后面的帧照常解码。
        var result = FF.avcodec_send_packet(context, raw);
        FF.av_packet_unref(raw);
        if (result < 0 && result != FF.AVERROR_EAGAIN)
            return;
        Receive(onFrame);
    }

    private void Receive(Action<Frame, long> onFrame)
    {
        AVFrame* raw = frame;
        while (true)
        {
            var result = FF.avcodec_receive_frame(context, raw);
            if (result < 0)
                return;
            try
            {
                if (raw->nb_samples <= 0 || raw->sample_rate <= 0)
                    continue;
                var pts = raw->pts != FF.AV_NOPTS_VALUE ? raw->pts : expectedPts;
                if (pts == FF.AV_NOPTS_VALUE)
                    continue;
                expectedPts = pts + (long)Math.Round(raw->nb_samples * 90_000.0 / raw->sample_rate);
                onFrame(frame, pts);
            }
            finally
            {
                frame.Unref();
            }
        }
    }

    public void Dispose()
    {
        if (parser != null)
        {
            FF.av_parser_close(parser);
            parser = null;
        }
        if (context != null)
        {
            var raw = context;
            FF.avcodec_free_context(&raw);
            context = null;
        }
        packet.Dispose();
        frame.Dispose();
    }
}

/// <summary>
/// 按帧头中的帧长切分 MPEG 音频（Layer I/II/III）与 ADTS AAC：缓存跨 PES 的数据，从同步字开始逐帧取出。
/// 帧的时间戳取它的首字节所在 PES 的 PTS（每个 PES 只给第一帧），其余帧为 NOPTS，由解码器按样本数推算。
/// 帧头无效（数据损坏）时向后搜索同步字。
/// </summary>
internal sealed class HeaderFrameSplitter
{
    private static readonly int[][] MpegBitrates =
    [
        [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448], // MPEG-1 Layer I
        [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384],    // MPEG-1 Layer II
        [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320],     // MPEG-1 Layer III
        [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256],    // MPEG-2/2.5 Layer I
        [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160]          // MPEG-2/2.5 Layer II/III
    ];

    private readonly bool adts;
    private byte[] buffer = new byte[8192];
    private int count;
    // 缓存中各 PES 起点的位置与 PTS；取出帧后前移。
    private readonly List<(int Offset, long Pts)> timestamps = [];

    private HeaderFrameSplitter(bool adts) => this.adts = adts;

    public static HeaderFrameSplitter? TryCreate(AVCodecID id) => id switch
    {
        AVCodecID.AV_CODEC_ID_MP2 or AVCodecID.AV_CODEC_ID_MP3 or AVCodecID.AV_CODEC_ID_MP1 => new HeaderFrameSplitter(false),
        AVCodecID.AV_CODEC_ID_AAC => new HeaderFrameSplitter(true),
        _ => null
    };

    public void Append(ReadOnlySpan<byte> payload, long pts)
    {
        if (count + payload.Length > buffer.Length)
            Array.Resize(ref buffer, Math.Max(buffer.Length * 2, count + payload.Length));
        if (pts != FF.AV_NOPTS_VALUE)
            timestamps.Add((count, pts));
        payload.CopyTo(buffer.AsSpan(count));
        count += payload.Length;
    }

    public bool TryTake(out byte[] frame, out long pts)
    {
        frame = [];
        pts = FF.AV_NOPTS_VALUE;
        var start = 0;
        while (start + 7 <= count)
        {
            var length = FrameLength(buffer.AsSpan(start, count - start));
            if (length <= 0)
            {
                start++;
                continue;
            }
            if (start + length > count)
                break;
            frame = buffer.AsSpan(start, length).ToArray();
            // 首字节所在 PES 的时间戳；用过的时间戳去掉，同一 PES 中后面的帧不再使用。
            for (var i = timestamps.Count - 1; i >= 0; i--)
            {
                if (timestamps[i].Offset > start)
                    continue;
                pts = timestamps[i].Pts;
                timestamps.RemoveRange(0, i + 1);
                break;
            }
            Consume(start + length);
            return true;
        }
        Consume(start);
        return false;
    }

    private void Consume(int bytes)
    {
        if (bytes <= 0)
            return;
        buffer.AsSpan(bytes, count - bytes).CopyTo(buffer);
        count -= bytes;
        for (var i = 0; i < timestamps.Count; i++)
            timestamps[i] = (timestamps[i].Offset - bytes, timestamps[i].Pts);
        // 落到缓存开头之前的时间戳只保留最后一个：它的 PES 可能还有帧没有开始。
        var stale = timestamps.FindLastIndex(entry => entry.Offset < 0);
        if (stale > 0)
            timestamps.RemoveRange(0, stale);
    }

    private int FrameLength(ReadOnlySpan<byte> data) => adts ? AdtsLength(data) : MpegLength(data);

    private static int AdtsLength(ReadOnlySpan<byte> data)
    {
        if (data[0] != 0xFF || (data[1] & 0xF6) != 0xF0)
            return 0;
        var length = ((data[3] & 0x03) << 11) | (data[4] << 3) | (data[5] >> 5);
        return length >= 7 ? length : 0;
    }

    private static int MpegLength(ReadOnlySpan<byte> data)
    {
        if (data[0] != 0xFF || (data[1] & 0xE0) != 0xE0)
            return 0;
        var version = (data[1] >> 3) & 0x03; // 3 = MPEG-1，2 = MPEG-2，0 = MPEG-2.5
        var layer = (data[1] >> 1) & 0x03;   // 3 = Layer I，2 = Layer II，1 = Layer III
        var bitrateIndex = data[2] >> 4;
        var rateIndex = (data[2] >> 2) & 0x03;
        var padding = (data[2] >> 1) & 0x01;
        if (version == 1 || layer == 0 || bitrateIndex is 0 or 15 || rateIndex == 3)
            return 0;
        int[] rates = version switch { 3 => [44100, 48000, 32000], 2 => [22050, 24000, 16000], _ => [11025, 12000, 8000] };
        var rate = rates[rateIndex];
        var table = version == 3 ? 3 - layer : layer == 3 ? 3 : 4;
        var bitrate = MpegBitrates[table][bitrateIndex] * 1000;
        return layer switch
        {
            3 => (12 * bitrate / rate + padding) * 4,
            2 => 144 * bitrate / rate + padding,
            _ => (version == 3 ? 144 : 72) * bitrate / rate + padding
        };
    }
}
