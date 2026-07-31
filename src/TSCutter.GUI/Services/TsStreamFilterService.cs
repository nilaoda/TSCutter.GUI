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

public sealed class TsStreamFilterService
{
    private const int PacketSize = TsStreamAnalyzer.PacketSize;
    private const int ReadBufferSize = PacketSize * 32_768;

    public TsFilterPlan BuildPlan(
        TsCheckResult catalog,
        IReadOnlySet<int> selectedPids,
        bool includeServiceInformation = true)
    {
        var plan = new TsFilterPlan();
        plan.ExplicitPids.UnionWith(selectedPids);
        plan.EffectivePids.UnionWith(selectedPids);

        if (includeServiceInformation)
        {
            foreach (var pid in catalog.Pids.Keys)
            {
                if (!IsServiceInformationPid(pid))
                    continue;
                plan.ServiceInformationPids.Add(pid);
                plan.EffectivePids.Add(pid);
            }
        }

        foreach (var program in catalog.Programs.Values)
        {
            var hasSelectedStream = program.Streams.Keys.Any(selectedPids.Contains);
            if (!hasSelectedStream && !selectedPids.Contains(program.PmtPid))
                continue;

            plan.SelectedPrograms.Add(program.ProgramNumber);
            plan.EffectivePids.Add(program.PmtPid);
            if (program.PcrPid >= 0)
            {
                plan.EffectivePids.Add(program.PcrPid);
                if (!selectedPids.Contains(program.PcrPid))
                    plan.PcrOnlyPids.Add(program.PcrPid);
            }
        }

        // PAT 是兼容输出的入口；即使只选择私有 PID，也输出一个合法的空 PAT。
        plan.EffectivePids.Add(0);
        plan.PsiSections[0] = BuildPatSection(catalog, plan.SelectedPrograms);
        foreach (var programNumber in plan.SelectedPrograms)
        {
            var program = catalog.Programs[programNumber];
            plan.PsiSections[program.PmtPid] = BuildPmtSection(program, selectedPids);
        }

        foreach (var pid in plan.EffectivePids)
        {
            if (catalog.Pids.TryGetValue(pid, out var summary))
                plan.EstimatedPacketCount += summary.PacketCount;
        }
        return plan;
    }

    private static bool IsServiceInformationPid(int pid) => pid is
        0x0010 or // DVB NIT
        0x0011 or // DVB SDT / BAT
        0x0012 or // DVB EIT
        0x0013 or // DVB RST
        0x0014 or // DVB TDT / TOT
        0x1FFB;   // ATSC PSIP

    public Task<TsFilterResult> FilterAsync(
        string sourcePath,
        string outputPath,
        TsCheckResult catalog,
        TsFilterPlan plan,
        IProgress<TsFilterProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        FilterCoreAsync(sourcePath, outputPath, catalog, plan, null, null, null, null, null, null,
            progress, cancellationToken);

    internal Task<TsFilterResult> FilterWithInsertionsAsync(
        string sourcePath,
        string outputPath,
        TsCheckResult catalog,
        TsFilterPlan plan,
        IReadOnlyDictionary<long, List<TsPacketInsertion>> insertions,
        IReadOnlyDictionary<long, List<TsLargeGapInsertion>> largeGapInsertions,
        IReadOnlyList<TsPacketReplacement> replacements,
        IReadOnlySet<long> discardPacketOffsets,
        TsRepairOutputValidator outputValidator,
        TsTimelineRepairService.OutputPacketRewriter? timelineRewriter,
        IProgress<TsFilterProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        FilterCoreAsync(sourcePath, outputPath, catalog, plan, insertions, largeGapInsertions, replacements,
            discardPacketOffsets, outputValidator, timelineRewriter, progress, cancellationToken);

    private async Task<TsFilterResult> FilterCoreAsync(
        string sourcePath,
        string outputPath,
        TsCheckResult catalog,
        TsFilterPlan plan,
        IReadOnlyDictionary<long, List<TsPacketInsertion>>? insertions,
        IReadOnlyDictionary<long, List<TsLargeGapInsertion>>? largeGapInsertions,
        IReadOnlyList<TsPacketReplacement>? replacements,
        IReadOnlySet<long>? discardPacketOffsets,
        TsRepairOutputValidator? outputValidator,
        TsTimelineRepairService.OutputPacketRewriter? timelineRewriter,
        IProgress<TsFilterProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (Path.GetFullPath(sourcePath) == Path.GetFullPath(outputPath))
            throw new TsFilterException(TsFilterErrorCode.SameFile);

        var inputBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize + PacketSize);
        var outputBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize + PacketSize * 16);
        var stopwatch = Stopwatch.StartNew();
        var lastProgressTicks = 0L;
        var buffered = 0;
        var outputLength = 0;
        var bytesProcessed = Math.Max(0, catalog.SyncOffset);
        var bytesWritten = 0L;
        var packetsWritten = 0L;
        var psiContinuity = new int[8192];
        Array.Fill(psiContinuity, -1);
        var donorStreams = new Dictionary<string, FileStream>(StringComparer.Ordinal);
        var repairPacket = new byte[PacketSize];
        var replacementByStart = replacements?.ToDictionary(item => item.ReferenceStartOffset) ?? [];
        var pendingContinuity = new int[8192];
        Array.Fill(pendingContinuity, -1);
        var continuityOffsets = new int[8192];
        var hasContinuityOffset = new bool[8192];
        var nextServiceInformationContinuity = new int[8192];
        var hasServiceInformationContinuity = new bool[8192];
        var activeReplacements = new Dictionary<int, ActiveReplacementWriter>();
        var activeElementaryReplacements = new Dictionary<int, ActiveElementaryReplacementWriter>();
        var largeGapDiscardRanges = new Dictionary<int, (long StartOffset, long EndOffset)>();
        var largeGapBoundaryOffsets = largeGapInsertions?.Keys.Order().ToArray() ?? [];
        var nextWrittenContinuity = new int[8192];
        var hasWrittenContinuity = new bool[8192];
        var continuityTrackedLength = 0;

        void CompletePacket(
            long referenceFileOffset,
            bool applyPcrCorrection,
            bool applyTimestampCorrection)
        {
            // 辅助包已经在计划阶段落到修复后的参考时间轴，这里只参与最终 PCR
            // 连续性复核；保留的参考包才应用校正，二者共同维护同一个输出时钟状态。
            timelineRewriter?.ProcessPacket(
                outputBuffer.AsSpan(outputLength, PacketSize), referenceFileOffset,
                applyPcrCorrection, applyTimestampCorrection);
            outputLength += PacketSize;
            packetsWritten++;
        }

        try
        {
            await using var input = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                ReadBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(
                outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
                ReadBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            input.Position = Math.Max(0, catalog.SyncOffset);

            async ValueTask FlushAsync()
            {
                if (outputLength == 0)
                    return;
                TrackWrittenContinuity(
                    outputBuffer.AsSpan(continuityTrackedLength, outputLength - continuityTrackedLength),
                    nextWrittenContinuity, hasWrittenContinuity);
                await output.WriteAsync(outputBuffer.AsMemory(0, outputLength), cancellationToken)
                    .ConfigureAwait(false);
                // 对刚写出的最终字节同步做轻量复检，避免输出完成后再次读取整个文件。
                outputValidator?.ProcessPackets(outputBuffer.AsSpan(0, outputLength));
                bytesWritten += outputLength;
                outputLength = 0;
                continuityTrackedLength = 0;
            }

            async ValueTask EnsurePacketSpaceAsync()
            {
                if (outputLength + PacketSize > outputBuffer.Length)
                    await FlushAsync().ConfigureAwait(false);
            }

            async ValueTask WriteInsertionsAsync(
                IReadOnlyList<TsPacketInsertion> packetInsertions,
                long referenceFileOffset)
            {
                foreach (var insertion in packetInsertions)
                {
                    if (insertion.ElementaryPayload is { Length: > 0 } elementaryPayload)
                    {
                        var packetCount = insertion.SynthesizedPacketCount;
                        if (!TsElementaryRepairPacketizer.TryCreateLayout(
                                elementaryPayload.Length, insertion.PesBoundaries,
                                packetCount, out var layout))
                        {
                            throw new TsRepairException(TsRepairErrorCode.InvalidRepairData);
                        }

                        var synthesizedContinuityCounter = insertion.StartContinuityCounter;
                        foreach (var segment in layout)
                        {
                            var segmentPayloadOffset = 0;
                            for (var packetIndex = 0; packetIndex < segment.PacketCount; packetIndex++)
                            {
                                await EnsurePacketSpaceAsync().ConfigureAwait(false);
                                var packet = outputBuffer.AsSpan(outputLength, PacketSize);
                                packet.Fill(0xFF);
                                packet[0] = 0x47;
                                var payloadStart = packetIndex == 0 && segment.PesHeader is not null;
                                packet[1] = (byte)((payloadStart ? 0x40 : 0) |
                                                  ((insertion.TargetPid >> 8) & 0x1F));
                                packet[2] = (byte)insertion.TargetPid;

                                var copyLength = TsElementaryRepairPacketizer.GetNextPayloadLength(
                                    segment, segmentPayloadOffset, packetIndex);
                                if (copyLength <= 0)
                                    throw new TsRepairException(TsRepairErrorCode.InvalidRepairData);
                                var targetPayloadOffset = 4;
                                if (copyLength == 184)
                                {
                                    packet[3] = (byte)(0x10 | synthesizedContinuityCounter);
                                }
                                else
                                {
                                    // 使用 adaptation stuffing 将同一段 PES/ES 精确摊入参考缺失的包数；
                                    // 每个包仍至少携带一个字节，因此连续计数器可以逐包递增。
                                    packet[3] = (byte)(0x30 | synthesizedContinuityCounter);
                                    var adaptationLength = 183 - copyLength;
                                    packet[4] = (byte)adaptationLength;
                                    if (adaptationLength > 0)
                                        packet[5] = 0;
                                    targetPayloadOffset = 5 + adaptationLength;
                                }
                                TsElementaryRepairPacketizer.CopyPayload(
                                    elementaryPayload, segment, segmentPayloadOffset,
                                    packet.Slice(targetPayloadOffset, copyLength));
                                if (payloadStart)
                                    TsTimestampFieldCodec.RewritePesTimestamps(
                                        packet, insertion.TimestampOffset90k,
                                        preserveFirstMarkerBit: false);
                                segmentPayloadOffset += copyLength;
                                synthesizedContinuityCounter =
                                    (synthesizedContinuityCounter + 1) & 0x0F;
                                CompletePacket(referenceFileOffset, false, false);
                            }
                        }
                        continue;
                    }

                    if (!donorStreams.TryGetValue(insertion.SourcePath, out var donor))
                    {
                        donor = new FileStream(
                            insertion.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                            PacketSize * 256, FileOptions.Asynchronous | FileOptions.RandomAccess);
                        donorStreams[insertion.SourcePath] = donor;
                    }
                    var continuityCounter = insertion.StartContinuityCounter;
                    foreach (var sourceOffset in insertion.SourcePacketOffsets)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        donor.Position = sourceOffset;
                        await donor.ReadExactlyAsync(repairPacket.AsMemory(0, PacketSize), cancellationToken)
                            .ConfigureAwait(false);
                        var sourcePid = ((repairPacket[1] & 0x1F) << 8) | repairPacket[2];
                        if (repairPacket[0] != 0x47 || sourcePid != insertion.SourcePid ||
                            (repairPacket[1] & 0x80) != 0)
                        {
                            throw new TsRepairException(TsRepairErrorCode.SourceChanged);
                        }

                        await EnsurePacketSpaceAsync().ConfigureAwait(false);
                        var packet = outputBuffer.AsSpan(outputLength, PacketSize);
                        repairPacket.CopyTo(packet);
                        // PID 与 CC 按目标流重写；负载、PUSI 和 adaptation field 保持来源原值，
                        // PCR、PTS/DTS 仅应用匹配阶段已经确认的固定时间偏移。
                        packet[1] = (byte)((packet[1] & 0x60) | ((insertion.TargetPid >> 8) & 0x1F));
                        packet[2] = (byte)insertion.TargetPid;
                        packet[3] = (byte)((packet[3] & 0xF0) | continuityCounter);
                        if (((packet[3] >> 4) & 0x01) != 0)
                            continuityCounter = (continuityCounter + 1) & 0x0F;
                        if ((packet[1] & 0x40) != 0)
                            TsTimestampFieldCodec.RewritePesTimestamps(
                                packet, insertion.TimestampOffset90k,
                                preserveFirstMarkerBit: false);
                        TsTimestampFieldCodec.RewritePcr(
                            packet, insertion.PcrTimestampOffset90k);
                        CompletePacket(referenceFileOffset, true, false);
                    }
                }
            }

            async ValueTask WriteLargeGapInsertionsAsync(
                IReadOnlyList<TsLargeGapInsertion> rangeInsertions,
                long referenceFileOffset)
            {
                // 计划阶段只能从缺口后的参考包反推 CC；不同录制工具的封包数量可能
                // 模 16 不一致。插入前以实际已写出的最后一包为准，避免在接缝起点造出 miss。
                TrackWrittenContinuity(
                    outputBuffer.AsSpan(continuityTrackedLength, outputLength - continuityTrackedLength),
                    nextWrittenContinuity, hasWrittenContinuity);
                continuityTrackedLength = outputLength;

                // 整段媒体缺失时不复制辅助源的业务表，以免把其他录制源的节目元数据混入输出。
                // 缺口结束后业务表的首个包仍需接上参考源缺口前的 CC 序列。
                foreach (var pid in plan.ServiceInformationPids)
                {
                    if (hasServiceInformationContinuity[pid])
                        pendingContinuity[pid] = nextServiceInformationContinuity[pid];
                }

                foreach (var insertion in rangeInsertions)
                {
                    if (!donorStreams.TryGetValue(insertion.SourcePath, out var donor))
                    {
                        donor = new FileStream(
                            insertion.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                            ReadBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        donorStreams[insertion.SourcePath] = donor;
                    }

                    var tracks = insertion.Tracks.ToDictionary(item => item.SourcePid);
                    var continuityCounters = insertion.Tracks.ToDictionary(
                        item => item.TargetPid,
                        item => hasWrittenContinuity[item.TargetPid]
                            ? nextWrittenContinuity[item.TargetPid]
                            : item.StartContinuityCounter);
                    var packetCounts = insertion.Tracks.ToDictionary(item => item.TargetPid, _ => 0);
                    var payloadPacketCounts = insertion.Tracks.ToDictionary(item => item.TargetPid, _ => 0);
                    donor.Position = insertion.SourceStartOffset;
                    while (donor.Position < insertion.SourceEndOffset)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var sourceOffset = donor.Position;
                        await donor.ReadExactlyAsync(
                                repairPacket.AsMemory(0, PacketSize), cancellationToken)
                            .ConfigureAwait(false);
                        if (repairPacket[0] != 0x47)
                            throw new TsRepairException(TsRepairErrorCode.SourceChanged);
                        var sourcePid = ((repairPacket[1] & 0x1F) << 8) | repairPacket[2];
                        if (!tracks.TryGetValue(sourcePid, out var track) ||
                            sourceOffset < track.SourceStartOffset ||
                            sourceOffset >= track.SourceEndOffset)
                        {
                            continue;
                        }
                        if ((repairPacket[1] & 0x80) != 0)
                            throw new TsRepairException(TsRepairErrorCode.SourceChanged);

                        await EnsurePacketSpaceAsync().ConfigureAwait(false);
                        var packet = outputBuffer.AsSpan(outputLength, PacketSize);
                        repairPacket.CopyTo(packet);
                        packet[1] = (byte)((packet[1] & 0x60) | ((track.TargetPid >> 8) & 0x1F));
                        packet[2] = (byte)track.TargetPid;
                        var continuityCounter = continuityCounters[track.TargetPid];
                        packet[3] = (byte)((packet[3] & 0xF0) | continuityCounter);
                        var hasPayload = (((packet[3] >> 4) & 0x03) & 0x01) != 0;
                        if (hasPayload)
                        {
                            continuityCounters[track.TargetPid] = (continuityCounter + 1) & 0x0F;
                            payloadPacketCounts[track.TargetPid]++;
                        }
                        if ((packet[1] & 0x40) != 0)
                            TsTimestampFieldCodec.RewritePesTimestamps(
                                packet, track.TimestampOffset90k,
                                preserveFirstMarkerBit: false);
                        TsTimestampFieldCodec.RewritePcr(
                            packet, track.PcrTimestampOffset90k);
                        packetCounts[track.TargetPid]++;
                        CompletePacket(referenceFileOffset, true, false);
                    }

                    foreach (var track in insertion.Tracks)
                    {
                        if (packetCounts[track.TargetPid] != track.SourcePacketCount ||
                            payloadPacketCounts[track.TargetPid] != track.SourcePayloadPacketCount)
                        {
                            throw new TsRepairException(TsRepairErrorCode.SourceChanged);
                        }
                        // 大段补入结束后沿用实际写出的下一个 CC；后续参考包只应用固定偏移，
                        // 不会顺手重编号并掩盖参考源中原本存在的其他 continuity 异常。
                        pendingContinuity[track.TargetPid] = continuityCounters[track.TargetPid];
                        if (largeGapDiscardRanges.TryGetValue(track.TargetPid, out var existingRange))
                        {
                            largeGapDiscardRanges[track.TargetPid] = (
                                Math.Min(existingRange.StartOffset, track.ReferenceDiscardStartOffset),
                                Math.Max(existingRange.EndOffset, track.ReferenceDiscardEndOffset));
                        }
                        else
                        {
                            largeGapDiscardRanges[track.TargetPid] = (
                                track.ReferenceDiscardStartOffset,
                                track.ReferenceDiscardEndOffset);
                        }
                    }
                }
            }

            async ValueTask<byte[]> LoadReplacementPacketsAsync(TsPacketReplacement replacement)
            {
                if (!donorStreams.TryGetValue(replacement.SourcePath, out var donor))
                {
                    donor = new FileStream(
                        replacement.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        PacketSize * 256, FileOptions.Asynchronous | FileOptions.RandomAccess);
                    donorStreams[replacement.SourcePath] = donor;
                }

                donor.Position = replacement.SourceStartOffset;
                var packets = new byte[replacement.PacketCount * PacketSize];
                var copiedPackets = 0;
                while (donor.Position < replacement.SourceEndOffset)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await donor.ReadExactlyAsync(repairPacket.AsMemory(0, PacketSize), cancellationToken)
                        .ConfigureAwait(false);
                    var sourcePid = ((repairPacket[1] & 0x1F) << 8) | repairPacket[2];
                    if (repairPacket[0] != 0x47)
                    {
                        throw new TsRepairException(TsRepairErrorCode.SourceChanged);
                    }
                    if (sourcePid != replacement.SourcePid)
                        continue;
                    if ((repairPacket[1] & 0x80) != 0)
                        throw new TsRepairException(TsRepairErrorCode.SourceChanged);

                    var packet = packets.AsSpan(copiedPackets * PacketSize, PacketSize);
                    repairPacket.CopyTo(packet);
                    packet[1] = (byte)((packet[1] & 0x60) | ((replacement.TargetPid >> 8) & 0x1F));
                    packet[2] = (byte)replacement.TargetPid;
                    if ((packet[1] & 0x40) != 0)
                        TsTimestampFieldCodec.RewritePesTimestamps(
                            packet, replacement.TimestampOffset90k,
                            preserveFirstMarkerBit: false);
                    TsTimestampFieldCodec.RewritePcr(
                        packet, replacement.PcrTimestampOffset90k);
                    copiedPackets++;
                }
                if (copiedPackets != replacement.PacketCount)
                    throw new TsRepairException(TsRepairErrorCode.SourceChanged);
                return packets;
            }

            async ValueTask<byte[]> LoadReplacementElementaryPayloadAsync(TsPacketReplacement replacement)
            {
                if (!donorStreams.TryGetValue(replacement.SourcePath, out var donor))
                {
                    donor = new FileStream(
                        replacement.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        PacketSize * 256, FileOptions.Asynchronous | FileOptions.RandomAccess);
                    donorStreams[replacement.SourcePath] = donor;
                }

                var elementary = new byte[replacement.ElementaryLength];
                var elementaryOffset = 0;
                donor.Position = replacement.SourceStartOffset;
                while (donor.Position < replacement.SourceEndOffset)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await donor.ReadExactlyAsync(repairPacket.AsMemory(0, PacketSize), cancellationToken)
                        .ConfigureAwait(false);
                    if (repairPacket[0] != 0x47)
                        throw new TsRepairException(TsRepairErrorCode.SourceChanged);
                    var sourcePid = ((repairPacket[1] & 0x1F) << 8) | repairPacket[2];
                    if (sourcePid != replacement.SourcePid)
                        continue;
                    if ((repairPacket[1] & 0x80) != 0)
                        throw new TsRepairException(TsRepairErrorCode.SourceChanged);
                    if (!TryGetElementaryPayload(repairPacket, out var payload))
                        continue;
                    if (elementaryOffset + payload.Length > elementary.Length)
                        throw new TsRepairException(TsRepairErrorCode.SourceChanged);
                    payload.CopyTo(elementary.AsSpan(elementaryOffset));
                    elementaryOffset += payload.Length;
                }
                if (elementaryOffset != elementary.Length)
                    throw new TsRepairException(TsRepairErrorCode.SourceChanged);
                return elementary;
            }

            async ValueTask WriteReplacementPacketAsync(
                ActiveReplacementWriter writer,
                long referenceFileOffset)
            {
                await EnsurePacketSpaceAsync().ConfigureAwait(false);
                var packet = outputBuffer.AsSpan(outputLength, PacketSize);
                writer.CopyNextPacket(packet);
                CompletePacket(referenceFileOffset, true, false);
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await input.ReadAsync(inputBuffer.AsMemory(buffered, ReadBufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;

                buffered += read;
                var completeLength = buffered / PacketSize * PacketSize;
                for (var offset = 0; offset < completeLength; offset += PacketSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (inputBuffer[offset] != 0x47)
                        throw new TsFilterException(TsFilterErrorCode.SyncLost, bytesProcessed + offset);

                    var absoluteOffset = bytesProcessed + offset;
                    var pid = ((inputBuffer[offset + 1] & 0x1F) << 8) | inputBuffer[offset + 2];
                    if (activeReplacements.TryGetValue(pid, out var endingWriter) &&
                        absoluteOffset >= endingWriter.Replacement.ReferenceEndOffset)
                    {
                        while (endingWriter.PacketsWritten < endingWriter.Replacement.PacketCount)
                            await WriteReplacementPacketAsync(endingWriter, absoluteOffset)
                                .ConfigureAwait(false);
                        pendingContinuity[pid] = endingWriter.NextContinuityCounter;
                        activeReplacements.Remove(pid);
                    }
                    if (activeElementaryReplacements.TryGetValue(pid, out var endingElementaryWriter) &&
                        absoluteOffset >= endingElementaryWriter.Replacement.ReferenceEndOffset)
                    {
                        if (!endingElementaryWriter.IsComplete)
                            throw new TsRepairException(TsRepairErrorCode.ReferenceChanged);
                        activeElementaryReplacements.Remove(pid);
                    }
                    // 区域结束点与下一处缺口可能位于同一个参考包。必须先写完前一区域，
                    // 再插入属于当前包之前的缺失内容，才能保持媒体字节的先后顺序。
                    if (largeGapInsertions is not null &&
                        largeGapInsertions.TryGetValue(absoluteOffset, out var rangeInsertions))
                    {
                        await WriteLargeGapInsertionsAsync(rangeInsertions, absoluteOffset)
                            .ConfigureAwait(false);
                    }
                    if (insertions is not null &&
                        insertions.TryGetValue(absoluteOffset, out var packetInsertions))
                    {
                        await WriteInsertionsAsync(packetInsertions, absoluteOffset)
                            .ConfigureAwait(false);
                    }

                    // TEI 包仍占据参考文件的物理包位。候选补包已在同一位置写入后，
                    // 必须跳过这些已确认损坏的原包，否则输出仍会携带坏负载和 TEI 标志。
                    if (discardPacketOffsets?.Contains(absoluteOffset) == true)
                        continue;
                    if (largeGapDiscardRanges.TryGetValue(pid, out var discardRange) &&
                        absoluteOffset >= discardRange.StartOffset)
                    {
                        if (absoluteOffset < discardRange.EndOffset)
                            continue;
                        largeGapDiscardRanges.Remove(pid);
                    }

                    // 先结束前一区域，再启动同一位置开始的新区域，避免相邻替换互相覆盖状态。
                    if (replacementByStart.TryGetValue(absoluteOffset, out var replacement))
                    {
                        if (replacement.ElementaryPayloadOnly)
                        {
                            var elementary = await LoadReplacementElementaryPayloadAsync(replacement)
                                .ConfigureAwait(false);
                            activeElementaryReplacements[pid] =
                                new ActiveElementaryReplacementWriter(replacement, elementary);
                        }
                        else
                        {
                            var packets = await LoadReplacementPacketsAsync(replacement).ConfigureAwait(false);
                            var startContinuityCounter = pendingContinuity[pid] >= 0
                                ? pendingContinuity[pid]
                                : hasContinuityOffset[pid]
                                    ? (replacement.StartContinuityCounter + continuityOffsets[pid]) & 0x0F
                                    : replacement.StartContinuityCounter;
                            // 辅助源整段数据先载入有界缓冲，再按参考 PID 原包位比例输出，避免将数秒
                            // 音视频数据一次性挤在替换起点，保持原复用流的交织节奏。
                            activeReplacements[pid] = new ActiveReplacementWriter(
                                replacement, packets, startContinuityCounter);
                        }
                    }
                    // 活动字典只包含当前 PID 正在覆盖的区间，热路径无需让每个 TS 包遍历全部替换范围。
                    if (activeReplacements.TryGetValue(pid, out var writer))
                    {
                        writer.ReferencePacketsSeen++;
                        // 替换区间的 TS 封包数量可能因录制工具不同而变化；按累计比例决定此包位
                        // 应输出到第几个辅助包，既覆盖全部内容，也让码率分布贴近参考流。
                        var targetWritten = (int)Math.Ceiling(
                            writer.ReferencePacketsSeen * writer.Replacement.PacketCount /
                            (double)writer.Replacement.ReferencePacketCount);
                        targetWritten = Math.Min(targetWritten, writer.Replacement.PacketCount);
                        while (writer.PacketsWritten < targetWritten)
                            await WriteReplacementPacketAsync(writer, absoluteOffset)
                                .ConfigureAwait(false);
                        continue;
                    }
                    if (activeElementaryReplacements.TryGetValue(pid, out var elementaryWriter))
                    {
                        // ES 级替换保留参考包的 TS 头、adaptation field、PCR、PES 头和时间戳，
                        // 只覆盖经前后锚点确认的编码负载，因此不会改变原复用节奏与时钟关系。
                        await EnsurePacketSpaceAsync().ConfigureAwait(false);
                        var packet = outputBuffer.AsSpan(outputLength, PacketSize);
                        inputBuffer.AsSpan(offset, PacketSize).CopyTo(packet);
                        elementaryWriter.ReplacePayload(packet);
                        CompletePacket(absoluteOffset, true, true);
                        continue;
                    }
                    var payloadStart = (inputBuffer[offset + 1] & 0x40) != 0;
                    if (plan.PsiSections.TryGetValue(pid, out var section))
                    {
                        // 丢弃原始 PSI 的所有分片，只在每次 PUSI 位置注入筛选后的完整 section。
                        if (!payloadStart)
                            continue;

                        if (psiContinuity[pid] < 0)
                            psiContinuity[pid] = inputBuffer[offset + 3] & 0x0F;
                        var sectionOffset = 0;
                        var firstPacket = true;
                        while (sectionOffset < section.Length)
                        {
                            await EnsurePacketSpaceAsync().ConfigureAwait(false);
                            var packet = outputBuffer.AsSpan(outputLength, PacketSize);
                            packet.Fill(0xFF);
                            packet[0] = 0x47;
                            packet[1] = (byte)((firstPacket ? 0x40 : 0) | ((pid >> 8) & 0x1F));
                            packet[2] = (byte)pid;
                            packet[3] = (byte)(0x10 | psiContinuity[pid]);
                            psiContinuity[pid] = (psiContinuity[pid] + 1) & 0x0F;
                            var payloadOffset = 4;
                            if (firstPacket)
                                packet[payloadOffset++] = 0;
                            var copyLength = Math.Min(PacketSize - payloadOffset, section.Length - sectionOffset);
                            section.AsSpan(sectionOffset, copyLength).CopyTo(packet[payloadOffset..]);
                            sectionOffset += copyLength;
                            CompletePacket(absoluteOffset, false, false);
                            firstPacket = false;
                        }
                    }
                    else if (plan.PcrOnlyPids.Contains(pid))
                    {
                        await EnsurePacketSpaceAsync().ConfigureAwait(false);
                        if (!TryWritePcrOnlyPacket(inputBuffer.AsSpan(offset, PacketSize), outputBuffer, ref outputLength))
                            continue;
                        timelineRewriter?.ProcessPacket(
                            outputBuffer.AsSpan(outputLength - PacketSize, PacketSize), absoluteOffset,
                            applyPcrCorrection: true, applyTimestampCorrection: true);
                        packetsWritten++;
                    }
                    else if (plan.EffectivePids.Contains(pid))
                    {
                        await EnsurePacketSpaceAsync().ConfigureAwait(false);
                        var packet = outputBuffer.AsSpan(outputLength, PacketSize);
                        inputBuffer.AsSpan(offset, PacketSize).CopyTo(packet);
                        if (plan.ServiceInformationPids.Contains(pid) &&
                            HasUnexpectedServiceInformationContinuity(
                                packet, pid, nextServiceInformationContinuity,
                                hasServiceInformationContinuity) &&
                            IsNearLargeGapBoundary(absoluteOffset, largeGapBoundaryOffsets))
                        {
                            // SI 包可能在复用顺序上位于首个媒体包之前。只在选中缺口附近且
                            // 实际观察到 CC 跳变时校正，避免把其他位置的真实 SI 错误抹掉。
                            pendingContinuity[pid] = nextServiceInformationContinuity[pid];
                        }
                        if (pendingContinuity[pid] >= 0 || hasContinuityOffset[pid])
                        {
                            RewriteContinuityPreservingGaps(
                                packet, pid, pendingContinuity, continuityOffsets, hasContinuityOffset);
                        }
                        if (plan.ServiceInformationPids.Contains(pid))
                        {
                            TrackServiceInformationContinuity(
                                packet, pid, nextServiceInformationContinuity,
                                hasServiceInformationContinuity);
                        }
                        CompletePacket(absoluteOffset, true, true);
                    }
                }

                bytesProcessed += completeLength;
                buffered -= completeLength;
                if (buffered > 0)
                    inputBuffer.AsSpan(completeLength, buffered).CopyTo(inputBuffer);

                var elapsedTicks = stopwatch.ElapsedTicks;
                if (progress is not null && elapsedTicks - lastProgressTicks >= Stopwatch.Frequency / 10)
                {
                    lastProgressTicks = elapsedTicks;
                    progress.Report(new TsFilterProgress(
                        Math.Min(bytesProcessed, catalog.FileSize), catalog.FileSize,
                        bytesWritten + outputLength, packetsWritten,
                        Math.Max(0, bytesProcessed - catalog.SyncOffset) / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds),
                        stopwatch.Elapsed));
                }
            }

            if (activeReplacements.Count > 0 || activeElementaryReplacements.Count > 0)
                throw new TsRepairException(TsRepairErrorCode.ReferenceChanged);

            await FlushAsync().ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }
        finally
        {
            foreach (var donor in donorStreams.Values)
                await donor.DisposeAsync().ConfigureAwait(false);
            ArrayPool<byte>.Shared.Return(inputBuffer);
            ArrayPool<byte>.Shared.Return(outputBuffer);
        }

        var result = new TsFilterResult
        {
            BytesProcessed = Math.Min(bytesProcessed, catalog.FileSize),
            BytesWritten = bytesWritten,
            PacketsWritten = packetsWritten,
            Elapsed = stopwatch.Elapsed
        };
        progress?.Report(new TsFilterProgress(
            result.BytesProcessed, catalog.FileSize, result.BytesWritten, result.PacketsWritten,
            Math.Max(0, result.BytesProcessed - catalog.SyncOffset) / Math.Max(0.001, result.Elapsed.TotalSeconds),
            result.Elapsed));
        return result;
    }

    private sealed class ActiveReplacementWriter(
        TsPacketReplacement replacement, byte[] packets, int startContinuityCounter)
    {
        public TsPacketReplacement Replacement { get; } = replacement;
        public int ReferencePacketsSeen { get; set; }
        public int PacketsWritten { get; private set; }
        public int NextContinuityCounter { get; private set; } = startContinuityCounter;

        public void CopyNextPacket(Span<byte> destination)
        {
            packets.AsSpan(PacketsWritten * PacketSize, PacketSize).CopyTo(destination);
            var hasPayload = ((((destination[3] >> 4) & 0x03) & 0x01) != 0);
            destination[3] = (byte)((destination[3] & 0xF0) | NextContinuityCounter);
            if (hasPayload)
                NextContinuityCounter = (NextContinuityCounter + 1) & 0x0F;
            PacketsWritten++;
        }
    }

    private sealed class ActiveElementaryReplacementWriter(
        TsPacketReplacement replacement, byte[] elementaryPayload)
    {
        public TsPacketReplacement Replacement { get; } = replacement;
        private int Offset { get; set; }
        public bool IsComplete => Offset == elementaryPayload.Length;

        public void ReplacePayload(Span<byte> packet)
        {
            if (!TryGetElementaryPayload(packet, out var payload))
                return;
            if (Offset + payload.Length > elementaryPayload.Length)
                throw new TsRepairException(TsRepairErrorCode.ReferenceChanged);
            elementaryPayload.AsSpan(Offset, payload.Length).CopyTo(payload);
            Offset += payload.Length;
        }
    }

    private static bool TryGetElementaryPayload(Span<byte> packet, out Span<byte> payload)
    {
        payload = default;
        var adaptationControl = (packet[3] >> 4) & 0x03;
        if ((adaptationControl & 0x01) == 0)
            return false;
        var payloadOffset = 4;
        if ((adaptationControl & 0x02) != 0)
            payloadOffset += packet[4] + 1;
        if (payloadOffset >= PacketSize)
            return false;
        if ((packet[1] & 0x40) != 0)
        {
            if (payloadOffset + 9 > PacketSize || packet[payloadOffset] != 0 ||
                packet[payloadOffset + 1] != 0 || packet[payloadOffset + 2] != 1)
            {
                return false;
            }
            payloadOffset += 9 + packet[payloadOffset + 8];
            if (payloadOffset > PacketSize)
                return false;
        }
        payload = packet[payloadOffset..];
        return !payload.IsEmpty;
    }

    private static void RewriteContinuityPreservingGaps(
        Span<byte> packet,
        int pid,
        int[] pendingContinuity,
        int[] continuityOffsets,
        bool[] hasContinuityOffset)
    {
        var adaptationControl = (packet[3] >> 4) & 0x03;
        var hasPayload = (adaptationControl & 0x01) != 0;
        var originalCounter = packet[3] & 0x0F;
        if (hasPayload)
        {
            if (pendingContinuity[pid] >= 0)
            {
                // 区域包数变化后只计算一个固定 CC 偏移。后续仍以参考包原始 CC 为基础，
                // 因而真实的重复包或跳号不会被顺序重编号掩盖。
                continuityOffsets[pid] =
                    (pendingContinuity[pid] - originalCounter + 16) & 0x0F;
                hasContinuityOffset[pid] = true;
                pendingContinuity[pid] = -1;
            }
            var counter = hasContinuityOffset[pid]
                ? (originalCounter + continuityOffsets[pid]) & 0x0F
                : originalCounter;
            packet[3] = (byte)((packet[3] & 0xF0) | counter);
        }
        else
        {
            var counter = pendingContinuity[pid] >= 0
                ? (pendingContinuity[pid] + 15) & 0x0F
                : hasContinuityOffset[pid]
                    ? (originalCounter + continuityOffsets[pid]) & 0x0F
                    : originalCounter;
            packet[3] = (byte)((packet[3] & 0xF0) | counter);
        }
    }

    private static void TrackServiceInformationContinuity(
        ReadOnlySpan<byte> packet,
        int pid,
        int[] nextContinuity,
        bool[] hasContinuity)
    {
        if ((packet[1] & 0x80) != 0)
        {
            hasContinuity[pid] = false;
            return;
        }

        var adaptationControl = (packet[3] >> 4) & 0x03;
        if ((adaptationControl & 0x02) != 0 && packet[4] > 0 &&
            (packet[5] & 0x80) != 0)
        {
            hasContinuity[pid] = false;
        }
        if ((adaptationControl & 0x01) == 0)
            return;

        nextContinuity[pid] = ((packet[3] & 0x0F) + 1) & 0x0F;
        hasContinuity[pid] = true;
    }

    private static void TrackWrittenContinuity(
        ReadOnlySpan<byte> packets,
        int[] nextContinuity,
        bool[] hasContinuity)
    {
        for (var offset = 0; offset + PacketSize <= packets.Length; offset += PacketSize)
        {
            var packet = packets.Slice(offset, PacketSize);
            if (packet[0] != 0x47)
                continue;
            var pid = ((packet[1] & 0x1F) << 8) | packet[2];
            var adaptationControl = (packet[3] >> 4) & 0x03;
            if ((packet[1] & 0x80) != 0 ||
                (adaptationControl & 0x02) != 0 && packet[4] > 0 &&
                (packet[5] & 0x80) != 0)
            {
                hasContinuity[pid] = false;
                continue;
            }
            if ((adaptationControl & 0x01) == 0)
                continue;

            nextContinuity[pid] = ((packet[3] & 0x0F) + 1) & 0x0F;
            hasContinuity[pid] = true;
        }
    }

    private static bool HasUnexpectedServiceInformationContinuity(
        ReadOnlySpan<byte> packet,
        int pid,
        int[] nextContinuity,
        bool[] hasContinuity)
    {
        var adaptationControl = (packet[3] >> 4) & 0x03;
        return (packet[1] & 0x80) == 0 &&
               (adaptationControl & 0x01) != 0 &&
               hasContinuity[pid] &&
               (packet[3] & 0x0F) != nextContinuity[pid] &&
               (packet[3] & 0x0F) != ((nextContinuity[pid] + 15) & 0x0F);
    }

    private static bool IsNearLargeGapBoundary(
        long fileOffset,
        long[] boundaries)
    {
        if (boundaries.Length == 0)
            return false;
        // SI 包数量很多而缺口数可能达到数千；对有序边界二分，仅检查插入点两侧，
        // 避免输出热路径退化为“SI 包数 × 缺口数”的线性扫描。
        var index = Array.BinarySearch(boundaries, fileOffset);
        if (index >= 0)
            return true;
        index = ~index;
        var tolerance = ReadBufferSize * 4L;
        return index < boundaries.Length && boundaries[index] - fileOffset <= tolerance ||
               index > 0 && fileOffset - boundaries[index - 1] <= tolerance;
    }

    internal static byte[] BuildPatSection(TsCheckResult catalog, HashSet<int> selectedPrograms)
    {
        var programs = catalog.Programs.Values
            .Where(item => selectedPrograms.Contains(item.ProgramNumber))
            .OrderBy(item => item.ProgramNumber)
            .Select(item => new TsPsiSectionBuilder.PatProgram(item.ProgramNumber, item.PmtPid))
            .ToArray();
        return TsPsiSectionBuilder.BuildPat(
            catalog.TransportStreamId,
            (byte)((catalog.PatVersion + 1) & 0x1F),
            programs);
    }

    private static bool TryWritePcrOnlyPacket(ReadOnlySpan<byte> source, byte[] outputBuffer, ref int outputLength)
    {
        var adaptationControl = (source[3] >> 4) & 0x03;
        var adaptationLength = source[4];
        // TEI 包中的 PCR 同样不可信；损坏包还可能声明超过 188 字节边界的
        // adaptation field。过滤时应直接丢弃，不能让纯音频输出携带坏时钟或切片越界。
        if ((source[1] & 0x80) != 0 ||
            (adaptationControl & 0x02) == 0 || adaptationLength is < 7 or > 183 ||
            5 + adaptationLength > source.Length || (source[5] & 0x10) == 0)
            return false;

        // PCR 可能与未选中的视频 payload 共用一个 PID。把该包改成仅含 adaptation field 的
        // PCR 包，既保留节目时钟，又不会把未选择的媒体数据带入输出文件。
        var packet = outputBuffer.AsSpan(outputLength, PacketSize);
        packet.Fill(0xFF);
        packet[0] = 0x47;
        packet[1] = (byte)(source[1] & ~0x40);
        packet[2] = source[2];
        packet[3] = (byte)(0x20 | (source[3] & 0x0F));
        packet[4] = 183;
        source.Slice(5, adaptationLength).CopyTo(packet[5..]);
        outputLength += PacketSize;
        return true;
    }

    internal static byte[] BuildPmtSection(TsCheckProgramSummary program, IReadOnlySet<int> selectedPids)
    {
        var streams = program.StreamDefinitions
            .Where(item => selectedPids.Contains(item.Key))
            .OrderBy(item => item.Key)
            .Select(item => new TsPsiSectionBuilder.PmtStream(item.Key, item.Value))
            .ToArray();
        return TsPsiSectionBuilder.BuildPmt(
            program.ProgramNumber,
            (byte)((program.PmtVersion + 1) & 0x1F),
            program.PcrPid >= 0 ? program.PcrPid : 0x1FFF,
            program.ProgramDescriptors,
            streams);
    }

    internal static void WriteCrc(Span<byte> section) => TsPsiSectionBuilder.WriteCrc(section);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 清理失败不覆盖原始扫描异常。
        }
    }
}
