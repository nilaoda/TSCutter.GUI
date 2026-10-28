using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using TSCutter.GUI.Utils.TransportStream.Packets;

namespace TSCutter.GUI.Utils.Editing;

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
    private long minimumEndPts;

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
        previousMaximumPts = previousMaximumDts = minimumEndPts = long.MinValue;
    }

    public void Observe(ReadOnlySpan<byte> packet)
    {
        if ((packet[1] & 0x40) == 0)
            return;
        var packetPid = ((packet[1] & 31) << 8) | packet[2];
        if (pid >= 0 && packetPid != pid)
            return;
        // 损坏、加扰包照常由合并流程保留，不用它们决定接缝时间。
        if ((packet[1] & 0x80) != 0 || (packet[3] & 0xC0) != 0 ||
            !TsTimestampFieldCodec.TryLocatePesTimestamps(packet, out _, out _))
            return;
        var payloadOffset = 4 + ((packet[3] & 0x20) != 0 ? packet[4] + 1 : 0);
        ObserveHeader(packetPid, packet[payloadOffset..]);
    }

    public void ObserveHeader(int packetPid, ReadOnlySpan<byte> header)
    {
        if (pid >= 0 && packetPid != pid)
            return;
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
        if ((header[7] >> 6) != 3 || header.Length < 19)
            return;
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
    public readonly long EndPts => Math.Max(minimumEndPts, maximumPts + Interval(maximumPts, previousMaximumPts));

    // 一个 PES 可包含多个音频访问单元，接缝不能仅用相邻 PES 的 PTS 差估计末帧时长。
    public void IncludeAudioFrameEnd(long rawEnd)
    {
        if (HasPts)
            minimumEndPts = Math.Max(minimumEndPts,
                TsTimestampFieldCodec.UnwrapTimestamp(rawEnd, lastRawPts, ptsWrapOffset));
    }
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

// PCR 与解码时间的间距会随复用排布变化，必须独立检查每个输出 PCR PID 的接缝。
internal sealed class TsClipMergePcrTimeline
{
    private const long TimestampWrap = 1L << 33;
    private const long PcrWrap = TimestampWrap * 300;
    private readonly Dictionary<int, Clock> last = [];
    private readonly Dictionary<int, long> heads = [];
    private readonly Dictionary<int, (bool Writable, int Counter, bool Payload)> firstPackets = [];
    private readonly HashSet<int> switches = [];
    private readonly HashSet<int> marked = [];
    private readonly Dictionary<int, byte[]> boundaryPackets = [];

    public bool HasClocks => last.Count > 0;
    public bool HasAllHeads => heads.Count == last.Count;

    public void BeginHead()
    {
        heads.Clear();
        firstPackets.Clear();
        switches.Clear();
        marked.Clear();
        boundaryPackets.Clear();
    }

    public void ObserveHead(ReadOnlySpan<byte> packet)
    {
        var pid = ((packet[1] & 31) << 8) | packet[2];
        if (!last.ContainsKey(pid))
            return;
        var writable = (packet[1] & 0x80) == 0 && (packet[3] & 0x20) != 0 && packet[4] is >= 1 and <= 183;
        firstPackets.TryAdd(pid, (writable, packet[3] & 15, (packet[3] & 0x10) != 0));
        var hasPcr = TryRead(packet, out _, out var pcr, out var discontinuity);
        if (discontinuity)
            heads.TryAdd(pid, long.MinValue);
        else if (hasPcr)
            heads.TryAdd(pid, pcr);
    }

    public void ObserveOutput(ReadOnlySpan<byte> packet)
    {
        var hasPcr = TryRead(packet, out var pid, out var pcr, out var discontinuity);
        if (discontinuity && last.TryGetValue(pid, out var reset))
            reset.SuppressInterval = true;
        if (!hasPcr)
            return;
        if (!last.TryGetValue(pid, out var clock))
        {
            last.Add(pid, new Clock { Pcr = pcr });
            return;
        }
        var delta = Difference(pcr, clock.Pcr);
        if (!clock.SuppressInterval && delta > 0)
            clock.Interval = delta;
        clock.SuppressInterval = false;
        clock.Pcr = pcr;
    }

    public long ConstrainCorrection(long correction)
    {
        foreach (var (pid, first) in heads)
        {
            if (first == long.MinValue)
                continue;
            var delta = Difference(first + correction % TimestampWrap * 300, last[pid].Pcr);
            // PCR extension 为 27 MHz；偏移仍以 90 kHz 整数表示，向上取整确保接缝不倒退。
            // 整段共用这一偏移，不逐包钳位，也不改变 PCR 与音视频时间戳的相对关系。
            if (delta <= 0)
                correction += -delta / 300 + 1;
        }
        foreach (var (pid, first) in heads)
        {
            if (first == long.MinValue)
                continue;
            var delta = Difference(first + correction % TimestampWrap * 300, last[pid].Pcr);
            // 不同源区间的解码缓冲延迟可不同。显著的相位切换要明确声明，
            // 否则接收端可能把合法剪接误认为此前 PCR 发生了渐进漂移。
            if (Math.Abs(delta - last[pid].Interval) >= TsPcrCadence.BoundaryThreshold90k * 300)
                switches.Add(pid);
        }
        return correction;
    }

    public byte[] CreateSwitchPrefix(TsPacketLayout layout, int videoPid, int[] continuity,
        ref uint arrival, uint permissions)
    {
        var pids = new List<int>();
        foreach (var pid in switches)
        {
            // 参考视频区间从 PES 起点开始，可直接使用其已有 adaptation field。
            // 其他 PID 可能先出现 PES 续包，提前插入无载荷标记以免在 PES 中途重置。
            if (pid != videoPid || !firstPackets.GetValueOrDefault(pid).Writable)
                pids.Add(pid);
        }
        if (pids.Count == 0)
            return [];
        var bytes = new byte[pids.Count * layout.PacketSize];
        bytes.AsSpan().Fill(0xFF);
        for (var index = 0; index < pids.Count; index++)
        {
            var pid = pids[index];
            var packet = layout.GetPacketBody(bytes.AsSpan(), index * layout.PacketSize);
            packet[0] = 0x47;
            packet[1] = (byte)(pid >> 8);
            packet[2] = (byte)pid;
            var first = firstPackets.GetValueOrDefault(pid);
            var counter = continuity[pid] >= 0 ? continuity[pid] :
                (first.Counter - (first.Payload ? 1 : 0) + 16) & 15;
            packet[3] = (byte)(0x20 | counter);
            packet[4] = 183;
            packet[5] = 0x80;
            if (layout.IsM2ts)
            {
                arrival = (arrival + 1) & ((1u << 30) - 1);
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(index * layout.PacketSize, layout.PrefixSize),
                    permissions | arrival);
            }
            marked.Add(pid);
            last[pid].SuppressInterval = true;
        }
        return bytes;
    }

    public void MarkOutputStart(Span<byte> packet)
    {
        if (switches.Count == 0)
            return;
        var pid = ((packet[1] & 31) << 8) | packet[2];
        if (!switches.Contains(pid))
            return;
        if (marked.Contains(pid))
        {
            // 重复的边界包必须同步设置标记，否则会变成同 CC、不同内容的冲突包。
            if (boundaryPackets.TryGetValue(pid, out var original) && packet.SequenceEqual(original))
                packet[5] |= 0x80;
            else if (original is null || (packet[3] & 0x10) != 0 || (original[3] & 0x10) == 0)
            {
                // 无载荷包不结束合法的 payload 重复窗口。
                switches.Remove(pid);
                boundaryPackets.Remove(pid);
            }
            return;
        }
        if ((packet[1] & 0x80) != 0 || (packet[3] & 0x20) == 0 || packet[4] is < 1 or > 183)
            return;
        boundaryPackets[pid] = packet.ToArray();
        packet[5] |= 0x80;
        marked.Add(pid);
        last[pid].SuppressInterval = true;
    }

    private static long Difference(long next, long previous)
    {
        var delta = (next - previous) % PcrWrap;
        if (delta > PcrWrap / 2)
            delta -= PcrWrap;
        else if (delta < -PcrWrap / 2)
            delta += PcrWrap;
        return delta;
    }

    private static bool TryRead(ReadOnlySpan<byte> packet, out int pid, out long pcr, out bool discontinuity)
    {
        pid = ((packet[1] & 31) << 8) | packet[2];
        pcr = 0;
        discontinuity = false;
        if ((packet[1] & 0x80) != 0 || (packet[3] & 0x20) == 0 || packet[4] > 183 ||
            !TsTimestampFieldCodec.TryReadPcr(packet, out var raw, out _, out discontinuity))
            return false;
        pcr = raw * 300 + ((packet[10] & 1) << 8) + packet[11];
        return true;
    }

    private sealed class Clock
    {
        public long Pcr;
        public long Interval;
        public bool SuppressInterval;
    }
}
