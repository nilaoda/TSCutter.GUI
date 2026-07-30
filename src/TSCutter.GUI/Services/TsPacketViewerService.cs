using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Services;

internal sealed class TsPacketViewerService : IAsyncDisposable
{
    private const int PacketSize = TsUtil.TsPacketSize;
    private const long MaxProbeBytes = 64L * 1024 * 1024;
    private const int SearchChunkPackets = 65_536;
    private FileStream? _stream;
    private TsPacketViewerSession? _session;

    public async Task<TsPacketViewerSession> OpenAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        await DisposeStreamAsync().ConfigureAwait(false);
        var fullPath = Path.GetFullPath(filePath);
        var analyzer = new TsStreamAnalyzer();
        var catalog = await analyzer.AnalyzeAsync(
            fullPath, cancellationToken: cancellationToken,
            options: new TsStreamAnalyzeOptions
            {
                InventoryOnly = true,
                IncludeServiceMetadata = true,
                MaxBytes = MaxProbeBytes,
                StablePacketCount = 8_192,
                Features = TsStreamAnalyzeFeatures.None
            }).ConfigureAwait(false);
        if (catalog.PacketCount == 0)
            throw new InvalidDataException("No valid 188-byte TS packet sequence was found.");

        var fileSize = new FileInfo(fullPath).Length;
        cancellationToken.ThrowIfCancellationRequested();
        _stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
        _session = new TsPacketViewerSession
        {
            FilePath = fullPath,
            FileSize = fileSize,
            SyncOffset = catalog.SyncOffset,
            TotalPackets = Math.Max(0, (fileSize - catalog.SyncOffset) / PacketSize),
            Catalog = catalog
        };
        return _session;
    }

    public async Task<IReadOnlyList<TsPacketData>> ReadWindowAsync(
        long startPacket,
        int packetCount,
        CancellationToken cancellationToken = default)
    {
        var session = EnsureOpen();
        startPacket = Math.Clamp(startPacket, 0, Math.Max(0, session.TotalPackets - 1));
        packetCount = (int)Math.Min(Math.Max(0, packetCount), session.TotalPackets - startPacket);
        if (packetCount == 0)
            return [];

        // 只读取当前窗口，不为整份文件建立逐包对象；超大文件的内存占用因此保持固定。
        var bytes = new byte[packetCount * PacketSize];
        var fileOffset = session.SyncOffset + startPacket * PacketSize;
        var read = await ReadAtMostAsync(bytes, fileOffset, cancellationToken).ConfigureAwait(false);
        var completePackets = read / PacketSize;
        var rows = new List<TsPacketData>(completePackets);
        for (var index = 0; index < completePackets; index++)
        {
            var packet = bytes.AsSpan(index * PacketSize, PacketSize);
            var info = TsPacketParser.Parse(packet);
            rows.Add(new TsPacketData
            {
                PacketIndex = startPacket + index,
                FileOffset = fileOffset + index * PacketSize,
                Data = packet.ToArray(),
                Info = info,
                TimestampText = FormatTimestamps(packet)
            });
        }
        return rows;
    }

    public async Task<long?> FindSamePidAsync(
        long currentPacket,
        int pid,
        bool forward,
        CancellationToken cancellationToken = default)
    {
        var session = EnsureOpen();
        var buffer = ArrayPool<byte>.Shared.Rent(SearchChunkPackets * PacketSize);
        try
        {
            // 同 PID 导航只复用一个分块缓冲，不保存全文件 PID 索引，也不随文件长度增加托管分配。
            if (forward)
            {
                for (var start = currentPacket + 1; start < session.TotalPackets; start += SearchChunkPackets)
                {
                    var count = (int)Math.Min(SearchChunkPackets, session.TotalPackets - start);
                    var match = await FindInChunkAsync(
                        start, count, pid, false, buffer, cancellationToken).ConfigureAwait(false);
                    if (match is not null)
                        return match;
                }
                return null;
            }

            for (var end = currentPacket; end > 0;)
            {
                var start = Math.Max(0, end - SearchChunkPackets);
                var count = (int)(end - start);
                var match = await FindInChunkAsync(
                    start, count, pid, true, buffer, cancellationToken).ConfigureAwait(false);
                if (match is not null)
                    return match;
                end = start;
            }
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<long?> FindInChunkAsync(
        long startPacket,
        int packetCount,
        int pid,
        bool reverse,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var session = EnsureOpen();
        var bytes = buffer.AsMemory(0, packetCount * PacketSize);
        var fileOffset = session.SyncOffset + startPacket * PacketSize;
        var read = await ReadAtMostAsync(bytes, fileOffset, cancellationToken).ConfigureAwait(false);
        var completePackets = read / PacketSize;
        if (reverse)
        {
            for (var index = completePackets - 1; index >= 0; index--)
            {
                if (TsPacketParser.TryParse(bytes.Span.Slice(index * PacketSize, PacketSize), out var info) &&
                    info.Pid == pid)
                    return startPacket + index;
            }
        }
        else
        {
            for (var index = 0; index < completePackets; index++)
            {
                if (TsPacketParser.TryParse(bytes.Span.Slice(index * PacketSize, PacketSize), out var info) &&
                    info.Pid == pid)
                    return startPacket + index;
            }
        }
        return null;
    }

    private async Task<int> ReadAtMostAsync(
        Memory<byte> buffer,
        long fileOffset,
        CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("No TS file is open.");
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await RandomAccess.ReadAsync(
                stream.SafeFileHandle, buffer[total..], fileOffset + total, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }

    private static string FormatTimestamps(ReadOnlySpan<byte> packet)
    {
        var values = new List<string>(3);
        if (TsTimestampFieldCodec.TryReadPcr(packet, out var pcr, out _, out _))
            values.Add($"PCR {TsCheckEvent.FormatTime(pcr / 90_000.0)}");
        if (TsTimestampFieldCodec.TryLocatePesTimestamps(packet, out var ptsOffset, out var dtsOffset))
        {
            values.Add($"PTS {TsCheckEvent.FormatTime(TsTimestampFieldCodec.ReadPesTimestamp(packet[ptsOffset..]) / 90_000.0)}");
            if (dtsOffset >= 0)
                values.Add($"DTS {TsCheckEvent.FormatTime(TsTimestampFieldCodec.ReadPesTimestamp(packet[dtsOffset..]) / 90_000.0)}");
        }
        return values.Count > 0 ? string.Join(" / ", values) : "-";
    }

    private TsPacketViewerSession EnsureOpen() =>
        _session ?? throw new InvalidOperationException("No TS file is open.");

    private async ValueTask DisposeStreamAsync()
    {
        _session = null;
        var stream = Interlocked.Exchange(ref _stream, null);
        if (stream is null)
            return;
        await stream.DisposeAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => DisposeStreamAsync();
}
