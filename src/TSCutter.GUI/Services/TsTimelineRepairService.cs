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

public sealed class TsTimelineRepairService
{
    private const int PacketSize = TsStreamAnalyzer.PacketSize;
    private const int ReadPacketCount = 32_768;
    private const long BoundaryThreshold90k = TsPcrCadence.BoundaryThreshold90k;
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
        {
            BuildPidPlan(pair.Key, pair.Value, checkResult, analysis);
            ValidatePidPlan(pair.Key, pair.Value, analysis);
        }
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
                    if (!transportError && TsTimestampFieldCodec.TryReadPcr(
                            packet, out var rawPcr90k, out var pcrOffset, out var discontinuity))
                    {
                        var state = GetOutputPcrState(pcrStates, pid);
                        var unwrapped = TsTimestampFieldCodec.UnwrapTimestamp(
                            rawPcr90k, state.LastRawPcr90k, state.WrapOffset90k);
                        state.LastRawPcr90k = rawPcr90k;
                        state.WrapOffset90k = unwrapped - rawPcr90k;
                        var correction = FindCorrection(analysis.Segments, pid, packetIndex);
                        var corrected = unwrapped + correction;
                        if (correction != 0)
                        {
                            TsTimestampFieldCodec.WritePcrBase(
                                packet.Slice(pcrOffset, 5), corrected);
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
            if (!segment.AffectsStreamTimestamps || segment.PcrPid != pcrPid ||
                packetIndex < segment.StartPacket ||
                packetIndex >= segment.EndPacketExclusive)
            {
                continue;
            }
            correction += GetTimestampCorrection(segment, packetIndex);
        }
        return correction;
    }

    /// <summary>
    /// 在其他工具的单遍输出管线中复用时间轴修复规则。调用方负责先把辅助包换算到
    /// 参考源原始时钟，再按该包在参考文件中的目标位置调用本类，避免重复应用校正量。
    /// </summary>
    internal sealed class OutputPacketRewriter
    {
        private readonly TsTimelineRepairAnalysis _analysis;
        private readonly List<TsTimelineCorrectionSegment> _segments;
        private readonly Dictionary<int, List<TsTimelineCorrectionSegment>> _streamSegments;
        private readonly Dictionary<int, OutputPcrState> _pcrStates = [];

        public OutputPacketRewriter(
            TsTimelineRepairAnalysis analysis,
            IReadOnlyList<(int Pid, long StartOffset, long EndOffset)>? replacedRanges = null)
        {
            _analysis = analysis;
            _segments = analysis.Segments.Where(segment =>
            {
                var startAnchorOffset = analysis.SyncOffset + segment.StartAnchorPacket * PacketSize;
                var startBoundaryOffset = analysis.SyncOffset + segment.StartPacket * PacketSize;
                var endBoundaryOffset = segment.EndPacketExclusive == long.MaxValue
                    ? long.MaxValue
                    : analysis.SyncOffset + segment.EndPacketExclusive * PacketSize;
                return replacedRanges is null || !replacedRanges.Any(range =>
                    range.Pid == segment.PcrPid &&
                    (IsInsideReplacedRange(startAnchorOffset, range) ||
                     IsInsideReplacedRange(startBoundaryOffset, range) ||
                     IsInsideReplacedRange(endBoundaryOffset, range)));
            }).ToList();
            // 若大段内容缺失本身造成 PCR 跨越，补回完整内容已经消除了该断点。
            // PCR 异常由前后两个采样点共同确定；整段替换只覆盖前置锚点时，原校正也
            // 已经失效。段首、前置锚点和渐进漂移闭合端都要判断，避免重复校正在接缝
            // 处制造反向跳变。补段边界采用闭区间，与输出替换计划的边界语义一致。
            _streamSegments = BuildStreamSegmentMap(analysis, _segments);
        }

        private static bool IsInsideReplacedRange(
            long boundaryOffset,
            (int Pid, long StartOffset, long EndOffset) range) =>
            boundaryOffset >= range.StartOffset && boundaryOffset <= range.EndOffset;

        public int RepairedIssueCount => _segments.Count;
        public long RewrittenPcrCount { get; private set; }
        public long RewrittenTimestampCount { get; private set; }
        public int RemainingPcrErrorCount { get; private set; }
        public int RemainingPcrWarningCount { get; private set; }

        public void ProcessPacket(
            Span<byte> packet,
            long referenceFileOffset,
            bool applyPcrCorrection,
            bool applyTimestampCorrection)
        {
            if (packet[0] != 0x47 || (packet[1] & 0x80) != 0)
                return;

            var packetIndex = Math.Max(
                0, (referenceFileOffset - _analysis.SyncOffset) / PacketSize);
            var pid = ((packet[1] & 0x1F) << 8) | packet[2];
            if (TsTimestampFieldCodec.TryReadPcr(
                    packet, out var rawPcr90k, out var pcrOffset, out var discontinuity))
            {
                var state = GetOutputPcrState(_pcrStates, pid);
                var unwrapped = TsTimestampFieldCodec.UnwrapTimestamp(
                    rawPcr90k, state.LastRawPcr90k, state.WrapOffset90k);
                state.LastRawPcr90k = rawPcr90k;
                state.WrapOffset90k = unwrapped - rawPcr90k;
                var correction = applyPcrCorrection
                    ? FindCorrection(_segments, pid, packetIndex)
                    : 0;
                var corrected = unwrapped + correction;
                if (correction != 0)
                {
                    TsTimestampFieldCodec.WritePcrBase(packet.Slice(pcrOffset, 5), corrected);
                    RewrittenPcrCount++;
                }
                var errors = RemainingPcrErrorCount;
                var warnings = RemainingPcrWarningCount;
                ValidateOutputPcr(state, corrected, discontinuity, ref errors, ref warnings);
                RemainingPcrErrorCount = errors;
                RemainingPcrWarningCount = warnings;
            }

            if (applyTimestampCorrection && _streamSegments.TryGetValue(pid, out var segments))
                RewrittenTimestampCount += PatchPacketTimestamps(packet, packetIndex, segments);
        }
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
                        !TsTimestampFieldCodec.TryReadPcr(
                            packet, out var rawPcr90k, out _, out var discontinuity))
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
                    var unwrapped = TsTimestampFieldCodec.UnwrapTimestamp(
                        rawPcr90k, state.LastRawPcr90k, state.WrapOffset90k);
                    state.LastRawPcr90k = rawPcr90k;
                    state.WrapOffset90k = unwrapped - rawPcr90k;
                    result[pid].Add(new PcrSample(packetIndex, unwrapped, discontinuity,
                        ReadDecodeTimestamp90k(packet, unwrapped)));
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
        var intervals = new List<long>(samples.Count - 1);
        for (var index = 1; index < samples.Count; index++)
        {
            var clockDelta = samples[index].Pcr90k - samples[index - 1].Pcr90k;
            if (!samples[index].Discontinuity && clockDelta > 0 && clockDelta < 45_000)
                intervals.Add(clockDelta);
        }
        intervals.Sort();
        if (!TsPcrCadence.TryGetInterval(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(intervals),
                out var interval90k))
            return;
        MarkTransportDamageIntervals(pid, samples, checkResult);
        var boundaries = new List<int>();
        for (var index = 1; index < samples.Count; index++)
        {
            if (samples[index].Discontinuity)
                continue;
            if (Math.Abs(GetIntervalError(samples, index, interval90k)) >= BoundaryThreshold90k)
                boundaries.Add(index);
        }
        if (boundaries.Count == 0)
            return;
        // 部分广播流在规则采样之间还插入短 PCR，按中位周期累计会有系统性偏差。
        // 正常短间隔的均值仅用于估计回溯的不确定度，不用包速率推断时钟。
        long normalSum = 0;
        var normalCount = 0;
        var upperNormalInterval = interval90k + Math.Max(90, interval90k / 20);
        foreach (var interval in intervals)
        {
            if (interval > upperNormalInterval)
                break;
            normalSum += interval;
            normalCount++;
        }
        var cadenceBias90k = Math.Abs(interval90k - normalSum / (double)normalCount);
        var lastBoundary = 0;
        var nextDiscontinuity = 0;
        for (var boundaryIndex = 0; boundaryIndex < boundaries.Count; boundaryIndex++)
        {
            var first = boundaries[boundaryIndex];
            var firstError = GetIntervalError(samples, first, interval90k);
            var paired = -1;
            if (boundaryIndex + 1 < boundaries.Count)
            {
                var candidate = boundaries[boundaryIndex + 1];
                var estimatedDuration = (candidate - first) * interval90k / 90_000.0;
                var candidateError = GetIntervalError(samples, candidate, interval90k);
                var tolerance = Math.Max(PairMinimumTolerance90k,
                    Math.Max(Math.Abs(firstError), Math.Abs(candidateError)) * 0.05);
                // 只配对相邻边界，不能跨过其他独立异常寻找远处的闭合点。
                if (estimatedDuration <= MaximumPairedDurationSeconds &&
                    Math.Sign(firstError) != Math.Sign(candidateError) &&
                    Math.Abs(firstError + candidateError) <= tolerance &&
                    !HasUncertainInterval(samples, first, candidate) &&
                    IsStableTemporaryOffset(samples, first, candidate, interval90k, tolerance))
                    paired = candidate;
            }

            if (paired > first)
            {
                AddInterpolatedSegment(pid, samples, first, paired,
                    TsTimelineIssueKind.TemporaryPcrOffset, firstError,
                    checkResult, analysis);
                boundaryIndex++;
                lastBoundary = paired;
                continue;
            }

            if (TryFindDriftStart(samples, interval90k, cadenceBias90k,
                    first, lastBoundary + 1, out var driftStart))
            {
                AddInterpolatedSegment(pid, samples, driftStart, first,
                    TsTimelineIssueKind.GradualPcrDrift, firstError,
                    checkResult, analysis);
                lastBoundary = first;
                continue;
            }

            // 下一显式边界只向前查找一次，避免每个持续段反复扫描文件余下样本。
            if (nextDiscontinuity >= 0 && nextDiscontinuity <= first)
                nextDiscontinuity = samples.FindIndex(first + 1, static sample => sample.Discontinuity);
            AddPersistentSegment(pid, samples, first, nextDiscontinuity, firstError, checkResult, analysis);
            lastBoundary = first;
        }
    }

    private static long GetIntervalError(List<PcrSample> samples, int index, long interval90k)
    {
        var clockDelta = samples[index].Pcr90k - samples[index - 1].Pcr90k;
        // 丢包会同时丢掉中间 PCR；长正向间隔不能作为压缩缺失内容的依据。
        if (clockDelta >= interval90k + BoundaryThreshold90k && samples[index].TransportDamage)
            return 0;
        // PCR 和同包 DTS 一起正向跨越时，可能只是缺少采样或媒体内容，不能压缩时间。
        if (clockDelta >= interval90k + BoundaryThreshold90k &&
            samples[index].DecodeTimestamp90k is { } dts &&
            samples[index - 1].DecodeTimestamp90k is { } previousDts &&
            Math.Abs(dts - previousDts - clockDelta) <= 90)
            return 0;
        return clockDelta - interval90k;
    }

    internal static bool IsTransportDamage(TsCheckEventType type) =>
        type is TsCheckEventType.ContinuityGap or TsCheckEventType.ConflictingDuplicate or
            TsCheckEventType.TransportError or TsCheckEventType.SyncLoss or TsCheckEventType.InvalidPacketHeader;

    private static void MarkTransportDamageIntervals(int pid, List<PcrSample> samples, TsCheckResult checkResult)
    {
        List<TsCheckEvent>? damage = null;
        foreach (var item in checkResult.Events)
        {
            if (!IsTransportDamage(item.Type))
                continue;
            var related = item.Pid < 0 || item.Pid == pid;
            if (!related)
                foreach (var program in checkResult.Programs.Values)
                    if (program.PcrPid == pid && program.Streams.ContainsKey(item.Pid))
                    {
                        related = true;
                        break;
                    }
            if (related)
                (damage ??= []).Add(item);
        }
        if (damage is null && checkResult.OmittedEventCount == 0)
            return;
        damage?.Sort(static (left, right) => left.StartPacket.CompareTo(right.StartPacket));
        var next = 0;
        long damagedThrough = -1;
        for (var index = 1; index < samples.Count; index++)
        {
            var sample = samples[index];
            while (damage is not null && next < damage.Count && damage[next].StartPacket <= sample.PacketIndex)
            {
                var item = damage[next++];
                damagedThrough = Math.Max(damagedThrough, Math.Max(item.StartPacket, item.EndPacket));
            }
            // 详情被截断时无法排除未记录的丢包，保守地保留正向缺口。
            if (damagedThrough > samples[index - 1].PacketIndex || checkResult.OmittedEventCount > 0)
                samples[index] = sample with { TransportDamage = true };
        }
    }

    private static bool HasUncertainInterval(List<PcrSample> samples, int start, int end)
    {
        for (var index = start; index <= end; index++)
            if (samples[index].Discontinuity || samples[index].TransportDamage)
                return true;
        return false;
    }

    private static bool IsStableTemporaryOffset(
        List<PcrSample> samples, int start, int end, long interval90k, double tolerance)
    {
        long accumulated = 0;
        for (var index = start + 1; index < end; index++)
        {
            accumulated += GetIntervalError(samples, index, interval90k);
            if (Math.Abs(accumulated) > tolerance)
                return false;
        }
        return true;
    }

    private static void ValidatePidPlan(int pid, List<PcrSample> samples, TsTimelineRepairAnalysis analysis)
    {
        var segments = analysis.Segments.Where(item => item.PcrPid == pid).ToList();
        if (segments.Count == 0)
            return;
        segments.Sort(static (left, right) => left.StartPacket.CompareTo(right.StartPacket));
        var nextSegment = 0;
        long constantCorrection = 0, constantTimestampCorrection = 0;
        // 常量段只在开始/结束边界更新累计值，持续到文件末尾的段不占用结束队列。
        var endingConstants = new PriorityQueue<TsTimelineCorrectionSegment, long>();
        List<TsTimelineCorrectionSegment>? interpolated = null;
        long? mediaOffset = null;
        long? previousCorrected = null;
        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            while (endingConstants.TryPeek(out _, out var end) && end <= sample.PacketIndex)
            {
                var segment = endingConstants.Dequeue();
                constantCorrection -= segment.ConstantOffset90k;
                if (segment.AffectsStreamTimestamps)
                    constantTimestampCorrection -= segment.ConstantOffset90k;
            }
            while (nextSegment < segments.Count && segments[nextSegment].StartPacket <= sample.PacketIndex)
            {
                var segment = segments[nextSegment++];
                if (segment.EndPacketExclusive <= sample.PacketIndex)
                    continue;
                if (segment.UseInterpolation)
                {
                    (interpolated ??= []).Add(segment);
                    continue;
                }
                constantCorrection += segment.ConstantOffset90k;
                if (segment.AffectsStreamTimestamps)
                    constantTimestampCorrection += segment.ConstantOffset90k;
                if (segment.EndPacketExclusive != long.MaxValue)
                    endingConstants.Enqueue(segment, segment.EndPacketExclusive);
            }
            long correction = constantCorrection, timestampCorrection = constantTimestampCorrection;
            // 只遍历当前有效的渐进段；构造方案时已禁止渐进段互相重叠。
            for (var active = (interpolated?.Count ?? 0) - 1; active >= 0; active--)
            {
                var segment = interpolated![active];
                if (segment.EndPacketExclusive <= sample.PacketIndex)
                {
                    interpolated.RemoveAt(active);
                    continue;
                }
                var value = segment.GetCorrection90k(sample.PacketIndex);
                correction += value;
                if (segment.AffectsStreamTimestamps)
                    timestampCorrection += value;
            }
            var corrected = sample.Pcr90k + correction;
            if (sample.Discontinuity)
                mediaOffset = null;
            if (!sample.Discontinuity && previousCorrected is { } previous)
            {
                var rawDelta = sample.Pcr90k - samples[index - 1].Pcr90k;
                var correctedDelta = corrected - previous;
                if (correctedDelta < Math.Min(-90, rawDelta) || correctedDelta <= 0 && rawDelta > 0 ||
                    correctedDelta > Math.Max(45_000, rawDelta))
                {
                    Reject();
                    return;
                }
            }
            if (sample.DecodeTimestamp90k is { } dts)
            {
                mediaOffset ??= dts - sample.Pcr90k;
                var rawError = Math.Abs(dts - sample.Pcr90k - mediaOffset.Value);
                var correctedError = Math.Abs(dts + timestampCorrection - corrected - mediaOffset.Value);
                if (correctedError > rawError + 90)
                {
                    Reject();
                    return;
                }
            }
            previousCorrected = corrected;
        }

        void Reject()
        {
            // 使用已收集的样本预演，不再读取素材；不交付会新增时钟异常的自动方案。
            analysis.Segments.RemoveAll(item => item.PcrPid == pid);
            analysis.Issues.RemoveAll(item => item.PcrPid == pid);
        }
    }

    internal static long? ReadDecodeTimestamp90k(ReadOnlySpan<byte> packet, long pcr90k)
    {
        if (!TsTimestampFieldCodec.TryLocatePesTimestamps(packet, out _, out var dtsOffset) || dtsOffset < 0)
            return null;
        var rawDts = TsTimestampFieldCodec.ReadPesTimestamp(packet.Slice(dtsOffset, 5));
        var rawPcr = pcr90k & ((1L << 33) - 1);
        return TsTimestampFieldCodec.UnwrapTimestamp(rawDts, rawPcr, pcr90k - rawPcr);
    }

    private static bool TryFindDriftStart(
        List<PcrSample> samples,
        long interval90k,
        double cadenceBias90k,
        int boundary,
        int minimumStart,
        out int start)
    {
        start = boundary;
        var boundaryError = GetIntervalError(samples, boundary, interval90k);
        if (boundary <= MinimumDriftIntervals || boundaryError == 0)
            return false;

        // 渐进漂移的每个 PCR 增量很小，录制抖动可能使个别增量短暂反号，不能要求整段
        // 逐点同号。向前累计“实际增量 - 正常增量”，寻找最能抵消末端跳变的位置；
        // 这样既能识别缓慢漂移，也不会把没有累计偏差的永久跳变误当成闭合区间。
        var accumulated = 0.0;
        var bestResidual = double.MaxValue;
        var bestStart = boundary;
        for (var index = boundary - 1; index >= minimumStart; index--)
        {
            if (samples[index].Discontinuity || samples[index].TransportDamage)
                break;
            var estimatedDuration = (boundary - index) * interval90k / 90_000.0;
            if (estimatedDuration > MaximumPairedDurationSeconds)
                break;
            accumulated += GetIntervalError(samples, index, interval90k);
            var count = boundary - index;
            if (count < MinimumDriftIntervals || Math.Sign(accumulated) == Math.Sign(boundaryError))
                continue;
            var residual = Math.Abs(accumulated + boundaryError);
            if (residual < bestResidual)
            {
                bestResidual = residual;
                bestStart = index;
            }
        }
        if (bestStart == boundary ||
            bestResidual > Math.Max(PairMinimumTolerance90k,
                Math.Abs(boundaryError) * 0.1 + cadenceBias90k * (boundary - bestStart + 1)))
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
        TsCheckResult checkResult,
        TsTimelineRepairAnalysis analysis)
    {
        if (startIndex <= 0 || endIndex >= samples.Count || startIndex >= endIndex)
            return;
        var affectsTimestamps = HasMatchingTimestampIssue(
            checkResult, pid, samples[startIndex].PacketIndex, samples[endIndex].PacketIndex, boundaryError90k);
        var gradual = kind == TsTimelineIssueKind.GradualPcrDrift;
        var correctionPoints = gradual ? new TsTimelineCorrectionPoint[endIndex - startIndex + 2] : [];
        for (var index = startIndex - 1; gradual && index <= endIndex; index++)
        {
            // PCR 按采样序号构造目标时钟，包位置仅用于定位和跨包 PTS/DTS 插值。
            var ratio = (index - startIndex + 1) / (double)(endIndex - startIndex + 1);
            var expected = samples[startIndex - 1].Pcr90k +
                           (long)Math.Round((samples[endIndex].Pcr90k - samples[startIndex - 1].Pcr90k) * ratio);
            correctionPoints[index - startIndex + 1] =
                new TsTimelineCorrectionPoint(samples[index].PacketIndex, expected - samples[index].Pcr90k);
        }
        analysis.Segments.Add(new TsTimelineCorrectionSegment
        {
            PcrPid = pid,
            StartPacket = samples[startIndex].PacketIndex,
            EndPacketExclusive = samples[endIndex].PacketIndex,
            StartAnchorPacket = samples[startIndex - 1].PacketIndex,
            EndAnchorPacket = samples[endIndex].PacketIndex,
            ConstantOffset90k = gradual ? 0 : (long)Math.Round(-boundaryError90k),
            UseInterpolation = gradual,
            CorrectionPoints = correctionPoints,
            AffectsStreamTimestamps = affectsTimestamps
        });
        analysis.Issues.Add(new TsTimelineRepairIssue
        {
            Kind = kind,
            PcrPid = pid,
            StartPacket = samples[startIndex].PacketIndex,
            EndPacket = samples[endIndex].PacketIndex,
            StartTimeSeconds = EstimateTime(samples, startIndex),
            EndTimeSeconds = EstimateTime(samples, endIndex),
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
        int end,
        double boundaryError90k,
        TsCheckResult checkResult,
        TsTimelineRepairAnalysis analysis)
    {
        var correction = (long)Math.Round(-boundaryError90k);
        var affectsTimestamps = HasMatchingTimestampIssue(
            checkResult, pid, samples[boundary].PacketIndex, samples[boundary].PacketIndex, boundaryError90k);
        analysis.Segments.Add(new TsTimelineCorrectionSegment
        {
            PcrPid = pid,
            StartPacket = samples[boundary].PacketIndex,
            EndPacketExclusive = end < 0 ? long.MaxValue : samples[end].PacketIndex,
            StartAnchorPacket = samples[boundary - 1].PacketIndex,
            EndAnchorPacket = samples[boundary].PacketIndex,
            ConstantOffset90k = correction,
            UseInterpolation = false,
            AffectsStreamTimestamps = affectsTimestamps
        });
        analysis.Issues.Add(new TsTimelineRepairIssue
        {
            Kind = TsTimelineIssueKind.PersistentClockDiscontinuity,
            PcrPid = pid,
            StartPacket = samples[boundary].PacketIndex,
            EndPacket = end < 0 ? long.MaxValue : samples[end].PacketIndex,
            StartTimeSeconds = EstimateTime(samples, boundary),
            EndTimeSeconds = EstimateTime(samples, end < 0 ? samples.Count - 1 : end),
            CorrectionSeconds = correction / 90_000.0,
            AffectsStreamTimestamps = affectsTimestamps
        });
    }

    private static bool HasMatchingTimestampIssue(
        TsCheckResult result,
        int pcrPid,
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
            if (pcrError90k > 0 && item.Type != TsCheckEventType.PtsJump ||
                pcrError90k < 0 && item.Type == TsCheckEventType.PtsJump)
                continue;
            var sameProgram = false;
            foreach (var program in result.Programs.Values)
                if (program.PcrPid == pcrPid && program.Streams.ContainsKey(item.Pid))
                {
                    sameProgram = true;
                    break;
                }
            if (!sameProgram)
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
        TsTimelineRepairAnalysis analysis,
        IReadOnlyList<TsTimelineCorrectionSegment>? sourceSegments = null)
    {
        var result = new Dictionary<int, List<TsTimelineCorrectionSegment>>();
        var allSegments = sourceSegments ?? analysis.Segments;
        foreach (var program in analysis.CheckResult.Programs.Values)
        {
            var segments = allSegments
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
        // 先确认当前包确实携带 PTS/DTS，再计算异常区段修正量，避免普通负载包
        // 在高码率文件中反复遍历时间轴方案。
        if (!TsTimestampFieldCodec.TryLocatePesTimestamps(
                packet, out var ptsOffset, out var dtsOffset))
        {
            return 0;
        }

        var correction = FindTimestampCorrection(segments, packetIndex);
        return TsTimestampFieldCodec.RewritePesTimestamps(
            packet, correction, ptsOffset, dtsOffset);
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
        return segment.GetCorrection90k(packetIndex);
    }

    private static long FindCorrection(
        List<TsTimelineCorrectionSegment> segments,
        int pid,
        long packetIndex)
    {
        long correction = 0;
        foreach (var segment in segments)
        {
            if (segment.PcrPid == pid)
                correction += segment.GetCorrection90k(packetIndex);
        }
        return correction;
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

    private static double EstimateTime(List<PcrSample> samples, int index) =>
        Math.Max(0, (samples[index].Pcr90k - samples[0].Pcr90k) / 90_000.0);

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

    internal readonly record struct PcrSample(
        long PacketIndex, long Pcr90k, bool Discontinuity)
    {
        public bool TransportDamage { get; init; }
        private readonly int _decodeOffset90k = int.MinValue;
        // 保存相对 PCR 的 DTS 偏移，利用原结构的对齐空间；不增加每个样本的结构大小。
        // 超过 int 范围的时钟关系不作为自动修复依据。
        internal PcrSample(long packetIndex, long pcr90k, bool discontinuity, long? decodeTimestamp90k)
            : this(packetIndex, pcr90k, discontinuity)
        {
            _decodeOffset90k = decodeTimestamp90k is { } dts && dts - pcr90k is > int.MinValue and <= int.MaxValue
                ? (int)(dts - pcr90k)
                : int.MinValue;
        }

        public long? DecodeTimestamp90k => _decodeOffset90k == int.MinValue ? null : Pcr90k + _decodeOffset90k;
    }

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
