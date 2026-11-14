using System;
using System.IO;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Discs;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Services.Media;

/// <summary>
/// 静音检测与多轨合并分析共用的抽样：在全片均匀取若干段，每段只读很短的一段，读取量与片长无关。
/// TS 文件按字节位置定位：拼接的文件中可能有很大的时间戳跳变，按时长定位会落在没有数据的位置；
/// 定位后以读到的第一个音频或视频包的时间为参照确定这一段的范围。其他封装（不支持按字节定位）与光盘按时间定位。
/// </summary>
internal sealed class AudioSampleWindows
{
    public const int Count = 12;
    public const double WindowSeconds = 1.5;
    // 复用时音频与同一时刻的视频在文件中的位置可能错开约一秒，每段多读一些。
    public const double MarginSeconds = 1.5;
    // 按字节定位后，从读到的第一个包往后留这么多，让各音轨都开始。
    private const double ByteSeekLeadSeconds = 0.5;
    // 按字节定位后一直没有读到带时间戳的包时，读这么多包就结束这一段（不读到文件尾）。
    private const int MaxPacketsWithoutTime = 20_000;

    private readonly FormatContext input;
    private readonly MediaStream clock;
    private readonly long bytes;
    private readonly double start;
    private readonly double duration;
    private int packetsWithoutTime;

    /// <param name="clock">按时间定位时的参照流（通常是视频）。</param>
    /// <param name="bytes">按字节定位时的文件大小；0 表示按时间定位。</param>
    public AudioSampleWindows(FormatContext input, MediaStream clock, long bytes)
    {
        this.input = input;
        this.clock = clock;
        this.bytes = bytes;
        (start, duration) = Range(input, clock);
    }

    /// <summary>TS 文件按字节定位时的文件大小；其他情况返回 0（按时间定位）。</summary>
    public static long ByteSeekSize(FormatContext input, string path, DiscInput? disc) =>
        disc is null && input.InputFormat?.Name == "mpegts" && File.Exists(path) ? new FileInfo(path).Length : 0;

    /// <summary>当前段的起止时间（秒）；按字节定位后、读到第一个包之前为 NaN。</summary>
    public double From { get; private set; } = double.NaN;
    public double To { get; private set; } = double.NaN;
    public bool HasRange => !double.IsNaN(From);

    /// <summary>定位到第 <paramref name="window"/> 段。</summary>
    public void Seek(int window)
    {
        packetsWithoutTime = 0;
        if (bytes > 0)
        {
            input.SeekFrame((long)(bytes * (window + 0.5) / Count), -1, AVSEEK_FLAG.Byte);
            From = To = double.NaN;
            return;
        }
        From = start + duration * (window + 0.5) / Count - WindowSeconds / 2;
        To = From + WindowSeconds;
        input.SeekFrame(Ticks(Math.Max(start, From - MarginSeconds), clock.TimeBase), clock.Index);
    }

    /// <summary>
    /// 读到一个包：<paramref name="seconds"/> 为它的时间（没有时间戳或不是参照的流时为 null）。
    /// 按字节定位时第一个带时间的包确定本段范围，此时返回 true。
    /// </summary>
    public bool Observe(double? seconds)
    {
        if (HasRange)
            return false;
        if (seconds is not { } time)
        {
            packetsWithoutTime++;
            return false;
        }
        From = time + ByteSeekLeadSeconds;
        To = From + WindowSeconds;
        return true;
    }

    /// <summary>这一段已经读完：读过了终点（含余量），或按字节定位后一直没有读到带时间的包。</summary>
    public bool IsDone(double? seconds) => HasRange
        ? seconds is { } time && time > To + MarginSeconds
        : packetsWithoutTime >= MaxPacketsWithoutTime;

    /// <summary>可抽样的时间范围（秒）：流的起点与时长，缺失时取文件的。</summary>
    private static unsafe (double Start, double Duration) Range(FormatContext input, MediaStream clock)
    {
        AVFormatContext* raw = input;
        var begin = clock.StartTime != FF.AV_NOPTS_VALUE ? Seconds(clock.StartTime, clock.TimeBase)
            : raw->start_time != FF.AV_NOPTS_VALUE ? raw->start_time / 1_000_000.0 : 0;
        var length = clock.Duration > 0 ? Seconds(clock.Duration, clock.TimeBase)
            : raw->duration > 0 ? raw->duration / 1_000_000.0 : WindowSeconds * Count;
        return (begin, Math.Max(length, WindowSeconds));
    }

    public static double Seconds(long value, AVRational timeBase) => value * (double)timeBase.num / timeBase.den;

    private static long Ticks(double seconds, AVRational timeBase) =>
        (long)Math.Floor(seconds * timeBase.den / timeBase.num);
}
