using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models;
using TSCutter.GUI.Services;

namespace TSCutter.GUI.Utils;

[InlineArray(19)]
internal struct PesTimestampBytes { private byte first; }

[InlineArray(TsUtil.TsPacketSize)]
internal struct PesPreviousPacket { private byte first; }

// 只保留 PTS/DTS 所需的前 19 字节，不缓存整个 PES 或压缩载荷。
internal struct TsSplitPesHeader
{
    private PesTimestampBytes bytes;
    private int count;
    public int Pid { get; private set; }
    public readonly int Length => count >= 9 && (bytes[7] >> 6) == 2 ? 14 : 19;
    public readonly bool IsComplete => count < 0 || count >= Length;
    public readonly bool IsValid => count >= Length;
    public readonly bool HasTimestampHeader => count >= 9;
    public void Invalidate() => count = -1;

    public static TsSplitPesHeader Create(int pid, ReadOnlySpan<byte> payload)
    {
        var header = new TsSplitPesHeader { Pid = pid };
        header.Append(payload);
        return header;
    }

    public void Append(ReadOnlySpan<byte> payload)
    {
        var copied = Math.Min(19 - count, payload.Length);
        payload[..copied].CopyTo(((Span<byte>)bytes)[count..]);
        count += copied;
        ReadOnlySpan<byte> prefix = [0, 0, 1];
        if (!((ReadOnlySpan<byte>)bytes)[..Math.Min(count, 3)].SequenceEqual(prefix[..Math.Min(count, 3)]) ||
            count >= 9 && ((bytes[6] & 0xC0) != 0x80 || (bytes[7] >> 6) is not (2 or 3) || bytes[8] < Length - 9))
            count = -1;
    }

    public readonly void CopyTo(Span<byte> destination) => ((ReadOnlySpan<byte>)bytes)[..Length].CopyTo(destination);
    public readonly byte GetByte(int index) => bytes[index];
    public void Rewrite(long correction) => TsTimestampFieldCodec.RewritePesTimestamps(
        ((Span<byte>)bytes)[..Length], correction, 9, Length == 19 ? 14 : -1);
}

// 普通完整 PES 头保留原快路径；只有跨包头才前读，通常直接使用现有复制缓冲。
internal sealed class TsSplitPesHeaderReader : IDisposable
{
    private const int PacketSize = TsUtil.TsPacketSize;
    private const int PeekBufferSize = PacketSize * 348;
    private byte[]? peekBuffer;

    public static bool NeedsRead(ReadOnlySpan<byte> packet)
    {
        if ((packet[1] & 0xC0) != 0x40 || (packet[3] & 0xC0) != 0 ||
            !TsPacketParser.TryParse(packet, out var info) || !info.HasPayload || info.Pid is 0 or 1 or 0x1FFF)
            return false;
        var payload = packet[info.PayloadOffset..];
        if (payload.Length >= 19) return false;
        var header = TsSplitPesHeader.Create(info.Pid, payload);
        return !header.IsComplete;
    }

    public async ValueTask<TsSplitPesHeader> ReadAsync(FileStream source, byte[] copyBuffer,
        long chunkStart, int chunkLength, int packetOffset, long rangeEnd, CancellationToken token)
    {
        var packetPosition = chunkStart + packetOffset;
        var scanEnd = Math.Min(rangeEnd,
            packetPosition + Math.Min(long.MaxValue - packetPosition, TsClipBoundaryResolver.MaximumScanBytes));
        var state = new ReadState(copyBuffer.AsSpan(packetOffset, PacketSize));
        var buffer = copyBuffer;
        var bufferStart = chunkStart;
        var bufferLength = chunkLength;
        var offset = packetOffset + PacketSize;
        var savedPosition = source.Position;
        var didRead = false;
        var started = Stopwatch.GetTimestamp();
        try
        {
            while (!state.Header.IsComplete)
            {
                token.ThrowIfCancellationRequested();
                var position = bufferStart + offset;
                if (position > scanEnd - PacketSize)
                {
                    if (state.Header.HasTimestampHeader)
                        throw new TsClipMergeException(TsClipMergeErrorCode.InvalidRange);
                    state.Header.Invalidate();
                    break;
                }
                if (offset + PacketSize > bufferLength)
                {
                    if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(30))
                        throw new TsClipMergeException(TsClipMergeErrorCode.InvalidRange);
                    peekBuffer ??= ArrayPool<byte>.Shared.Rent(PeekBufferSize);
                    buffer = peekBuffer;
                    bufferStart = position;
                    bufferLength = (int)Math.Min(PeekBufferSize, scanEnd - position);
                    bufferLength -= bufferLength % PacketSize;
                    source.Position = position;
                    didRead = true;
                    await source.ReadExactlyAsync(buffer.AsMemory(0, bufferLength), token).ConfigureAwait(false);
                    offset = 0;
                }
                state.Append(buffer.AsSpan(offset, PacketSize), position);
                offset += PacketSize;
            }
            return state.Header;
        }
        finally
        {
            // 前读不能改变外层复制循环下一次读取的位置。
            if (didRead) source.Position = savedPosition;
        }
    }

    private struct ReadState
    {
        public TsSplitPesHeader Header;
        private int lastContinuity;
        private PesPreviousPacket previous;

        public ReadState(ReadOnlySpan<byte> packet)
        {
            TsPacketParser.TryParse(packet, out var info);
            Header = TsSplitPesHeader.Create(info.Pid, packet[info.PayloadOffset..]);
            lastContinuity = packet[3] & 15;
            previous = default;
            packet.CopyTo(previous);
        }

        public void Append(ReadOnlySpan<byte> packet, long position)
        {
            if (packet[0] != TsUtil.TsSyncByte)
                throw new TsClipMergeException(TsClipMergeErrorCode.SourceChanged, position);
            var pid = ((packet[1] & 31) << 8) | packet[2];
            if (pid != Header.Pid) return;
            if ((packet[1] & 0x80) != 0 || (packet[3] & 0xC0) != 0 ||
                !TsPacketParser.TryParse(packet, out var info))
            {
                RejectIncompleteHeader();
                return;
            }
            if (!info.HasPayload)
            {
                if (info.Discontinuity) lastContinuity = -1;
                return;
            }
            var continuity = packet[3] & 15;
            if (continuity == lastContinuity && packet.SequenceEqual(previous)) return;
            if ((packet[1] & 0x40) != 0 ||
                lastContinuity >= 0 && !info.Discontinuity && continuity != ((lastContinuity + 1) & 15))
            {
                RejectIncompleteHeader();
                return;
            }
            lastContinuity = continuity;
            packet.CopyTo(previous);
            Header.Append(packet[info.PayloadOffset..]);
        }

        private void RejectIncompleteHeader()
        {
            // 一两个零字节也可能是 PSI 的 pointer_field/table_id，不能据此判定为 PES。
            // 已确认含时间戳的 PES 头则必须完整取得，否则不能部分改写。
            if (Header.HasTimestampHeader)
                throw new TsClipMergeException(TsClipMergeErrorCode.InvalidRange);
            Header.Invalidate();
        }
    }

    public void Dispose()
    {
        if (peekBuffer is not null) ArrayPool<byte>.Shared.Return(peekBuffer);
    }
}

// 预先取得完整头后，在顺序复制时逐片改写；无需回写输出文件或缓存已复制的包。
internal struct TsSplitPesTimestampRewriter
{
    private Dictionary<int, RewriteState>? pending;

    public bool RewriteContinuation(Span<byte> packet)
    {
        var pid = ((packet[1] & 31) << 8) | packet[2];
        if (pending is null || !pending.TryGetValue(pid, out var state)) return false;
        if (!TsPacketParser.TryParse(packet, out var info) || (packet[1] & 0x80) != 0 || (packet[3] & 0xC0) != 0)
            throw new TsClipMergeException(TsClipMergeErrorCode.InvalidRange);
        if (!info.HasPayload)
        {
            if (info.Discontinuity)
            {
                state.LastContinuity = -1;
                pending[pid] = state;
            }
            return true;
        }
        var duplicate = (packet[3] & 15) == state.LastContinuity && packet.SequenceEqual(state.Previous);
        if (!duplicate && state.Consumed >= state.Header.Length)
        {
            pending.Remove(pid);
            return false;
        }
        if (!duplicate && ((packet[1] & 0x40) != 0 ||
            state.LastContinuity >= 0 && !info.Discontinuity && (packet[3] & 15) != ((state.LastContinuity + 1) & 15)))
            throw new TsClipMergeException(TsClipMergeErrorCode.InvalidRange);
        state.Apply(packet, info.PayloadOffset, TsUtil.TsPacketSize - info.PayloadOffset, duplicate);
        pending[pid] = state;
        return true;
    }

    public void Start(Span<byte> packet, TsSplitPesHeader header, long correction)
    {
        header.Rewrite(correction);
        var state = new RewriteState { Header = header };
        TsPacketParser.TryParse(packet, out var info);
        state.Apply(packet, info.PayloadOffset, TsUtil.TsPacketSize - info.PayloadOffset, duplicate: false);
        // 只为出现跨包头的 PID 分配状态；PID 数量本身最多为 8192。
        (pending ??= new())[info.Pid] = state;
    }

    public void CompleteSegment()
    {
        if (pending is null) return;
        foreach (var item in pending)
            if (item.Value.Consumed < item.Value.Header.Length)
                throw new TsClipMergeException(TsClipMergeErrorCode.InvalidRange);
        pending.Clear();
    }

    private struct RewriteState
    {
        public TsSplitPesHeader Header;
        public int Consumed;
        public int LastContinuity;
        public PesPreviousPacket Previous;
        private int lastFragmentStart;

        public void Apply(Span<byte> packet, int payloadOffset, int payloadLength, bool duplicate)
        {
            var start = duplicate ? lastFragmentStart : Consumed;
            var end = Math.Min(Header.Length, start + payloadLength);
            if (!duplicate)
            {
                LastContinuity = packet[3] & 15;
                packet.CopyTo(Previous);
                lastFragmentStart = start;
                Consumed = end;
            }
            for (var index = Math.Max(9, start); index < end; index++)
                packet[payloadOffset + index - start] = Header.GetByte(index);
        }
    }
}
