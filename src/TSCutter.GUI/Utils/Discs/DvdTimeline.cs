using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Discs;
using TSCutter.GUI.Utils.Editing;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Utils.Discs;

/// <summary>
/// DVD 中的一帧：所在位置附近某个关键帧压缩包的签名，加上该帧相对这个关键帧的 PTS 偏移。
/// dvdvideo 每次定位都以目标时间重建时间戳，同一帧在不同次读取中的 PTS 不同；关键帧签名与帧间距不随定位变化。
/// </summary>
internal readonly record struct DvdFrameAnchor(PacketEndSignature Key, long Offset)
{
    public bool IsValid => Key.IsValid;

    /// <summary>文本形式为关键帧签名，偏移不为 0 时在后面加“|偏移”；只有签名的文本即关键帧本身。</summary>
    public string? ToTextOrNull() => !IsValid ? null
        : Offset == 0 ? Key.ToText() : $"{Key.ToText()}|{Offset.ToString(CultureInfo.InvariantCulture)}";

    public static bool TryParse(string? text, out DvdFrameAnchor anchor)
    {
        anchor = default;
        if (string.IsNullOrEmpty(text))
            return false;
        var separator = text.IndexOf('|');
        var offset = 0L;
        if (separator >= 0 && !long.TryParse(text.AsSpan(separator + 1), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out offset))
            return false;
        if (!PacketEndSignature.TryParse(separator >= 0 ? text[..separator] : text, out var key))
            return false;
        anchor = new DvdFrameAnchor(key, offset);
        return true;
    }
}

/// <summary>
/// DVD 导出的固定锚点：dvdvideo 按标题时间定位只到 VOBU，并以目标时间重建时间戳，有的光盘定位偏差可达数十秒。
/// 导出时所有定位都回到最早起点之前的同一锚点，每次读取的内容与时间戳完全相同，再按关键帧签名找到所选画面。
/// </summary>
internal static class DvdExportAnchor
{
    /// <summary>锚点在最早起点之前的秒数：覆盖片头回退查找衔接点的范围与光盘时间定位的偏差。</summary>
    public const double LookbackSeconds = 180;

    public static double For(double startSeconds) => Math.Max(0, startSeconds - LookbackSeconds);
}

/// <summary>
/// DVD 视频关键帧的签名与统一时间轴上的 PTS。同一标题的预览读取（主预览、导航条、播放、波形）共用一份，
/// 各处读到的同一画面时间戳相同；导出各用一份，不受预览中同时进行的读取影响。可在多个线程中使用。
/// </summary>
internal sealed class DvdKeyFrameIndex
{
    private const int MaximumEntries = 100_000;
    // 随标题对象的生命周期共享，关闭视频后与标题一起回收。
    private static readonly ConditionalWeakTable<DiscInput, DvdKeyFrameIndex> Shared = new();

    private readonly Dictionary<PacketEndSignature, (long Pts, long Generation)> keyFrames = [];
    private long generation;

    public static DvdKeyFrameIndex For(DiscInput disc) => Shared.GetValue(disc, _ => new DvdKeyFrameIndex());

    /// <summary>新一次读取的序号，在共用这份记录的所有读取中递增。</summary>
    public long NextGeneration() => Interlocked.Increment(ref generation);

    public bool TryGet(PacketEndSignature key, out (long Pts, long Generation) entry)
    {
        lock (keyFrames)
            return keyFrames.TryGetValue(key, out entry);
    }

    /// <summary>记录关键帧；最近一次读取的记录优先，后续定位按当前看到的画面校正，逐帧步进前后一致。</summary>
    public void Set(PacketEndSignature key, long pts, long readGeneration)
    {
        lock (keyFrames)
        {
            var exists = keyFrames.TryGetValue(key, out var current);
            if (exists && current.Generation > readGeneration)
                return;
            if (!exists && keyFrames.Count >= MaximumEntries)
                keyFrames.Clear();
            keyFrames[key] = (pts, readGeneration);
        }
    }
}

/// <summary>
/// 把 DVD 各次定位后的时间戳统一到同一时间轴：记录读到的视频关键帧签名及其校正后的 PTS；
/// 定位后先读到两个关键帧（都没有记录过时继续往后找，最多 8 个），按其中最近记录过的一个算出本次读取的校正量，
/// 之后的包都加上这个校正量。
/// 读取过程中按包的原样顺序交出，只在定位后的头几个包上有短暂缓冲。
/// </summary>
internal sealed unsafe class DvdTimeline(DvdKeyFrameIndex keyFrames) : IDisposable
{
    private const int LookaheadKeyFrames = 2;
    // 头两个关键帧都没有记录过时（定位落在已读范围之前），继续往后找已知关键帧的上限。
    private const int MaximumLookaheadKeyFrames = 8;
    private const int MaximumBufferedPackets = 4096;

    private readonly Queue<Packet> buffered = new();
    private readonly Stack<Packet> pool = new();
    private long generation = keyFrames.NextGeneration();
    private long correction;
    private bool calibrating;
    private int pendingError;

    /// <summary>当前读取的序号，每次定位取一个新值（与共用记录的其他读取不重复）；同一序号内的时间戳必定在同一时间轴上。</summary>
    public long Generation => generation;

    /// <summary>底层已读到标题结尾；dvdnav 的停止状态可能使后续定位失效。</summary>
    public bool ReachedEof { get; private set; }

    /// <summary>为 true 时，定位后头两个关键帧都没有记录过即报错：导出要求每次读取都能对上已建立的时间轴。</summary>
    public bool RequireKnownKeyFrames { get; set; }

    /// <summary>按签名查找关键帧在统一时间轴上的 PTS。</summary>
    public bool TryGetKeyFramePts(PacketEndSignature key, out long pts)
    {
        var found = keyFrames.TryGet(key, out var entry);
        pts = entry.Pts;
        return found;
    }

    /// <summary>定位后调用：丢弃尚未交出的缓冲包，下一次读取重新校正。</summary>
    public void Reset()
    {
        ReleaseBuffered();
        pendingError = 0;
        ReachedEof = false;
        calibrating = true;
        generation = keyFrames.NextGeneration();
    }

    public int Read(AVFormatContext* context, Packet packet)
    {
        if (calibrating)
            Calibrate(context);
        if (buffered.TryDequeue(out var next))
        {
            FF.av_packet_move_ref(packet, next);
            pool.Push(next);
            return 0;
        }
        if (pendingError < 0)
        {
            var error = pendingError;
            pendingError = 0;
            return error;
        }
        var result = FF.av_read_frame(context, packet);
        ReachedEof |= result == FF.AVERROR_EOF;
        if (result >= 0)
            Apply(context, packet);
        return result;
    }

    private void Calibrate(AVFormatContext* context)
    {
        calibrating = false;
        var seen = 0;
        (long Correction, long Generation)? best = null;
        while ((seen < LookaheadKeyFrames || best is null && seen < MaximumLookaheadKeyFrames) &&
               buffered.Count < MaximumBufferedPackets)
        {
            var packet = pool.TryPop(out var reused) ? reused : new Packet();
            var result = FF.av_read_frame(context, packet);
            if (result < 0)
            {
                pool.Push(packet);
                pendingError = result;
                ReachedEof |= result == FF.AVERROR_EOF;
                break;
            }
            buffered.Enqueue(packet);
            if (!IsVideoKeyFrame(context, packet) || packet.Pts == FF.AV_NOPTS_VALUE)
                continue;
            seen++;
            if (keyFrames.TryGet(packet.EndSignature, out var known) && (best is null || known.Generation > best.Value.Generation))
                best = (known.Pts - packet.Pts, known.Generation);
        }
        if (best is null && RequireKnownKeyFrames && buffered.Count > 0)
            throw new InvalidDataException("The DVD position could not be aligned with the indexed timeline.");
        correction = best?.Correction ?? 0;
        foreach (var packet in buffered)
            Apply(context, packet);
    }

    private void Apply(AVFormatContext* context, Packet packet)
    {
        AVPacket* raw = packet;
        if (raw->pts != FF.AV_NOPTS_VALUE)
            raw->pts += correction;
        if (raw->dts != FF.AV_NOPTS_VALUE)
            raw->dts += correction;
        if (raw->pts != FF.AV_NOPTS_VALUE && IsVideoKeyFrame(context, packet))
            keyFrames.Set(packet.EndSignature, raw->pts, generation);
    }

    private static bool IsVideoKeyFrame(AVFormatContext* context, Packet packet)
    {
        AVPacket* raw = packet;
        return (raw->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0 && raw->stream_index >= 0 && raw->stream_index < context->nb_streams &&
               context->streams[raw->stream_index]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO;
    }

    private void ReleaseBuffered()
    {
        while (buffered.TryDequeue(out var packet))
        {
            packet.Unref();
            pool.Push(packet);
        }
    }

    public void Dispose()
    {
        ReleaseBuffered();
        while (pool.TryPop(out var packet))
            packet.Dispose();
    }
}
