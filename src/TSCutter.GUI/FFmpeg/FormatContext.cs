using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.FFmpeg;

[Flags]
public enum AVSEEK_FLAG { Backward = 1, Byte = 2, Any = 4, Frame = 8 }

public unsafe class FormatContext : SafeHandle
{
    protected FormatContext(AVFormatContext* context, bool isOwner) : base(IntPtr.Zero, isOwner)
        => SetHandle((IntPtr)context);
    public override bool IsInvalid => handle == IntPtr.Zero;
    public static implicit operator AVFormatContext*(FormatContext? value)
        => value is null ? null : (AVFormatContext*)value.handle;
    private Packet? reusablePacket;
    private bool readingPackets;
    private AVFormatContext* Raw => (AVFormatContext*)handle;
    public IReadOnlyList<MediaStream> Streams { get; private set; } = [];
    public IReadOnlyList<MediaProgram> Programs { get; private set; } = [];
    public long Duration => Raw->duration;
    public long BitRate => Raw->bit_rate;
    public AVIOContext* Pb => Raw->pb;
    public InputFormatInfo? InputFormat => Raw->iformat == null ? null
        : new InputFormatInfo(Marshal.PtrToStringUTF8((IntPtr)Raw->iformat->name) ?? "");

    public static FormatContext OpenInputUrl(string path)
        => Utils.InterruptibleInputFormatContext.Open(path, static () => false);

    public void LoadStreamInfo()
    {
        // 纯二进制剪辑可能从两次节目表之间开始。仅在 TS 尚未找到节目表时
        // 扩大有界探测范围以读取节目清单。AV3A 内容探测修复后仍需要这一步：
        // 某些视频（如 AVS3）在节目表到达前仍不能正确识别。
        // 已有节目表的普通文件保留原探测上限，不额外扫描整个文件。
        if (Raw->nb_programs == 0 && InputFormat?.Name == "mpegts")
            Raw->probesize = Math.Max(Raw->probesize, Models.TsStreamAnalyzeOptions.StandardProbeBytes);
        FFmpegException.Check(FF.avformat_find_stream_info(Raw, null));
        var streams = new MediaStream[Raw->nb_streams];
        for (var i = 0; i < streams.Length; i++) streams[i] = new MediaStream(Raw->streams[i]);
        Streams = streams;
        var programs = new MediaProgram[Raw->nb_programs];
        for (var i = 0; i < programs.Length; i++) programs[i] = new MediaProgram(Raw->programs[i]);
        Programs = programs;
    }
    public MediaStream GetVideoStream() => Streams.First(s => s.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_VIDEO);
    public MediaStream GetAudioStream() => Streams.First(s => s.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_AUDIO);
    public void SeekFrame(long timestamp, int streamIndex, AVSEEK_FLAG flags = AVSEEK_FLAG.Backward)
        => FFmpegException.Check(FF.av_seek_frame(Raw, streamIndex, timestamp, (int)flags));

    // 输入上下文始终复用一个原生包；连续搜索每次返回关键帧后，
    // 不必在下一次枚举重新分配 AVPacket，提前返回也会解除载荷引用。
    public IEnumerable<Packet> ReadPackets()
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        if (readingPackets) throw new InvalidOperationException("Concurrent packet enumeration is not supported.");
        readingPackets = true;
        try
        {
            var packet = reusablePacket ??= new Packet();
            while (ReadPacket(packet))
            {
                try { yield return packet; }
                finally { packet.Unref(); }
            }
        }
        finally { readingPackets = false; }
    }
    private bool ReadPacket(Packet packet)
    {
        var result = FF.av_read_frame(Raw, packet);
        if (result == FF.AVERROR_EOF) return false;
        FFmpegException.Check(result);
        return true;
    }
    protected override bool ReleaseHandle()
    {
        reusablePacket?.Dispose();
        var raw = Raw;
        FF.avformat_close_input(&raw);
        handle = IntPtr.Zero;
        return true;
    }
}

public sealed record InputFormatInfo(string Name);

// 以下结构只借用输入上下文中的指针，不能在 FormatContext 释放后继续访问。
public readonly unsafe struct MediaStream
{
    private readonly AVStream* raw;
    internal MediaStream(AVStream* raw) { this.raw = raw; Codecpar = raw->codecpar == null ? null : new CodecParameters(raw->codecpar); }
    public CodecParameters? Codecpar { get; }
    public int Index => raw == null ? 0 : raw->index;
    public int Id => raw == null ? -1 : raw->id;
    public long Duration => raw == null ? 0 : raw->duration;
    public long StartTime => raw == null ? FF.AV_NOPTS_VALUE : raw->start_time;
    public AVRational TimeBase => raw == null ? default : raw->time_base;
    public AVRational AvgFrameRate => raw == null ? default : raw->avg_frame_rate;
    public AVRational SampleAspectRatio => raw == null ? default : raw->sample_aspect_ratio;
    public NativeMetadata Metadata => new(raw == null ? null : raw->metadata);
    public static implicit operator AVStream*(MediaStream stream) => stream.raw;
}

public readonly unsafe struct MediaProgram(AVProgram* raw)
{
    public int PmtPid => raw->pmt_pid;
    public long StartTime => raw->start_time;
    public long EndTime => raw->end_time;
    public NativeMetadata Metadata => new(raw->metadata);
}

public readonly unsafe struct NativeMetadata(AVDictionary* raw)
{
    public bool TryGetValue(string key, out string value)
    {
        var entry = FF.av_dict_get(raw, key, null, 0);
        value = entry == null ? "" : Marshal.PtrToStringUTF8((IntPtr)entry->value) ?? "";
        return entry != null;
    }
    public Enumerator GetEnumerator() => new(raw);
    public unsafe struct Enumerator(AVDictionary* raw)
    {
        private AVDictionaryEntry* entry;
        public bool MoveNext() { entry = FF.av_dict_iterate(raw, entry); return entry != null; }
        public KeyValuePair<string, string> Current => new(
            Marshal.PtrToStringUTF8((IntPtr)entry->key) ?? "",
            Marshal.PtrToStringUTF8((IntPtr)entry->value) ?? "");
    }
}
