using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using TSCutter.GUI.Models.TransportStream.Analysis;
using TSCutter.GUI.Models.TransportStream.Processing;
using TSCutter.GUI.Utils.Media;
using TSCutter.GUI.Utils.TransportStream.Packets;

namespace TSCutter.GUI.Services.TransportStream.Processing;

// 按输出偏移报告 SPS 和 SEI 的字节改动，保留包长、PES 边界和时钟字段。
internal sealed class TsHevcTransferEditor
{
    private const int PacketSize = TsUtil.TsPacketSize;
    private readonly Track?[] tracksByPid = new Track[8192];
    private readonly List<Track> tracks;

    public TsHevcTransferEditor(IReadOnlyDictionary<int, TsRemuxTransferCharacteristic> edits)
    {
        tracks = new List<Track>(edits.Count);
        foreach (var (pid, transfer) in edits)
        {
            var track = new Track(pid, (byte)transfer);
            tracksByPid[pid] = track;
            tracks.Add(track);
        }
    }

    public long FirstPendingPosition
    {
        get
        {
            var first = long.MaxValue;
            foreach (var track in tracks)
                first = Math.Min(first, track.FirstPendingPosition);
            return first;
        }
    }

    public void ProcessPacket(ReadOnlySpan<byte> packets, int offset, TsPacketInfo info, long batchStart,
        Action<long, byte> patch)
    {
        tracksByPid[info.Pid]?.ProcessPacket(packets, offset, info, batchStart, patch);
    }

    public void EndBatch(ReadOnlySpan<byte> packets, long batchStart)
    {
        foreach (var track in tracks)
            track.SavePendingPayload(packets, batchStart);
    }

    public void Complete(Action<long, byte> patch)
    {
        foreach (var track in tracks)
            track.Complete(patch);
    }

    private sealed class Track(int pid, byte transferCharacteristic)
    {
        private const int MaxSpsLength = 64 * 1024;
        private const int MaxSeiLength = 1024 * 1024;
        private readonly byte[] pendingPayload = new byte[PacketSize];
        private int pendingLength;
        private int pendingCounter;
        private bool pendingStart;
        private long pendingPosition;
        private long duplicatePosition = -1;
        private bool discontinuity;

        private readonly byte[] pesHeader = new byte[9];
        private bool hasPes;
        private int pesHeaderLength;
        private int optionalHeaderRemaining;
        private int pesPayloadRemaining;
        private bool HasIncompletePes => hasPes &&
            (pesHeaderLength != pesHeader.Length || optionalHeaderRemaining != 0 || pesPayloadRemaining > 0);

        private readonly List<byte> nal = [];
        private readonly List<(long Position, long Duplicate)> nalPositions = [];
        private int zeros;
        private bool awaitingNalHeader;
        private int metadataNalType;
        private bool foundSps;

        public long FirstPendingPosition => nalPositions.Count != 0
            ? nalPositions[0].Position
            : pendingLength != 0 ? pendingPosition : long.MaxValue;

        public void ProcessPacket(ReadOnlySpan<byte> packets, int offset, TsPacketInfo info, long batchStart,
            Action<long, byte> patch)
        {
            discontinuity |= info.Discontinuity;
            if (!info.HasPayload)
                return;
            if (info.TransportError || info.ScramblingControl != 0)
                throw RewriteFailed();

            var payloadOffset = offset + info.PayloadOffset;
            var payloadLength = PacketSize - info.PayloadOffset;
            var previousPayload = pendingPosition >= batchStart
                ? packets.Slice((int)(pendingPosition - batchStart), pendingLength)
                : pendingPayload.AsSpan(0, pendingLength);
            if (pendingLength != 0 && !discontinuity && pendingCounter == info.ContinuityCounter)
            {
                if (duplicatePosition >= 0 || pendingStart != info.PayloadStart ||
                    !packets.Slice(payloadOffset, payloadLength).SequenceEqual(previousPayload))
                    throw RewriteFailed();
                duplicatePosition = batchStart + payloadOffset;
                return;
            }

            // 延后一包处理：遇到合法重传，修改同一字节在两份包中的位置。
            if (pendingLength != 0)
            {
                ProcessPendingPayload(previousPayload, patch);
                discontinuity |= info.ContinuityCounter != ((pendingCounter + 1) & 0x0F);
            }
            if (discontinuity)
            {
                // 只在确定的新 PES 边界恢复，避免把缺包前后的字节拼成参数集。
                if (hasPes && (!info.PayloadStart || metadataNalType != 0 || awaitingNalHeader))
                    throw RewriteFailed();
                hasPes = false;
                zeros = 0;
                discontinuity = false;
            }
            pendingLength = payloadLength;
            pendingCounter = info.ContinuityCounter;
            pendingStart = info.PayloadStart;
            pendingPosition = batchStart + payloadOffset;
            duplicatePosition = -1;
        }

        public void SavePendingPayload(ReadOnlySpan<byte> packets, long batchStart)
        {
            // 同一批中的上一个包直接读缓冲区，只有跨批次的包才需要保存负载。
            if (pendingLength > 0 && pendingPosition >= batchStart)
                packets.Slice((int)(pendingPosition - batchStart), pendingLength).CopyTo(pendingPayload);
        }

        public void Complete(Action<long, byte> patch)
        {
            ProcessPendingPayload(pendingPayload.AsSpan(0, pendingLength), patch);
            if (metadataNalType != 0)
            {
                if (HasIncompletePes)
                    throw RewriteFailed();
                RewriteNal(nal.Count, patch);
            }
            if (!foundSps)
                throw RewriteFailed();
        }

        private void ProcessPendingPayload(ReadOnlySpan<byte> payload, Action<long, byte> patch)
        {
            if (payload.IsEmpty)
                return;
            if (pendingStart)
            {
                if (HasIncompletePes)
                    throw RewriteFailed();
                hasPes = true;
                pesHeaderLength = 0;
            }
            // 从文件中间开始时，首个 PES 起始包之前可能是一段不完整的 PES 头。
            if (!hasPes)
                return;

            var offset = 0;
            if (pesHeaderLength < pesHeader.Length)
            {
                offset = Math.Min(payload.Length, pesHeader.Length - pesHeaderLength);
                payload[..offset].CopyTo(pesHeader.AsSpan(pesHeaderLength));
                pesHeaderLength += offset;
                if (pesHeaderLength < pesHeader.Length)
                    return;
                // PES_CRC_flag 涉及前一个 PES。此处只支持无需重算 CRC 的 PES。
                if (pesHeader[0] != 0 || pesHeader[1] != 0 || pesHeader[2] != 1 ||
                    pesHeader[3] is not (>= 0xE0 and <= 0xEF or 0xFD) ||
                    (pesHeader[6] & 0xF0) != 0x80 || (pesHeader[7] & 0x02) != 0)
                    throw RewriteFailed();
                optionalHeaderRemaining = pesHeader[8];
                var pesLength = (pesHeader[4] << 8) | pesHeader[5];
                pesPayloadRemaining = pesLength == 0 ? -1 : pesLength - 3 - optionalHeaderRemaining;
                if (pesLength != 0 && pesPayloadRemaining < 0)
                    throw RewriteFailed();
            }

            var skipped = Math.Min(payload.Length - offset, optionalHeaderRemaining);
            optionalHeaderRemaining -= skipped;
            offset += skipped;
            var length = payload.Length - offset;
            if (pesPayloadRemaining >= 0)
            {
                length = Math.Min(length, pesPayloadRemaining);
                pesPayloadRemaining -= length;
            }
            ScanAnnexB(payload.Slice(offset, length), offset, patch);
        }

        private void ScanAnnexB(ReadOnlySpan<byte> payload, int payloadOffset, Action<long, byte> patch)
        {
            for (var index = 0; index < payload.Length; index++)
            {
                if (metadataNalType == 0 && !awaitingNalHeader && zeros == 0)
                {
                    var startCode = payload[index..].IndexOf("\0\0\u0001"u8);
                    if (startCode < 0)
                    {
                        if (payload[^1] == 0)
                            zeros = payload.Length - index >= 2 && payload[^2] == 0 ? 2 : 1;
                        return;
                    }
                    index += startCode + 2;
                    awaitingNalHeader = true;
                    continue;
                }
                var value = payload[index];
                if (awaitingNalHeader)
                {
                    var type = (value >> 1) & 0x3F;
                    metadataNalType = type is 33 or 39 or 40 ? type : 0;
                    awaitingNalHeader = false;
                }
                if (metadataNalType != 0)
                {
                    if (nal.Count == (metadataNalType == 33 ? MaxSpsLength : MaxSeiLength))
                        throw RewriteFailed();
                    var offset = payloadOffset + index;
                    nal.Add(value);
                    nalPositions.Add((pendingPosition + offset,
                        duplicatePosition < 0 ? -1 : duplicatePosition + offset));
                }
                if (value == 0)
                {
                    zeros = Math.Min(zeros + 1, 2);
                    continue;
                }
                if (value == 1 && zeros == 2)
                {
                    if (metadataNalType != 0)
                        RewriteNal(nal.Count - 3, patch);
                    metadataNalType = 0;
                    awaitingNalHeader = true;
                }
                zeros = 0;
            }
        }

        private void RewriteNal(int length, Action<long, byte> patch)
        {
            // 去掉 Annex B 的 zero_byte 或 trailing_zero_8bits，使长度校验只比较 NAL。
            while (length > 0 && nal[length - 1] == 0)
                length--;
            var original = CollectionsMarshal.AsSpan(nal)[..length];
            byte[]? rewritten;
            try
            {
                if (metadataNalType == 33)
                {
                    rewritten = HevcParameterSetRenumbering.RewriteSpsTransferCharacteristic(original, transferCharacteristic);
                    foundSps = true;
                }
                else
                {
                    rewritten = HevcParameterSetRenumbering.RewriteSeiTransferCharacteristic(original, transferCharacteristic);
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
            {
                throw RewriteFailed();
            }
            if (rewritten is not null)
            {
                for (var index = 0; index < length; index++)
                {
                    if (original[index] == rewritten[index])
                        continue;
                    var (position, duplicate) = nalPositions[index];
                    var value = rewritten[index];
                    patch(position, value);
                    if (duplicate >= 0)
                        patch(duplicate, value);
                }
            }
            nal.Clear();
            nalPositions.Clear();
        }

        private TsRemuxException RewriteFailed() =>
            new(TsRemuxErrorCode.TransferCharacteristicRewriteFailed, $"0x{pid:X4}");
    }
}
