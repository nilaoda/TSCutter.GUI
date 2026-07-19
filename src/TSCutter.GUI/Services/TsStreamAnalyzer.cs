using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.Services;

public sealed class TsStreamAnalyzer
{
    public const int PacketSize = 188;
    private const int ReadBufferSize = PacketSize * 32_768;
    private const int MaxStoredEvents = 20_000;
    private const int MaxMpegAudioProbeBytes = 64 * 1024;
    private const long TimestampWrap = 1L << 33;
    private const double PcrGapThresholdSeconds = 0.5;
    private const double TimestampJumpThresholdSeconds = 10;
    private const double AvDriftThresholdSeconds = 0.5;
    private const int MaxTimelineBuckets = 4_096;
    private const double InitialTimelineBucketSeconds = 1;
    private static readonly Encoding Gb18030Encoding = CreateGb18030Encoding();

    private readonly Dictionary<int, PidState> _pidStates = [];
    private readonly Dictionary<int, PsiAssembler> _psiAssemblers = [];
    private readonly Dictionary<int, int> _pmtPidPrograms = [];
    private readonly Dictionary<int, int> _streamPidPrograms = [];
    private readonly Dictionary<int, int> _pcrPidPrograms = [];
    private readonly Dictionary<int, ProgramClockState> _programClocks = [];
    private TsCheckResult _result = null!;
    private IProgress<TsCheckProgress>? _progress;
    private Stopwatch _stopwatch = null!;
    private long _lastProgressTicks;
    private long _packetIndex;
    private long _syncOffset = -1;
    private long _pendingSyncLossOffset = -1;
    private long _pendingSyncLossBytes;
    private long _lastKnownClock90k = long.MinValue;
    private long _lastKnownPcr90k = long.MinValue;
    private long _timelineOrigin90k = long.MinValue;
    private int _errorCount;
    private int _warningCount;
    private int _globalErrorCount;
    private int _globalWarningCount;
    private TsCheckEvent? _pendingProgressEvent;
    private TsStreamAnalyzeOptions _options = new();
    private long _lastCatalogChangePacket;
    private bool _stopRequested;
    private bool _sdtSeen;
    private int _timelineReferencePcrPid = -1;
    private bool _timelineStarted;
    private double _timelineLastClockSeconds;
    private double _timelineBucketStartSeconds;
    private double _timelineBucketDurationSeconds = InitialTimelineBucketSeconds;
    private double _timelineBucketPacketCount;
    private long _timelineIntervalPacketCount;
    private int _timelineSegment;
    private int _timelineCompactionThreshold = MaxTimelineBuckets;

    public async Task<TsCheckResult> AnalyzeAsync(
        string filePath,
        IProgress<TsCheckProgress>? progress = null,
        CancellationToken cancellationToken = default,
        TsStreamAnalyzeOptions? options = null)
    {
        Reset(filePath, progress, options);
        var readBufferSize = _options.InventoryOnly ? PacketSize * 8_192 : ReadBufferSize;
        // 使用池化大缓冲区减少系统调用和大对象堆分配；额外空间用于保留跨 ReadAsync 的残余包。
        var buffer = ArrayPool<byte>.Shared.Rent(readBufferSize + PacketSize * 4);
        var buffered = 0;

        try
        {
            await using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                ReadBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer.AsMemory(buffered, readBufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;

                buffered += read;
                var consumed = ProcessBuffer(buffer.AsSpan(0, buffered), stream.Position - buffered, cancellationToken);
                if (consumed > 0)
                {
                    buffered -= consumed;
                    if (buffered > 0)
                        buffer.AsSpan(consumed, buffered).CopyTo(buffer);
                }

                _result.BytesScanned = _syncOffset < 0
                    ? Math.Min(stream.Position, _result.FileSize)
                    : Math.Min(_result.FileSize, _syncOffset + _packetIndex * PacketSize);
                ReportProgress(null, force: false);
                if (_stopRequested || _result.BytesScanned >= _options.MaxBytes)
                    break;
            }

            if (!_options.InventoryOnly && _syncOffset < 0)
            {
                AddEvent(TsCheckSeverity.Error, TsCheckEventType.SyncLoss, -1, 0, 0,
                    TsCheckMessageCode.NoSync, [], null, false);
            }
            else if (!_options.InventoryOnly && _pendingSyncLossOffset >= 0)
            {
                AddEvent(TsCheckSeverity.Error, TsCheckEventType.SyncLoss, -1, _packetIndex,
                    _pendingSyncLossOffset, TsCheckMessageCode.SyncLostAtEnd,
                    [_pendingSyncLossBytes + buffered], GetEstimatedTime(), true);
            }
            else if (!_options.InventoryOnly && buffered > 0 && !_stopRequested)
            {
                AddEvent(TsCheckSeverity.Warning, TsCheckEventType.TrailingBytes, -1, _packetIndex,
                    stream.Length - buffered, TsCheckMessageCode.TrailingBytes, [buffered],
                    GetEstimatedTime(), true);
            }

            if (!_options.InventoryOnly)
                ValidateCompletedScan(stream.Length);
            CompleteResult(_stopRequested ? _result.BytesScanned : Math.Min(stream.Length, _options.MaxBytes), false);
        }
        catch (OperationCanceledException)
        {
            CompleteResult(_result.BytesScanned, true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        ReportProgress(null, force: true);
        return _result;
    }

    private void Reset(string filePath, IProgress<TsCheckProgress>? progress, TsStreamAnalyzeOptions? options)
    {
        _pidStates.Clear();
        _psiAssemblers.Clear();
        _pmtPidPrograms.Clear();
        _streamPidPrograms.Clear();
        _pcrPidPrograms.Clear();
        _programClocks.Clear();
        _result = new TsCheckResult
        {
            FilePath = filePath,
            FileSize = new FileInfo(filePath).Length
        };
        _progress = progress;
        _stopwatch = Stopwatch.StartNew();
        _lastProgressTicks = 0;
        _packetIndex = 0;
        _syncOffset = -1;
        _pendingSyncLossOffset = -1;
        _pendingSyncLossBytes = 0;
        _lastKnownClock90k = long.MinValue;
        _lastKnownPcr90k = long.MinValue;
        _timelineOrigin90k = long.MinValue;
        _errorCount = 0;
        _warningCount = 0;
        _globalErrorCount = 0;
        _globalWarningCount = 0;
        _pendingProgressEvent = null;
        _options = options ?? new TsStreamAnalyzeOptions();
        _lastCatalogChangePacket = 0;
        _stopRequested = false;
        _sdtSeen = false;
        _timelineReferencePcrPid = -1;
        _timelineStarted = false;
        _timelineLastClockSeconds = 0;
        _timelineBucketStartSeconds = 0;
        _timelineBucketDurationSeconds = InitialTimelineBucketSeconds;
        _timelineBucketPacketCount = 0;
        _timelineIntervalPacketCount = 0;
        _timelineSegment = 0;
        _timelineCompactionThreshold = MaxTimelineBuckets;
        _psiAssemblers[0] = new PsiAssembler();
        if (_options.IncludeServiceMetadata)
            _psiAssemblers[0x0011] = new PsiAssembler();
    }

    private int ProcessBuffer(ReadOnlySpan<byte> data, long absoluteOffset, CancellationToken cancellationToken)
    {
        var position = 0;
        if (_syncOffset < 0)
        {
            var found = FindSync(data);
            if (found < 0)
                return Math.Max(0, data.Length - PacketSize * 3);

            _syncOffset = absoluteOffset + found;
            _result.SyncOffset = _syncOffset;
            position = found;
        }

        var packetCounter = 0;
        while (position + PacketSize <= data.Length)
        {
            if ((packetCounter++ & 0x3FFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            if (data[position] != 0x47)
            {
                // 中途失步时在当前缓冲区内重新寻找连续同步点，恢复后继续保持单遍扫描。
                var resync = FindSync(data[position..]);
                if (resync < 0)
                {
                    // 最后 3 个包暂留给下一次读取拼接，其余损坏字节可立即丢弃，防止残余缓冲无限增长。
                    var discard = Math.Max(0, data.Length - position - PacketSize * 3);
                    if (discard > 0)
                    {
                        if (_pendingSyncLossOffset < 0)
                            _pendingSyncLossOffset = absoluteOffset + position;
                        _pendingSyncLossBytes += discard;
                        position += discard;
                    }
                    break;
                }

                AddEvent(TsCheckSeverity.Error, TsCheckEventType.SyncLoss, -1, _packetIndex,
                    _pendingSyncLossOffset >= 0 ? _pendingSyncLossOffset : absoluteOffset + position,
                    TsCheckMessageCode.SyncRecovered, [_pendingSyncLossBytes + resync],
                    GetEstimatedTime(), true);
                _pendingSyncLossOffset = -1;
                _pendingSyncLossBytes = 0;
                position += resync;
                continue;
            }

            ProcessPacket(data.Slice(position, PacketSize), absoluteOffset + position);
            position += PacketSize;
            _packetIndex++;
            if (_options.InventoryOnly && HasCompleteCatalog() &&
                _packetIndex - _lastCatalogChangePacket >= _options.StablePacketCount)
            {
                _stopRequested = true;
                break;
            }
        }

        return position;
    }

    private static int FindSync(ReadOnlySpan<byte> data)
    {
        var limit = data.Length - PacketSize * 3;
        for (var index = 0; index < limit; index++)
        {
            if (data[index] == 0x47 &&
                data[index + PacketSize] == 0x47 &&
                data[index + PacketSize * 2] == 0x47 &&
                data[index + PacketSize * 3] == 0x47)
                return index;
        }
        return -1;
    }

    private void ProcessPacket(ReadOnlySpan<byte> packet, long fileOffset)
    {
        var transportError = (packet[1] & 0x80) != 0;
        var payloadStart = (packet[1] & 0x40) != 0;
        var pid = ((packet[1] & 0x1F) << 8) | packet[2];
        var adaptationControl = (packet[3] >> 4) & 0x03;
        var continuityCounter = packet[3] & 0x0F;
        var hasAdaptation = (adaptationControl & 0x02) != 0;
        var hasPayload = (adaptationControl & 0x01) != 0;

        var state = GetPidState(pid);
        state.Summary.PacketCount++;
        if (_timelineStarted)
        {
            state.TimelineIntervalPacketCount++;
            _timelineIntervalPacketCount++;
        }
        if (hasPayload)
            state.Summary.PayloadPacketCount++;

        if (transportError)
        {
            state.Summary.TransportErrors++;
            if (!_options.InventoryOnly)
                AddEvent(TsCheckSeverity.Error, TsCheckEventType.TransportError, pid, _packetIndex, fileOffset,
                    TsCheckMessageCode.TransportError, [], GetPacketEventTime(state), true);
        }

        if (adaptationControl == 0)
        {
            if (!_options.InventoryOnly)
                AddEvent(TsCheckSeverity.Error, TsCheckEventType.InvalidPacketHeader, pid, _packetIndex, fileOffset,
                    TsCheckMessageCode.InvalidAdaptationControl, [], GetPacketEventTime(state), true);
            return;
        }

        var payloadOffset = 4;
        var discontinuity = false;
        ReadOnlySpan<byte> pcrBytes = default;
        if (hasAdaptation)
        {
            var adaptationLength = packet[4];
            if (adaptationLength > 183)
            {
                if (!_options.InventoryOnly)
                    AddEvent(TsCheckSeverity.Error, TsCheckEventType.SyncLoss, pid, _packetIndex, fileOffset,
                        TsCheckMessageCode.InvalidAdaptationLength, [adaptationLength], GetPacketEventTime(state), true);
                return;
            }

            payloadOffset += adaptationLength + 1;
            if (adaptationLength > 0)
            {
                var flags = packet[5];
                discontinuity = (flags & 0x80) != 0;
                if ((flags & 0x10) != 0 && adaptationLength >= 7)
                    pcrBytes = packet.Slice(6, 6);
            }
        }

        if (discontinuity)
        {
            state.ResetContinuity();
            state.ResetTimestamps();
            if (_streamPidPrograms.TryGetValue(pid, out var programNumber) && _programClocks.TryGetValue(programNumber, out var clock))
                clock.ResetSync();
        }

        if (!pcrBytes.IsEmpty)
        {
            if (!_options.InventoryOnly)
                ProcessPcr(pid, pcrBytes, state, fileOffset, discontinuity);
        }

        // Null packet 的 CC 没有连续性语义，不参与丢包判断。
        if (!_options.InventoryOnly && hasPayload && payloadOffset < PacketSize && pid != 0x1FFF &&
            !ProcessContinuity(packet, pid, state, continuityCounter, fileOffset))
            return;

        if (!hasPayload || payloadOffset >= PacketSize)
            return;

        var payload = packet[payloadOffset..];
        if (pid == 0 || _pmtPidPrograms.ContainsKey(pid) ||
            (_options.IncludeServiceMetadata && pid == 0x0011))
            ProcessPsi(pid, payload, payloadStart, fileOffset);

        if (_streamPidPrograms.ContainsKey(pid))
        {
            if (_options.InventoryOnly)
                ProbeMpegAudioLayer(payload, payloadStart, state);
            else
                ProcessPes(pid, payload, payloadStart, state, fileOffset);
        }
    }

    private bool ProcessContinuity(ReadOnlySpan<byte> packet, int pid, PidState state, int counter, long fileOffset)
    {
        // CC 只对带 payload 的包递增；仅在 CC 重复时用 SIMD 优化的 SequenceEqual 比较上一包。
        var packetBody = packet[4..];
        if (state.HasContinuity)
        {
            var expected = (state.LastContinuityCounter + 1) & 0x0F;
            if (counter == state.LastContinuityCounter)
            {
                state.Summary.DuplicatePackets++;
                var same = packetBody.SequenceEqual(state.LastPacketBody);
                AddEvent(same ? TsCheckSeverity.Warning : TsCheckSeverity.Error,
                    same ? TsCheckEventType.DuplicatePacket : TsCheckEventType.ConflictingDuplicate,
                    pid, _packetIndex, fileOffset,
                    same ? TsCheckMessageCode.DuplicatePacket : TsCheckMessageCode.ConflictingDuplicate,
                    [counter],
                    GetPacketEventTime(state), true);
                return false;
            }
            else if (counter != expected)
            {
                state.Summary.ContinuityErrors++;
                // payload 已不连续，丢弃跨包的 PSI/PES 半成品，避免错误拼接出伪 section 或时间戳。
                state.PesHeaderLength = 0;
                if (_psiAssemblers.TryGetValue(pid, out var assembler))
                    assembler.Discard();
                var missingModulo = (counter - expected + 16) & 0x0F;
                AddEvent(TsCheckSeverity.Error, TsCheckEventType.ContinuityGap, pid, _packetIndex, fileOffset,
                    TsCheckMessageCode.ContinuityGap, [expected, counter, Math.Max(1, missingModulo)],
                    GetPacketEventTime(state), true);
            }
        }

        state.HasContinuity = true;
        state.LastContinuityCounter = counter;
        packetBody.CopyTo(state.LastPacketBody);
        return true;
    }

    private void ProcessPcr(int pid, ReadOnlySpan<byte> bytes, PidState state, long fileOffset, bool discontinuity)
    {
        // PCR base 与 PTS/DTS 同为 90 kHz，先展开 33 位回绕，再检查倒退、跳变和长间隔。
        var raw = ((long)bytes[0] << 25) |
                  ((long)bytes[1] << 17) |
                  ((long)bytes[2] << 9) |
                  ((long)bytes[3] << 1) |
                  ((long)bytes[4] >> 7);
        var pcr = Unwrap(raw, state.LastRawPcr, state.PcrWrapOffset);
        state.LastRawPcr = raw;
        state.PcrWrapOffset = pcr - raw;

        if (!discontinuity && state.LastPcr90k != long.MinValue)
        {
            var delta = (pcr - state.LastPcr90k) / 90_000.0;
            if (delta < -0.001)
            {
                AddEvent(TsCheckSeverity.Error, TsCheckEventType.PcrBackward, pid, _packetIndex, fileOffset,
                    TsCheckMessageCode.PcrBackward, [-delta], ptsToSeconds(state.LastPcr90k), false);
            }
            else if (delta > TimestampJumpThresholdSeconds)
            {
                AddEvent(TsCheckSeverity.Error, TsCheckEventType.PcrJump, pid, _packetIndex, fileOffset,
                    TsCheckMessageCode.PcrJump, [delta], ptsToSeconds(pcr), false);
            }
            else if (delta > PcrGapThresholdSeconds)
            {
                AddEvent(TsCheckSeverity.Warning, TsCheckEventType.PcrGap, pid, _packetIndex, fileOffset,
                    TsCheckMessageCode.PcrGap, [delta], ptsToSeconds(pcr), false);
            }
        }

        state.LastPcr90k = pcr;
        _lastKnownPcr90k = pcr;
        _lastKnownClock90k = pcr;
        SetTimelineOrigin(pcr);
        UpdateTimeline(pid, ptsToSeconds(pcr), discontinuity);
        if (_pcrPidPrograms.TryGetValue(pid, out var programNumber))
            CheckAvSyncAtPcr(programNumber, pcr, fileOffset);
    }

    private void ProcessPsi(int pid, ReadOnlySpan<byte> payload, bool payloadStart, long fileOffset)
    {
        if (!_psiAssemblers.TryGetValue(pid, out var assembler))
        {
            assembler = new PsiAssembler();
            _psiAssemblers[pid] = assembler;
        }

        assembler.Push(payload, payloadStart, section => ProcessPsiSection(pid, section, fileOffset));
    }

    private void ProcessPsiSection(int pid, ReadOnlySpan<byte> section, long fileOffset)
    {
        if (section.Length < 8)
            return;

        if (!HasValidPsiCrc(section))
        {
            AddEvent(TsCheckSeverity.Error, TsCheckEventType.PsiCrcError, pid, _packetIndex, fileOffset,
                TsCheckMessageCode.PsiCrcError, [$"0x{section[0]:X2}"], GetEstimatedTime(), true);
            return;
        }

        if (pid == 0 && section[0] == 0x00)
            ParsePat(section);
        else if (_pmtPidPrograms.TryGetValue(pid, out var programNumber) && section[0] == 0x02)
            ParsePmt(programNumber, pid, section, fileOffset);
        else if (_options.IncludeServiceMetadata && pid == 0x0011 && section[0] == 0x42)
            ParseSdt(section);
    }

    private void ParsePat(ReadOnlySpan<byte> section)
    {
        _result.TransportStreamId = (section[3] << 8) | section[4];
        _result.PatVersion = (byte)((section[5] >> 1) & 0x1F);
        var sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        var end = Math.Min(section.Length - 4, 3 + sectionLength - 4);
        for (var offset = 8; offset + 4 <= end; offset += 4)
        {
            var programNumber = (section[offset] << 8) | section[offset + 1];
            if (programNumber == 0)
                continue;

            var pmtPid = ((section[offset + 2] & 0x1F) << 8) | section[offset + 3];
            var catalogChanged = !_pmtPidPrograms.TryGetValue(pmtPid, out var oldProgramNumber) ||
                                 oldProgramNumber != programNumber;
            _pmtPidPrograms[pmtPid] = programNumber;
            _psiAssemblers.TryAdd(pmtPid, new PsiAssembler());
            var pmtSummary = GetPidState(pmtPid).Summary;
            pmtSummary.ProgramNumber = programNumber;
            pmtSummary.IsPmtPid = true;
            if (!_result.Programs.TryGetValue(programNumber, out var program))
            {
                program = new TsCheckProgramSummary { ProgramNumber = programNumber, PmtPid = pmtPid };
                _result.Programs[programNumber] = program;
                catalogChanged = true;
            }
            else
            {
                catalogChanged |= program.PmtPid != pmtPid;
                program.PmtPid = pmtPid;
            }
            if (catalogChanged)
                _lastCatalogChangePacket = _packetIndex;
        }
    }

    private void ParsePmt(int programNumber, int pmtPid, ReadOnlySpan<byte> section, long fileOffset)
    {
        if (section.Length < 16)
            return;

        var sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        var sectionEnd = Math.Min(section.Length - 4, 3 + sectionLength - 4);
        var pcrPid = ((section[8] & 0x1F) << 8) | section[9];
        var programInfoLength = ((section[10] & 0x0F) << 8) | section[11];
        var offset = 12 + programInfoLength;
        if (offset > sectionEnd)
        {
            AddEvent(TsCheckSeverity.Error, TsCheckEventType.PsiMalformed, pmtPid, _packetIndex, fileOffset,
                TsCheckMessageCode.PmtInfoLength, [], GetEstimatedTime(), true);
            return;
        }

        if (!_result.Programs.TryGetValue(programNumber, out var program))
        {
            program = new TsCheckProgramSummary { ProgramNumber = programNumber, PmtPid = pmtPid };
            _result.Programs[programNumber] = program;
        }

        var pmtVersion = (byte)((section[5] >> 1) & 0x1F);
        var rebuildDefinitions = program.StreamDefinitions.Count == 0 || program.PmtVersion != pmtVersion;
        var catalogChanged = rebuildDefinitions || program.PcrPid != pcrPid;
        program.PcrPid = pcrPid;
        if (rebuildDefinitions)
        {
            program.PmtVersion = pmtVersion;
            program.ProgramDescriptors = section.Slice(12, programInfoLength).ToArray();
            program.StreamDefinitions.Clear();
        }
        _pcrPidPrograms[pcrPid] = programNumber;
        GetPidState(pcrPid).Summary.IsPcrPid = true;
        _programClocks.TryAdd(programNumber, new ProgramClockState());

        while (offset + 5 <= sectionEnd)
        {
            var originalStreamType = section[offset];
            var elementaryPid = ((section[offset + 1] & 0x1F) << 8) | section[offset + 2];
            var infoLength = ((section[offset + 3] & 0x0F) << 8) | section[offset + 4];
            if (offset + 5 + infoLength > sectionEnd)
                break;

            // DVB 常把 AC-3/E-AC-3 标成 private data (0x06)，需要结合 descriptor 还原实际类型。
            var descriptors = section.Slice(offset + 5, infoLength);
            var streamInfo = ResolveStreamInfo(originalStreamType, descriptors);
            program.Streams[elementaryPid] = streamInfo.StreamType;
            if (rebuildDefinitions)
            {
                program.StreamDefinitions[elementaryPid] = new TsStreamDefinition
                {
                    StreamType = originalStreamType,
                    Descriptors = descriptors.ToArray()
                };
            }
            _streamPidPrograms[elementaryPid] = programNumber;
            var summary = GetPidState(elementaryPid).Summary;
            summary.StreamType = streamInfo.StreamType;
            summary.SupplementaryStreamType = streamInfo.SupplementaryStreamType;
            summary.Language = streamInfo.Language;
            summary.ProgramNumber = programNumber;
            offset += 5 + infoLength;
        }
        if (catalogChanged)
            _lastCatalogChangePacket = _packetIndex;
    }

    private void ParseSdt(ReadOnlySpan<byte> section)
    {
        if (section.Length < 15)
            return;

        _sdtSeen = true;
        var sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        var sectionEnd = Math.Min(section.Length - 4, 3 + sectionLength - 4);
        var version = (byte)((section[5] >> 1) & 0x1F);
        var originalNetworkId = (section[8] << 8) | section[9];
        for (var offset = 11; offset + 5 <= sectionEnd;)
        {
            var serviceId = (section[offset] << 8) | section[offset + 1];
            var descriptorLength = ((section[offset + 3] & 0x0F) << 8) | section[offset + 4];
            var descriptorOffset = offset + 5;
            if (descriptorOffset + descriptorLength > sectionEnd)
                break;

            var descriptors = section.Slice(descriptorOffset, descriptorLength);
            var serviceType = (byte)0;
            var serviceName = string.Empty;
            var providerName = string.Empty;
            ParseServiceDescriptor(descriptors, ref serviceType, ref providerName, ref serviceName);

            var changed = !_result.Services.TryGetValue(serviceId, out var service) ||
                          service.SdtVersion != version ||
                          !service.Descriptors.AsSpan().SequenceEqual(descriptors);
            service ??= new TsServiceSummary { ServiceId = serviceId };
            service.ServiceName = serviceName;
            service.ProviderName = providerName;
            service.ServiceType = serviceType;
            service.SdtVersion = version;
            service.OriginalNetworkId = originalNetworkId;
            service.EitSchedule = (section[offset + 2] & 0x02) != 0;
            service.EitPresentFollowing = (section[offset + 2] & 0x01) != 0;
            service.RunningStatus = (byte)((section[offset + 3] >> 5) & 0x07);
            service.FreeCaMode = (section[offset + 3] & 0x10) != 0;
            service.Descriptors = descriptors.ToArray();
            _result.Services[serviceId] = service;
            if (changed)
                _lastCatalogChangePacket = _packetIndex;
            offset = descriptorOffset + descriptorLength;
        }
    }

    private static void ParseServiceDescriptor(
        ReadOnlySpan<byte> descriptors,
        ref byte serviceType,
        ref string providerName,
        ref string serviceName)
    {
        for (var offset = 0; offset + 2 <= descriptors.Length;)
        {
            var tag = descriptors[offset];
            var length = descriptors[offset + 1];
            if (offset + 2 + length > descriptors.Length)
                return;
            if (tag == 0x48 && length >= 3)
            {
                var body = descriptors.Slice(offset + 2, length);
                serviceType = body[0];
                var providerLength = body[1];
                if (2 + providerLength >= body.Length)
                    return;
                providerName = DecodeDvbText(body.Slice(2, providerLength));
                var nameLengthOffset = 2 + providerLength;
                var nameLength = body[nameLengthOffset];
                if (nameLengthOffset + 1 + nameLength <= body.Length)
                    serviceName = DecodeDvbText(body.Slice(nameLengthOffset + 1, nameLength));
                return;
            }
            offset += 2 + length;
        }
    }

    private static string DecodeDvbText(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
            return string.Empty;

        // DVB 文本以首字节选择字符集；优先覆盖广播中常见的 UTF-8/UTF-16，
        // 未声明字符集时按 ISO-8859-1 安全回退，无法识别的控制字符不会进入文件名或界面。
        Encoding encoding = Encoding.Latin1;
        var offset = 0;
        if (value[0] == 0x15)
        {
            encoding = Encoding.UTF8;
            offset = 1;
        }
        else if (value[0] == 0x11)
        {
            encoding = Encoding.BigEndianUnicode;
            offset = 1;
        }
        else if (value[0] is >= 0x01 and <= 0x0B)
        {
            var isoPart = value[0] switch
            {
                0x01 => 5,
                0x02 => 6,
                0x03 => 7,
                0x04 => 8,
                0x05 => 9,
                0x06 => 10,
                0x07 => 11,
                0x09 => 13,
                0x0A => 14,
                0x0B => 15,
                _ => 0
            };
            if (isoPart > 0)
                encoding = Encoding.GetEncoding($"ISO-8859-{isoPart}");
            offset = 1;
        }
        else if (value[0] == 0x10 && value.Length >= 3)
        {
            if (value[1] == 0 && value[2] is >= 1 and <= 15 && value[2] != 12)
                encoding = Encoding.GetEncoding($"ISO-8859-{value[2]}");
            offset = 3;
        }

        var textBytes = value[offset..];
        var text = encoding.GetString(textBytes).Trim();
        var highByteCount = 0;
        foreach (var item in textBytes)
        {
            if (item >= 0x80)
                highByteCount++;
        }
        if (offset == 0 && highByteCount >= 2)
        {
            var gbText = Gb18030Encoding.GetString(textBytes).Trim();
            // 国内 DVB 前端常把未带字符集标识的 GB2312/GBK 直接放入 SDT。
            // 仅在解码结果明确包含多个中日韩字符时采用 GB18030，避免影响标准拉丁文本。
            if (gbText.Count(IsCjkCharacter) >= 2)
                text = gbText;
        }
        return string.Concat(text.Where(character => !char.IsControl(character)));
    }

    private static Encoding CreateGb18030Encoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding("GB18030", EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    }

    private static bool IsCjkCharacter(char value) => value is >= '\u3400' and <= '\u9FFF';

    private bool HasCompleteCatalog()
    {
        if (_result.Programs.Count == 0)
            return false;
        foreach (var program in _result.Programs.Values)
        {
            if (program.Streams.Count == 0)
                return false;
            if (_options.IncludeServiceMetadata && _sdtSeen && !_result.Services.ContainsKey(program.ProgramNumber))
                return false;
        }
        return true;
    }

    private void ProcessPes(int pid, ReadOnlySpan<byte> payload, bool payloadStart, PidState state, long fileOffset)
    {
        ProbeMpegAudioLayer(payload, payloadStart, state);

        // 仅解析 PES 头中的 PTS/DTS，不读取 ES 帧内容，扫描速度不受编码复杂度和分辨率影响。
        if (payloadStart)
            state.PesHeaderLength = 0;
        else if (state.PesHeaderLength == 0)
            return;

        if (payloadStart && payload.Length >= 9)
        {
            var expectedLength = 9 + payload[8];
            if (expectedLength <= payload.Length)
            {
                ParsePesHeader(pid, payload[..expectedLength], state, fileOffset);
                return;
            }
        }

        // adaptation field 很大时 PES 头可能跨包，最多缓存 64 字节直到完整头到齐。
        var copyLength = Math.Min(payload.Length, state.PesHeader.Length - state.PesHeaderLength);
        payload[..copyLength].CopyTo(state.PesHeader.AsSpan(state.PesHeaderLength));
        state.PesHeaderLength += copyLength;
        if (state.PesHeaderLength < 9)
            return;

        var totalHeaderLength = 9 + state.PesHeader[8];
        if (totalHeaderLength > state.PesHeader.Length)
        {
            state.PesHeaderLength = 0;
            return;
        }
        if (state.PesHeaderLength < totalHeaderLength)
            return;

        ParsePesHeader(pid, state.PesHeader.AsSpan(0, totalHeaderLength), state, fileOffset);
        state.PesHeaderLength = 0;
    }

    private static void ProbeMpegAudioLayer(ReadOnlySpan<byte> payload, bool payloadStart, PidState state)
    {
        if (state.Summary.MpegAudioLayer is not null ||
            state.Summary.StreamType is not (TsStreamTypes.Mpeg1Audio or TsStreamTypes.Mpeg2Audio) ||
            state.MpegAudioProbeBytes >= MaxMpegAudioProbeBytes)
        {
            return;
        }

        if (payloadStart)
        {
            state.MpegAudioProbeTailLength = 0;
            if (payload.Length < 9 || payload[0] != 0 || payload[1] != 0 || payload[2] != 1)
                return;

            var elementaryStreamOffset = 9 + payload[8];
            if (elementaryStreamOffset > payload.Length)
                return;
            payload = payload[elementaryStreamOffset..];
        }

        var remainingProbeBytes = MaxMpegAudioProbeBytes - state.MpegAudioProbeBytes;
        if (payload.IsEmpty || remainingProbeBytes <= 0)
            return;
        if (payload.Length > remainingProbeBytes)
            payload = payload[..remainingProbeBytes];

        // MPEG Audio 帧头只有 4 字节；拼接上个 TS 包末尾 3 字节即可覆盖所有跨包情况。
        Span<byte> probe = stackalloc byte[PacketSize];
        state.MpegAudioProbeTail.AsSpan(0, state.MpegAudioProbeTailLength).CopyTo(probe);
        payload.CopyTo(probe[state.MpegAudioProbeTailLength..]);
        var probeLength = state.MpegAudioProbeTailLength + payload.Length;

        for (var offset = 0; offset + 4 <= probeLength; offset++)
        {
            var header = BinaryPrimitives.ReadUInt32BigEndian(probe.Slice(offset, 4));
            if ((header & 0xFFE00000u) != 0xFFE00000u)
                continue;

            var versionBits = (header >> 19) & 0x03;
            var layerBits = (header >> 17) & 0x03;
            var bitrateIndex = (header >> 12) & 0x0F;
            var sampleRateIndex = (header >> 10) & 0x03;
            if (versionBits == 0x01 || layerBits == 0 ||
                bitrateIndex is 0 or 0x0F || sampleRateIndex == 0x03)
            {
                continue;
            }

            state.Summary.MpegAudioLayer = layerBits switch
            {
                0x03 => TsMpegAudioLayer.LayerI,
                0x02 => TsMpegAudioLayer.LayerII,
                _ => TsMpegAudioLayer.LayerIII
            };
            state.MpegAudioProbeTailLength = 0;
            return;
        }

        state.MpegAudioProbeBytes += payload.Length;
        state.MpegAudioProbeTailLength = Math.Min(3, probeLength);
        probe.Slice(probeLength - state.MpegAudioProbeTailLength, state.MpegAudioProbeTailLength)
            .CopyTo(state.MpegAudioProbeTail);
    }

    private void ParsePesHeader(int pid, ReadOnlySpan<byte> payload, PidState state, long fileOffset)
    {
        if (payload.Length < 9 || payload[0] != 0 || payload[1] != 0 || payload[2] != 1)
            return;

        var flags = (payload[7] >> 6) & 0x03;
        long? rawPts = null;
        long? rawDts = null;
        if ((flags & 0x02) != 0 && payload.Length >= 14)
            rawPts = ReadTimestamp(payload[9..14]);
        if (flags == 0x03 && payload.Length >= 19)
            rawDts = ReadTimestamp(payload[14..19]);

        if (rawPts is not null)
        {
            var pts = Unwrap(rawPts.Value, state.LastRawPts, state.PtsWrapOffset);
            state.LastRawPts = rawPts.Value;
            state.PtsWrapOffset = pts - rawPts.Value;
            CheckPts(pid, state, pts, fileOffset);
            state.LastPts90k = pts;
            _lastKnownClock90k = pts;
            SetTimelineOrigin(pts);
            UpdateStreamClock(pid, pts);
        }

        if (rawDts is not null)
        {
            var dts = Unwrap(rawDts.Value, state.LastRawDts, state.DtsWrapOffset);
            state.LastRawDts = rawDts.Value;
            state.DtsWrapOffset = dts - rawDts.Value;
            if (state.LastDts90k != long.MinValue && dts + 90 < state.LastDts90k)
            {
                AddEvent(TsCheckSeverity.Error, TsCheckEventType.DtsBackward, pid, _packetIndex, fileOffset,
                    TsCheckMessageCode.DtsBackward, [(state.LastDts90k - dts) / 90_000.0], ptsToSeconds(dts), false);
            }
            if (rawPts is not null && dts > state.LastPts90k + 90)
            {
                AddEvent(TsCheckSeverity.Error, TsCheckEventType.DtsAfterPts, pid, _packetIndex, fileOffset,
                    TsCheckMessageCode.DtsAfterPts, [(dts - state.LastPts90k) / 90_000.0], ptsToSeconds(state.LastPts90k), false);
            }
            state.LastDts90k = dts;
        }
    }

    private static ResolvedStreamInfo ResolveStreamInfo(byte streamType, ReadOnlySpan<byte> descriptors)
    {
        var resolvedStreamType = streamType;
        TsSupplementaryStreamType? supplementaryStreamType = null;
        string? language = null;
        int? componentTag = null;
        int? dataComponentId = null;

        for (var offset = 0; offset + 2 <= descriptors.Length;)
        {
            var tag = descriptors[offset];
            var length = descriptors[offset + 1];
            if (offset + 2 + length > descriptors.Length)
                break;

            // registration_descriptor 用 4 字节格式标识补充 PMT 的私有 stream_type。
            if (tag == 0x05 && length >= 4)
            {
                var registration = descriptors.Slice(offset + 2, 4);
                if (registration.SequenceEqual("AC-3"u8))
                    resolvedStreamType = TsStreamTypes.Ac3;
                else if (registration.SequenceEqual("EAC3"u8))
                    resolvedStreamType = TsStreamTypes.Eac3;
                else if (registration.SequenceEqual("DTS1"u8) ||
                    registration.SequenceEqual("DTS2"u8) ||
                    registration.SequenceEqual("DTS3"u8))
                    resolvedStreamType = TsStreamTypes.Dts;
                else if (registration.SequenceEqual("HEVC"u8))
                    resolvedStreamType = TsStreamTypes.Hevc;
                else if (registration.SequenceEqual("VVC "u8))
                    resolvedStreamType = TsStreamTypes.Vvc;
                else if (registration.SequenceEqual("VC-1"u8))
                    resolvedStreamType = TsStreamTypes.Vc1;
                else if (registration.SequenceEqual("drac"u8))
                    resolvedStreamType = TsStreamTypes.Dirac;
                else if (registration.SequenceEqual("AC-4"u8))
                    supplementaryStreamType = TsSupplementaryStreamType.Ac4;
                else if (registration.SequenceEqual("Opus"u8))
                    supplementaryStreamType = TsSupplementaryStreamType.Opus;
                else if (registration.SequenceEqual("BSSD"u8))
                    supplementaryStreamType = TsSupplementaryStreamType.Smpte302M;
                else if (registration.SequenceEqual("DRA1"u8))
                    supplementaryStreamType = TsSupplementaryStreamType.Dra;
                else if (registration.SequenceEqual("KLVA"u8))
                    supplementaryStreamType = TsSupplementaryStreamType.SmpteKlv;
                else if (registration.SequenceEqual("VANC"u8))
                    supplementaryStreamType = TsSupplementaryStreamType.Smpte2038;
                else if (registration.SequenceEqual("ID3 "u8))
                    supplementaryStreamType = TsSupplementaryStreamType.TimedId3;
            }

            if (tag == 0x6A)
                resolvedStreamType = TsStreamTypes.Ac3;
            else if (tag == 0x7A)
                resolvedStreamType = TsStreamTypes.Eac3Atsc;
            else if (tag == 0x7B)
                resolvedStreamType = TsStreamTypes.Dts;
            else if (tag == 0x7C)
                resolvedStreamType = TsStreamTypes.Aac;
            else if (tag == 0x59)
            {
                supplementaryStreamType = TsSupplementaryStreamType.DvbSubtitle;
                language = ReadDescriptorLanguages(descriptors.Slice(offset + 2, length), 8);
            }
            else if (tag == 0x56)
            {
                supplementaryStreamType = TsSupplementaryStreamType.DvbTeletext;
                language = ReadDescriptorLanguages(descriptors.Slice(offset + 2, length), 5);
            }
            else if (tag == 0xFD && length >= 2)
            {
                dataComponentId = (descriptors[offset + 2] << 8) | descriptors[offset + 3];
            }
            else if (tag == 0x52 && length >= 1)
            {
                componentTag = descriptors[offset + 2];
            }
            else if (tag == 0x0A && string.IsNullOrEmpty(language))
            {
                language = ReadDescriptorLanguages(descriptors.Slice(offset + 2, length), 4);
            }
            offset += 2 + length;
        }

        // FFmpeg 还会结合 stream_identifier 校验 ARIB 字幕的 profile，避免仅凭 data_component_id 误报普通数据流。
        if (streamType == TsStreamTypes.PrivateData &&
            (dataComponentId == 0x0008 && componentTag is >= 0x30 and <= 0x37 ||
             dataComponentId == 0x0012 && componentTag == 0x87))
        {
            supplementaryStreamType = TsSupplementaryStreamType.AribCaption;
        }
        return new ResolvedStreamInfo(resolvedStreamType, supplementaryStreamType, language);
    }

    private static string? ReadDescriptorLanguages(ReadOnlySpan<byte> descriptor, int entrySize)
    {
        string? result = null;
        for (var offset = 0; offset + 3 <= descriptor.Length; offset += entrySize)
        {
            if (offset + entrySize > descriptor.Length)
                break;
            var code = string.Create(3,
                (descriptor[offset], descriptor[offset + 1], descriptor[offset + 2]),
                static (span, bytes) =>
                {
                    span[0] = (char)bytes.Item1;
                    span[1] = (char)bytes.Item2;
                    span[2] = (char)bytes.Item3;
                });
            result = result is null ? code : result + ", " + code;
        }
        return result;
    }

    private readonly record struct ResolvedStreamInfo(
        byte StreamType,
        TsSupplementaryStreamType? SupplementaryStreamType,
        string? Language);

    private void CheckPts(int pid, PidState state, long pts, long fileOffset)
    {
        if (state.LastPts90k == long.MinValue)
            return;

        var delta = (pts - state.LastPts90k) / 90_000.0;
        var isVideo = state.Summary.StreamType is { } type && TsStreamTypes.IsVideo(type);
        if (!isVideo && delta < -0.001)
        {
            AddEvent(TsCheckSeverity.Error, TsCheckEventType.PtsBackward, pid, _packetIndex, fileOffset,
                TsCheckMessageCode.PtsBackward, [-delta], ptsToSeconds(pts), false);
        }
        else if (delta > TimestampJumpThresholdSeconds)
        {
            AddEvent(TsCheckSeverity.Error, TsCheckEventType.PtsJump, pid, _packetIndex, fileOffset,
                TsCheckMessageCode.PtsJump, [delta], ptsToSeconds(pts), false);
        }
        else if (delta > 2 && !isVideo)
        {
            AddEvent(TsCheckSeverity.Warning, TsCheckEventType.StreamGap, pid, _packetIndex, fileOffset,
                TsCheckMessageCode.AudioGap, [delta], ptsToSeconds(pts), false);
        }
    }

    private void UpdateStreamClock(int pid, long pts)
    {
        // 用首个稳定的音视频 PTS 差建立基线，仅报告相对基线的漂移，避免误报合法固定延迟。
        if (!_streamPidPrograms.TryGetValue(pid, out var programNumber) ||
            !_programClocks.TryGetValue(programNumber, out var clock) ||
            !_result.Programs.TryGetValue(programNumber, out var program) ||
            !program.Streams.TryGetValue(pid, out var streamType))
            return;

        if (TsStreamTypes.IsVideo(streamType))
        {
            if (clock.VideoPid < 0 || clock.VideoPid == pid)
            {
                clock.VideoPid = pid;
                clock.VideoPts90k = pts;
                if (clock.FirstVideoPts90k == long.MinValue)
                    clock.FirstVideoPts90k = pts;
            }
        }
        else if (TsStreamTypes.IsAudio(streamType, _result.Pids[pid].SupplementaryStreamType))
        {
            clock.AudioPts90k[pid] = pts;
            clock.FirstAudioPts90k.TryAdd(pid, pts);
        }
        else
        {
            return;
        }

    }

    private void CheckAvSyncAtPcr(int programNumber, long pcr, long fileOffset)
    {
        if (!_programClocks.TryGetValue(programNumber, out var clock) ||
            clock.FirstVideoPts90k == long.MinValue ||
            (clock.LastSyncSamplePcr90k != long.MinValue && pcr - clock.LastSyncSamplePcr90k < 90_000))
            return;

        clock.LastSyncSamplePcr90k = pcr;
        foreach (var pair in clock.AudioPts90k)
        {
            if (!clock.FirstAudioPts90k.TryGetValue(pair.Key, out var firstAudioPts))
                continue;

            // 分别计算两条轨道从各自起点推进的时长，消除 TS 复用交错顺序造成的瞬时偏移。
            var videoElapsed = clock.VideoPts90k - clock.FirstVideoPts90k;
            var audioElapsed = pair.Value - firstAudioPts;
            var drift = (audioElapsed - videoElapsed) / 90_000.0;
            if (Math.Abs(drift) >= AvDriftThresholdSeconds)
            {
                var consecutiveCount = clock.SyncDriftCounts.GetValueOrDefault(pair.Key) + 1;
                clock.SyncDriftCounts[pair.Key] = consecutiveCount;
                if (consecutiveCount >= 3 &&
                    (clock.LastDriftReport90k == long.MinValue || pcr - clock.LastDriftReport90k >= 450_000))
                {
                    AddEvent(TsCheckSeverity.Warning, TsCheckEventType.AvSyncDrift, pair.Key, _packetIndex, fileOffset,
                        TsCheckMessageCode.AvSyncDrift,
                        [(firstAudioPts - clock.FirstVideoPts90k) / 90.0, (pair.Value - clock.VideoPts90k) / 90.0, drift * 1000],
                        ptsToSeconds(pcr), false);
                    clock.LastDriftReport90k = pcr;
                }
            }
            else
            {
                clock.SyncDriftCounts[pair.Key] = 0;
            }
        }
    }

    private PidState GetPidState(int pid)
    {
        if (_pidStates.TryGetValue(pid, out var state))
            return state;

        var summary = new TsCheckPidSummary { Pid = pid };
        _result.Pids[pid] = summary;
        state = new PidState(summary);
        _pidStates[pid] = state;
        return state;
    }

    private void AddEvent(
        TsCheckSeverity severity, TsCheckEventType type, int pid, long packetIndex,
        long fileOffset, TsCheckMessageCode messageCode, object[] messageArguments, double? timeSeconds, bool estimated)
    {
        if (_options.InventoryOnly)
            return;

        if (severity == TsCheckSeverity.Error)
            _errorCount++;
        else if (severity == TsCheckSeverity.Warning)
            _warningCount++;

        if (pid >= 0)
        {
            var summary = GetPidState(pid).Summary;
            if (severity == TsCheckSeverity.Error)
                summary.ErrorCount++;
            else if (severity == TsCheckSeverity.Warning)
                summary.WarningCount++;
        }
        else if (severity == TsCheckSeverity.Error)
        {
            _globalErrorCount++;
        }
        else if (severity == TsCheckSeverity.Warning)
        {
            _globalWarningCount++;
        }

        TsCheckEvent? item = null;
        var isNewEvent = false;
        if (_result.Events.Count > 0)
        {
            var previous = _result.Events[^1];
            // 邻近同类异常合并为一个区间，避免严重坏流生成海量 UI 行和报告文本。
            if (previous.Type == type && previous.Pid == pid && packetIndex - previous.EndPacket <= 256)
            {
                previous.EndPacket = packetIndex;
                previous.Occurrences++;
                item = previous;
            }
        }

        if (item is null)
        {
            if (_result.Events.Count >= MaxStoredEvents)
            {
                _result.OmittedEventCount++;
                return;
            }

            item = new TsCheckEvent
            {
                Severity = severity,
                Type = type,
                Pid = pid,
                StartPacket = packetIndex,
                EndPacket = packetIndex,
                FileOffset = fileOffset,
                SourceTimeSeconds = timeSeconds is { } value
                    ? value + (_timelineOrigin90k == long.MinValue ? 0 : _timelineOrigin90k / 90_000.0)
                    : null,
                TimeSeconds = timeSeconds,
                IsEstimatedTime = estimated,
                MessageCode = messageCode,
                MessageArguments = messageArguments
            };
            _result.Events.Add(item);
            isNewEvent = true;
        }

        // 暂存一个新事件，在下一次 10 Hz 进度回调中交给 UI，错误风暴也不会突破刷新上限。
        if (isNewEvent)
            _pendingProgressEvent ??= item;
        ReportProgress(null, force: false);
    }

    private void ReportProgress(TsCheckEvent? newEvent, bool force)
    {
        if (_progress is null)
            return;

        var elapsedTicks = _stopwatch.ElapsedTicks;
        if (!force && elapsedTicks - _lastProgressTicks < Stopwatch.Frequency / 10)
            return;

        _lastProgressTicks = elapsedTicks;
        var bytesScanned = _syncOffset < 0 ? 0 : Math.Min(_result.FileSize, _syncOffset + _packetIndex * PacketSize);
        _result.BytesScanned = bytesScanned;
        var progressEvent = newEvent ?? _pendingProgressEvent;
        _pendingProgressEvent = null;
        // PID 数量通常很少，每 100 ms 复制一次轻量快照可让摘要准确实时更新，且不暴露后台字典给 UI 线程。
        var pidSnapshot = new TsCheckPidProgress[_result.Pids.Count + (_globalErrorCount + _globalWarningCount > 0 ? 1 : 0)];
        var snapshotIndex = 0;
        if (_globalErrorCount + _globalWarningCount > 0)
        {
            pidSnapshot[snapshotIndex++] = new TsCheckPidProgress(
                -1, 0, _globalErrorCount, _globalWarningCount, null, null, null, null, null,
                false, false, true);
        }
        foreach (var pid in _result.Pids.Values)
        {
            pidSnapshot[snapshotIndex++] = new TsCheckPidProgress(
                pid.Pid, pid.PacketCount, pid.ErrorCount, pid.WarningCount, pid.ProgramNumber,
                pid.StreamType, pid.MpegAudioLayer, pid.SupplementaryStreamType, pid.Language,
                pid.IsPcrPid, pid.IsPmtPid, false);
        }
        _progress.Report(new TsCheckProgress(
            bytesScanned, _result.FileSize, _packetIndex, _errorCount, _warningCount,
            bytesScanned / Math.Max(0.001, _stopwatch.Elapsed.TotalSeconds), _stopwatch.Elapsed,
            pidSnapshot, progressEvent, _result.Timeline.ToArray()));
    }

    private void CompleteResult(long bytesScanned, bool cancelled)
    {
        CompleteTimeline();
        _result.PacketCount = _packetIndex;
        _result.BytesScanned = Math.Min(_result.FileSize, bytesScanned);
        _result.Elapsed = _stopwatch.Elapsed;
        _result.WasCancelled = cancelled;
        _result.TotalErrorCount = _errorCount;
        _result.TotalWarningCount = _warningCount;
        _result.GlobalErrorCount = _globalErrorCount;
        _result.GlobalWarningCount = _globalWarningCount;
    }

    private void UpdateTimeline(int pid, double clockSeconds, bool discontinuity)
    {
        // 码率以首个已确认的节目 PCR 为参考时钟，避免多个节目交错 PCR 导致时间轴来回跳动。
        if (_timelineReferencePcrPid < 0)
        {
            if (!_pcrPidPrograms.ContainsKey(pid))
                return;
            _timelineReferencePcrPid = pid;
            _result.TimelineReferencePcrPid = pid;
            StartTimeline(clockSeconds);
            return;
        }
        if (pid != _timelineReferencePcrPid || !_timelineStarted)
            return;

        var intervalDuration = clockSeconds - _timelineLastClockSeconds;
        if (discontinuity || intervalDuration <= 0 || intervalDuration > TimestampJumpThresholdSeconds)
        {
            // 时钟不连续时保留已有片段并开启新段，不能把跳变区间平均成虚假的低码率。
            FlushTimelineBucket(_timelineLastClockSeconds - _timelineBucketStartSeconds);
            _timelineSegment++;
            ResetTimelineCounters();
            _timelineBucketStartSeconds = Math.Max(clockSeconds, GetTimelineEndSeconds());
            _timelineLastClockSeconds = _timelineBucketStartSeconds;
            return;
        }

        var cursor = _timelineLastClockSeconds;
        while (cursor < clockSeconds)
        {
            var bucketEnd = _timelineBucketStartSeconds + _timelineBucketDurationSeconds;
            var overlap = Math.Min(clockSeconds, bucketEnd) - cursor;
            if (overlap <= 0)
                break;

            var ratio = overlap / intervalDuration;
            _timelineBucketPacketCount += _timelineIntervalPacketCount * ratio;
            foreach (var state in _pidStates.Values)
                state.TimelineBucketPacketCount += state.TimelineIntervalPacketCount * ratio;

            cursor += overlap;
            if (cursor >= bucketEnd - 0.000_001)
                SealTimelineBucket(_timelineBucketDurationSeconds);
        }

        ResetTimelineIntervalCounters();
        _timelineLastClockSeconds = clockSeconds;
    }

    private void StartTimeline(double clockSeconds)
    {
        _timelineStarted = true;
        _timelineLastClockSeconds = clockSeconds;
        _timelineBucketStartSeconds = clockSeconds;
        ResetTimelineCounters();
    }

    private void CompleteTimeline()
    {
        if (!_timelineStarted)
            return;
        FlushTimelineBucket(_timelineLastClockSeconds - _timelineBucketStartSeconds);
        ResetTimelineIntervalCounters();
        _timelineStarted = false;
    }

    private void FlushTimelineBucket(double durationSeconds)
    {
        if (durationSeconds <= 0 || _timelineBucketPacketCount <= 0)
            return;
        SealTimelineBucket(durationSeconds);
    }

    private void SealTimelineBucket(double durationSeconds)
    {
        var sampleCount = 0;
        foreach (var state in _pidStates.Values)
        {
            if (state.TimelineBucketPacketCount > 0)
                sampleCount++;
        }

        var samples = new TsCheckTimelinePidSample[sampleCount];
        var index = 0;
        foreach (var state in _pidStates.Values)
        {
            if (state.TimelineBucketPacketCount > 0)
            {
                samples[index++] = new TsCheckTimelinePidSample(
                    state.Summary.Pid, state.TimelineBucketPacketCount);
            }
            state.TimelineBucketPacketCount = 0;
        }
        Array.Sort(samples, static (left, right) => left.Pid.CompareTo(right.Pid));

        _result.Timeline.Add(new TsCheckTimelineBucket
        {
            StartSeconds = _timelineBucketStartSeconds,
            DurationSeconds = durationSeconds,
            TotalPacketCount = _timelineBucketPacketCount,
            Segment = _timelineSegment,
            Pids = samples
        });
        _timelineBucketStartSeconds += durationSeconds;
        _timelineBucketPacketCount = 0;

        if (_result.Timeline.Count >= _timelineCompactionThreshold)
            CompactTimeline();
    }

    private void CompactTimeline()
    {
        var source = _result.Timeline;
        var compacted = new List<TsCheckTimelineBucket>((source.Count + 1) / 2);
        for (var index = 0; index < source.Count;)
        {
            var first = source[index++];
            if (index < source.Count && source[index].Segment == first.Segment)
            {
                compacted.Add(MergeTimelineBuckets(first, source[index++]));
            }
            else
            {
                compacted.Add(first);
            }
        }

        source.Clear();
        source.AddRange(compacted);
        _timelineBucketDurationSeconds *= 2;
        // 如果异常分段过多导致本轮无法充分压缩，推迟下一轮，避免每增加一个桶都重复整理。
        _timelineCompactionThreshold = source.Count < MaxTimelineBuckets
            ? MaxTimelineBuckets
            : source.Count + MaxTimelineBuckets;
    }

    private static TsCheckTimelineBucket MergeTimelineBuckets(
        TsCheckTimelineBucket first, TsCheckTimelineBucket second)
    {
        var merged = new TsCheckTimelinePidSample[first.Pids.Length + second.Pids.Length];
        var firstIndex = 0;
        var secondIndex = 0;
        var outputIndex = 0;
        while (firstIndex < first.Pids.Length || secondIndex < second.Pids.Length)
        {
            if (secondIndex >= second.Pids.Length ||
                firstIndex < first.Pids.Length && first.Pids[firstIndex].Pid < second.Pids[secondIndex].Pid)
            {
                merged[outputIndex++] = first.Pids[firstIndex++];
            }
            else if (firstIndex >= first.Pids.Length ||
                     second.Pids[secondIndex].Pid < first.Pids[firstIndex].Pid)
            {
                merged[outputIndex++] = second.Pids[secondIndex++];
            }
            else
            {
                merged[outputIndex++] = new TsCheckTimelinePidSample(
                    first.Pids[firstIndex].Pid,
                    first.Pids[firstIndex].PacketCount + second.Pids[secondIndex].PacketCount);
                firstIndex++;
                secondIndex++;
            }
        }
        if (outputIndex != merged.Length)
            Array.Resize(ref merged, outputIndex);

        return new TsCheckTimelineBucket
        {
            StartSeconds = first.StartSeconds,
            DurationSeconds = first.DurationSeconds + second.DurationSeconds,
            TotalPacketCount = first.TotalPacketCount + second.TotalPacketCount,
            Segment = first.Segment,
            Pids = merged
        };
    }

    private double GetTimelineEndSeconds() => _result.Timeline.Count > 0
        ? _result.Timeline[^1].EndSeconds
        : 0;

    private void ResetTimelineCounters()
    {
        _timelineBucketPacketCount = 0;
        foreach (var state in _pidStates.Values)
            state.TimelineBucketPacketCount = 0;
        ResetTimelineIntervalCounters();
    }

    private void ResetTimelineIntervalCounters()
    {
        _timelineIntervalPacketCount = 0;
        foreach (var state in _pidStates.Values)
            state.TimelineIntervalPacketCount = 0;
    }

    private void ValidateCompletedScan(long fileSize)
    {
        // 完整扫描后再判断结构性缺失，避免在 PAT/PMT 尚未出现时提前误报。
        if (_result.Programs.Count == 0)
        {
            AddEvent(TsCheckSeverity.Error, TsCheckEventType.MissingProgramTable, 0, _packetIndex,
                fileSize, TsCheckMessageCode.MissingPat, [], GetEstimatedTime(), true);
            return;
        }

        foreach (var program in _result.Programs.Values)
        {
            if (program.Streams.Count == 0)
            {
                AddEvent(TsCheckSeverity.Error, TsCheckEventType.MissingProgramTable, program.PmtPid, _packetIndex,
                    fileSize, TsCheckMessageCode.MissingPmt, [program.ProgramNumber], GetEstimatedTime(), true);
                continue;
            }

            if (program.PcrPid >= 0 &&
                (!_pidStates.TryGetValue(program.PcrPid, out var pcrState) || pcrState.LastPcr90k == long.MinValue))
            {
                AddEvent(TsCheckSeverity.Warning, TsCheckEventType.MissingPcr, program.PcrPid, _packetIndex,
                    fileSize, TsCheckMessageCode.MissingPcr, [program.ProgramNumber], GetEstimatedTime(), true);
            }

            foreach (var stream in program.Streams)
            {
                if (!TsStreamTypes.IsVideo(stream.Value) && !TsStreamTypes.IsAudio(stream.Value))
                    continue;
                if (_pidStates.TryGetValue(stream.Key, out var state) &&
                    state.Summary.PayloadPacketCount > 0 && state.LastPts90k == long.MinValue)
                {
                    AddEvent(TsCheckSeverity.Warning, TsCheckEventType.MissingTimestamp, stream.Key, _packetIndex,
                        fileSize, TsCheckMessageCode.MissingTimestamp, [], GetEstimatedTime(), true);
                }
            }
        }
    }

    private double? GetPacketEventTime(PidState state)
    {
        var pid = state.Summary.Pid;
        int programNumber;
        var hasProgram = _streamPidPrograms.TryGetValue(pid, out programNumber) ||
                         _pmtPidPrograms.TryGetValue(pid, out programNumber) ||
                         _pcrPidPrograms.TryGetValue(pid, out programNumber);
        if (hasProgram &&
            _result.Programs.TryGetValue(programNumber, out var program) &&
            _pidStates.TryGetValue(program.PcrPid, out var pcrState) &&
            pcrState.LastPcr90k != long.MinValue)
        {
            return ptsToSeconds(pcrState.LastPcr90k);
        }

        // PAT 等全局 PID 没有所属节目，使用文件中最近的 PCR 保持包级事件时间单调。
        if (_lastKnownPcr90k != long.MinValue)
            return ptsToSeconds(_lastKnownPcr90k);
        if (state.LastPts90k != long.MinValue)
            return ptsToSeconds(state.LastPts90k);
        return GetEstimatedTime();
    }

    private double? GetEstimatedTime()
    {
        var clock = _lastKnownPcr90k != long.MinValue ? _lastKnownPcr90k : _lastKnownClock90k;
        return clock == long.MinValue ? null : ptsToSeconds(clock);
    }

    private double ptsToSeconds(long value) => _timelineOrigin90k == long.MinValue
        ? value / 90_000.0
        : Math.Max(0, (value - _timelineOrigin90k) / 90_000.0);

    private void SetTimelineOrigin(long value)
    {
        if (_timelineOrigin90k == long.MinValue)
            _timelineOrigin90k = value;
    }

    private static long ReadTimestamp(ReadOnlySpan<byte> value) =>
        ((long)(value[0] & 0x0E) << 29) |
        ((long)value[1] << 22) |
        ((long)(value[2] & 0xFE) << 14) |
        ((long)value[3] << 7) |
        ((long)value[4] >> 1);

    private static long Unwrap(long raw, long lastRaw, long wrapOffset)
    {
        // 跨越半个 33 位周期才视为回绕，避免把普通时间戳倒退错误吞掉。
        if (lastRaw == long.MinValue)
            return raw;
        if (lastRaw - raw > TimestampWrap / 2)
            wrapOffset += TimestampWrap;
        else if (raw - lastRaw > TimestampWrap / 2)
            wrapOffset -= TimestampWrap;
        return raw + wrapOffset;
    }

    private static bool HasValidPsiCrc(ReadOnlySpan<byte> section)
    {
        uint crc = uint.MaxValue;
        foreach (var value in section)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }
        return crc == 0;
    }

    private sealed class PidState(TsCheckPidSummary summary)
    {
        public TsCheckPidSummary Summary { get; } = summary;
        public bool HasContinuity;
        public int LastContinuityCounter;
        public byte[] LastPacketBody { get; } = new byte[PacketSize - 4];
        public long LastRawPcr = long.MinValue;
        public long PcrWrapOffset;
        public long LastPcr90k = long.MinValue;
        public long LastRawPts = long.MinValue;
        public long PtsWrapOffset;
        public long LastPts90k = long.MinValue;
        public long LastRawDts = long.MinValue;
        public long DtsWrapOffset;
        public long LastDts90k = long.MinValue;
        public byte[] PesHeader { get; } = new byte[64];
        public int PesHeaderLength;
        public byte[] MpegAudioProbeTail { get; } = new byte[3];
        public int MpegAudioProbeTailLength;
        public int MpegAudioProbeBytes;
        // 时间轴仅在 PCR 到达时汇总，逐包热路径只做整数累加，不创建采样对象。
        public long TimelineIntervalPacketCount;
        public double TimelineBucketPacketCount;

        public void ResetContinuity() => HasContinuity = false;

        public void ResetTimestamps()
        {
            LastRawPcr = LastRawPts = LastRawDts = long.MinValue;
            LastPcr90k = LastPts90k = LastDts90k = long.MinValue;
            PcrWrapOffset = PtsWrapOffset = DtsWrapOffset = 0;
            PesHeaderLength = 0;
            MpegAudioProbeTailLength = 0;
        }
    }

    private sealed class ProgramClockState
    {
        public int VideoPid = -1;
        public long VideoPts90k = long.MinValue;
        public long FirstVideoPts90k = long.MinValue;
        public Dictionary<int, long> AudioPts90k { get; } = [];
        public Dictionary<int, long> FirstAudioPts90k { get; } = [];
        public Dictionary<int, int> SyncDriftCounts { get; } = [];
        public long LastSyncSamplePcr90k = long.MinValue;
        public long LastDriftReport90k = long.MinValue;

        public void ResetSync()
        {
            VideoPts90k = long.MinValue;
            FirstVideoPts90k = long.MinValue;
            AudioPts90k.Clear();
            FirstAudioPts90k.Clear();
            SyncDriftCounts.Clear();
            LastSyncSamplePcr90k = long.MinValue;
            LastDriftReport90k = long.MinValue;
        }
    }

    private sealed class PsiAssembler
    {
        private readonly byte[] _buffer = new byte[4096];
        private int _length;
        private int _expectedLength;

        public void Push(ReadOnlySpan<byte> payload, bool payloadStart, Action<ReadOnlySpan<byte>> sectionHandler)
        {
            // PAT/PMT section 可跨多个 TS 包；pointer_field 之前的数据用于补完上一 section。
            if (payloadStart)
            {
                if (payload.IsEmpty)
                    return;
                var pointer = payload[0];
                payload = payload[1..];
                if (pointer > payload.Length)
                {
                    Reset();
                    return;
                }

                if (_length > 0 && pointer > 0)
                    Append(payload[..pointer], sectionHandler);
                payload = payload[pointer..];
                Reset();
            }

            Append(payload, sectionHandler);
        }

        private void Append(ReadOnlySpan<byte> data, Action<ReadOnlySpan<byte>> sectionHandler)
        {
            while (!data.IsEmpty)
            {
                if (_length == 0 && data[0] == 0xFF)
                    return;

                var needed = _expectedLength > 0 ? _expectedLength - _length : Math.Min(3 - _length, data.Length);
                if (needed <= 0)
                    needed = data.Length;
                var take = Math.Min(needed, data.Length);
                if (_length + take > _buffer.Length)
                {
                    Reset();
                    return;
                }

                data[..take].CopyTo(_buffer.AsSpan(_length));
                _length += take;
                data = data[take..];

                if (_length >= 3 && _expectedLength == 0)
                {
                    _expectedLength = 3 + ((_buffer[1] & 0x0F) << 8) + _buffer[2];
                    if (_expectedLength < 8 || _expectedLength > _buffer.Length)
                    {
                        Reset();
                        return;
                    }
                }

                if (_expectedLength > 0 && _length == _expectedLength)
                {
                    sectionHandler(_buffer.AsSpan(0, _length));
                    Reset();
                }
            }
        }

        private void Reset()
        {
            _length = 0;
            _expectedLength = 0;
        }

        public void Discard() => Reset();
    }
}
