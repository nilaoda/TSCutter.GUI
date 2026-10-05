using System;
using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TSCutter.GUI.Utils;

/// <summary>
/// 将预览帧所在 PES 的起点转换为包含该帧的排他复制终点，不改写源文件字节。
/// </summary>
internal static class TsClipBoundaryResolver
{
    private const int PacketSize = TsUtil.TsPacketSize;
    private const int BufferSize = PacketSize * 512;
    internal const long MaximumScanBytes = 64L * 1024 * 1024;
    private const long TimestampWrap = 1L << 33;
    private static readonly TimeSpan MaximumScanTime = TimeSpan.FromSeconds(1);

    public static Task<long> ResolveEndAsync(string path, long framePosition, long framePts90k,
        PacketEndSignature signature, CancellationToken cancellationToken = default) =>
        // 即使缓存命中使 ReadAsync 同步完成，逐包扫描也不能占用界面线程。
        Task.Run(() => ResolveEndCoreAsync(path, framePosition, framePts90k, cancellationToken, signature),
            cancellationToken);

    private static async Task<long> ResolveEndCoreAsync(string path, long framePosition,
        long framePts90k, CancellationToken cancellationToken, PacketEndSignature signature)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!signature.IsValid)
            throw new InvalidDataException("The selected frame has no compressed packet boundary.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ResolveEndCoreAsync(stream, framePosition, framePts90k, signature,
            cancellationToken, TimeProvider.System).ConfigureAwait(false);
    }

    // 输入流由调用方持有；测试可模拟慢速读取，验证读取等待不会消耗扫描预算。
    internal static async Task<long> ResolveEndCoreAsync(Stream stream, long framePosition,
        long framePts90k, PacketEndSignature signature, CancellationToken token, TimeProvider timeProvider)
    {
        token.ThrowIfCancellationRequested();
        if (!signature.IsValid)
            throw new InvalidDataException("The selected frame has no compressed packet boundary.");
        var length = stream.Length;
        if (framePosition < 0 || framePosition > length - PacketSize || framePts90k == long.MinValue)
            throw new InvalidDataException("The selected frame has no valid TS boundary.");
        stream.Position = framePosition;
        // 一次租用扫描缓冲、跨包 PES 头和上一包副本，不按包分配对象。
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize + 19 + PacketSize + 32 + 215);
        try
        {
            using var matcher = new PacketContentMatcher(signature);
            var scanner = new PesScanner(framePts90k, matcher);
            var patternOffset = BufferSize + 19 + PacketSize;
            signature.WriteTo(buffer.AsSpan(patternOffset, 32));
            var position = framePosition;
            var scanEnd = framePosition + Math.Min(length - framePosition, MaximumScanBytes);
            var elapsedScan = TimeSpan.Zero;
            while (position <= scanEnd - PacketSize)
            {
                token.ThrowIfCancellationRequested();
                var count = (int)Math.Min(BufferSize, scanEnd - position);
                count -= count % PacketSize;
                await stream.ReadExactlyAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                // 只累计缓冲内解析和摘要校验的耗时，打开文件和等待读取均不计时。
                // 每个缓冲检查一次，无需为每个 TS 包读时钟或创建计时器。
                var scanStarted = timeProvider.GetTimestamp();
                var resolvedEnd = -1L;
                for (var offset = 0; offset < count; offset += PacketSize)
                {
                    resolvedEnd = scanner.Process(buffer.AsSpan(offset, PacketSize), position + offset,
                        buffer.AsSpan(BufferSize, 19), buffer.AsSpan(BufferSize + 19, PacketSize),
                        buffer.AsSpan(patternOffset, 32),
                        buffer.AsSpan(patternOffset + 32, 215));
                    if (resolvedEnd >= 0)
                        break;
                }
                elapsedScan += timeProvider.GetElapsedTime(scanStarted);
                token.ThrowIfCancellationRequested();
                if (elapsedScan >= MaximumScanTime)
                    throw new TimeoutException("Locating the selected frame exceeded the scan processing time limit.");
                if (resolvedEnd >= 0)
                    return resolvedEnd;
                position += count;
            }
            if (scanEnd < length || !scanner.HasCompleteHeader)
                throw new InvalidDataException("Cannot safely locate the end of the selected frame.");
            // 最后一个 PES 没有后继包；固定为本次读取时的 EOF，不跟随增长中的录制文件。
            return length;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private struct PesScanner(long targetPts, PacketContentMatcher matcher)
    {
        private int pid = -1;
        private int lastContinuity = -1;
        private long pesPosition;
        private int headerBytes;
        private bool headerComplete;
        private bool firstPes = true;
        private bool passedTargetPts;
        private int pesBytesSeen;
        private readonly bool PacketEndFound => matcher.IsComplete;
        public readonly bool HasCompleteHeader => headerComplete && PacketEndFound;

        public long Process(ReadOnlySpan<byte> packet, long position, Span<byte> header,
            Span<byte> previousPacket, ReadOnlySpan<byte> prefix, Span<byte> combined)
        {
            if (packet[0] != TsUtil.TsSyncByte)
                throw new InvalidDataException("The selected range is not aligned to 188-byte TS packets.");
            var packetPid = ((packet[1] & 0x1F) << 8) | packet[2];
            var startsPes = (packet[1] & 0x40) != 0;
            if (pid < 0)
            {
                if (!startsPes || packetPid == 0x1FFF)
                    throw new InvalidDataException("The selected frame does not start at a PES header.");
                pid = packetPid;
            }
            if (packetPid != pid)
                return -1;
            if ((packet[1] & 0x80) != 0 || (packet[3] & 0xC0) != 0)
                throw new InvalidDataException("The selected frame contains damaged or scrambled TS packets.");
            var control = (packet[3] >> 4) & 3;
            var payloadOffset = 4;
            if ((control & 2) != 0)
                payloadOffset += 1 + packet[4];
            if (control == 0 || payloadOffset > PacketSize)
                throw new InvalidDataException("Invalid TS adaptation field.");
            // discontinuity_indicator 允许发送端主动重置连续计数，不能误判为丢包。
            if ((control & 2) != 0 && packet[4] > 0 && (packet[5] & 0x80) != 0)
                lastContinuity = -1;
            if ((control & 1) == 0 || payloadOffset == PacketSize)
                return -1;
            var continuity = packet[3] & 15;
            if (lastContinuity >= 0)
            {
                if (continuity == lastContinuity && packet.SequenceEqual(previousPacket))
                    return -1;
                if (continuity != ((lastContinuity + 1) & 15))
                    throw new InvalidDataException("The selected frame contains missing TS packets.");
            }
            lastContinuity = continuity;
            packet.CopyTo(previousPacket);
            if (startsPes)
            {
                if (!headerComplete && headerBytes > 0)
                    throw new InvalidDataException("Incomplete PES header.");
                pesPosition = position;
                headerBytes = 0;
                headerComplete = false;
                pesBytesSeen = 0;
            }
            var payload = packet[payloadOffset..];
            if (!headerComplete)
            {
                var copied = Math.Min(header.Length - headerBytes, payload.Length);
                payload[..copied].CopyTo(header[headerBytes..]);
                headerBytes += copied;
                if (headerBytes < 9)
                {
                    pesBytesSeen += payload.Length;
                    return -1;
                }
                if (header[0] != 0 || header[1] != 0 || header[2] != 1 ||
                    (header[6] & 0xC0) != 0x80)
                    throw new InvalidDataException("Invalid video PES header.");
                var flags = header[7] >> 6;
                var required = flags == 3 ? 19 : flags == 2 ? 14 : 9;
                if (flags == 1 || header[8] < required - 9)
                    throw new InvalidDataException("Invalid PES timestamp header.");
                if (headerBytes < required)
                {
                    pesBytesSeen += payload.Length;
                    return -1;
                }
                headerComplete = true;
                if (flags >= 2)
                {
                    if ((header[9] >> 4) != flags || (header[9] & 1) == 0 ||
                        (header[11] & 1) == 0 || (header[13] & 1) == 0)
                        throw new InvalidDataException("Invalid PES presentation timestamp.");
                    var rawPts = TsTimestampFieldCodec.ReadPesTimestamp(header[9..14]);
                    // PTS 为 33 位；以所选帧为锚点比较，既支持回绕，也保留普通重排序。
                    var delta = (rawPts - (targetPts & (TimestampWrap - 1)) + TimestampWrap / 2)
                        & (TimestampWrap - 1);
                    delta -= TimestampWrap / 2;
                    // 首 PES 必须完整保留。后继的 HEVC RADL 等前导帧虽然编码在关键帧后，
                    // 播放时间仍早于关键帧；读到更晚的 PES 才能安全结束连续复制。
                    if (!firstPes && delta > 0 && PacketEndFound)
                        return pesPosition;
                    if (!firstPes && delta > 0)
                        passedTargetPts = true;
                }
                // 所选压缩帧可能跨越 PES；更晚 PTS 的 PES 开头仍可能携带它的尾部。
                // 先确认压缩包尾部已出现，必要时完整保留这个 PES，到下一 PES 才停止。
                if (!firstPes && passedTargetPts && PacketEndFound)
                    return pesPosition;
                firstPes = false;
            }
            var bodyOffset = Math.Clamp(9 + header[8] - pesBytesSeen, 0, payload.Length);
            var bodyLength = payload.Length - bodyOffset;
            var pesLength = (header[4] << 8) | header[5];
            if (pesLength > 0)
                bodyLength = Math.Min(bodyLength, Math.Max(0, 6 + pesLength - pesBytesSeen - bodyOffset));
            pesBytesSeen += payload.Length;
            if (!matcher.IsComplete && bodyLength > 0)
                matcher.Feed(payload.Slice(bodyOffset, bodyLength), prefix, combined);
            return -1;
        }
    }

    private sealed class PacketContentMatcher(PacketEndSignature signature) : IDisposable
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private int suffixBytes;
        private int remaining = signature.Length;
        private bool started;
        public bool IsComplete { get; private set; }

        public void Feed(ReadOnlySpan<byte> body, ReadOnlySpan<byte> prefix, Span<byte> combined)
        {
            if (!started)
            {
                body.CopyTo(combined[suffixBytes..]);
                var available = suffixBytes + body.Length;
                var index = combined[..available].IndexOf(prefix);
                if (index < 0 && prefix[0] == 0 && prefix[1] == 0 && prefix[2] == 0 && prefix[3] == 1)
                {
                    index = combined[..available].IndexOf(prefix[1..]);
                    if (index >= 0)
                    {
                        // FFmpeg 的 Annex B 解析器可能把首个三字节起始码规范为四字节。
                        // 仅在摘要计算中补回这个零；复制范围和源文件字节仍完全不变。
                        Span<byte> leadingZero = stackalloc byte[1];
                        leadingZero[0] = 0;
                        hash.AppendData(leadingZero);
                        remaining--;
                    }
                }
                if (index < 0)
                {
                    suffixBytes = Math.Min(31, available);
                    combined.Slice(available - suffixBytes, suffixBytes).CopyTo(combined);
                    return;
                }
                started = true;
                body = combined.Slice(index, available - index);
            }
            var count = Math.Min(remaining, body.Length);
            hash.AppendData(body[..count]);
            remaining -= count;
            if (remaining != 0) return;
            Span<byte> digest = stackalloc byte[32];
            hash.GetHashAndReset(digest);
            if (!signature.MatchesHash(digest))
                throw new InvalidDataException("The selected compressed frame does not match the source range.");
            IsComplete = true;
        }

        public void Dispose() => hash.Dispose();
    }
}
