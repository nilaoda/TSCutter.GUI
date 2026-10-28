using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Utils.TransportStream.Packets;

namespace TSCutter.GUI.Utils.Editing;

// AC-3/E-AC-3 帧可跨 PES。仅调整接缝附近的 PES，片段内部仍复制原包并保留原有传输错误。
internal sealed class TsClipMergeAudioFrames
{
    private readonly Dictionary<long, byte[]> heads = [];
    private long lastHeadPosition = -1;
    public List<(int Pid, byte[] Data)> Tails { get; } = [];
    public Dictionary<int, long> EndPts { get; } = [];

    public void RewriteHead(Span<byte> packet, long position)
    {
        if (position <= lastHeadPosition && heads.TryGetValue(position, out var replacement))
            replacement.CopyTo(packet);
    }

    public static async Task<TsClipMergeAudioFrames> ReadAsync(FileStream source, long start, long end,
        IReadOnlyList<int> audioPids, TsPacketLayout layout, CancellationToken token)
    {
        var result = new TsClipMergeAudioFrames();
        if (audioPids.Count == 0)
            return result;
        var first = await ReadPesAsync(source, start, audioPids, layout, long.MaxValue, token).ConfigureAwait(false);
        var dolbyPids = new List<int>();
        foreach (var pid in audioPids.Distinct())
        {
            var records = first[pid];
            if (records.Count < 2 || records[0].Start >= end || records.Any(record => !record.Reliable))
                continue;
            var body = JoinBodies(records);
            var sync = FindSync(body);
            if (sync < 0)
                continue;
            var head = new TsClipMergeAudioFrames();
            var ready = true;
            foreach (var record in records)
            {
                if (sync == 0)
                    break;
                var skipped = Math.Min(sync, record.Body.Length);
                if (!head.TrimHead(record, skipped))
                {
                    ready = false;
                    break;
                }
                sync -= skipped;
            }
            if (!ready || sync != 0)
                continue;
            foreach (var replacement in head.heads)
            {
                result.heads.Add(replacement.Key, replacement.Value);
                result.lastHeadPosition = Math.Max(result.lastHeadPosition, replacement.Key);
            }
            dolbyPids.Add(pid);
        }
        if (dolbyPids.Count == 0)
            return result;
        // 向后扩大局部窗口，直到每轨取得片尾完整 PES 及后继上下文；上限与视频边界探测一致。
        var window = (long)layout.PacketSize * 512;
        Dictionary<int, List<Pes>> last;
        while (true)
        {
            var tailStart = end - Math.Min(end - start, window);
            tailStart += (end - tailStart) % layout.PacketSize;
            last = await ReadPesAsync(source, tailStart, dolbyPids, layout, end, token).ConfigureAwait(false);
            if (tailStart == start || window >= TsClipBoundaryResolver.MaximumScanBytes ||
                dolbyPids.All(pid => last[pid].Count(record => record.Start < end) >= 2))
                break;
            window = Math.Min(TsClipBoundaryResolver.MaximumScanBytes, window * 2);
        }
        foreach (var pid in dolbyPids)
        {
            var records = last[pid];
            var selected = records.FindLastIndex(record => record.Start < end);
            if (selected < 0 || selected + 1 >= records.Count || records.Any(record => !record.Reliable))
                continue;
            var body = JoinBodies(records);
            var boundary = records.Take(selected + 1).Sum(record => record.Body.Length);
            var sync = FindSync(body);
            if (sync < 0)
                continue;
            var recordIndex = 0;
            var recordOffset = 0;
            var previousRecord = -1;
            var lastEnd = long.MinValue;
            while (TryFrame(body.AsSpan(sync), out var length, out var independent, requireComplete: false))
            {
                while (recordIndex + 1 < records.Count && sync >= recordOffset + records[recordIndex].Body.Length)
                    recordOffset += records[recordIndex++].Body.Length;
                // E-AC-3 的 dependent substream 必须随同它所属的独立帧保留。
                if (sync >= boundary && independent)
                {
                    if (lastEnd == long.MinValue)
                        break;
                    result.EndPts[pid] = lastEnd;
                    if (sync > boundary && sync - boundary <= ushort.MaxValue - 3)
                    {
                        // 这里只是上一音频帧的尾部，没有新访问单元起点，不能沿用下一 PES 的 PTS。
                        var tail = new byte[9 + sync - boundary];
                        tail[2] = 1;
                        tail[3] = records[selected].Header[3];
                        tail[4] = (byte)((tail.Length - 6) >> 8);
                        tail[5] = (byte)(tail.Length - 6);
                        tail[6] = 0x80;
                        body.AsSpan(boundary, sync - boundary).CopyTo(tail.AsSpan(9));
                        result.Tails.Add((pid, tail));
                    }
                    break;
                }
                if (independent)
                {
                    var pts = previousRecord != recordIndex
                        ? TsTimestampFieldCodec.ReadPesTimestamp(records[recordIndex].Header.AsSpan(9, 5)) : lastEnd;
                    lastEnd = (pts + FrameDuration90k(body.AsSpan(sync))) & ((1L << 33) - 1);
                    previousRecord = recordIndex;
                }
                sync += length;
                if (sync >= body.Length)
                    break;
            }
        }
        return result;
    }

    private bool TrimHead(Pes pes, int skipped)
    {
        var packets = pes.Packets.Where(packet => !packet.Duplicate).ToArray();
        var body = pes.Body.AsSpan(skipped);
        byte[] data;
        if (body.IsEmpty)
        {
            var stuffing = Math.Max(0, packets.Length - 1);
            if (stuffing > 255)
                return false;
            data = new byte[9 + stuffing];
            data[2] = 1;
            data[3] = pes.Header[3];
            data[6] = 0x80;
            data[8] = (byte)stuffing;
            data.AsSpan(9).Fill(0xFF);
        }
        else
        {
            data = new byte[pes.Header.Length + body.Length];
            pes.Header.CopyTo(data, 0);
            body.CopyTo(data.AsSpan(pes.Header.Length));
            data[6] |= 4;
        }
        data[4] = (byte)((data.Length - 6) >> 8);
        data[5] = (byte)(data.Length - 6);
        if (data.Length < packets.Length)
            return false;
        var replacements = new Dictionary<long, byte[]>();
        var offset = 0;
        var count = 0;
        byte[]? previous = null;
        foreach (var item in pes.Packets)
        {
            if (item.Duplicate)
            {
                replacements[item.Position] = previous!;
                continue;
            }
            var original = item.Data;
            var info = TsPacketParser.Parse(original);
            var available = Math.Min(188 - info.PayloadOffset, data.Length - offset - (packets.Length - ++count));
            if (available <= 0)
                return false;
            var rewritten = new byte[188];
            rewritten.AsSpan().Fill(0xFF);
            original.AsSpan(0, 4).CopyTo(rewritten);
            if (available < 184)
            {
                rewritten[3] |= 0x20;
                rewritten[4] = (byte)(183 - available);
                if (rewritten[4] > 0)
                {
                    rewritten[5] = 0;
                    if (info.AdaptationLength > 0)
                        original.AsSpan(5, info.AdaptationLength).CopyTo(rewritten.AsSpan(5));
                }
            }
            data.AsSpan(offset, available).CopyTo(rewritten.AsSpan(188 - available));
            offset += available;
            replacements[item.Position] = previous = rewritten;
        }
        if (offset != data.Length)
            return false;
        foreach (var replacement in replacements)
            heads.Add(replacement.Key, replacement.Value);
        return true;
    }

    private static byte[] JoinBodies(List<Pes> records)
    {
        var bytes = new byte[records.Sum(record => record.Body.Length)];
        var offset = 0;
        foreach (var record in records)
        {
            record.Body.CopyTo(bytes, offset);
            offset += record.Body.Length;
        }
        return bytes;
    }

    private static int FindSync(ReadOnlySpan<byte> data)
    {
        for (var index = 0; index < Math.Min(8192, data.Length); index++)
        {
            if (TryFrame(data[index..], out var length, out var independent) && independent &&
                index + length < data.Length && TryFrame(data[(index + length)..], out _, out _, requireComplete: false))
                return index;
        }
        return -1;
    }

    private static long FrameDuration90k(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<int> rates = [48000, 44100, 32000];
        var rate = data[4] >> 6;
        var bsid = data[5] >> 3;
        var samples = 1536;
        int sampleRate;
        if (bsid <= 10)
            sampleRate = rates[rate] >> Math.Max(0, bsid - 8);
        else if (rate == 3)
            sampleRate = rates[data[4] >> 4 & 3] / 2;
        else
        {
            ReadOnlySpan<int> blocks = [1, 2, 3, 6];
            samples = blocks[data[4] >> 4 & 3] * 256;
            sampleRate = rates[rate];
        }
        return (long)Math.Round(samples * 90_000d / sampleRate);
    }

    private static bool TryFrame(ReadOnlySpan<byte> data, out int length, out bool independent,
        bool requireComplete = true)
    {
        length = 0;
        independent = true;
        if (data.Length < 7 || data[0] != 0x0B || data[1] != 0x77)
            return false;
        var bsid = data[5] >> 3;
        if (bsid <= 10)
        {
            ReadOnlySpan<int> bitrates = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640];
            var code = data[4] & 63;
            var rate = data[4] >> 6;
            if (code >= 38 || rate == 3)
                return false;
            var bitrate = bitrates[code >> 1];
            length = rate switch { 0 => bitrate * 4, 1 => 2 * (320 * bitrate / 147 + (code & 1)), _ => bitrate * 6 };
        }
        else if (bsid <= 16 && data[2] >> 6 != 3)
        {
            length = 2 * ((((data[2] & 7) << 8) | data[3]) + 1);
            independent = data[2] >> 6 != 1 && (data[2] >> 3 & 7) == 0;
            if (length < 7 || data[4] >> 6 == 3 && (data[4] >> 4 & 3) == 3)
                return false;
        }
        return length > 0 && (!requireComplete || length <= data.Length);
    }

    private static async Task<Dictionary<int, List<Pes>>> ReadPesAsync(FileStream source, long start,
        IReadOnlyList<int> pids, TsPacketLayout layout, long tailEnd, CancellationToken token)
    {
        var records = pids.Distinct().ToDictionary(pid => pid, _ => new List<Pes>());
        var current = new Dictionary<int, Pes>();
        var damaged = new HashSet<int>();
        var buffer = ArrayPool<byte>.Shared.Rent(layout.PacketSize * 512);
        var end = start + Math.Min(source.Length - start, TsClipBoundaryResolver.MaximumScanBytes);
        try
        {
            source.Position = start;
            var position = start;
            while (position <= end - layout.PacketSize)
            {
                var count = (int)Math.Min(layout.PacketSize * 512, end - position);
                count -= count % layout.PacketSize;
                await source.ReadExactlyAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                for (var offset = 0; offset < count; offset += layout.PacketSize)
                {
                    var packet = layout.GetPacketBody(buffer.AsSpan(), offset);
                    var info = TsPacketParser.Parse(packet);
                    if (!records.TryGetValue(info.Pid, out var list) || !info.HasPayload)
                        continue;
                    var limit = tailEnd == long.MaxValue ? list.Count >= 3 : list.Count(record => record.Start >= tailEnd) >= 2;
                    if (limit)
                        continue;
                    current.TryGetValue(info.Pid, out var pes);
                    if (!info.IsValid || info.TransportError || info.ScramblingControl != 0)
                    {
                        damaged.Add(info.Pid);
                        if (pes is not null)
                            pes.Reliable = false;
                        continue;
                    }
                    var duplicate = pes is not null && packet.SequenceEqual(pes.Packets[^1].Data);
                    if (info.PayloadStart && !duplicate)
                    {
                        var payload = packet[info.PayloadOffset..];
                        if (payload.Length < 14 || !payload.StartsWith((ReadOnlySpan<byte>)[0, 0, 1]) ||
                            payload[8] + 9 > payload.Length || (payload[7] >> 6) is not (2 or 3))
                        {
                            current.Remove(info.Pid);
                            continue;
                        }
                        var length = (payload[4] << 8) | payload[5];
                        if (length == 0 || length + 6 < 9 + payload[8])
                        {
                            current.Remove(info.Pid);
                            continue;
                        }
                        if (pes is not null && (!pes.Complete || info.Discontinuity ||
                            info.ContinuityCounter != ((pes.Packets[^1].Data[3] + 1) & 15)))
                            damaged.Add(info.Pid);
                        pes = new Pes(position + offset, payload[..(9 + payload[8])].ToArray(), length + 6);
                        current[info.Pid] = pes;
                    }
                    if (pes is null)
                        continue;
                    var original = packet.ToArray();
                    if (duplicate)
                    {
                        pes.Packets.Add(new(position + offset, original, true));
                        continue;
                    }
                    if (pes.Complete)
                        continue;
                    if (pes.Packets.Count > 0 && (info.Discontinuity ||
                        info.ContinuityCounter != ((pes.Packets[^1].Data[3] + 1) & 15)))
                        pes.Reliable = false;
                    pes.Packets.Add(new(position + offset, original, false));
                    var bytes = packet[info.PayloadOffset..];
                    var copied = Math.Min(bytes.Length, pes.Expected - pes.Length);
                    bytes[..copied].CopyTo(pes.Data.AsSpan(pes.Length));
                    pes.Length += bytes.Length;
                    if (pes.Complete)
                    {
                        pes.Reliable &= pes.Length == pes.Expected;
                        pes.Body = pes.Data[pes.Header.Length..];
                        list.Add(pes);
                        if (tailEnd != long.MaxValue)
                            while (list.Count(record => record.Start < tailEnd) > 4)
                                list.RemoveAt(0);
                    }
                }
                position += count;
                if (records.Values.All(list => tailEnd == long.MaxValue ? list.Count >= 3 :
                        list.Count(record => record.Start >= tailEnd) >= 2))
                    break;
            }
            token.ThrowIfCancellationRequested();
            foreach (var pid in damaged)
                records[pid].Clear();
            return records;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private sealed record SourcePacket(long Position, byte[] Data, bool Duplicate);
    private sealed class Pes(long start, byte[] header, int expected)
    {
        public long Start { get; } = start;
        public byte[] Header { get; } = header;
        public int Expected { get; } = expected;
        public bool Reliable { get; set; } = true;
        public byte[] Body { get; set; } = [];
        public byte[] Data { get; } = new byte[expected];
        public int Length { get; set; }
        public List<SourcePacket> Packets { get; } = [];
        public bool Complete => Length >= Expected;
    }
}
