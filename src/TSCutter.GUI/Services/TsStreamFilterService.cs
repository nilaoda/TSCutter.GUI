using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models;

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

    public async Task<TsFilterResult> FilterAsync(
        string sourcePath,
        string outputPath,
        TsCheckResult catalog,
        TsFilterPlan plan,
        IProgress<TsFilterProgress>? progress = null,
        CancellationToken cancellationToken = default)
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
                await output.WriteAsync(outputBuffer.AsMemory(0, outputLength), cancellationToken)
                    .ConfigureAwait(false);
                bytesWritten += outputLength;
                outputLength = 0;
            }

            async ValueTask EnsurePacketSpaceAsync()
            {
                if (outputLength + PacketSize > outputBuffer.Length)
                    await FlushAsync().ConfigureAwait(false);
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

                    var pid = ((inputBuffer[offset + 1] & 0x1F) << 8) | inputBuffer[offset + 2];
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
                            outputLength += PacketSize;
                            packetsWritten++;
                            firstPacket = false;
                        }
                    }
                    else if (plan.PcrOnlyPids.Contains(pid))
                    {
                        await EnsurePacketSpaceAsync().ConfigureAwait(false);
                        if (!TryWritePcrOnlyPacket(inputBuffer.AsSpan(offset, PacketSize), outputBuffer, ref outputLength))
                            continue;
                        packetsWritten++;
                    }
                    else if (plan.EffectivePids.Contains(pid))
                    {
                        await EnsurePacketSpaceAsync().ConfigureAwait(false);
                        inputBuffer.AsSpan(offset, PacketSize).CopyTo(outputBuffer.AsSpan(outputLength));
                        outputLength += PacketSize;
                        packetsWritten++;
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

    internal static byte[] BuildPatSection(TsCheckResult catalog, HashSet<int> selectedPrograms)
    {
        var programs = catalog.Programs.Values
            .Where(item => selectedPrograms.Contains(item.ProgramNumber))
            .OrderBy(item => item.ProgramNumber)
            .ToArray();
        var sectionLength = 9 + programs.Length * 4;
        if (sectionLength > 1021)
            throw new TsFilterException(TsFilterErrorCode.PatTooLarge);

        var section = new byte[3 + sectionLength];
        section[0] = 0x00;
        section[1] = (byte)(0xB0 | (sectionLength >> 8));
        section[2] = (byte)sectionLength;
        section[3] = (byte)(catalog.TransportStreamId >> 8);
        section[4] = (byte)catalog.TransportStreamId;
        section[5] = (byte)(0xC1 | (((catalog.PatVersion + 1) & 0x1F) << 1));
        section[6] = 0;
        section[7] = 0;
        var offset = 8;
        foreach (var program in programs)
        {
            section[offset++] = (byte)(program.ProgramNumber >> 8);
            section[offset++] = (byte)program.ProgramNumber;
            section[offset++] = (byte)(0xE0 | (program.PmtPid >> 8));
            section[offset++] = (byte)program.PmtPid;
        }
        WriteCrc(section);
        return section;
    }

    private static bool TryWritePcrOnlyPacket(ReadOnlySpan<byte> source, byte[] outputBuffer, ref int outputLength)
    {
        var adaptationControl = (source[3] >> 4) & 0x03;
        if ((adaptationControl & 0x02) == 0 || source[4] < 7 || (source[5] & 0x10) == 0)
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
        source.Slice(5, source[4]).CopyTo(packet[5..]);
        outputLength += PacketSize;
        return true;
    }

    internal static byte[] BuildPmtSection(TsCheckProgramSummary program, IReadOnlySet<int> selectedPids)
    {
        var streams = program.StreamDefinitions
            .Where(item => selectedPids.Contains(item.Key))
            .OrderBy(item => item.Key)
            .ToArray();
        var sectionLength = 13 + program.ProgramDescriptors.Length +
                            streams.Sum(item => 5 + item.Value.Descriptors.Length);
        if (sectionLength > 1021)
            throw new TsFilterException(TsFilterErrorCode.PmtTooLarge, program.ProgramNumber);

        var section = new byte[3 + sectionLength];
        section[0] = 0x02;
        section[1] = (byte)(0xB0 | (sectionLength >> 8));
        section[2] = (byte)sectionLength;
        section[3] = (byte)(program.ProgramNumber >> 8);
        section[4] = (byte)program.ProgramNumber;
        section[5] = (byte)(0xC1 | (((program.PmtVersion + 1) & 0x1F) << 1));
        section[6] = 0;
        section[7] = 0;
        var pcrPid = program.PcrPid >= 0 ? program.PcrPid : 0x1FFF;
        section[8] = (byte)(0xE0 | (pcrPid >> 8));
        section[9] = (byte)pcrPid;
        section[10] = (byte)(0xF0 | (program.ProgramDescriptors.Length >> 8));
        section[11] = (byte)program.ProgramDescriptors.Length;
        var offset = 12;
        program.ProgramDescriptors.CopyTo(section, offset);
        offset += program.ProgramDescriptors.Length;
        foreach (var stream in streams)
        {
            section[offset++] = stream.Value.StreamType;
            section[offset++] = (byte)(0xE0 | (stream.Key >> 8));
            section[offset++] = (byte)stream.Key;
            section[offset++] = (byte)(0xF0 | (stream.Value.Descriptors.Length >> 8));
            section[offset++] = (byte)stream.Value.Descriptors.Length;
            stream.Value.Descriptors.CopyTo(section, offset);
            offset += stream.Value.Descriptors.Length;
        }
        WriteCrc(section);
        return section;
    }

    internal static void WriteCrc(Span<byte> section)
    {
        var crc = ComputeCrc(section[..^4]);
        BinaryPrimitives.WriteUInt32BigEndian(section[^4..], crc);
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }
        return crc;
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
            // 清理失败不覆盖原始扫描异常。
        }
    }
}
