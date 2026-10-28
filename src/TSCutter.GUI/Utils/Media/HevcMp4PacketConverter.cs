using System;
using System.Buffers.Binary;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Utils.Media;

/// <summary>一次写出长度前缀 HEVC 包，保留 hvc1/dvh1 转换时可能被移除的帧内参数。</summary>
internal sealed unsafe class HevcMp4PacketConverter : IDisposable
{
    private readonly Packet converted = new();

    public static HevcMp4PacketConverter? Create(AVCodecParameters* parameters)
    {
        var extra = new ReadOnlySpan<byte>(parameters->extradata, parameters->extradata_size);
        if (!extra.StartsWith((ReadOnlySpan<byte>)[0, 0, 1]) && !extra.StartsWith((ReadOnlySpan<byte>)[0, 0, 0, 1]))
            return null;
        var configuration = CreateConfiguration(parameters);
        var replacement = (byte*)FF.av_mallocz(checked((ulong)configuration.Length + ffmpeg.AV_INPUT_BUFFER_PADDING_SIZE));
        if (replacement == null)
            throw new OutOfMemoryException();
        configuration.CopyTo(new Span<byte>(replacement, configuration.Length));
        FF.av_free(parameters->extradata);
        parameters->extradata = replacement;
        parameters->extradata_size = configuration.Length;
        return new HevcMp4PacketConverter();
    }

    public AVPacket* Convert(AVPacket* source)
    {
        var data = new ReadOnlySpan<byte>(source->data, source->size);
        var remaining = data;
        var size = 0;
        while (TakeNal(ref remaining, out var nal))
            size = checked(size + 4 + nal.Length);
        if (size == 0)
            throw FFmpegException.FromErrorCode(FF.AVERROR_INVALIDDATA);
        converted.Unref();
        AVPacket* output = converted;
        FFmpegException.Check(FF.av_new_packet(output, size));
        FFmpegException.Check(FF.av_packet_copy_props(output, source));
        var destination = new Span<byte>(output->data, size);
        remaining = data;
        while (TakeNal(ref remaining, out var nal))
        {
            BinaryPrimitives.WriteInt32BigEndian(destination, nal.Length);
            nal.CopyTo(destination[4..]);
            destination = destination[(4 + nal.Length)..];
        }
        return output;
    }

    private static bool TakeNal(ref ReadOnlySpan<byte> data, out ReadOnlySpan<byte> nal)
    {
        while (true)
        {
            var start = data.IndexOf((ReadOnlySpan<byte>)[0, 0, 1]);
            if (start < 0)
            {
                nal = default;
                return false;
            }
            data = data[(start + 3)..];
            var next = data.IndexOf((ReadOnlySpan<byte>)[0, 0, 1]);
            // 四字节起始码的第一个零也属于起始码，不写入前一 NAL；与 FFmpeg 的边界识别一致。
            var end = next > 0 && data[next - 1] == 0 ? next - 1 : next;
            nal = next < 0 ? data : data[..end];
            data = next < 0 ? default : data[next..];
            if (!nal.IsEmpty)
                return true;
        }
    }

    private static byte[] CreateConfiguration(AVCodecParameters* parameters)
    {
        AVFormatContext* muxer = null;
        byte* buffer = null;
        try
        {
            FFmpegException.Check(FF.avformat_alloc_output_context2(&muxer, null, "mp4", null!));
            if (muxer == null)
                throw new OutOfMemoryException();
            muxer->strict_std_compliance = -1;
            muxer->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
            var stream = FF.avformat_new_stream(muxer, null);
            if (stream == null)
                throw new OutOfMemoryException();
            FFmpegException.Check(FF.avcodec_parameters_copy(stream->codecpar, parameters));
            stream->codecpar->codec_tag = RemuxCodecTag.Hev1;
            stream->time_base = new AVRational { num = 1, den = 1000 };
            FFmpegException.Check(FF.avio_open_dyn_buf(&muxer->pb));
            FFmpegException.Check(FF.avformat_write_header(muxer, null));
            // 仅在内存中写一个 AUD 占位样本，让 FFmpeg 生成完整 hvcC；不读取或解码源视频。
            using var sample = new Packet();
            AVPacket* packet = sample;
            ReadOnlySpan<byte> aud = [0, 0, 0, 1, 0x46, 1, 0x50];
            FFmpegException.Check(FF.av_new_packet(packet, aud.Length));
            aud.CopyTo(new Span<byte>(packet->data, aud.Length));
            packet->pts = packet->dts = 0;
            packet->duration = 1;
            packet->flags = 1;
            packet->stream_index = 0;
            FFmpegException.Check(FF.av_interleaved_write_frame(muxer, packet));
            FFmpegException.Check(FF.av_write_trailer(muxer));
            var size = FF.avio_close_dyn_buf(muxer->pb, &buffer);
            muxer->pb = null;
            FFmpegException.Check(size);
            var data = new ReadOnlySpan<byte>(buffer, size);
            foreach (var type in new[] { "moov", "trak", "mdia", "minf", "stbl", "stsd" })
                data = FindBox(data, type);
            if (data.Length < 8)
                throw FFmpegException.FromErrorCode(FF.AVERROR_INVALIDDATA);
            var entry = FindBox(data[8..], "hev1");
            // VisualSampleEntry 的固定字段共 78 字节，后面才是 hvcC 等子 box。
            if (entry.Length < 78)
                throw FFmpegException.FromErrorCode(FF.AVERROR_INVALIDDATA);
            return FindBox(entry[78..], "hvcC").ToArray();
        }
        finally
        {
            if (muxer != null)
            {
                if (muxer->pb != null)
                {
                    byte* remaining = null;
                    FF.avio_close_dyn_buf(muxer->pb, &remaining);
                    muxer->pb = null;
                    FF.av_free(remaining);
                }
                FF.avformat_free_context(muxer);
            }
            FF.av_free(buffer);
        }
    }

    private static ReadOnlySpan<byte> FindBox(ReadOnlySpan<byte> data, string type)
    {
        while (data.Length >= 8)
        {
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(data);
            var header = 8;
            if (size == 1)
            {
                if (data.Length < 16)
                    break;
                size = BinaryPrimitives.ReadUInt64BigEndian(data[8..]);
                header = 16;
            }
            else if (size == 0)
                size = (ulong)data.Length;
            if (size < (ulong)header || size > (ulong)data.Length)
                break;
            if (data[4] == type[0] && data[5] == type[1] && data[6] == type[2] && data[7] == type[3])
                return data.Slice(header, (int)size - header);
            data = data[(int)size..];
        }
        throw FFmpegException.FromErrorCode(FF.AVERROR_INVALIDDATA);
    }

    public void Dispose() => converted.Dispose();
}
