using System;
using System.Buffers;
using System.IO;
using System.Threading;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Services;

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
                        var info = TsPacketParser.Parse(data.Slice(offset, TsUtil.TsPacketSize));
                        if (info.IsValid && info.Pid != 0x1fff && info.HasPayload && info.ScramblingControl >= 2)
                            return new(new(packetSize, syncOffset), true);
                    }
                }
                if (length < ProbeBufferBytes || bytesRead >= MaximumProbeBytes)
                    return new(new(packetSize, syncOffset), false);

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
