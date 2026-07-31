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

public sealed class TsEsExtractorService
{
    private const int PacketSize = TsUtil.TsPacketSize;
    private const int ReadBufferSize = PacketSize * 32_768;
    private const int OutputBufferSize = 64 * 1024;

    public async Task<TsEsExtractionResult> ExtractAsync(
        string inputPath,
        IReadOnlyList<TsEsExtractionOutput> outputs,
        long syncOffset,
        IProgress<TsEsExtractionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ValidatePaths(inputPath, outputs);

        var statesByPid = new TrackState?[0x2000];
        var states = new TrackState[outputs.Count];
        var createdPaths = new List<string>(outputs.Count);
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize + PacketSize);
        var stopwatch = Stopwatch.StartNew();
        var lastProgressTicks = 0L;
        var bytesProcessed = Math.Max(0, syncOffset);
        var syncLossBytes = 0L;

        try
        {
            for (var index = 0; index < outputs.Count; index++)
            {
                var output = outputs[index];
                var fileStream = new FileStream(
                    output.OutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                    OutputBufferSize, FileOptions.SequentialScan);
                createdPaths.Add(output.OutputPath);
                var state = new TrackState(output.Pid, output.OutputPath, fileStream);
                states[index] = state;
                statesByPid[output.Pid] = state;
            }

            await using var input = new FileStream(
                inputPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                ReadBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (syncOffset > 0)
                input.Position = syncOffset;

            var buffered = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await input.ReadAsync(buffer.AsMemory(buffered, ReadBufferSize), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;
                buffered += read;

                var consumed = ProcessBuffer(
                    buffer.AsSpan(0, buffered), statesByPid, states,
                    ref syncLossBytes, cancellationToken);
                bytesProcessed += consumed;
                buffered -= consumed;
                if (buffered > 0)
                    buffer.AsSpan(consumed, buffered).CopyTo(buffer);

                var elapsedTicks = stopwatch.ElapsedTicks;
                if (progress is not null && elapsedTicks - lastProgressTicks >= Stopwatch.Frequency / 10)
                {
                    lastProgressTicks = elapsedTicks;
                    ReportProgress(
                        progress, states, bytesProcessed, input.Length,
                        Math.Max(0, syncOffset), stopwatch.Elapsed);
                }
            }

            foreach (var state in states)
            {
                state.CompleteInput();
                await state.DisposeOutputAsync().ConfigureAwait(false);
            }

            var results = states.Select(state => state.CreateResult()).ToArray();
            var bytesWritten = results.Sum(item => item.BytesWritten);
            var anomalyCount = results.Sum(item =>
                item.TransportErrors + item.ContinuityErrors + item.InvalidPackets +
                item.MalformedPesHeaders + item.ScrambledPackets) +
                (syncLossBytes > 0 ? 1 : 0);
            bytesProcessed = input.Position;
            progress?.Report(new TsEsExtractionProgress(
                bytesProcessed, input.Length, bytesWritten,
                (bytesProcessed - Math.Max(0, syncOffset)) /
                Math.Max(0.001, stopwatch.Elapsed.TotalSeconds), stopwatch.Elapsed));
            return new TsEsExtractionResult
            {
                BytesProcessed = bytesProcessed,
                BytesWritten = bytesWritten,
                Elapsed = stopwatch.Elapsed,
                Tracks = results,
                SyncLossBytes = syncLossBytes,
                AnomalyCount = anomalyCount
            };
        }
        catch
        {
            foreach (var state in states)
            {
                if (state is null)
                    continue;
                try
                {
                    await state.DisposeOutputAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 清理阶段不覆盖原始异常，仍需继续关闭其他流并删除全部半成品。
                }
            }

            // 取消或失败时所有轨道视为同一批任务，统一删除半成品，避免留下难以辨认的不完整 ES。
            foreach (var path in createdPaths)
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // 清理失败不覆盖原始异常。
                }
            }
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int ProcessBuffer(
        ReadOnlySpan<byte> data,
        TrackState?[] statesByPid,
        TrackState[] states,
        ref long syncLossBytes,
        CancellationToken cancellationToken)
    {
        var position = 0;
        var packetCounter = 0;
        while (position + PacketSize <= data.Length)
        {
            if ((packetCounter++ & 0x3FFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            if (data[position] != TsUtil.TsSyncByte)
            {
                // 字节失步后所有已选 PID 的半截 PES 都不再可靠，恢复同步前不能继续拼接 ES。
                foreach (var state in states)
                    state?.DiscardForSyncLoss();
                var resync = TsUtil.FindPacketSync(data[position..]);
                if (resync < 0)
                {
                    var discard = Math.Max(0, data.Length - position - PacketSize * 3);
                    syncLossBytes += discard;
                    position += discard;
                    break;
                }
                syncLossBytes += resync;
                position += resync;
                continue;
            }

            var packet = data.Slice(position, PacketSize);
            var info = TsPacketParser.Parse(packet);
            statesByPid[info.Pid]?.ProcessPacket(packet, info);
            position += PacketSize;
        }
        return position;
    }

    private static void ReportProgress(
        IProgress<TsEsExtractionProgress> progress,
        TrackState[] states,
        long bytesProcessed,
        long fileSize,
        long scanStartOffset,
        TimeSpan elapsed)
    {
        long bytesWritten = 0;
        foreach (var state in states)
            bytesWritten += state?.BytesWritten ?? 0;
        progress.Report(new TsEsExtractionProgress(
            bytesProcessed, fileSize, bytesWritten,
            (bytesProcessed - scanStartOffset) / Math.Max(0.001, elapsed.TotalSeconds), elapsed));
    }

    private static void ValidatePaths(string inputPath, IReadOnlyList<TsEsExtractionOutput> outputs)
    {
        if (outputs.Count == 0)
            throw new TsEsExtractionException(TsEsExtractionErrorCode.NoOutputs);

        var inputFullPath = Path.GetFullPath(inputPath);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pids = new HashSet<int>();
        foreach (var output in outputs)
        {
            if ((uint)output.Pid >= 0x2000 || string.IsNullOrWhiteSpace(output.OutputPath))
                throw new TsEsExtractionException(TsEsExtractionErrorCode.InvalidOutputPath, output.OutputPath);
            if (!pids.Add(output.Pid))
                throw new TsEsExtractionException(TsEsExtractionErrorCode.DuplicatePid, $"0x{output.Pid:X4}");
            var fullPath = Path.GetFullPath(output.OutputPath);
            if (string.Equals(inputFullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                throw new TsEsExtractionException(TsEsExtractionErrorCode.SameAsSource, fullPath);
            if (!paths.Add(fullPath))
                throw new TsEsExtractionException(TsEsExtractionErrorCode.DuplicateOutputPath, fullPath);
            if (File.Exists(fullPath))
                throw new TsEsExtractionException(TsEsExtractionErrorCode.OutputExists, fullPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                throw new TsEsExtractionException(TsEsExtractionErrorCode.InvalidOutputPath, fullPath);
        }
    }

    private sealed class TrackState(int pid, string outputPath, FileStream output)
    {
        private const int MaxPesHeaderLength = 264;
        private readonly byte[] _pesHeader = new byte[MaxPesHeaderLength];
        private readonly byte[] _lastPacketBody = new byte[PacketSize - 4];
        private FileStream? _output = output;
        private int _pesHeaderLength;
        private int _expectedPesHeaderLength;
        private int _lastContinuityCounter;
        private bool _hasContinuity;
        private bool _insidePes;
        private bool _collectingPesHeader;

        public long BytesWritten { get; private set; }
        private long TransportErrors { get; set; }
        private long ContinuityErrors { get; set; }
        private long DuplicatePackets { get; set; }
        private long InvalidPackets { get; set; }
        private long MalformedPesHeaders { get; set; }
        private long ScrambledPackets { get; set; }

        public void ProcessPacket(ReadOnlySpan<byte> packet, TsPacketInfo info)
        {
            if (!info.IsValid)
            {
                InvalidPackets++;
                ResetPayloadState();
                _hasContinuity = false;
                return;
            }
            if (info.TransportError)
            {
                // TEI 包的 PID、CC 和负载都可能不可靠，当前 PES 必须整体停止续写，等待下一个 PUSI。
                TransportErrors++;
                ResetPayloadState();
                _hasContinuity = false;
                return;
            }
            if (!info.HasPayload)
            {
                if (info.Discontinuity)
                {
                    ResetPayloadState();
                    _hasContinuity = false;
                }
                return;
            }
            if (info.ScramblingControl >= 2)
            {
                ScrambledPackets++;
                ResetPayloadState();
                _hasContinuity = false;
                return;
            }

            var packetBody = packet[4..];
            var continuityValid = true;
            if (info.Discontinuity)
            {
                ResetPayloadState();
                _hasContinuity = false;
            }
            if (_hasContinuity)
            {
                var expected = (_lastContinuityCounter + 1) & 0x0F;
                if (info.ContinuityCounter == _lastContinuityCounter)
                {
                    DuplicatePackets++;
                    if (packetBody.SequenceEqual(_lastPacketBody))
                        return;
                    ContinuityErrors++;
                    // 同一 CC 出现不同内容时无法判断哪一包可信，本包也不能作为新的 PES 起点使用。
                    ResetPayloadState();
                    _hasContinuity = false;
                    return;
                }
                else if (info.ContinuityCounter != expected)
                {
                    ContinuityErrors++;
                    continuityValid = false;
                }
            }

            _hasContinuity = true;
            _lastContinuityCounter = info.ContinuityCounter;
            packetBody.CopyTo(_lastPacketBody);
            if (!continuityValid)
                ResetPayloadState();

            var payload = packet[info.PayloadOffset..];
            if (info.PayloadStart)
                StartPes(payload);
            else if (_collectingPesHeader)
                ContinuePesHeader(payload);
            else if (_insidePes)
                Write(payload);
        }

        public void DiscardForSyncLoss()
        {
            ResetPayloadState();
            _hasContinuity = false;
        }

        public void CompleteInput()
        {
            if (_collectingPesHeader)
                MalformedPesHeaders++;
            ResetPayloadState();
        }

        private void StartPes(ReadOnlySpan<byte> payload)
        {
            if (_collectingPesHeader)
                MalformedPesHeaders++;
            ResetPayloadState();
            _collectingPesHeader = true;
            ContinuePesHeader(payload);
        }

        private void ContinuePesHeader(ReadOnlySpan<byte> payload)
        {
            // PES 可选头可能跨越多个 TS 包，先用固定 264 字节缓冲拼齐，再直接流式写入余下 ES。
            while (!payload.IsEmpty && _collectingPesHeader)
            {
                var required = _expectedPesHeaderLength > 0
                    ? _expectedPesHeaderLength
                    : Math.Min(9, MaxPesHeaderLength);
                var copyLength = Math.Min(payload.Length, required - _pesHeaderLength);
                payload[..copyLength].CopyTo(_pesHeader.AsSpan(_pesHeaderLength));
                _pesHeaderLength += copyLength;
                payload = payload[copyLength..];

                if (_pesHeaderLength >= 6 && _expectedPesHeaderLength == 0)
                {
                    if (_pesHeader[0] != 0 || _pesHeader[1] != 0 || _pesHeader[2] != 1)
                    {
                        MalformedPesHeaders++;
                        ResetPayloadState();
                        return;
                    }
                    if (HasNoOptionalPesHeader(_pesHeader[3]))
                        _expectedPesHeaderLength = 6;
                }
                if (_pesHeaderLength >= 9 && _expectedPesHeaderLength == 0)
                    _expectedPesHeaderLength = 9 + _pesHeader[8];

                if (_expectedPesHeaderLength > 0 && _pesHeaderLength >= _expectedPesHeaderLength)
                {
                    _collectingPesHeader = false;
                    _insidePes = true;
                }
            }

            if (_insidePes && !payload.IsEmpty)
                Write(payload);
        }

        private static bool HasNoOptionalPesHeader(byte streamId) => streamId is
            0xBC or 0xBE or 0xBF or 0xF0 or 0xF1 or 0xF2 or 0xF8 or 0xFF;

        private void Write(ReadOnlySpan<byte> bytes)
        {
            _output!.Write(bytes);
            BytesWritten += bytes.Length;
        }

        private void ResetPayloadState()
        {
            _insidePes = false;
            _collectingPesHeader = false;
            _pesHeaderLength = 0;
            _expectedPesHeaderLength = 0;
        }

        public async ValueTask DisposeOutputAsync()
        {
            if (_output is null)
                return;
            await _output.DisposeAsync().ConfigureAwait(false);
            _output = null;
        }

        public TsEsExtractionTrackResult CreateResult() => new()
        {
            Pid = pid,
            OutputPath = outputPath,
            BytesWritten = BytesWritten,
            TransportErrors = TransportErrors,
            ContinuityErrors = ContinuityErrors,
            DuplicatePackets = DuplicatePackets,
            InvalidPackets = InvalidPackets,
            MalformedPesHeaders = MalformedPesHeaders,
            ScrambledPackets = ScrambledPackets
        };
    }
}
