using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TSCutter.GUI.Models.TransportStream.Analysis;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.Utils.TransportStream.Packets;

namespace TSCutter.GUI.Services.TransportStream.Analysis;

internal static class TsScramblingProbe
{
    internal const int MaximumProbeBytes = 4 * 1024 * 1024;
    internal const int ProbeBufferBytes = 64 * 1024;

    internal static bool ShouldProbeBeforeOpening(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".m2ts", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mts", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".m2t", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mpegts", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasScrambledPayload(string path) =>
        ShouldProbeBeforeOpening(path) && Probe(path).HasScrambledPayload;

    /// <param name="HasScrambledPayload">
    /// 节目使用的基本流中有加扰载荷。探测窗口内节目表不完整时，任何加扰 PID 都计入。
    /// </param>
    internal readonly record struct ProbeResult(TsPacketLayout Layout, bool HasScrambledPayload)
    {
        public bool Is188ByteTransportStream => Layout.PacketSize == 188;
        public bool IsSupportedTransportStream => Layout.IsSupported;
        public long SyncOffset => Layout.SyncOffset;
    }

    internal static ProbeResult Probe(
        string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var input = MediaFileUtil.OpenRead(path);
        var buffer = ArrayPool<byte>.Shared.Rent(ProbeBufferBytes);
        try
        {
            var length = input.ReadAtLeast(buffer.AsSpan(0, ProbeBufferBytes),
                ProbeBufferBytes, throwOnEndOfStream: false);
            var bytesRead = length;
            long syncOffset = -1;
            var packetSize = 0;
            long nextSyncOffset = -1;
            // 部分录制只解密了节目使用的流，节目表之外的 PID 仍带加扰标志；这些包不会被解复用，不影响预览。
            // 因此先收集加扰 PID，读完窗口后再与 PAT/PMT 列出的基本流比对。
            var tables = new ProgramTableTracker();
            var scrambledPids = new HashSet<int>();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = buffer.AsSpan(0, length);
                // 允许损坏前缀和非零起点；在原有加扰探测上限内按块寻找包同步。
                if (syncOffset < 0)
                {
                    var layout = FindPacketLayout(data);
                    if (layout.PacketSize == 0 && length < ProbeBufferBytes)
                        layout = TsPacketLayout.FindShortLayout(data);
                    if (layout.PacketSize != 0)
                    {
                        syncOffset = bytesRead - length + layout.SyncOffset;
                        packetSize = layout.PacketSize;
                        nextSyncOffset = syncOffset;
                        data = data[layout.SyncOffset..];
                    }
                }
                if (syncOffset >= 0)
                {
                    // 每个缓冲的起点可能落在包中间，按绝对偏移恢复同步相位。
                    var first = (int)((nextSyncOffset - (bytesRead - data.Length)) % packetSize);
                    if (first < 0) first += packetSize;
                    for (var offset = first; offset + TsUtil.TsPacketSize <= data.Length; offset += packetSize)
                    {
                        if (data[offset] != TsUtil.TsSyncByte)
                        {
                            var resync = TsUtil.FindPacketSync(data[offset..], packetSize);
                            if (resync < 0) break;
                            offset += resync;
                        }
                        nextSyncOffset = bytesRead - data.Length + offset + packetSize;
                        var packet = data.Slice(offset, TsUtil.TsPacketSize);
                        var info = TsPacketParser.Parse(packet);
                        if (!info.IsValid || info.Pid == 0x1fff || !info.HasPayload)
                            continue;
                        if (info.ScramblingControl < 2)
                        {
                            tables.Push(packet, info);
                            continue;
                        }
                        // 已知属于节目的流加扰时不必读完窗口。
                        if (tables.IsElementaryStream(info.Pid))
                            return new(new(packetSize, syncOffset), true);
                        scrambledPids.Add(info.Pid);
                    }
                }
                if (length < ProbeBufferBytes || bytesRead >= MaximumProbeBytes)
                    return new(new(packetSize, syncOffset), tables.AffectsPrograms(scrambledPids));

                // 保留最大包步长的同步窗口，也覆盖跨块的加扰包。
                const int overlap = TsPacketLayout.SyncProbeOverlapBytes;
                buffer.AsSpan(length - overlap, overlap).CopyTo(buffer);
                var requested = Math.Min(ProbeBufferBytes - overlap, MaximumProbeBytes - bytesRead);
                var read = input.ReadAtLeast(buffer.AsSpan(overlap, requested),
                    requested, throwOnEndOfStream: false);
                bytesRead += read;
                length = overlap + read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>读取探测窗口内未加扰的 PAT/PMT，记录各节目列出的基本流 PID。</summary>
    private sealed class ProgramTableTracker
    {
        private readonly Dictionary<int, SectionState> sections = [];
        // 节目号 → PMT PID。
        private readonly Dictionary<int, int> pmtPids = [];
        private readonly HashSet<int> parsedPrograms = [];
        private readonly HashSet<int> elementaryPids = [];
        private readonly HashSet<int> patSections = [];
        private int patLastSection = -1;

        // PAT 各分段与其中每个节目的 PMT 都已读到，节目表才算完整。
        private bool IsComplete => patLastSection >= 0 && patSections.Count == patLastSection + 1 &&
            pmtPids.Count > 0 && pmtPids.Keys.All(parsedPrograms.Contains);

        public bool IsElementaryStream(int pid) => elementaryPids.Contains(pid);

        /// <summary>节目表不完整时无法判断归属，按旧规则把任何加扰 PID 都计入。</summary>
        public bool AffectsPrograms(HashSet<int> scrambledPids) =>
            scrambledPids.Count > 0 && (!IsComplete || scrambledPids.Overlaps(elementaryPids));

        public void Push(ReadOnlySpan<byte> packet, TsPacketInfo info)
        {
            var pid = info.Pid;
            if (info.TransportError || info.ScramblingControl != 0 || (pid != 0 && !pmtPids.ContainsValue(pid)))
                return;
            if (!sections.TryGetValue(pid, out var state))
                sections[pid] = state = new SectionState();
            if (info.Discontinuity)
            {
                state.Assembler.DiscardUntilPayloadStart();
                state.Continuity = -1;
            }
            // 重复包跳过；计数不连续时丢弃未完成的分段。读下一块时重叠区的包会再送一遍，
            // 最多丢掉跨边界的一个分段（节目表会重复发送），CRC 校验保证不会拼出错误的表。
            if (state.Continuity == info.ContinuityCounter)
                return;
            if (state.Continuity >= 0 && ((state.Continuity + 1) & 15) != info.ContinuityCounter)
                state.Assembler.DiscardUntilPayloadStart();
            state.Continuity = info.ContinuityCounter;
            state.Assembler.Push(packet[info.PayloadOffset..], info.PayloadStart, section => ReadSection(pid, section));
        }

        private void ReadSection(int pid, ReadOnlySpan<byte> section)
        {
            if (section.Length < 12 || (section[5] & 1) == 0 || !TsPsiSectionBuilder.HasValidCrc(section))
                return;
            var end = section.Length - 4;
            if (pid == 0 && section[0] == 0)
            {
                for (var i = 8; i + 4 <= end; i += 4)
                {
                    var number = (section[i] << 8) | section[i + 1];
                    if (number != 0)
                        pmtPids[number] = ((section[i + 2] & 31) << 8) | section[i + 3];
                }
                patSections.Add(section[6]);
                patLastSection = section[7];
            }
            else if (section[0] == 2 && section.Length >= 16)
            {
                var number = (section[3] << 8) | section[4];
                if (!pmtPids.TryGetValue(number, out var pmtPid) || pmtPid != pid)
                    return;
                var i = 12 + (((section[10] & 15) << 8) | section[11]);
                while (i + 5 <= end)
                {
                    elementaryPids.Add(((section[i + 1] & 31) << 8) | section[i + 2]);
                    i += 5 + (((section[i + 3] & 15) << 8) | section[i + 4]);
                }
                parsedPrograms.Add(number);
            }
        }

        private sealed class SectionState
        {
            public SectionState() => Assembler.DiscardUntilPayloadStart();
            public TsPsiSectionAssembler Assembler { get; } = new();
            public int Continuity { get; set; } = -1;
        }
    }

    internal static (int PacketSize, int SyncOffset) FindPacketLayout(ReadOnlySpan<byte> data, bool validateHeaders = true) =>
        TsPacketLayout.FindLayout(data, validateHeaders);

    internal static bool HasScrambledPayload(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var syncOffset = TsUtil.FindPacketSync(data[offset..]);
            if (syncOffset < 0)
                break;
            offset += syncOffset;

            for (; offset + TsUtil.TsPacketSize <= data.Length; offset += TsUtil.TsPacketSize)
            {
                var info = TsPacketParser.Parse(data.Slice(offset, TsUtil.TsPacketSize));
                if (!info.IsValid)
                {
                    offset++;
                    break;
                }
                if (info.Pid != 0x1fff && info.HasPayload && info.ScramblingControl >= 2)
                    return true;
            }
        }
        return false;
    }
}
