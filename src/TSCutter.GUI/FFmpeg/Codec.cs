using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.FFmpeg;

public readonly unsafe struct Codec
{
    private readonly AVCodec* raw;
    internal Codec(AVCodec* raw) => this.raw = raw;
    public string Name => Marshal.PtrToStringUTF8((IntPtr)raw->name) ?? "";
    public AVCodecID Id => raw->id;
    public static implicit operator AVCodec*(Codec codec) => codec.raw;
    public static Codec FindDecoderById(AVCodecID id)
    {
        var raw = FF.avcodec_find_decoder(id);
        if (raw == null) throw new InvalidOperationException($"Decoder unavailable: {id}");
        return new Codec(raw);
    }
    public static IReadOnlyList<Codec> FindDecoders(AVCodecID id)
    {
        var result = new List<Codec>();
        void* cursor = null;
        AVCodec* codec;
        while ((codec = FF.av_codec_iterate(&cursor)) != null)
            if (codec->id == id && FF.av_codec_is_decoder(codec) != 0) result.Add(new Codec(codec));
        return result;
    }
}

// 编码参数由输入流持有，此封装不释放或复制原生参数。
public sealed unsafe class CodecParameters
{
    private readonly AVCodecParameters* raw;
    internal CodecParameters(AVCodecParameters* raw) => this.raw = raw;
    public AVCodecID CodecId => raw->codec_id;
    public string CodecName => CodecNames.Get(CodecId);
    public AVMediaType CodecType => raw->codec_type;
    public long BitRate => raw->bit_rate;
    public int Width => raw->width;
    public int Height => raw->height;
    public int Format => raw->format;
    public int SampleRate => raw->sample_rate;
    public int BitsPerRawSample => raw->bits_per_raw_sample;
    public AVRational SampleAspectRatio => raw->sample_aspect_ratio;
    public AVFieldOrder FieldOrder => raw->field_order;
    public AVColorRange ColorRange => raw->color_range;
    public AVColorPrimaries ColorPrimaries => raw->color_primaries;
    public AVColorTransferCharacteristic ColorTrc => raw->color_trc;
    public AVColorSpace ColorSpace => raw->color_space;
    public AVChromaLocation ChromaLocation => raw->chroma_location;
    public AVChannelLayout ChLayout => raw->ch_layout;
    public uint CodecTag => raw->codec_tag;
    public static implicit operator AVCodecParameters*(CodecParameters parameters) => parameters.raw;
}

public static class CodecNames
{
    // 媒体信息显示编码名称，不把绑定枚举的原生前缀暴露给用户。
    public static string Get(AVCodecID id)
    {
        var value = id.ToString();
        const string prefix = "AV_CODEC_ID_";
        return value.StartsWith(prefix, StringComparison.Ordinal) ? value[prefix.Length..] : value;
    }
}

public sealed unsafe class CodecContext : SafeHandle
{
    private readonly Codec codec;
    private FramePacketPositionSource packetPositionSource;
    public CodecContext(Codec codec) : base(IntPtr.Zero, true)
    {
        this.codec = codec;
        SetHandle((IntPtr)FF.avcodec_alloc_context3(codec));
        if (IsInvalid) throw new OutOfMemoryException();
    }
    public override bool IsInvalid => handle == IntPtr.Zero;
    private AVCodecContext* Raw => (AVCodecContext*)handle;
    public static implicit operator AVCodecContext*(CodecContext? value)
        => value is null ? null : (AVCodecContext*)value.handle;
    public AVRational PacketTimeBase { set => Raw->pkt_timebase = value; }
    public AVDiscard SkipFrame { set => Raw->skip_frame = value; }
    public void FillParameters(CodecParameters parameters)
        => FFmpegException.Check(FF.avcodec_parameters_to_context(Raw, parameters));
    public void Open() => Open("export_packet_pos");

    // 内部重载用于验证私有选项缺失的实际原生错误路径，公开调用始终使用正式选项名。
    internal void Open(string packetPositionOption)
    {
        // AVS2/AVS3 异步解码器自行输出来源包位置；
        // 不能将最近输入包的位置赋给延迟输出帧。
        if (codec.Name is "libdavs2" or "libuavs3d")
        {
            Raw->flags &= ~FF.AV_CODEC_FLAG_COPY_OPAQUE;
            var result = FF.av_opt_set_int(Raw->priv_data, packetPositionOption, 1, 0);
            // 普通运行时可能没有私有扩展，仍允许预览；其他错误不能静默忽略。
            if (result == FF.AVERROR_OPTION_NOT_FOUND)
                packetPositionSource = FramePacketPositionSource.Unknown;
            else
            {
                FFmpegException.Check(result);
                packetPositionSource = FramePacketPositionSource.DecoderMetadata;
            }
        }
        else
        {
            Raw->flags |= FF.AV_CODEC_FLAG_COPY_OPAQUE;
            packetPositionSource = FramePacketPositionSource.Opaque;
        }
        FFmpegException.Check(FF.avcodec_open2(Raw, codec, null));
    }

    public DecodedFrames DecodePacket(Packet? packet, Frame destination, bool preserveRemainingOutput = false)
    {
        if (packet is not null && packetPositionSource == FramePacketPositionSource.Opaque)
            packet.SetPositionToken();
        var result = FF.avcodec_send_packet(Raw, packet);
        if (result != FF.AVERROR_EOF) FFmpegException.Check(result);
        return new DecodedFrames(this, destination, preserveRemainingOutput);
    }

    // 仅接收已有输出，不发送新包，供连续画面搜索恢复上次未读完的帧。
    public DecodedFrames ReceiveFrames(Frame destination)
        => new(this, destination, preserveRemainingOutput: true);

    // 结构体枚举器避免逐包托管分配；普通预览提前返回时清空剩余输出，
    // 连续搜索则由下一次 ReceiveFrames 继续读取，防止发送新包时遇到 EAGAIN。
    public readonly struct DecodedFrames(CodecContext owner, Frame destination, bool preserveRemainingOutput)
    {
        public Enumerator GetEnumerator() => new(owner, destination, preserveRemainingOutput);
        public struct Enumerator(CodecContext owner, Frame destination, bool preserveRemainingOutput) : IDisposable
        {
            private bool done;
            public Frame Current => destination;
            public bool MoveNext()
            {
                if (done) return false;
                destination.Unref();
                var result = FF.avcodec_receive_frame(owner, destination);
                if (result == FF.AVERROR_EAGAIN || result == FF.AVERROR_EOF) { done = true; return false; }
                if (result < 0) done = true;
                FFmpegException.Check(result);
                destination.PacketPositionSource = owner.packetPositionSource;
                return true;
            }
            public void Dispose()
            {
                try
                {
                    // 提前结束枚举时只释放剩余输出；不在 Dispose 中抛出第二个异常，
                    // 以免覆盖调用者的取消或原始解码错误。
                    // 连续画面搜索保留原生解码器中的剩余输出，下次先接收再读新包，
                    // 不建立托管帧队列，也不丢弃文件尾的其他关键帧。
                    while (!preserveRemainingOutput && !done)
                    {
                        destination.Unref();
                        if (FF.avcodec_receive_frame(owner, destination) < 0) done = true;
                    }
                }
                finally { destination.Unref(); }
            }
        }
    }
    protected override bool ReleaseHandle()
    {
        var raw = Raw;
        FF.avcodec_free_context(&raw);
        handle = IntPtr.Zero;
        return true;
    }
}
