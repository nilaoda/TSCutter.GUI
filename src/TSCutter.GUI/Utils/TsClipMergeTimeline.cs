using System;

namespace TSCutter.GUI.Utils;

// 只统计参考 PID 的时间戳，不保存包内容，也不为每个包分配对象。
internal struct TsClipMergeTimeline
{
    private int pid;
    private long lastRawPts;
    private long ptsWrapOffset;
    private long lastRawDts;
    private long dtsWrapOffset;
    private long maximumPts;
    private long previousMaximumPts;
    private long maximumDts;
    private long previousMaximumDts;

    public bool HasPts { get; private set; }
    public bool HasDts { get; private set; }
    public bool HasSuccessor { get; private set; }
    public long FirstPts { get; private set; }
    public long MinimumPts { get; private set; }
    public long MinimumDts { get; private set; }
    public readonly int Pid => pid;

    public void Reset(int referencePid = -1)
    {
        this = default;
        pid = referencePid;
        lastRawPts = lastRawDts = long.MinValue;
        previousMaximumPts = previousMaximumDts = long.MinValue;
    }

    public void Observe(ReadOnlySpan<byte> packet)
    {
        if ((packet[1] & 0x40) == 0) return;
        var packetPid = ((packet[1] & 31) << 8) | packet[2];
        if (pid >= 0 && packetPid != pid) return;
        // 损坏、加扰包照常由合并流程保留，不用它们决定接缝时间。
        if ((packet[1] & 0x80) != 0 || (packet[3] & 0xC0) != 0 ||
            !TsTimestampFieldCodec.TryLocatePesTimestamps(packet, out _, out _))
            return;
        var payloadOffset = 4 + ((packet[3] & 0x20) != 0 ? packet[4] + 1 : 0);
        ObserveHeader(packetPid, packet[payloadOffset..]);
    }

    public void ObserveHeader(int packetPid, ReadOnlySpan<byte> header)
    {
        if (pid >= 0 && packetPid != pid) return;
        if (header.Length < 14 || header[0] != 0 || header[1] != 0 || header[2] != 1)
            return;
        if (pid < 0)
        {
            var streamId = header[3];
            if (streamId is < 0xE0 or > 0xEF)
                return;
        }
        var field = header.Slice(9, 5);
        if ((field[0] & 1) == 0 || (field[2] & 1) == 0 || (field[4] & 1) == 0)
            return;
        pid = packetPid;
        var pts = TsTimestampFieldCodec.UnwrapTimestamp(
            TsTimestampFieldCodec.ReadPesTimestamp(field), ref lastRawPts, ref ptsWrapOffset);
        if (!HasPts)
        {
            FirstPts = MinimumPts = maximumPts = pts;
            HasPts = true;
        }
        else
        {
            MinimumPts = Math.Min(MinimumPts, pts);
            HasSuccessor |= pts > FirstPts;
            ObserveMaximum(pts, ref maximumPts, ref previousMaximumPts);
        }
        if ((header[7] >> 6) != 3 || header.Length < 19) return;
        field = header.Slice(14, 5);
        if ((field[0] & 1) == 0 || (field[2] & 1) == 0 || (field[4] & 1) == 0)
            return;
        // DTS 和 PTS 共用同一个回绕周期起点，不能分别从零展开。
        if (!HasDts)
        {
            lastRawDts = lastRawPts;
            dtsWrapOffset = ptsWrapOffset;
        }
        var dts = TsTimestampFieldCodec.UnwrapTimestamp(
            TsTimestampFieldCodec.ReadPesTimestamp(field), ref lastRawDts, ref dtsWrapOffset);
        if (!HasDts)
        {
            MinimumDts = maximumDts = dts;
            HasDts = true;
        }
        else
        {
            MinimumDts = Math.Min(MinimumDts, dts);
            ObserveMaximum(dts, ref maximumDts, ref previousMaximumDts);
        }
    }

    // 终点已包含最后一帧；以最后两个播放时间估计它的时长，不能把下一帧放在同一 PTS。
    public readonly long EndPts => maximumPts + Interval(maximumPts, previousMaximumPts);
    public readonly long EndDts => maximumDts + Interval(maximumDts, previousMaximumDts);

    private static long Interval(long maximum, long previous) =>
        previous == long.MinValue ? 1 : Math.Max(1, maximum - previous);

    private static void ObserveMaximum(long value, ref long maximum, ref long previous)
    {
        if (value > maximum)
        {
            previous = maximum;
            maximum = value;
        }
        else if (value < maximum && value > previous)
            previous = value;
    }
}
