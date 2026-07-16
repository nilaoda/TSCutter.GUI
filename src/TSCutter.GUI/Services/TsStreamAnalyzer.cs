using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

    public async Task<TsCheckResult> AnalyzeAsync(
        string filePath,
        IProgress<TsCheckProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Reset(filePath, progress);
        // 使用池化大缓冲区减少系统调用和大对象堆分配；额外空间用于保留跨 ReadAsync 的残余包。
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize + PacketSize * 4);
        var buffered = 0;

        try
        {
            await using var stream = new FileStream(
                filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                ReadBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(buffer.AsMemory(buffered, ReadBufferSize), cancellationToken)
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

                ReportProgress(null, force: false);
            }

            if (_syncOffset < 0)
            {
                AddEvent(TsCheckSeverity.Error, TsCheckEventType.SyncLoss, -1, 0, 0,
                    TsCheckMessageCode.NoSync, [], null, false);
            }
            else if (_pendingSyncLossOffset >= 0)
            {
                AddEvent(TsCheckSeverity.Error, TsCheckEventType.SyncLoss, -1, _packetIndex,
                    _pendingSyncLossOffset, TsCheckMessageCode.SyncLostAtEnd,
                    [_pendingSyncLossBytes + buffered], GetEstimatedTime(), true);
            }
            else if (buffered > 0)
            {
                AddEvent(TsCheckSeverity.Warning, TsCheckEventType.TrailingBytes, -1, _packetIndex,
                    stream.Length - buffered, TsCheckMessageCode.TrailingBytes, [buffered],
                    GetEstimatedTime(), true);
            }

            ValidateCompletedScan(stream.Length);
            CompleteResult(stream.Length, false);
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

    private void Reset(string filePath, IProgress<TsCheckProgress>? progress)
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
        _psiAssemblers[0] = new PsiAssembler();
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
        if (hasPayload)
            state.Summary.PayloadPacketCount++;

        if (transportError)
        {
            state.Summary.TransportErrors++;
            AddEvent(TsCheckSeverity.Error, TsCheckEventType.TransportError, pid, _packetIndex, fileOffset,
                TsCheckMessageCode.TransportError, [], GetPacketEventTime(state), true);
        }

        if (adaptationControl == 0)
        {
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
            ProcessPcr(pid, pcrBytes, state, fileOffset, discontinuity);

        // Null packet 的 CC 没有连续性语义，不参与丢包判断。
        if (hasPayload && payloadOffset < PacketSize && pid != 0x1FFF &&
            !ProcessContinuity(packet, pid, state, continuityCounter, fileOffset))
            return;

        if (!hasPayload || payloadOffset >= PacketSize)
            return;

        var payload = packet[payloadOffset..];
        if (pid == 0 || _pmtPidPrograms.ContainsKey(pid))
            ProcessPsi(pid, payload, payloadStart, fileOffset);

        if (_streamPidPrograms.ContainsKey(pid))
            ProcessPes(pid, payload, payloadStart, state, fileOffset);
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
    }

    private void ParsePat(ReadOnlySpan<byte> section)
    {
        var sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        var end = Math.Min(section.Length - 4, 3 + sectionLength - 4);
        for (var offset = 8; offset + 4 <= end; offset += 4)
        {
            var programNumber = (section[offset] << 8) | section[offset + 1];
            if (programNumber == 0)
                continue;

            var pmtPid = ((section[offset + 2] & 0x1F) << 8) | section[offset + 3];
            _pmtPidPrograms[pmtPid] = programNumber;
            _psiAssemblers.TryAdd(pmtPid, new PsiAssembler());
            var pmtSummary = GetPidState(pmtPid).Summary;
            pmtSummary.ProgramNumber = programNumber;
            pmtSummary.IsPmtPid = true;
            if (!_result.Programs.TryGetValue(programNumber, out var program))
            {
                program = new TsCheckProgramSummary { ProgramNumber = programNumber, PmtPid = pmtPid };
                _result.Programs[programNumber] = program;
            }
            else
            {
                program.PmtPid = pmtPid;
            }
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

        program.PcrPid = pcrPid;
        _pcrPidPrograms[pcrPid] = programNumber;
        GetPidState(pcrPid).Summary.IsPcrPid = true;
        _programClocks.TryAdd(programNumber, new ProgramClockState());

        while (offset + 5 <= sectionEnd)
        {
            var streamType = section[offset];
            var elementaryPid = ((section[offset + 1] & 0x1F) << 8) | section[offset + 2];
            var infoLength = ((section[offset + 3] & 0x0F) << 8) | section[offset + 4];
            if (offset + 5 + infoLength > sectionEnd)
                break;

            // DVB 常把 AC-3/E-AC-3 标成 private data (0x06)，需要结合 descriptor 还原实际类型。
            streamType = ResolveStreamType(streamType, section.Slice(offset + 5, infoLength));
            program.Streams[elementaryPid] = streamType;
            _streamPidPrograms[elementaryPid] = programNumber;
            var summary = GetPidState(elementaryPid).Summary;
            summary.StreamType = streamType;
            summary.ProgramNumber = programNumber;
            offset += 5 + infoLength;
        }
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

    private static byte ResolveStreamType(byte streamType, ReadOnlySpan<byte> descriptors)
    {
        if (streamType != TsStreamTypes.PrivateData)
            return streamType;

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
                    return TsStreamTypes.Ac3;
                if (registration.SequenceEqual("EAC3"u8))
                    return TsStreamTypes.Eac3;
                if (registration.SequenceEqual("DTS1"u8) ||
                    registration.SequenceEqual("DTS2"u8) ||
                    registration.SequenceEqual("DTS3"u8))
                    return TsStreamTypes.Dts;
                if (registration.SequenceEqual("HEVC"u8))
                    return TsStreamTypes.Hevc;
                if (registration.SequenceEqual("VVC "u8))
                    return TsStreamTypes.Vvc;
                if (registration.SequenceEqual("VC-1"u8))
                    return TsStreamTypes.Vc1;
                if (registration.SequenceEqual("drac"u8))
                    return TsStreamTypes.Dirac;
            }

            if (tag == 0x6A)
                return TsStreamTypes.Ac3;
            if (tag == 0x7A)
                return TsStreamTypes.Eac3Atsc;
            if (tag == 0x7B)
                return TsStreamTypes.Dts;
            if (tag == 0x7C)
                return TsStreamTypes.Aac;
            offset += 2 + length;
        }
        return streamType;
    }

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
        else if (TsStreamTypes.IsAudio(streamType))
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
                -1, 0, _globalErrorCount, _globalWarningCount, null, null, null, false, false, true);
        }
        foreach (var pid in _result.Pids.Values)
        {
            pidSnapshot[snapshotIndex++] = new TsCheckPidProgress(
                pid.Pid, pid.PacketCount, pid.ErrorCount, pid.WarningCount, pid.ProgramNumber,
                pid.StreamType, pid.MpegAudioLayer, pid.IsPcrPid, pid.IsPmtPid, false);
        }
        _progress.Report(new TsCheckProgress(
            bytesScanned, _result.FileSize, _packetIndex, _errorCount, _warningCount,
            bytesScanned / Math.Max(0.001, _stopwatch.Elapsed.TotalSeconds), _stopwatch.Elapsed,
            pidSnapshot, progressEvent));
    }

    private void CompleteResult(long bytesScanned, bool cancelled)
    {
        _result.PacketCount = _packetIndex;
        _result.BytesScanned = Math.Min(_result.FileSize, bytesScanned);
        _result.Elapsed = _stopwatch.Elapsed;
        _result.WasCancelled = cancelled;
        _result.TotalErrorCount = _errorCount;
        _result.TotalWarningCount = _warningCount;
        _result.GlobalErrorCount = _globalErrorCount;
        _result.GlobalWarningCount = _globalWarningCount;
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
