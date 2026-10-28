using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models.TransportStream.Processing;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Utils.Editing;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.Utils.TransportStream.Packets;

namespace TSCutter.GUI.Services.Editing;

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
        CancellationToken cancellationToken = default, bool addProgramTables = false,
        ExportTsCheckSession? checkSession = null)
    {
        var sourcePath = Path.GetFullPath(request.SourcePath);
        var outputPath = Path.GetFullPath(request.OutputPath);
        if (PathsEqual(sourcePath, outputPath))
            throw new TsClipMergeException(TsClipMergeErrorCode.OutputMatchesSource);

        var sourceInfo = new FileInfo(sourcePath);
        if (!sourceInfo.Exists)
            throw new TsClipMergeException(TsClipMergeErrorCode.SourceChanged);

        var stopwatch = Stopwatch.StartNew();
        await using var source = MediaFileUtil.OpenRead(
            sourcePath,
            PacketSize * ReadPacketCount, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var sourceLength = source.Length;
        // 已标记区间从其真实起点探测，保留损坏长前缀及非零包相位的支持。
        var probeStart = request.Ranges.Count == 0 ? 0 : Math.Max(0, request.Ranges.Min(item => item.StartPosition));
        var layout = await Task.Run(() => TsPacketLayout.Detect(source, cancellationToken, probeStart), cancellationToken).ConfigureAwait(false);
        if (layout.PacketSize == 0)
            throw new TsClipMergeException(TsClipMergeErrorCode.NoSync);
        layout.RequireSupported();
        var stride = layout.PacketSize;
        var ranges = NormalizeRanges(request.Ranges, sourceLength, layout);
        if (ranges.Count == 0)
            throw new TsClipMergeException(TsClipMergeErrorCode.InvalidRange);

        var tables = addProgramTables && request.ProgramId is null
            ? await TsExportProgramTableProbe.ReadTablesNearCutAsync(sourcePath, ranges[0].StartPosition, cancellationToken)
                .ConfigureAwait(false) : null;

        using var splitHeaderReader = new TsSplitPesHeaderReader();
        var splitTimestampRewriter = new TsSplitPesTimestampRewriter();
        var totalBytes = ranges.Sum(item => item.EndPosition - item.StartPosition);
        var buffer = ArrayPool<byte>.Shared.Rent(stride * ReadPacketCount);
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

        uint lastArrivalTimestamp = 0;
        var hasArrivalTimestamp = false;
        var outputCreated = false;
        long prefixBytes = 0;
        try
        {
            await using var programOutput = request.ProgramId is { } programId
                ? await TsProgramClipExportService.OpenWriterAsync(sourcePath, outputPath, programId, cancellationToken, checkSession)
                    .ConfigureAwait(false)
                : null;
            // 提供检测会话时用检测型流包装最终输出；补入的节目表前缀与全部数据块都会经过检测。
            await using var output = programOutput is null
                ? checkSession is null
                    ? (Stream)new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
                        buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan)
                    : new TsDetectingWriteStream(new FileStream(outputPath, FileMode.Create, FileAccess.Write,
                        FileShare.None, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan), checkSession)
                : null;
            outputCreated = true;
            programOutput?.WriteInitialTables();

            if (tables is not null)
            {
                uint arrivalHeader = 0;
                if (layout.IsM2ts)
                {
                    source.Position = ranges[0].StartPosition;
                    await source.ReadExactlyAsync(buffer.AsMemory(0, layout.PrefixSize), cancellationToken).ConfigureAwait(false);
                    arrivalHeader = BinaryPrimitives.ReadUInt32BigEndian(buffer);
                }
                // 新增表包与原始表包的 CC 连续衔接，音视频载荷保持原样。
                var prefix = TsProgramTablePacketizer.Create(tables, layout, arrivalHeader, lastPayloadContinuity);
                await output!.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
                prefixBytes = prefix.Length;
            }

            for (var rangeIndex = 0; rangeIndex < ranges.Count; rangeIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var range = ranges[rangeIndex];
                programOutput?.BeginSegment();
                var correction = (long)Math.Round((expectedStartTime - range.StartTimeSeconds) * 90_000.0);
                if (rangeIndex > 0 && previousTimeline.HasPts)
                {
                    var head = await ReadTimelineHeadAsync(source, range, buffer, splitHeaderReader,
                            previousTimeline.Pid, audioTimelines, audioHeads, cancellationToken, layout)
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
                long arrivalCorrection = 0;
                var firstArrival = true;

                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requested = (int)Math.Min(buffer.Length, remaining);
                    requested -= requested % stride;
                    if (requested <= 0)
                        throw new TsClipMergeException(TsClipMergeErrorCode.InvalidRange);

                    await source.ReadExactlyAsync(buffer.AsMemory(0, requested), cancellationToken)
                        .ConfigureAwait(false);
                    var chunkStart = range.StartPosition + (range.EndPosition - range.StartPosition - remaining);
                    for (var offset = 0; offset < requested; offset += stride)
                    {
                        TsSplitPesHeader splitHeader = default;
                        if ((rangeIndex + 1 < ranges.Count || correction != 0) &&
                            TsSplitPesHeaderReader.NeedsRead(layout.GetPacketBody(buffer.AsSpan(), offset)))
                            splitHeader = await splitHeaderReader.ReadAsync(source, buffer, chunkStart,
                                requested, offset, range.EndPosition, cancellationToken, layout).ConfigureAwait(false);
                        var packet = layout.GetPacketBody(buffer.AsSpan(), offset);
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
                            packet, rangeIndex + (tables is null ? 0 : 1), segmentContinuityOffsets, lastPayloadContinuity);
                        if (layout.IsM2ts)
                        {
                            var header = buffer.AsSpan(offset, layout.PrefixSize);
                            var arrival = BinaryPrimitives.ReadUInt32BigEndian(header);
                            const uint mask = (1u << 30) - 1;
                            // 后续片段的 ATS 接续上一包，并保留片段内间隔、30 位回绕和复制许可位。
                            if (firstArrival && hasArrivalTimestamp)
                                arrivalCorrection = (long)lastArrivalTimestamp + 1 - (arrival & mask);
                            firstArrival = false;
                            lastArrivalTimestamp = (uint)((arrival & mask) + arrivalCorrection) & mask;
                            BinaryPrimitives.WriteUInt32BigEndian(header, (arrival & ~mask) | lastArrivalTimestamp);
                            hasArrivalTimestamp = true;
                        }
                    }

                    if (programOutput is null)
                        await output!.WriteAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                    else
                    {
                        for (var offset = 0; offset < requested; offset += stride)
                            programOutput.WriteFilteredPacket(buffer.AsSpan(offset, stride), chunkStart + offset);
                        await programOutput.FlushIfNeededAsync(cancellationToken).ConfigureAwait(false);
                    }
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

            if (programOutput is null)
                await output!.FlushAsync(cancellationToken).ConfigureAwait(false);
            else
                await programOutput.CompleteAsync(cancellationToken).ConfigureAwait(false);
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
                OutputBytes = programOutput?.BytesWritten ?? totalBytes + prefixBytes,
                SegmentCount = ranges.Count,
                RewrittenPcrCount = rewrittenPcrCount,
                RewrittenTimestampCount = rewrittenTimestampCount,
                RewrittenContinuityCount = rewrittenContinuityCount,
                Elapsed = stopwatch.Elapsed
            };
        }
        catch (EndOfStreamException)
        {
            if (outputCreated)
                TryDelete(outputPath);
            throw new TsClipMergeException(TsClipMergeErrorCode.SourceChanged);
        }
        catch
        {
            if (outputCreated)
                TryDelete(outputPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static List<NormalizedRange> NormalizeRanges(
        IReadOnlyList<TsClipMergeRange> source,
        long fileLength, TsPacketLayout layout)
    {
        var fileStart = layout.PacketOffset;
        var stride = layout.PacketSize;
        var ranges = new List<NormalizedRange>(source.Count);
        foreach (var item in source)
        {
            var start = item.StartPosition == 0 ? fileStart : item.StartPosition;
            if (start < 0 || start > fileLength || layout.IsM2ts && !layout.IsAligned(start))
                throw new TsClipMergeException(TsClipMergeErrorCode.InvalidRange);
            var end = item.EndPosition > 0 ? Math.Min(item.EndPosition, fileLength) : fileLength;
            // EOF 可能带有不完整尾包，仅这种终点按当前片段起点截去尾部。
            // 已标记的起止位置保持原值，不能按文件头的包相位重新取整。
            if (end == fileLength)
                end -= (end - start) % stride;
            if (end <= start || item.EndTimeSeconds <= item.StartTimeSeconds)
                continue;
            if ((end - start) % stride != 0)
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
        CancellationToken cancellationToken, TsPacketLayout layout)
    {
        var stride = layout.PacketSize;
        var timeline = new TsClipMergeTimeline();
        timeline.Reset(videoPid);
        for (var index = 0; index < audioHeads.Length; index++)
            audioHeads[index].Reset(previousAudio[index].Pid);
        source.Position = range.StartPosition;
        // 只读起点及其前导帧，复用复制缓冲；异常素材最多额外读取 64 MiB。
        var remaining = Math.Min(range.EndPosition - range.StartPosition,
            TsClipBoundaryResolver.MaximumScanBytes);
        while (remaining >= stride)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, remaining);
            requested -= requested % stride;
            await source.ReadExactlyAsync(buffer.AsMemory(0, requested), cancellationToken)
                .ConfigureAwait(false);
            for (var offset = 0; offset < requested; offset += stride)
            {
                TsSplitPesHeader splitHeader = default;
                var chunkStart = source.Position - requested;
                if (TsSplitPesHeaderReader.NeedsRead(layout.GetPacketBody(buffer.AsSpan(), offset)))
                    splitHeader = await splitHeaderReader.ReadAsync(source, buffer, chunkStart,
                        requested, offset, Math.Min(range.EndPosition,
                            range.StartPosition + TsClipBoundaryResolver.MaximumScanBytes), cancellationToken, layout).ConfigureAwait(false);
                var packet = layout.GetPacketBody(buffer.AsSpan(), offset);
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

    internal readonly record struct NormalizedRange(
        long StartPosition,
        long EndPosition,
        double StartTimeSeconds,
        double EndTimeSeconds);
}
