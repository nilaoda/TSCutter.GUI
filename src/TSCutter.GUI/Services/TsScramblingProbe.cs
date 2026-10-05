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

    internal static (bool Is188ByteTransportStream, bool HasScrambledPayload, long SyncOffset) Probe(
        string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var input = File.OpenRead(path);
        var buffer = ArrayPool<byte>.Shared.Rent(ProbeBufferBytes);
        try
        {
            var length = input.ReadAtLeast(buffer.AsSpan(0, ProbeBufferBytes),
                ProbeBufferBytes, throwOnEndOfStream: false);
            var bytesRead = length;
            long syncOffset = -1;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = buffer.AsSpan(0, length);
                // 允许损坏前缀和非零起点；在原有加扰探测上限内按块寻找包同步。
                if (syncOffset < 0)
                {
                    var layout = FindPacketLayout(data);
                    if (layout.PacketSize != 0 && layout.PacketSize != TsUtil.TsPacketSize)
                        return (false, false, -1);
                    if (layout.PacketSize == TsUtil.TsPacketSize)
                    {
                        syncOffset = bytesRead - length + layout.SyncOffset;
                        data = data[layout.SyncOffset..];
                    }
                }
                if (syncOffset >= 0 && HasScrambledPayload(data))
                    return (true, true, syncOffset);
                if (length < ProbeBufferBytes || bytesRead >= MaximumProbeBytes)
                    return (syncOffset >= 0, false, syncOffset);

                // 保留最大包步长的同步窗口，也覆盖跨块的加扰包。
                const int overlap = 204 * 4;
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

    internal static (int PacketSize, int SyncOffset) FindPacketLayout(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<int> packetSizes = [188, 192, 204];
        for (var offset = 0; offset + TsUtil.TsPacketSize * 4 <= data.Length; offset++)
        {
            if (data[offset] != TsUtil.TsSyncByte)
                continue;
            foreach (var size in packetSizes)
            {
                if (offset + size * 3 + TsUtil.TsPacketSize > data.Length)
                    continue;
                var valid = true;
                for (var index = 0; index < 4; index++)
                {
                    if (!TsPacketParser.Parse(data.Slice(offset + size * index, TsUtil.TsPacketSize)).IsValid)
                    {
                        valid = false;
                        break;
                    }
                }
                if (valid)
                    return (size, offset);
            }
        }
        return (0, -1);
    }

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
