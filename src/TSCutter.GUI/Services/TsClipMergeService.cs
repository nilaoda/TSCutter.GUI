using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Services;

/// <summary>
/// 将同一 TS 文件中的多个剪辑区间合并为单个文件，并在接缝处连续化时间戳和 CC。
/// </summary>
internal sealed class TsClipMergeService
{
    private const int PacketSize = TsStreamAnalyzer.PacketSize;
    private const int ReadPacketCount = 4096;
    private const int SyncProbeBytes = 1024 * 1024;
    private const int RequiredSyncPackets = 5;

    public async Task<TsClipMergeResult> MergeAsync(
        TsClipMergeRequest request,
        IProgress<TsClipMergeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = Path.GetFullPath(request.SourcePath);
        var outputPath = Path.GetFullPath(request.OutputPath);
        if (PathsEqual(sourcePath, outputPath))
            throw new TsClipMergeException(TsClipMergeErrorCode.OutputMatchesSource);

        var sourceInfo = new FileInfo(sourcePath);
        if (!sourceInfo.Exists)
            throw new TsClipMergeException(TsClipMergeErrorCode.SourceChanged);

        var stopwatch = Stopwatch.StartNew();
        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            PacketSize * ReadPacketCount, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sourceLength = source.Length;
        var syncOffset = await FindSyncOffsetAsync(source, cancellationToken).ConfigureAwait(false);
        if (syncOffset < 0)
            throw new TsClipMergeException(TsClipMergeErrorCode.NoSync);

        var ranges = NormalizeRanges(request.Ranges, syncOffset, sourceLength);
        if (ranges.Count == 0)
            throw new TsClipMergeException(TsClipMergeErrorCode.InvalidRange);

        var totalBytes = ranges.Sum(item => item.EndPosition - item.StartPosition);
        var buffer = ArrayPool<byte>.Shared.Rent(PacketSize * ReadPacketCount);
        var lastPayloadContinuity = new int[8192];
        Array.Fill(lastPayloadContinuity, -1);
        long processedBytes = 0;
        long rewrittenPcrCount = 0;
        long rewrittenTimestampCount = 0;
        long rewrittenContinuityCount = 0;

        try
        {
            await using var output = new FileStream(
                outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);

            for (var rangeIndex = 0; rangeIndex < ranges.Count; rangeIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var range = ranges[rangeIndex];
                var segmentContinuityOffsets = new int[8192];
                Array.Fill(segmentContinuityOffsets, int.MinValue);
                source.Position = range.StartPosition;
                var remaining = range.EndPosition - range.StartPosition;

                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requested = (int)Math.Min(buffer.Length, remaining);
                    requested -= requested % PacketSize;
                    if (requested <= 0)
                        throw new TsClipMergeException(TsClipMergeErrorCode.InvalidRange);

                    await source.ReadExactlyAsync(buffer.AsMemory(0, requested), cancellationToken)
                        .ConfigureAwait(false);
                    var packets = buffer.AsSpan(0, requested);
                    for (var offset = 0; offset < packets.Length; offset += PacketSize)
                    {
                        var packet = packets.Slice(offset, PacketSize);
                        if (packet[0] != 0x47)
                            throw new TsClipMergeException(
                                TsClipMergeErrorCode.SourceChanged,
                                range.StartPosition + (range.EndPosition - range.StartPosition - remaining) + offset);

                        var transportError = (packet[1] & 0x80) != 0;
                        if (!transportError && range.TimestampCorrection90k != 0)
                        {
                            if (TsTimestampFieldCodec.RewritePcr(
                                    packet, range.TimestampCorrection90k))
                                rewrittenPcrCount++;
                            rewrittenTimestampCount += TsTimestampFieldCodec.RewritePesTimestamps(
                                packet, range.TimestampCorrection90k);
                        }

                        // 每个后续片段对各 PID 只计算一次固定 CC 偏移。这样既能消除人为
                        // 剪切产生的接缝跳号，又会完整保留片段内部原有的跳号、重复包和 TEI。
                        rewrittenContinuityCount += RewriteContinuity(
                            packet, rangeIndex, segmentContinuityOffsets, lastPayloadContinuity);
                    }

                    await output.WriteAsync(buffer.AsMemory(0, requested), cancellationToken)
                        .ConfigureAwait(false);
                    remaining -= requested;
                    processedBytes += requested;
                    progress?.Report(new TsClipMergeProgress(
                        processedBytes,
                        totalBytes,
                        processedBytes / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds),
                        stopwatch.Elapsed));
                }
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (source.Length != sourceLength)
                throw new TsClipMergeException(TsClipMergeErrorCode.SourceChanged);

            progress?.Report(new TsClipMergeProgress(
                totalBytes,
                totalBytes,
                totalBytes / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds),
                stopwatch.Elapsed));
            return new TsClipMergeResult
            {
                OutputPath = outputPath,
                OutputBytes = totalBytes,
                SegmentCount = ranges.Count,
                RewrittenPcrCount = rewrittenPcrCount,
                RewrittenTimestampCount = rewrittenTimestampCount,
                RewrittenContinuityCount = rewrittenContinuityCount,
                Elapsed = stopwatch.Elapsed
            };
        }
        catch (EndOfStreamException)
        {
            TryDelete(outputPath);
            throw new TsClipMergeException(TsClipMergeErrorCode.SourceChanged);
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static List<NormalizedRange> NormalizeRanges(
        IReadOnlyList<TsClipMergeRange> source,
        long syncOffset,
        long fileLength)
    {
        var aligned = new List<NormalizedRange>(source.Count);
        foreach (var item in source)
        {
            var rawEnd = item.EndPosition > 0 ? Math.Min(item.EndPosition, fileLength) : fileLength;
            var rawStart = Math.Clamp(item.StartPosition, syncOffset, fileLength);
            var start = AlignDown(rawStart, syncOffset);
            var end = AlignDown(rawEnd, syncOffset);
            if (end <= start || item.EndTimeSeconds <= item.StartTimeSeconds)
                continue;
            aligned.Add(new NormalizedRange(
                start, end, item.StartTimeSeconds, item.EndTimeSeconds, 0));
        }

        aligned.Sort(static (left, right) => left.StartPosition.CompareTo(right.StartPosition));
        var merged = new List<NormalizedRange>(aligned.Count);
        foreach (var item in aligned)
        {
            if (merged.Count == 0 || item.StartPosition > merged[^1].EndPosition)
            {
                merged.Add(item);
                continue;
            }

            var previous = merged[^1];
            merged[^1] = previous with
            {
                EndPosition = Math.Max(previous.EndPosition, item.EndPosition),
                EndTimeSeconds = Math.Max(previous.EndTimeSeconds, item.EndTimeSeconds)
            };
        }

        if (merged.Count == 0)
            return merged;

        var outputStartTime = merged[0].StartTimeSeconds;
        var accumulatedDuration = 0.0;
        for (var index = 0; index < merged.Count; index++)
        {
            var item = merged[index];
            var expectedStart = outputStartTime + accumulatedDuration;
            merged[index] = item with
            {
                TimestampCorrection90k = (long)Math.Round(
                    (expectedStart - item.StartTimeSeconds) * 90_000.0)
            };
            accumulatedDuration += item.EndTimeSeconds - item.StartTimeSeconds;
        }
        return merged;
    }

    private static long AlignDown(long value, long syncOffset)
    {
        if (value <= syncOffset)
            return syncOffset;
        return syncOffset + (value - syncOffset) / PacketSize * PacketSize;
    }

    private static async Task<long> FindSyncOffsetAsync(
        FileStream source,
        CancellationToken cancellationToken)
    {
        var requested = (int)Math.Min(
            source.Length,
            SyncProbeBytes + RequiredSyncPackets * PacketSize);
        if (requested < RequiredSyncPackets * PacketSize)
            return -1;

        var buffer = ArrayPool<byte>.Shared.Rent(requested);
        try
        {
            source.Position = 0;
            await source.ReadExactlyAsync(buffer.AsMemory(0, requested), cancellationToken)
                .ConfigureAwait(false);
            var data = buffer.AsSpan(0, requested);
            for (var offset = 0; offset + RequiredSyncPackets * PacketSize <= data.Length; offset++)
            {
                if (data[offset] != 0x47)
                    continue;
                var valid = true;
                for (var packet = 1; packet < RequiredSyncPackets; packet++)
                {
                    if (data[offset + packet * PacketSize] == 0x47)
                        continue;
                    valid = false;
                    break;
                }
                if (valid)
                    return offset;
            }
            return -1;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int RewriteContinuity(
        Span<byte> packet,
        int rangeIndex,
        int[] segmentOffsets,
        int[] lastPayloadContinuity)
    {
        var pid = ((packet[1] & 0x1F) << 8) | packet[2];
        var adaptationControl = (packet[3] >> 4) & 0x03;
        var hasPayload = (adaptationControl & 0x01) != 0;
        if (pid == 0x1FFF || adaptationControl == 0)
            return 0;

        var original = packet[3] & 0x0F;
        var changed = 0;
        if (rangeIndex > 0)
        {
            var offset = segmentOffsets[pid];
            if (offset == int.MinValue && hasPayload)
            {
                offset = lastPayloadContinuity[pid] < 0
                    ? 0
                    : (lastPayloadContinuity[pid] + 1 - original + 16) & 0x0F;
                segmentOffsets[pid] = offset;
            }
            if (offset != int.MinValue)
            {
                var rewritten = (original + offset) & 0x0F;
                if (rewritten != original)
                {
                    packet[3] = (byte)((packet[3] & 0xF0) | rewritten);
                    changed = 1;
                }
            }
        }

        if (hasPayload)
            lastPayloadContinuity[pid] = packet[3] & 0x0F;
        return changed;
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        left,
        right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 清理失败不能覆盖真正的合并或取消异常。
        }
    }

    private readonly record struct NormalizedRange(
        long StartPosition,
        long EndPosition,
        double StartTimeSeconds,
        double EndTimeSeconds,
        long TimestampCorrection90k);
}
