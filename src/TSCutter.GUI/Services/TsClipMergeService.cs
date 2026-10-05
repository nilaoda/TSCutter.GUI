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
        long fileStart = 0;
        // 已标记的边界是实际文件偏移；仅未标记、从文件头开始的范围需要寻找首包。
        if (request.Ranges.Any(item => item.StartPosition == 0))
        {
            fileStart = await FindSyncOffsetAsync(source, cancellationToken).ConfigureAwait(false);
            if (fileStart < 0)
                throw new TsClipMergeException(TsClipMergeErrorCode.NoSync);
        }

        var ranges = NormalizeRanges(request.Ranges, fileStart, sourceLength);
        if (ranges.Count == 0)
            throw new TsClipMergeException(TsClipMergeErrorCode.InvalidRange);

        using var splitHeaderReader = new TsSplitPesHeaderReader();
        var splitTimestampRewriter = new TsSplitPesTimestampRewriter();
        var totalBytes = ranges.Sum(item => item.EndPosition - item.StartPosition);
        var buffer = ArrayPool<byte>.Shared.Rent(PacketSize * ReadPacketCount);
        var lastPayloadContinuity = new int[8192];
        var segmentContinuityOffsets = new int[8192];
        Array.Fill(lastPayloadContinuity, -1);
        long processedBytes = 0;
        long rewrittenPcrCount = 0;
        long rewrittenTimestampCount = 0;
        long rewrittenContinuityCount = 0;
        var previousTimeline = new TsClipMergeTimeline();
        previousTimeline.Reset(request.VideoPid);
        // 只为实际音轨保留定额统计，不按全部 8192 个 PID 分配状态。
        TsClipMergeTimeline[] audioTimelines = request.AudioPids.Count == 0 ? [] : new TsClipMergeTimeline[request.AudioPids.Count];
        TsClipMergeTimeline[] audioHeads = request.AudioPids.Count == 0 ? [] : new TsClipMergeTimeline[request.AudioPids.Count];
        for (var index = 0; index < audioTimelines.Length; index++)
            audioTimelines[index].Reset(request.AudioPids[index]);
        var previousCorrection = 0L;
        var expectedStartTime = ranges[0].StartTimeSeconds;

        try
        {
            await using var output = new FileStream(
                outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);

            for (var rangeIndex = 0; rangeIndex < ranges.Count; rangeIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var range = ranges[rangeIndex];
                var correction = (long)Math.Round((expectedStartTime - range.StartTimeSeconds) * 90_000.0);
                if (rangeIndex > 0 && previousTimeline.HasPts)
                {
                    var head = await ReadTimelineHeadAsync(source, range, buffer, splitHeaderReader,
                            previousTimeline.Pid, audioTimelines, audioHeads, cancellationToken)
                        .ConfigureAwait(false);
                    if (head.HasPts)
                    {
                        // HEVC 前导帧可能早于起点关键帧，按实际最早 PTS 接续上一段末帧。
                        correction = previousTimeline.EndPts + previousCorrection - head.MinimumPts;
                        if (previousTimeline.HasDts && head.HasDts)
                            correction = LaterCorrection(correction,
                                previousTimeline.EndDts + previousCorrection - head.MinimumDts);
                        // 音频 PES 可能覆盖多帧并超过视频终点；同一偏移应用于全部音视频，
                        // 既保留片段内的音画关系，也避免接缝音轨倒退或重叠。
                        for (var index = 0; index < audioTimelines.Length; index++)
                            if (audioTimelines[index].HasPts && audioHeads[index].HasPts)
                                correction = LaterCorrection(correction,
                                    audioTimelines[index].EndPts + previousCorrection - audioHeads[index].MinimumPts);
                    }
                }
                var timeline = new TsClipMergeTimeline();
                timeline.Reset(previousTimeline.Pid);
                for (var index = 0; index < audioTimelines.Length; index++)
                    audioTimelines[index].Reset(request.AudioPids[index]);
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
                    var chunkStart = range.StartPosition + (range.EndPosition - range.StartPosition - remaining);
                    for (var offset = 0; offset < requested; offset += PacketSize)
                    {
                        TsSplitPesHeader splitHeader = default;
                        if ((rangeIndex + 1 < ranges.Count || correction != 0) &&
                            TsSplitPesHeaderReader.NeedsRead(buffer.AsSpan(offset, PacketSize)))
                            splitHeader = await splitHeaderReader.ReadAsync(source, buffer, chunkStart,
                                requested, offset, range.EndPosition, cancellationToken).ConfigureAwait(false);
                        var packet = buffer.AsSpan(offset, PacketSize);
                        if (packet[0] != 0x47)
                            throw new TsClipMergeException(
                                TsClipMergeErrorCode.SourceChanged,
                                range.StartPosition + (range.EndPosition - range.StartPosition - remaining) + offset);

                        var transportError = (packet[1] & 0x80) != 0;
                        // 最后一段之后没有接缝；单段导出也无需统计时间戳。
                        if (rangeIndex + 1 < ranges.Count)
                        {
                            if (splitHeader.IsValid)
                                ObserveSplitHeader(splitHeader, ref timeline, audioTimelines);
                            else
                            {
                                timeline.Observe(packet);
                                ObserveAudio(packet, audioTimelines);
                            }
                        }
                        if (!transportError && correction != 0)
                        {
                            // 完整头在前读时已取得，分片在写入输出前完成改写。
                            // 重复包使用同一头片段，不能让重复的部分仍保留旧时间戳。
                            var continuation = splitTimestampRewriter.RewriteContinuation(packet);
                            if (!continuation && splitHeader.IsValid)
                            {
                                splitTimestampRewriter.Start(packet, splitHeader, correction);
                                rewrittenTimestampCount += splitHeader.Length == 19 ? 2 : 1;
                            }
                            else if (!continuation)
                                rewrittenTimestampCount += TsTimestampFieldCodec.RewritePesTimestamps(packet, correction);
                            if (TsTimestampFieldCodec.RewritePcr(
                                    packet, correction))
                                rewrittenPcrCount++;
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
                splitTimestampRewriter.CompleteSegment();
                previousTimeline = timeline;
                previousCorrection = correction;
                expectedStartTime = range.EndTimeSeconds + correction / 90_000.0;
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
        long fileStart,
        long fileLength)
    {
        var ranges = new List<NormalizedRange>(source.Count);
        foreach (var item in source)
        {
            var start = item.StartPosition == 0 ? fileStart : item.StartPosition;
            if (start < 0 || start > fileLength)
                throw new TsClipMergeException(TsClipMergeErrorCode.InvalidRange);
            var end = item.EndPosition > 0 ? Math.Min(item.EndPosition, fileLength) : fileLength;
            // EOF 可能带有不完整尾包，仅这种终点按当前片段起点截去尾部。
            // 已标记的起止位置保持原值，不能按文件头的包相位重新取整。
            if (end == fileLength)
                end -= (end - start) % PacketSize;
            if (end <= start || item.EndTimeSeconds <= item.StartTimeSeconds)
                continue;
            if ((end - start) % PacketSize != 0)
                throw new TsClipMergeException(TsClipMergeErrorCode.InvalidRange);
            ranges.Add(new NormalizedRange(
                start, end, item.StartTimeSeconds, item.EndTimeSeconds));
        }

        ranges.Sort(static (left, right) => left.StartPosition.CompareTo(right.StartPosition));
        var merged = new List<NormalizedRange>(ranges.Count);
        foreach (var item in ranges)
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

        return merged;
    }

    private static async Task<TsClipMergeTimeline> ReadTimelineHeadAsync(
        FileStream source, NormalizedRange range, byte[] buffer, TsSplitPesHeaderReader splitHeaderReader, int videoPid,
        TsClipMergeTimeline[] previousAudio, TsClipMergeTimeline[] audioHeads,
        CancellationToken cancellationToken)
    {
        var timeline = new TsClipMergeTimeline();
        timeline.Reset(videoPid);
        for (var index = 0; index < audioHeads.Length; index++)
            audioHeads[index].Reset(previousAudio[index].Pid);
        source.Position = range.StartPosition;
        // 只读起点及其前导帧，复用复制缓冲；异常素材最多额外读取 64 MiB。
        var remaining = Math.Min(range.EndPosition - range.StartPosition,
            TsClipBoundaryResolver.MaximumScanBytes);
        while (remaining >= PacketSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, remaining);
            requested -= requested % PacketSize;
            await source.ReadExactlyAsync(buffer.AsMemory(0, requested), cancellationToken)
                .ConfigureAwait(false);
            for (var offset = 0; offset < requested; offset += PacketSize)
            {
                TsSplitPesHeader splitHeader = default;
                var chunkStart = source.Position - requested;
                if (TsSplitPesHeaderReader.NeedsRead(buffer.AsSpan(offset, PacketSize)))
                    splitHeader = await splitHeaderReader.ReadAsync(source, buffer, chunkStart,
                        requested, offset, Math.Min(range.EndPosition,
                            range.StartPosition + TsClipBoundaryResolver.MaximumScanBytes), cancellationToken).ConfigureAwait(false);
                var packet = buffer.AsSpan(offset, PacketSize);
                if (packet[0] != 0x47)
                    throw new TsClipMergeException(TsClipMergeErrorCode.SourceChanged,
                        source.Position - requested + offset);
                if (splitHeader.IsValid)
                    ObserveSplitHeader(splitHeader, ref timeline, audioHeads);
                else
                {
                    timeline.Observe(packet);
                    ObserveAudio(packet, audioHeads);
                }
                if (timeline.HasSuccessor && HasAudioHeads(previousAudio, audioHeads))
                    return timeline;
            }
            remaining -= requested;
        }
        if (!timeline.HasSuccessor && range.EndPosition - range.StartPosition > TsClipBoundaryResolver.MaximumScanBytes)
            throw new TsClipMergeException(TsClipMergeErrorCode.InvalidRange);
        return timeline;
    }

    private static long LaterCorrection(long current, long candidate)
    {
        const long wrap = 1L << 33;
        // 不同音轨在回绕两侧起步时可能相差一个完整周期，先对齐到视频的周期。
        var difference = ((candidate - current + wrap / 2) & (wrap - 1)) - wrap / 2;
        return current + Math.Max(0, difference);
    }

    private static void ObserveAudio(ReadOnlySpan<byte> packet, TsClipMergeTimeline[] timelines)
    {
        if ((packet[1] & 0x40) == 0) return;
        var pid = ((packet[1] & 31) << 8) | packet[2];
        for (var index = 0; index < timelines.Length; index++)
            if (timelines[index].Pid == pid)
                timelines[index].Observe(packet);
    }

    private static void ObserveSplitHeader(TsSplitPesHeader header, ref TsClipMergeTimeline video,
        TsClipMergeTimeline[] audio)
    {
        Span<byte> bytes = stackalloc byte[19];
        header.CopyTo(bytes);
        var data = bytes[..header.Length];
        video.ObserveHeader(header.Pid, data);
        for (var index = 0; index < audio.Length; index++)
            if (audio[index].Pid == header.Pid)
                audio[index].ObserveHeader(header.Pid, data);
    }

    private static bool HasAudioHeads(TsClipMergeTimeline[] previous, TsClipMergeTimeline[] heads)
    {
        for (var index = 0; index < previous.Length; index++)
            if (previous[index].HasPts && !heads[index].HasPts)
                return false;
        return true;
    }

    private static async Task<long> FindSyncOffsetAsync(
        FileStream source,
        CancellationToken cancellationToken)
    {
        const int bufferSize = TsScramblingProbe.ProbeBufferBytes;
        const int overlap = 204 * 4;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            source.Position = 0;
            var carry = 0;
            long bytesRead = 0;
            // 与预览能力探测使用相同的有界同步搜索，并保留非零起点。
            while (bytesRead < TsScramblingProbe.MaximumProbeBytes)
            {
                var requested = (int)Math.Min(bufferSize - carry,
                    TsScramblingProbe.MaximumProbeBytes - bytesRead);
                var read = await source.ReadAtLeastAsync(buffer.AsMemory(carry, requested),
                    requested, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                bytesRead += read;
                var length = carry + read;
                var layout = TsScramblingProbe.FindPacketLayout(buffer.AsSpan(0, length));
                if (layout.PacketSize != 0)
                    return layout.PacketSize == PacketSize ? bytesRead - length + layout.SyncOffset : -1;
                if (read < requested)
                    break;
                carry = Math.Min(length, overlap);
                buffer.AsSpan(length - carry, carry).CopyTo(buffer);
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
        double EndTimeSeconds);
}
