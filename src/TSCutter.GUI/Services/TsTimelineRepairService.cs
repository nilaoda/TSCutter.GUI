using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.Services;

public sealed class TsTimelineRepairService
{
    private const int PacketSize = TsStreamAnalyzer.PacketSize;
    private const int ReadPacketCount = 32_768;
    private const long TimestampWrap = 1L << 33;
    private const long BoundaryThreshold90k = 22_500;
    private const long PairMinimumTolerance90k = 9_000;
    private const double MaximumPairedDurationSeconds = 120;
    private const int MinimumDriftIntervals = 20;

    public async Task<TsTimelineRepairAnalysis> AnalyzeAsync(
        string filePath,
        TsCheckResult? existingCheckResult = null,
        IProgress<TsTimelineRepairProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(filePath);
        var fileSize = new FileInfo(fullPath).Length;
        var stopwatch = Stopwatch.StartNew();
        var checkResult = existingCheckResult;
        var performsBaseAnalysis = checkResult is null ||
                                   !Path.GetFullPath(checkResult.FilePath).Equals(fullPath, PathComparison) ||
                                   checkResult.FileSize != fileSize || checkResult.WasCancelled;
        if (performsBaseAnalysis)
        {
            var analyzerProgress = progress is null
                ? null
                : new Progress<TsCheckProgress>(value => progress.Report(new TsTimelineRepairProgress(
                    value.BytesScanned / 2, fileSize, value.BytesPerSecond, value.Elapsed, false)));
            var analyzer = new TsStreamAnalyzer();
            checkResult = await analyzer.AnalyzeAsync(
                fullPath, analyzerProgress, cancellationToken,
                new TsStreamAnalyzeOptions
                {
                    Features = TsStreamAnalyzeFeatures.ContinuityValidation |
                               TsStreamAnalyzeFeatures.TimestampValidation |
                               TsStreamAnalyzeFeatures.DetailedEvents
                }).ConfigureAwait(false);
        }

        var finalCheckResult = checkResult!;
        if (finalCheckResult.SyncOffset < 0)
            throw new TsTimelineRepairException(TsTimelineRepairErrorCode.NoSync);

        var samples = await CollectPcrSamplesAsync(
            fullPath, finalCheckResult.SyncOffset, fileSize, performsBaseAnalysis,
            stopwatch, progress, cancellationToken)
            .ConfigureAwait(false);
        return BuildAnalysisFromPcrSamples(fullPath, finalCheckResult, samples);
    }

    // 多源修复的参考源会在主扫描中同时收集 PCR 样本，因此不能再从网络盘单独读取一遍。
    // 这里复用同一套候选判定逻辑，在主扫描完成后从已收集的样本构造时间轴方案。
    internal static TsTimelineRepairAnalysis BuildAnalysisFromPcrSamples(
        string filePath,
        TsCheckResult checkResult,
        IReadOnlyDictionary<int, List<PcrSample>> samples)
    {
        var fullPath = Path.GetFullPath(filePath);
        var analysis = new TsTimelineRepairAnalysis
        {
            FilePath = fullPath,
            FileSize = checkResult.FileSize,
            SyncOffset = checkResult.SyncOffset,
            CheckResult = checkResult
        };
        foreach (var pair in samples)
            BuildPidPlan(pair.Key, pair.Value, checkResult, analysis);
        return analysis;
    }

    public async Task<TsTimelineRepairResult> RepairAsync(
        TsTimelineRepairAnalysis analysis,
        string outputPath,
        bool synchronizePtsDts,
        IProgress<TsTimelineRepairProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var sourcePath = Path.GetFullPath(analysis.FilePath);
        var targetPath = Path.GetFullPath(outputPath);
        if (sourcePath.Equals(targetPath, PathComparison))
            throw new TsTimelineRepairException(TsTimelineRepairErrorCode.OutputMatchesSource);
        if (!File.Exists(sourcePath) || new FileInfo(sourcePath).Length != analysis.FileSize)
            throw new TsTimelineRepairException(TsTimelineRepairErrorCode.SourceChanged);

        var stopwatch = Stopwatch.StartNew();
        var readBufferSize = PacketSize * ReadPacketCount;
        var buffer = ArrayPool<byte>.Shared.Rent(readBufferSize + PacketSize);
        var pcrStates = new Dictionary<int, OutputPcrState>();
        var streamSegments = BuildStreamSegmentMap(analysis);
        long bytesProcessed = 0;
        long packetIndex = 0;
        long rewrittenPcrCount = 0;
        long rewrittenTimestampCount = 0;
        var remainingErrors = 0;
        var remainingWarnings = 0;
        var buffered = 0;
        try
        {
            await using var input = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(
                targetPath, FileMode.Create, FileAccess.Write, FileShare.None,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (analysis.SyncOffset > 0)
            {
                var prefix = new byte[analysis.SyncOffset];
                await input.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
                bytesProcessed += prefix.Length;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 固定读取 188 的整数倍，并保留最多 187 字节尾部，不能使用池化数组的实际容量
                // 作为块大小；ArrayPool 可能返回更大的数组，末块补齐时会因此越界。
                var read = await input.ReadAsync(buffer.AsMemory(buffered, readBufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;
                buffered += read;
                var completeBytes = buffered / PacketSize * PacketSize;

                for (var offset = 0; offset < completeBytes; offset += PacketSize, packetIndex++)
                {
                    var packet = buffer.AsSpan(offset, PacketSize);
                    if (packet[0] != 0x47)
                        throw new TsTimelineRepairException(TsTimelineRepairErrorCode.SyncLost, packetIndex);
                    var pid = ((packet[1] & 0x1F) << 8) | packet[2];
                    var transportError = (packet[1] & 0x80) != 0;
                    if (!transportError && TryGetPcr(packet, out var rawPcr90k, out var pcrOffset, out var discontinuity))
                    {
                        var state = GetOutputPcrState(pcrStates, pid);
                        var unwrapped = Unwrap(rawPcr90k, state.LastRawPcr90k, state.WrapOffset90k);
                        state.LastRawPcr90k = rawPcr90k;
                        state.WrapOffset90k = unwrapped - rawPcr90k;
                        var correction = FindCorrection(analysis.Segments, pid, packetIndex, unwrapped);
                        var corrected = unwrapped + correction;
                        if (correction != 0)
                        {
                            WritePcrBase(packet.Slice(pcrOffset, 5), corrected);
                            rewrittenPcrCount++;
                        }
                        ValidateOutputPcr(state, corrected, discontinuity,
                            ref remainingErrors, ref remainingWarnings);
                    }

                    // TEI 包的 payload 已被发送端标记为不可靠；即使字节形状恰好像 PES，也不能改写其中的时间戳。
                    if (!transportError && synchronizePtsDts && streamSegments.TryGetValue(pid, out var segments))
                        rewrittenTimestampCount += PatchPacketTimestamps(packet, packetIndex, segments);
                }

                await output.WriteAsync(buffer.AsMemory(0, completeBytes), cancellationToken).ConfigureAwait(false);
                bytesProcessed += completeBytes;
                buffered -= completeBytes;
                if (buffered > 0)
                    buffer.AsSpan(completeBytes, buffered).CopyTo(buffer);
                progress?.Report(new TsTimelineRepairProgress(
                    bytesProcessed, analysis.FileSize,
                    bytesProcessed / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds), stopwatch.Elapsed, true));
            }

            // 源文件尾部若有非完整 TS 数据则保持原样；分析器会继续将其报告为残缺尾包。
            if (buffered > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, buffered), cancellationToken).ConfigureAwait(false);
                bytesProcessed += buffered;
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(targetPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new TsTimelineRepairResult
        {
            OutputPath = targetPath,
            FileSize = new FileInfo(targetPath).Length,
            RepairedIssueCount = analysis.RepairableIssueCount,
            RewrittenPcrCount = rewrittenPcrCount,
            RewrittenTimestampCount = rewrittenTimestampCount,
            RemainingPcrErrorCount = remainingErrors,
            RemainingPcrWarningCount = remainingWarnings,
            Elapsed = stopwatch.Elapsed
        };
    }

    public static long GetVirtualTimestampCorrection90k(
        TsTimelineRepairAnalysis analysis,
        int pcrPid,
        long packetIndex)
    {
        long correction = 0;
        foreach (var segment in analysis.Segments)
        {
            if (segment.PcrPid != pcrPid || packetIndex < segment.StartPacket ||
                packetIndex >= segment.EndPacketExclusive)
            {
                continue;
            }
            correction += GetTimestampCorrection(segment, packetIndex);
        }
        return correction;
    }

    private static async Task<Dictionary<int, List<PcrSample>>> CollectPcrSamplesAsync(
        string filePath,
        long syncOffset,
        long fileSize,
        bool followsBaseAnalysis,
        Stopwatch stopwatch,
        IProgress<TsTimelineRepairProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, List<PcrSample>>();
        var states = new Dictionary<int, InputPcrState>();
        var buffer = ArrayPool<byte>.Shared.Rent(PacketSize * ReadPacketCount + PacketSize);
        var buffered = 0;
        long packetIndex = 0;
        try
        {
            await using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
            stream.Position = Math.Max(0, syncOffset);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer.AsMemory(buffered, buffer.Length - buffered), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;
                buffered += read;
                var completeBytes = buffered / PacketSize * PacketSize;
                for (var offset = 0; offset < completeBytes; offset += PacketSize, packetIndex++)
                {
                    var packet = buffer.AsSpan(offset, PacketSize);
                    if (packet[0] != 0x47)
                        throw new TsTimelineRepairException(TsTimelineRepairErrorCode.SyncLost, packetIndex);
                    if ((packet[1] & 0x80) != 0 ||
                        !TryGetPcr(packet, out var rawPcr90k, out _, out var discontinuity))
                    {
                        continue;
                    }
                    var pid = ((packet[1] & 0x1F) << 8) | packet[2];
                    if (!states.TryGetValue(pid, out var state))
                    {
                        state = new InputPcrState();
                        states[pid] = state;
                        result[pid] = [];
                    }
                    var unwrapped = Unwrap(rawPcr90k, state.LastRawPcr90k, state.WrapOffset90k);
                    state.LastRawPcr90k = rawPcr90k;
                    state.WrapOffset90k = unwrapped - rawPcr90k;
                    result[pid].Add(new PcrSample(packetIndex, unwrapped, discontinuity));
                }

                buffered -= completeBytes;
                if (buffered > 0)
                    buffer.AsSpan(completeBytes, buffered).CopyTo(buffer);
                var bytesRead = Math.Min(fileSize, syncOffset + packetIndex * PacketSize);
                var progressBytes = followsBaseAnalysis
                    ? fileSize / 2 + bytesRead / 2
                    : bytesRead;
                progress?.Report(new TsTimelineRepairProgress(
                    progressBytes, fileSize,
                    bytesRead / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds), stopwatch.Elapsed, false));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return result;
    }

    private static void BuildPidPlan(
        int pid,
        List<PcrSample> samples,
        TsCheckResult checkResult,
        TsTimelineRepairAnalysis analysis)
    {
        if (samples.Count < 3)
            return;
        var rates = new List<double>(samples.Count - 1);
        for (var index = 1; index < samples.Count; index++)
        {
            var packetDelta = samples[index].PacketIndex - samples[index - 1].PacketIndex;
            var clockDelta = samples[index].Pcr90k - samples[index - 1].Pcr90k;
            if (!samples[index].Discontinuity && packetDelta > 0 && clockDelta > 0 && clockDelta < 45_000)
                rates.Add(clockDelta / (double)packetDelta);
        }
        if (rates.Count == 0)
            return;
        rates.Sort();
        var ticksPerPacket = rates[rates.Count / 2];
        var errors = new double[samples.Count];
        var boundaries = new List<int>();
        for (var index = 1; index < samples.Count; index++)
        {
            var expected = (samples[index].PacketIndex - samples[index - 1].PacketIndex) * ticksPerPacket;
            errors[index] = samples[index].Pcr90k - samples[index - 1].Pcr90k - expected;
            if (!samples[index].Discontinuity && Math.Abs(errors[index]) >= BoundaryThreshold90k)
                boundaries.Add(index);
        }

        var consumed = new HashSet<int>();
        for (var boundaryIndex = 0; boundaryIndex < boundaries.Count; boundaryIndex++)
        {
            var first = boundaries[boundaryIndex];
            if (consumed.Contains(first))
                continue;
            var paired = -1;
            for (var candidateIndex = boundaryIndex + 1; candidateIndex < boundaries.Count; candidateIndex++)
            {
                var candidate = boundaries[candidateIndex];
                var estimatedDuration =
                    (samples[candidate].PacketIndex - samples[first].PacketIndex) * ticksPerPacket / 90_000.0;
                if (estimatedDuration > MaximumPairedDurationSeconds)
                    break;
                if (Math.Sign(errors[first]) == Math.Sign(errors[candidate]))
                    continue;
                var tolerance = Math.Max(PairMinimumTolerance90k,
                    Math.Max(Math.Abs(errors[first]), Math.Abs(errors[candidate])) * 0.05);
                if (Math.Abs(errors[first] + errors[candidate]) <= tolerance)
                {
                    paired = candidate;
                    break;
                }
            }

            if (paired > first)
            {
                AddInterpolatedSegment(pid, samples, first, paired,
                    TsTimelineIssueKind.TemporaryPcrOffset, errors[first], ticksPerPacket,
                    checkResult, analysis);
                consumed.Add(first);
                consumed.Add(paired);
                continue;
            }

            if (TryFindDriftStart(samples, errors, ticksPerPacket, first, out var driftStart))
            {
                AddInterpolatedSegment(pid, samples, driftStart, first,
                    TsTimelineIssueKind.GradualPcrDrift, errors[first], ticksPerPacket,
                    checkResult, analysis);
                consumed.Add(first);
                continue;
            }

            AddPersistentSegment(pid, samples, first, errors[first], ticksPerPacket, checkResult, analysis);
            consumed.Add(first);
        }
    }

    private static bool TryFindDriftStart(
        List<PcrSample> samples,
        double[] errors,
        double ticksPerPacket,
        int boundary,
        out int start)
    {
        start = boundary;
        if (boundary <= MinimumDriftIntervals || errors[boundary] == 0)
            return false;

        // 渐进漂移的每个 PCR 增量很小，录制抖动可能使个别增量短暂反号，不能要求整段
        // 逐点同号。向前累计“实际增量 - 正常增量”，寻找最能抵消末端跳变的位置；
        // 这样既能识别缓慢漂移，也不会把没有累计偏差的永久跳变误当成闭合区间。
        var accumulated = 0.0;
        var bestResidual = double.MaxValue;
        var bestStart = boundary;
        for (var index = boundary - 1; index > 0; index--)
        {
            var estimatedDuration =
                (samples[boundary].PacketIndex - samples[index].PacketIndex) * ticksPerPacket / 90_000.0;
            if (estimatedDuration > MaximumPairedDurationSeconds)
                break;
            accumulated += errors[index];
            var count = boundary - index;
            if (count < MinimumDriftIntervals || Math.Sign(accumulated) == Math.Sign(errors[boundary]))
                continue;
            var residual = Math.Abs(accumulated + errors[boundary]);
            if (residual < bestResidual)
            {
                bestResidual = residual;
                bestStart = index;
            }
        }
        if (bestStart == boundary ||
            bestResidual > Math.Max(PairMinimumTolerance90k, Math.Abs(errors[boundary]) * 0.1))
        {
            return false;
        }
        start = bestStart;
        return true;
    }

    private static void AddInterpolatedSegment(
        int pid,
        List<PcrSample> samples,
        int startIndex,
        int endIndex,
        TsTimelineIssueKind kind,
        double boundaryError90k,
        double ticksPerPacket,
        TsCheckResult checkResult,
        TsTimelineRepairAnalysis analysis)
    {
        if (startIndex <= 0 || endIndex >= samples.Count || startIndex >= endIndex)
            return;
        var affectsTimestamps = HasMatchingTimestampIssue(
            checkResult, samples[startIndex].PacketIndex, samples[endIndex].PacketIndex, boundaryError90k);
        analysis.Segments.Add(new TsTimelineCorrectionSegment
        {
            PcrPid = pid,
            StartPacket = samples[startIndex].PacketIndex,
            EndPacketExclusive = samples[endIndex].PacketIndex,
            StartAnchorPacket = samples[startIndex - 1].PacketIndex,
            EndAnchorPacket = samples[endIndex].PacketIndex,
            StartAnchorPcr90k = samples[startIndex - 1].Pcr90k,
            EndAnchorPcr90k = samples[endIndex].Pcr90k,
            ConstantOffset90k = 0,
            TimestampStartCorrection90k = kind == TsTimelineIssueKind.GradualPcrDrift
                ? 0
                : (long)Math.Round(-boundaryError90k),
            TimestampEndCorrection90k = (long)Math.Round(kind == TsTimelineIssueKind.GradualPcrDrift
                ? boundaryError90k
                : -boundaryError90k),
            UseInterpolation = true,
            AffectsStreamTimestamps = affectsTimestamps
        });
        analysis.Issues.Add(new TsTimelineRepairIssue
        {
            Kind = kind,
            PcrPid = pid,
            StartPacket = samples[startIndex].PacketIndex,
            EndPacket = samples[endIndex].PacketIndex,
            StartTimeSeconds = EstimateTime(samples, startIndex, ticksPerPacket),
            EndTimeSeconds = EstimateTime(samples, endIndex, ticksPerPacket),
            CorrectionSeconds = kind == TsTimelineIssueKind.GradualPcrDrift
                ? boundaryError90k / 90_000.0
                : -boundaryError90k / 90_000.0,
            AffectsStreamTimestamps = affectsTimestamps
        });
    }

    private static void AddPersistentSegment(
        int pid,
        List<PcrSample> samples,
        int boundary,
        double boundaryError90k,
        double ticksPerPacket,
        TsCheckResult checkResult,
        TsTimelineRepairAnalysis analysis)
    {
        var expected = (samples[boundary].PacketIndex - samples[boundary - 1].PacketIndex) * ticksPerPacket;
        var desired = samples[boundary - 1].Pcr90k + (long)Math.Round(expected);
        var correction = desired - samples[boundary].Pcr90k;
        var affectsTimestamps = HasMatchingTimestampIssue(
            checkResult, samples[boundary].PacketIndex, samples[boundary].PacketIndex, boundaryError90k);
        analysis.Segments.Add(new TsTimelineCorrectionSegment
        {
            PcrPid = pid,
            StartPacket = samples[boundary].PacketIndex,
            EndPacketExclusive = long.MaxValue,
            StartAnchorPacket = samples[boundary - 1].PacketIndex,
            EndAnchorPacket = samples[boundary].PacketIndex,
            StartAnchorPcr90k = samples[boundary - 1].Pcr90k,
            EndAnchorPcr90k = samples[boundary].Pcr90k,
            ConstantOffset90k = correction,
            TimestampStartCorrection90k = correction,
            TimestampEndCorrection90k = correction,
            UseInterpolation = false,
            AffectsStreamTimestamps = affectsTimestamps
        });
        analysis.Issues.Add(new TsTimelineRepairIssue
        {
            Kind = TsTimelineIssueKind.PersistentClockDiscontinuity,
            PcrPid = pid,
            StartPacket = samples[boundary].PacketIndex,
            EndPacket = long.MaxValue,
            StartTimeSeconds = EstimateTime(samples, boundary, ticksPerPacket),
            EndTimeSeconds = EstimateTime(samples, samples.Count - 1, ticksPerPacket),
            CorrectionSeconds = correction / 90_000.0,
            AffectsStreamTimestamps = affectsTimestamps
        });
    }

    private static bool HasMatchingTimestampIssue(
        TsCheckResult result,
        long startPacket,
        long endPacket,
        double pcrError90k)
    {
        var magnitudeSeconds = Math.Abs(pcrError90k) / 90_000.0;
        var padding = 16_384L;
        foreach (var item in result.Events)
        {
            if (item.Type is not (TsCheckEventType.PtsJump or TsCheckEventType.PtsBackward or
                TsCheckEventType.DtsBackward))
                continue;
            if (item.StartPacket < startPacket - padding || item.StartPacket > endPacket + padding)
                continue;
            if (item.MessageArguments.Length > 0 && item.MessageArguments[0] is double value &&
                Math.Abs(value - magnitudeSeconds) <= Math.Max(0.25, magnitudeSeconds * 0.1))
            {
                return true;
            }
        }
        return false;
    }

    private static Dictionary<int, List<TsTimelineCorrectionSegment>> BuildStreamSegmentMap(
        TsTimelineRepairAnalysis analysis)
    {
        var result = new Dictionary<int, List<TsTimelineCorrectionSegment>>();
        foreach (var program in analysis.CheckResult.Programs.Values)
        {
            var segments = analysis.Segments
                .Where(item => item.PcrPid == program.PcrPid && item.AffectsStreamTimestamps)
                .ToList();
            if (segments.Count == 0)
                continue;
            foreach (var pid in program.Streams.Keys)
                result[pid] = segments;
        }
        return result;
    }

    private static int PatchPacketTimestamps(
        Span<byte> packet,
        long packetIndex,
        List<TsTimelineCorrectionSegment> segments)
    {
        if ((packet[1] & 0x40) == 0)
            return 0;
        var adaptationControl = (packet[3] >> 4) & 3;
        if ((adaptationControl & 1) == 0)
            return 0;
        var payloadOffset = 4;
        if ((adaptationControl & 2) != 0)
            payloadOffset += packet[4] + 1;
        if (payloadOffset + 14 > PacketSize)
            return 0;
        var payload = packet[payloadOffset..];
        if (payload[0] != 0 || payload[1] != 0 || payload[2] != 1 || payload.Length < 9)
            return 0;
        var correction = FindTimestampCorrection(segments, packetIndex);
        if (correction == 0)
            return 0;
        var flags = (payload[7] >> 6) & 3;
        var count = 0;
        if ((flags & 2) != 0 && payload.Length >= 14)
        {
            WritePesTimestamp(payload[9..14], ReadPesTimestamp(payload[9..14]) + correction);
            count++;
        }
        if (flags == 3 && payload.Length >= 19)
        {
            WritePesTimestamp(payload[14..19], ReadPesTimestamp(payload[14..19]) + correction);
            count++;
        }
        return count;
    }

    private static long FindTimestampCorrection(List<TsTimelineCorrectionSegment> segments, long packetIndex)
    {
        long correction = 0;
        foreach (var segment in segments)
        {
            if (packetIndex < segment.StartPacket || packetIndex >= segment.EndPacketExclusive)
                continue;
            correction += GetTimestampCorrection(segment, packetIndex);
        }
        return correction;
    }

    private static long GetTimestampCorrection(TsTimelineCorrectionSegment segment, long packetIndex)
    {
        if (!segment.UseInterpolation || segment.EndAnchorPacket <= segment.StartAnchorPacket)
            return segment.ConstantOffset90k;
        // PTS/DTS 与 PCR 处于同一 90 kHz 时钟域。临时阶跃使用恒定校正量，
        // 渐进漂移则从 0 线性过渡到末端校正量，避免媒体时间戳与 PCR 再次分离。
        var ratio = (packetIndex - segment.StartAnchorPacket) /
                    (double)(segment.EndAnchorPacket - segment.StartAnchorPacket);
        return segment.TimestampStartCorrection90k +
               (long)Math.Round((segment.TimestampEndCorrection90k -
                                segment.TimestampStartCorrection90k) * ratio);
    }

    private static long FindCorrection(
        List<TsTimelineCorrectionSegment> segments,
        int pid,
        long packetIndex,
        long currentPcr90k)
    {
        long correction = 0;
        foreach (var segment in segments)
        {
            if (segment.PcrPid == pid)
                correction += segment.GetCorrection90k(packetIndex, currentPcr90k + correction);
        }
        return correction;
    }

    private static bool TryGetPcr(
        ReadOnlySpan<byte> packet,
        out long rawPcr90k,
        out int pcrOffset,
        out bool discontinuity)
    {
        rawPcr90k = 0;
        pcrOffset = 0;
        discontinuity = false;
        var adaptationControl = (packet[3] >> 4) & 3;
        if ((adaptationControl & 2) == 0 || packet[4] < 1)
            return false;
        var flags = packet[5];
        discontinuity = (flags & 0x80) != 0;
        if ((flags & 0x10) == 0 || packet[4] < 7)
            return false;
        pcrOffset = 6;
        rawPcr90k = ((long)packet[6] << 25) |
                    ((long)packet[7] << 17) |
                    ((long)packet[8] << 9) |
                    ((long)packet[9] << 1) |
                    ((long)packet[10] >> 7);
        return true;
    }

    private static void WritePcrBase(Span<byte> value, long pcr90k)
    {
        var raw = ModuloTimestamp(pcr90k);
        value[0] = (byte)(raw >> 25);
        value[1] = (byte)(raw >> 17);
        value[2] = (byte)(raw >> 9);
        value[3] = (byte)(raw >> 1);
        value[4] = (byte)((value[4] & 0x7F) | (byte)((raw & 1) << 7));
    }

    private static long ReadPesTimestamp(ReadOnlySpan<byte> value) =>
        ((long)(value[0] & 0x0E) << 29) |
        ((long)value[1] << 22) |
        ((long)(value[2] & 0xFE) << 14) |
        ((long)value[3] << 7) |
        ((long)value[4] >> 1);

    private static void WritePesTimestamp(Span<byte> value, long timestamp90k)
    {
        var raw = ModuloTimestamp(timestamp90k);
        value[0] = (byte)((value[0] & 0xF1) | (byte)(((raw >> 30) & 7) << 1));
        value[1] = (byte)(raw >> 22);
        value[2] = (byte)((((raw >> 15) & 0x7F) << 1) | 1);
        value[3] = (byte)(raw >> 7);
        value[4] = (byte)(((raw & 0x7F) << 1) | 1);
    }

    private static void ValidateOutputPcr(
        OutputPcrState state,
        long correctedPcr90k,
        bool discontinuity,
        ref int errors,
        ref int warnings)
    {
        if (!discontinuity && state.LastCorrectedPcr90k != long.MinValue)
        {
            var delta = (correctedPcr90k - state.LastCorrectedPcr90k) / 90_000.0;
            if (delta < -0.001 || delta > 10)
                errors++;
            else if (delta > 0.5)
                warnings++;
        }
        state.LastCorrectedPcr90k = correctedPcr90k;
    }

    private static OutputPcrState GetOutputPcrState(Dictionary<int, OutputPcrState> states, int pid)
    {
        if (states.TryGetValue(pid, out var state))
            return state;
        state = new OutputPcrState();
        states[pid] = state;
        return state;
    }

    private static double EstimateTime(List<PcrSample> samples, int index, double ticksPerPacket) =>
        (samples[index].PacketIndex - samples[0].PacketIndex) * ticksPerPacket / 90_000.0;

    private static long Unwrap(long raw, long lastRaw, long wrapOffset)
    {
        if (lastRaw != long.MinValue)
        {
            if (lastRaw - raw > TimestampWrap / 2)
                wrapOffset += TimestampWrap;
            else if (raw - lastRaw > TimestampWrap / 2)
                wrapOffset -= TimestampWrap;
        }
        return raw + wrapOffset;
    }

    private static long ModuloTimestamp(long value)
    {
        value %= TimestampWrap;
        return value < 0 ? value + TimestampWrap : value;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 保留原始异常；清理未完成输出失败不应覆盖真正的处理错误。
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal readonly record struct PcrSample(long PacketIndex, long Pcr90k, bool Discontinuity);

    private sealed class InputPcrState
    {
        public long LastRawPcr90k = long.MinValue;
        public long WrapOffset90k;
    }

    private sealed class OutputPcrState
    {
        public long LastRawPcr90k = long.MinValue;
        public long WrapOffset90k;
        public long LastCorrectedPcr90k = long.MinValue;
    }
}
