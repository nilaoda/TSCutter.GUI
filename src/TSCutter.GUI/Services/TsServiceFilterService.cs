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

public sealed class TsServiceFilterService
{
    private const int PacketSize = TsStreamAnalyzer.PacketSize;
    private const int ReadBufferSize = PacketSize * 32_768;
    private const int OutputBufferSize = PacketSize * 1_024;

    public async Task<TsServiceFilterBatchResult> FilterAsync(
        string sourcePath,
        TsCheckResult catalog,
        IReadOnlyList<TsServiceFilterOutput> outputs,
        IProgress<TsFilterProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        if (outputs.Count == 0)
            throw new ArgumentException("At least one service output is required.", nameof(outputs));

        var sourceFullPath = Path.GetFullPath(sourcePath);
        var outputPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var output in outputs)
        {
            var fullPath = Path.GetFullPath(output.OutputPath);
            if (string.Equals(sourceFullPath, fullPath, StringComparison.OrdinalIgnoreCase))
                throw new TsFilterException(TsFilterErrorCode.SameFile);
            if (!outputPaths.Add(fullPath))
                throw new TsFilterException(TsFilterErrorCode.DuplicateOutputPath);
            if (!catalog.Programs.ContainsKey(output.ServiceId))
                throw new TsFilterException(TsFilterErrorCode.MissingProgram, output.ServiceId);
        }

        var contexts = new List<OutputContext>(outputs.Count);
        var routes = new List<OutputContext>?[8192];
        var stopwatch = Stopwatch.StartNew();
        var inputBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize + PacketSize);
        var eitAssembler = new PsiAssembler();
        var buffered = 0;
        var bytesProcessed = Math.Max(0, catalog.SyncOffset);
        var lastProgressTicks = 0L;

        try
        {
            foreach (var output in outputs)
            {
                var context = OutputContext.Create(output, catalog, cancellationToken);
                contexts.Add(context);
                foreach (var pid in context.RoutedPids)
                    (routes[pid] ??= []).Add(context);
            }

            await using var input = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                ReadBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            input.Position = Math.Max(0, catalog.SyncOffset);

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
                    if ((offset & 0x3FFFF) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    var packet = inputBuffer.AsSpan(offset, PacketSize);
                    if (packet[0] != 0x47)
                        throw new TsFilterException(TsFilterErrorCode.SyncLost, bytesProcessed + offset);

                    var pid = ((packet[1] & 0x1F) << 8) | packet[2];
                    var payloadStart = (packet[1] & 0x40) != 0;
                    if (pid == 0x0012)
                    {
                        // EIT 的 section 可能跨包；只组装这一条 SI PID，并按 section 内的 service_id
                        // 分发给对应输出，避免节目单继续包含其他频道。
                        if (TryGetPayload(packet, out var payload))
                        {
                            eitAssembler.Push(payload, payloadStart, section =>
                            {
                                if (section.Length < 14 ||
                                    section[0] != 0x4E && section[0] is < 0x50 or > 0x5F ||
                                    ((section[8] << 8) | section[9]) != catalog.TransportStreamId)
                                    return;
                                var serviceId = (section[3] << 8) | section[4];
                                foreach (var context in contexts)
                                {
                                    if (context.ServiceId == serviceId)
                                        context.WriteSection(0x0012, section);
                                }
                            });
                        }
                        continue;
                    }

                    var targets = routes[pid];
                    if (targets is null)
                        continue;
                    foreach (var context in targets)
                    {
                        if (context.StaticSections.TryGetValue(pid, out var section))
                        {
                            // 原始 PSI 分片全部丢弃，仅借用 PUSI 的出现频率注入单服务 PAT/PMT/SDT。
                            if (payloadStart)
                                context.WriteSection(pid, section);
                        }
                        else
                        {
                            context.WritePacket(packet);
                        }
                    }
                }

                bytesProcessed += completeLength;
                buffered -= completeLength;
                if (buffered > 0)
                    inputBuffer.AsSpan(completeLength, buffered).CopyTo(inputBuffer);

                foreach (var context in contexts)
                    await context.FlushIfNeededAsync(cancellationToken).ConfigureAwait(false);

                var elapsedTicks = stopwatch.ElapsedTicks;
                if (progress is not null && elapsedTicks - lastProgressTicks >= Stopwatch.Frequency / 10)
                {
                    lastProgressTicks = elapsedTicks;
                    ReportProgress(progress, contexts, bytesProcessed, catalog, stopwatch.Elapsed);
                }
            }

            foreach (var context in contexts)
                await context.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            foreach (var context in contexts)
                await context.DisposeAndDeleteAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(inputBuffer);
            foreach (var context in contexts)
                await context.DisposeAsync().ConfigureAwait(false);
        }

        var result = new TsServiceFilterBatchResult
        {
            BytesProcessed = Math.Min(bytesProcessed, catalog.FileSize),
            BytesWritten = contexts.Sum(item => item.BytesWritten),
            PacketsWritten = contexts.Sum(item => item.PacketsWritten),
            OutputCount = contexts.Count,
            Elapsed = stopwatch.Elapsed
        };
        ReportProgress(progress, contexts, result.BytesProcessed, catalog, result.Elapsed);
        return result;
    }

    private static void ReportProgress(
        IProgress<TsFilterProgress>? progress,
        List<OutputContext> contexts,
        long bytesProcessed,
        TsCheckResult catalog,
        TimeSpan elapsed)
    {
        if (progress is null)
            return;
        progress.Report(new TsFilterProgress(
            Math.Min(bytesProcessed, catalog.FileSize), catalog.FileSize,
            contexts.Sum(item => item.BytesWritten + item.BufferedLength),
            contexts.Sum(item => item.PacketsWritten),
            Math.Max(0, bytesProcessed - catalog.SyncOffset) / Math.Max(0.001, elapsed.TotalSeconds),
            elapsed));
    }

    private static bool TryGetPayload(ReadOnlySpan<byte> packet, out ReadOnlySpan<byte> payload)
    {
        payload = default;
        var adaptationControl = (packet[3] >> 4) & 0x03;
        if ((adaptationControl & 0x01) == 0)
            return false;
        var offset = 4;
        if ((adaptationControl & 0x02) != 0)
            offset += packet[4] + 1;
        if (offset >= PacketSize)
            return false;
        payload = packet[offset..];
        return true;
    }

    private static byte[] BuildSdtSection(TsCheckResult catalog, TsServiceSummary? service, int serviceId)
    {
        var descriptors = service?.Descriptors ?? [];
        var sectionLength = 12 + 5 + descriptors.Length;
        if (sectionLength > 1021)
            throw new TsFilterException(TsFilterErrorCode.SdtTooLarge);
        var section = new byte[3 + sectionLength];
        section[0] = 0x42;
        section[1] = (byte)(0xF0 | (sectionLength >> 8));
        section[2] = (byte)sectionLength;
        section[3] = (byte)(catalog.TransportStreamId >> 8);
        section[4] = (byte)catalog.TransportStreamId;
        section[5] = (byte)(0xC1 | ((((service?.SdtVersion ?? 0) + 1) & 0x1F) << 1));
        section[6] = 0;
        section[7] = 0;
        var networkId = service?.OriginalNetworkId ?? 0;
        section[8] = (byte)(networkId >> 8);
        section[9] = (byte)networkId;
        section[10] = 0xFF;
        section[11] = (byte)(serviceId >> 8);
        section[12] = (byte)serviceId;
        section[13] = (byte)(0xFC |
            (service?.EitSchedule == true ? 0x02 : 0) |
            (service?.EitPresentFollowing == true ? 0x01 : 0));
        section[14] = (byte)(((service?.RunningStatus ?? 4) << 5) |
            (service?.FreeCaMode == true ? 0x10 : 0) |
            (descriptors.Length >> 8));
        section[15] = (byte)descriptors.Length;
        descriptors.CopyTo(section, 16);
        TsStreamFilterService.WriteCrc(section);
        return section;
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
            // 清理失败不覆盖真正的过滤异常。
        }
    }

    private sealed class OutputContext : IAsyncDisposable
    {
        private readonly FileStream _stream;
        private readonly byte[] _buffer;
        private readonly int[] _psiContinuity = new int[8192];
        private bool _disposed;

        private OutputContext(int serviceId, string path, FileStream stream, byte[] buffer)
        {
            ServiceId = serviceId;
            Path = path;
            _stream = stream;
            _buffer = buffer;
            Array.Fill(_psiContinuity, -1);
        }

        public int ServiceId { get; }
        public string Path { get; }
        public HashSet<int> RoutedPids { get; } = [];
        public Dictionary<int, byte[]> StaticSections { get; } = [];
        public int BufferedLength { get; private set; }
        public long BytesWritten { get; private set; }
        public long PacketsWritten { get; private set; }

        public static OutputContext Create(
            TsServiceFilterOutput output,
            TsCheckResult catalog,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var program = catalog.Programs[output.ServiceId];
            var selectedPids = program.StreamDefinitions.Keys.ToHashSet();
            var pat = TsStreamFilterService.BuildPatSection(catalog, [output.ServiceId]);
            var pmt = TsStreamFilterService.BuildPmtSection(program, selectedPids);
            catalog.Services.TryGetValue(output.ServiceId, out var service);
            var sdt = BuildSdtSection(catalog, service, output.ServiceId);

            FileStream stream;
            try
            {
                // CreateNew 与界面预检查共同避免静默覆盖；即使检查后文件才出现也会安全失败。
                stream = new FileStream(
                    output.OutputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    OutputBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            }
            catch (IOException) when (File.Exists(output.OutputPath))
            {
                throw new TsFilterException(TsFilterErrorCode.OutputExists, System.IO.Path.GetFileName(output.OutputPath));
            }

            byte[] buffer;
            try
            {
                buffer = ArrayPool<byte>.Shared.Rent(OutputBufferSize);
            }
            catch
            {
                stream.Dispose();
                TryDelete(output.OutputPath);
                throw;
            }
            var context = new OutputContext(output.ServiceId, output.OutputPath, stream, buffer);
            context.RoutedPids.UnionWith(selectedPids);
            if (program.PcrPid >= 0)
                context.RoutedPids.Add(program.PcrPid);
            context.RoutedPids.Add(0);
            context.RoutedPids.Add(program.PmtPid);
            context.RoutedPids.Add(0x0011);
            context.RoutedPids.Add(0x0014); // TDT/TOT 是全局时间表，不携带其他 service 项。

            context.StaticSections[0] = pat;
            context.StaticSections[program.PmtPid] = pmt;
            context.StaticSections[0x0011] = sdt;
            return context;
        }

        public void WritePacket(ReadOnlySpan<byte> packet)
        {
            EnsureSpace(PacketSize);
            packet.CopyTo(_buffer.AsSpan(BufferedLength));
            BufferedLength += PacketSize;
            PacketsWritten++;
        }

        public void WriteSection(int pid, ReadOnlySpan<byte> section)
        {
            var sectionOffset = 0;
            var firstPacket = true;
            if (_psiContinuity[pid] < 0)
                _psiContinuity[pid] = 0;
            while (sectionOffset < section.Length)
            {
                EnsureSpace(PacketSize);
                var packet = _buffer.AsSpan(BufferedLength, PacketSize);
                packet.Fill(0xFF);
                packet[0] = 0x47;
                packet[1] = (byte)((firstPacket ? 0x40 : 0) | ((pid >> 8) & 0x1F));
                packet[2] = (byte)pid;
                packet[3] = (byte)(0x10 | _psiContinuity[pid]);
                _psiContinuity[pid] = (_psiContinuity[pid] + 1) & 0x0F;
                var payloadOffset = 4;
                if (firstPacket)
                    packet[payloadOffset++] = 0;
                var copyLength = Math.Min(PacketSize - payloadOffset, section.Length - sectionOffset);
                section.Slice(sectionOffset, copyLength).CopyTo(packet[payloadOffset..]);
                sectionOffset += copyLength;
                BufferedLength += PacketSize;
                PacketsWritten++;
                firstPacket = false;
            }
        }

        private void EnsureSpace(int length)
        {
            if (BufferedLength + length > _buffer.Length)
                FlushSynchronously();
        }

        private void FlushSynchronously()
        {
            if (BufferedLength == 0)
                return;
            // 单个源包可能同时路由到多个输出。缓冲区满时同步写入可避免 Span 跨 await，
            // 同时仍以约 192 KiB 的连续块落盘，不会退化为逐包系统调用。
            _stream.Write(_buffer, 0, BufferedLength);
            BytesWritten += BufferedLength;
            BufferedLength = 0;
        }

        public async ValueTask FlushIfNeededAsync(CancellationToken cancellationToken)
        {
            if (BufferedLength < _buffer.Length - PacketSize * 32)
                return;
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask FlushAsync(CancellationToken cancellationToken)
        {
            if (BufferedLength == 0)
                return;
            await _stream.WriteAsync(_buffer.AsMemory(0, BufferedLength), cancellationToken).ConfigureAwait(false);
            BytesWritten += BufferedLength;
            BufferedLength = 0;
        }

        public async ValueTask CompleteAsync(CancellationToken cancellationToken)
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAndDeleteAsync()
        {
            await DisposeAsync().ConfigureAwait(false);
            TryDelete(Path);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            await _stream.DisposeAsync().ConfigureAwait(false);
            ArrayPool<byte>.Shared.Return(_buffer);
        }
    }

    private sealed class PsiAssembler
    {
        private readonly byte[] _buffer = new byte[4096];
        private int _length;
        private int _expectedLength;

        public void Push(ReadOnlySpan<byte> payload, bool payloadStart, Action<ReadOnlySpan<byte>> handler)
        {
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
                    Append(payload[..pointer], handler);
                payload = payload[pointer..];
                Reset();
            }
            Append(payload, handler);
        }

        private void Append(ReadOnlySpan<byte> data, Action<ReadOnlySpan<byte>> handler)
        {
            while (!data.IsEmpty)
            {
                if (_length == 0 && data[0] == 0xFF)
                    return;
                var need = _expectedLength > 0 ? _expectedLength - _length : 3 - _length;
                var take = Math.Min(Math.Max(1, need), data.Length);
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
                    if (_expectedLength is < 8 or > 4096)
                    {
                        Reset();
                        return;
                    }
                }
                if (_expectedLength > 0 && _length == _expectedLength)
                {
                    handler(_buffer.AsSpan(0, _length));
                    Reset();
                }
            }
        }

        private void Reset()
        {
            _length = 0;
            _expectedLength = 0;
        }
    }
}
