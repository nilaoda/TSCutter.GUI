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

public sealed class TsRemuxService
{
    private const int PacketSize = TsStreamAnalyzer.PacketSize;
    private const int ReadBufferSize = PacketSize * 32_768;
    private static readonly HashSet<int> ReservedOutputPids =
        [0x0000, 0x0001, 0x0010, 0x0011, 0x0012, 0x0013, 0x0014, 0x1FFF];

    internal TsRemuxPlan BuildPlan(TsCheckResult catalog, TsRemuxConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(configuration);
        var selectedServices = configuration.Services.ToArray();
        if (selectedServices.Length == 0)
            throw new TsRemuxException(TsRemuxErrorCode.NoServiceSelected);

        var serviceIds = new HashSet<int>();
        var targetPidSources = new Dictionary<int, int>();
        var sourcePidMap = new Dictionary<int, int>();
        var sourcePidServiceIds = new Dictionary<int, int>();
        var fullPayloadSourcePids = new HashSet<int>();
        var pcrOnlySourcePids = new HashSet<int>();
        var serviceIdMap = new Dictionary<int, int>();
        var programs = new List<TsRemuxProgramPlan>(selectedServices.Length);

        void RegisterPid(int sourcePid, int outputPid)
        {
            ValidateOutputPid(outputPid);
            if (sourcePidMap.TryGetValue(sourcePid, out var existingOutput) && existingOutput != outputPid)
                throw new TsRemuxException(TsRemuxErrorCode.DuplicatePid, FormatPid(outputPid));
            if (targetPidSources.TryGetValue(outputPid, out var existingSource) && existingSource != sourcePid)
                throw new TsRemuxException(TsRemuxErrorCode.DuplicatePid, FormatPid(outputPid));
            sourcePidMap[sourcePid] = outputPid;
            targetPidSources[outputPid] = sourcePid;
        }

        foreach (var item in selectedServices)
        {
            if (!catalog.Programs.TryGetValue(item.SourceServiceId, out var sourceProgram))
                throw new TsRemuxException(TsRemuxErrorCode.MissingProgram, item.SourceServiceId);
            if (item.OutputServiceId is <= 0 or > ushort.MaxValue)
                throw new TsRemuxException(TsRemuxErrorCode.InvalidServiceId, item.OutputServiceId);
            if (!serviceIds.Add(item.OutputServiceId))
                throw new TsRemuxException(TsRemuxErrorCode.DuplicateServiceId, item.OutputServiceId);

            RegisterPid(sourceProgram.PmtPid, item.OutputPmtPid);
            serviceIdMap[item.SourceServiceId] = item.OutputServiceId;

            var trackConfigurations = item.Tracks.ToDictionary(track => track.SourcePid);
            var streams = new List<TsRemuxStreamPlan>();
            foreach (var definition in sourceProgram.StreamDefinitions.OrderBy(track =>
                         trackConfigurations.TryGetValue(track.Key, out var configured) ? configured.Order : int.MaxValue))
            {
                if (!trackConfigurations.TryGetValue(definition.Key, out var track) || !track.Keep)
                    continue;
                RegisterPid(track.SourcePid, track.OutputPid);
                fullPayloadSourcePids.Add(track.SourcePid);
                sourcePidServiceIds.TryAdd(track.SourcePid, item.SourceServiceId);
                streams.Add(new TsRemuxStreamPlan
                {
                    SourcePid = track.SourcePid,
                    OutputPid = track.OutputPid,
                    Definition = new TsStreamDefinition
                    {
                        StreamType = definition.Value.StreamType,
                        Descriptors = TsRemuxService.UpdateLanguageDescriptor(
                            definition.Value.Descriptors, track.OutputLanguageCode)
                    }
                });
            }
            if (streams.Count == 0)
                throw new TsRemuxException(TsRemuxErrorCode.NoTrackSelected, item.SourceServiceId);

            if (sourceProgram.PcrPid < 0 ||
                !trackConfigurations.TryGetValue(sourceProgram.PcrPid, out var pcrTrack))
            {
                throw new TsRemuxException(TsRemuxErrorCode.InvalidPid, FormatPid(sourceProgram.PcrPid));
            }
            RegisterPid(sourceProgram.PcrPid, pcrTrack.OutputPid);
            if (!sourceProgram.StreamDefinitions.ContainsKey(sourceProgram.PcrPid) || !pcrTrack.Keep)
                pcrOnlySourcePids.Add(sourceProgram.PcrPid);

            catalog.Services.TryGetValue(item.SourceServiceId, out var sourceService);
            // SDT/PMT 中可能残留 CA 标记，甚至把 CA_PID 指向空包 PID；只有实际媒体负载被加扰时才拒绝。
            if (streams.Any(stream =>
                    catalog.Pids.TryGetValue(stream.SourcePid, out var summary) &&
                    summary.ScrambledPayloadPacketCount > 0))
                throw new TsRemuxException(TsRemuxErrorCode.EncryptedServiceUnsupported, item.SourceServiceId);

            programs.Add(new TsRemuxProgramPlan
            {
                SourceServiceId = item.SourceServiceId,
                OutputServiceId = item.OutputServiceId,
                SourcePmtPid = sourceProgram.PmtPid,
                OutputPmtPid = item.OutputPmtPid,
                SourcePcrPid = sourceProgram.PcrPid,
                OutputPcrPid = pcrTrack.OutputPid,
                Streams = streams,
                ServiceDescriptors = BuildServiceDescriptors(sourceService, item),
                SourceService = sourceService
            });
        }

        var nextVersion = (byte)((catalog.PatVersion + 1) & 0x1F);
        var pat = BuildPat(
            catalog.TransportStreamId,
            nextVersion,
            programs.OrderBy(item => item.OutputServiceId)
                .Select(item => new TsPsiSectionBuilder.PatProgram(item.OutputServiceId, item.OutputPmtPid))
                .ToArray());
        var staticSections = new Dictionary<int, byte[]> { [0] = pat };
        foreach (var program in programs)
        {
            var source = catalog.Programs[program.SourceServiceId];
            staticSections[program.SourcePmtPid] = BuildPmt(
                program.OutputServiceId,
                (byte)((source.PmtVersion + 1) & 0x1F),
                program.OutputPcrPid,
                source.ProgramDescriptors,
                program.Streams.Select(stream =>
                    new TsPsiSectionBuilder.PmtStream(stream.OutputPid, stream.Definition)).ToArray());
        }

        var needsSdt = programs.Any(item => item.SourceService is not null || item.ServiceDescriptors.Length > 0);
        if (needsSdt)
        {
            var firstService = programs.Select(item => item.SourceService).FirstOrDefault(item => item is not null);
            var sdtVersion = (byte)(((firstService?.SdtVersion ?? 0) + 1) & 0x1F);
            staticSections[0x0011] = BuildSdt(
                catalog.TransportStreamId,
                sdtVersion,
                firstService?.OriginalNetworkId ?? 0,
                programs.Select(program => new TsPsiSectionBuilder.SdtService(
                    program.OutputServiceId,
                    program.ServiceDescriptors,
                    program.SourceService?.EitSchedule ?? false,
                    program.SourceService?.EitPresentFollowing ?? false,
                    program.SourceService?.RunningStatus ?? 4,
                    program.SourceService?.FreeCaMode ?? false)).ToArray());
        }

        var injectSdtAfterPat = needsSdt && !catalog.Pids.ContainsKey(0x0011);
        if (injectSdtAfterPat && configuration.OutputMode == TsRemuxOutputMode.PreservePacketCount)
            throw new TsRemuxException(TsRemuxErrorCode.MetadataRequiresServiceInformationSlots);
        var preserveEitPackets = configuration.OutputMode == TsRemuxOutputMode.PreservePacketCount &&
                                 configuration.KeepEpg;
        if (preserveEitPackets &&
            (programs.Count != catalog.Programs.Count ||
             programs.Any(item => item.OutputServiceId != item.SourceServiceId)))
        {
            throw new TsRemuxException(TsRemuxErrorCode.PreserveEpgRequiresUnchangedServices);
        }

        return new TsRemuxPlan
        {
            Catalog = catalog,
            OutputMode = configuration.OutputMode,
            KeepEpg = configuration.KeepEpg,
            Programs = programs,
            SourcePidMap = sourcePidMap,
            SourcePidServiceIds = sourcePidServiceIds,
            FullPayloadSourcePids = fullPayloadSourcePids,
            PcrOnlySourcePids = pcrOnlySourcePids,
            StaticSectionsBySourcePid = staticSections,
            ServiceIdMap = serviceIdMap,
            NeedsSdt = needsSdt,
            InjectSdtAfterPat = injectSdtAfterPat,
            PreserveEitPackets = preserveEitPackets
        };
    }

    private static byte[] BuildPat(
        int transportStreamId,
        byte version,
        IReadOnlyList<TsPsiSectionBuilder.PatProgram> programs)
    {
        try
        {
            return TsPsiSectionBuilder.BuildPat(transportStreamId, version, programs);
        }
        catch (TsFilterException exception) when (exception.Code == TsFilterErrorCode.PatTooLarge)
        {
            throw new TsRemuxException(TsRemuxErrorCode.PatTooLarge);
        }
    }

    private static byte[] BuildPmt(
        int programNumber,
        byte version,
        int pcrPid,
        ReadOnlySpan<byte> programDescriptors,
        IReadOnlyList<TsPsiSectionBuilder.PmtStream> streams)
    {
        try
        {
            return TsPsiSectionBuilder.BuildPmt(
                programNumber, version, pcrPid, programDescriptors, streams);
        }
        catch (TsFilterException exception) when (exception.Code == TsFilterErrorCode.PmtTooLarge)
        {
            throw new TsRemuxException(TsRemuxErrorCode.PmtTooLarge, programNumber);
        }
    }

    private static byte[] UpdateLanguageDescriptor(ReadOnlySpan<byte> descriptors, string? language)
    {
        var code = language?.Trim().ToLowerInvariant() ?? string.Empty;
        if (code.Length != 0 &&
            (code.Length != 3 || code.Any(character => character is < 'a' or > 'z')))
            throw new TsRemuxException(TsRemuxErrorCode.InvalidLanguage, language ?? string.Empty);

        var output = new List<byte>(descriptors.Length + 5);
        var replaced = false;
        for (var offset = 0; offset + 2 <= descriptors.Length;)
        {
            var length = descriptors[offset + 1];
            if (offset + 2 + length > descriptors.Length)
            {
                // 描述符损坏时保留剩余原始字节，避免编辑语言时意外吞掉未知元数据。
                for (var index = offset; index < descriptors.Length; index++)
                    output.Add(descriptors[index]);
                break;
            }
            if (descriptors[offset] == 0x0A && length >= 3)
            {
                if (code.Length != 0)
                {
                    output.Add(0x0A);
                    output.Add(length);
                    output.Add((byte)code[0]);
                    output.Add((byte)code[1]);
                    output.Add((byte)code[2]);
                    for (var index = 5; index < length + 2; index++)
                        output.Add(descriptors[offset + index]);
                }
                replaced = true;
            }
            else
            {
                for (var index = 0; index < length + 2; index++)
                    output.Add(descriptors[offset + index]);
            }
            offset += length + 2;
        }
        if (!replaced && code.Length != 0)
        {
            output.Add(0x0A);
            output.Add(3);
            output.Add((byte)code[0]);
            output.Add((byte)code[1]);
            output.Add((byte)code[2]);
        }
        return output.ToArray();
    }

    private static byte[] BuildSdt(
        int transportStreamId,
        byte version,
        int originalNetworkId,
        IReadOnlyList<TsPsiSectionBuilder.SdtService> services)
    {
        try
        {
            return TsPsiSectionBuilder.BuildSdt(
                transportStreamId, version, originalNetworkId, services);
        }
        catch (TsFilterException exception) when (exception.Code == TsFilterErrorCode.SdtTooLarge)
        {
            throw new TsRemuxException(TsRemuxErrorCode.SdtTooLarge);
        }
    }

    public async Task<TsRemuxResult> RemuxAsync(
        string sourcePath,
        string outputPath,
        TsCheckResult catalog,
        TsRemuxConfiguration configuration,
        IProgress<TsRemuxProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            throw new TsRemuxException(TsRemuxErrorCode.SameFile);
        var plan = BuildPlan(catalog, configuration);
        return await RemuxCoreAsync(sourcePath, outputPath, plan, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<TsRemuxResult> RemuxCoreAsync(
        string sourcePath,
        string outputPath,
        TsRemuxPlan plan,
        IProgress<TsRemuxProgress>? progress,
        CancellationToken cancellationToken)
    {
        var inputBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize + PacketSize);
        var outputBuffer = ArrayPool<byte>.Shared.Rent(ReadBufferSize + PacketSize * 16);
        var packetScratch = new byte[PacketSize];
        var psiContinuity = new int[8192];
        Array.Fill(psiContinuity, -1);
        var eitAssembler = plan.KeepEpg && !plan.PreserveEitPackets ? new TsPsiSectionAssembler() : null;
        var eitState = eitAssembler is null
            ? null
            : new EitRewriteState(plan, outputBuffer, psiContinuity);
        var preserveSectionStates = plan.OutputMode == TsRemuxOutputMode.PreservePacketCount
            ? plan.StaticSectionsBySourcePid.ToDictionary(
                item => item.Key,
                item => new SectionSlotState(item.Value))
            : null;
        var stopwatch = Stopwatch.StartNew();
        var lastProgressTicks = 0L;
        var buffered = 0;
        var outputLength = 0;
        var bytesProcessed = Math.Max(0, plan.Catalog.SyncOffset);
        var bytesWritten = 0L;
        var packetsWritten = 0L;
        var continuityErrors = 0L;
        var transportErrors = 0L;
        var validator = new ContinuityValidator();

        try
        {
            await using var input = new FileStream(
                sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                ReadBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var output = new FileStream(
                outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
                ReadBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (output.ConfigureAwait(false))
            {
                input.Position = Math.Max(0, plan.Catalog.SyncOffset);

                async ValueTask FlushAsync()
                {
                    if (outputLength == 0)
                        return;
                    validator.Process(outputBuffer.AsSpan(0, outputLength));
                    await output.WriteAsync(outputBuffer.AsMemory(0, outputLength), cancellationToken)
                        .ConfigureAwait(false);
                    bytesWritten += outputLength;
                    outputLength = 0;
                }

                async ValueTask WritePacketAsync(byte[] source, int sourceOffset)
                {
                    if (outputLength + PacketSize > outputBuffer.Length)
                        await FlushAsync().ConfigureAwait(false);
                    source.AsSpan(sourceOffset, PacketSize).CopyTo(outputBuffer.AsSpan(outputLength, PacketSize));
                    outputLength += PacketSize;
                    packetsWritten++;
                }

                async ValueTask WriteNullPacketAsync()
                {
                    packetScratch.AsSpan().Fill(0xFF);
                    packetScratch[0] = 0x47;
                    packetScratch[1] = 0x1F;
                    packetScratch[2] = 0xFF;
                    packetScratch[3] = 0x10;
                    await WritePacketAsync(packetScratch, 0).ConfigureAwait(false);
                }

                async ValueTask WriteSectionAsync(int targetPid, byte[] section)
                {
                    if (psiContinuity[targetPid] < 0)
                        psiContinuity[targetPid] = 0;
                    var offset = 0;
                    var first = true;
                    while (offset < section.Length)
                    {
                        packetScratch.AsSpan().Fill(0xFF);
                        packetScratch[0] = 0x47;
                        packetScratch[1] = (byte)((first ? 0x40 : 0) | ((targetPid >> 8) & 0x1F));
                        packetScratch[2] = (byte)targetPid;
                        packetScratch[3] = (byte)(0x10 | psiContinuity[targetPid]);
                        psiContinuity[targetPid] = (psiContinuity[targetPid] + 1) & 0x0F;
                        var payloadOffset = 4;
                        if (first)
                            packetScratch[payloadOffset++] = 0;
                        var length = Math.Min(PacketSize - payloadOffset, section.Length - offset);
                        section.AsSpan(offset, length).CopyTo(packetScratch.AsSpan(payloadOffset));
                        offset += length;
                        await WritePacketAsync(packetScratch, 0).ConfigureAwait(false);
                        first = false;
                    }
                }

                async ValueTask WriteSectionSlotAsync(
                    int sourcePid,
                    int targetPid,
                    bool payloadStart,
                    SectionSlotState state)
                {
                    if (payloadStart)
                    {
                        if (state.IsIncomplete)
                            throw new TsRemuxException(TsRemuxErrorCode.InsufficientPsiPacketSlots, FormatPid(sourcePid));
                        state.Start();
                    }
                    if (!state.IsActive)
                    {
                        await WriteNullPacketAsync().ConfigureAwait(false);
                        return;
                    }

                    if (psiContinuity[targetPid] < 0)
                        psiContinuity[targetPid] = 0;
                    packetScratch.AsSpan().Fill(0xFF);
                    packetScratch[0] = 0x47;
                    packetScratch[1] = (byte)((state.IsFirstPacket ? 0x40 : 0) | ((targetPid >> 8) & 0x1F));
                    packetScratch[2] = (byte)targetPid;
                    packetScratch[3] = (byte)(0x10 | psiContinuity[targetPid]);
                    psiContinuity[targetPid] = (psiContinuity[targetPid] + 1) & 0x0F;
                    var payloadOffset = 4;
                    if (state.IsFirstPacket)
                        packetScratch[payloadOffset++] = 0;
                    var length = Math.Min(PacketSize - payloadOffset, state.RemainingLength);
                    state.CopyNext(packetScratch.AsSpan(payloadOffset, length));
                    await WritePacketAsync(packetScratch, 0).ConfigureAwait(false);
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
                        if ((offset & 0x3FFFF) == 0)
                            cancellationToken.ThrowIfCancellationRequested();
                        var packet = inputBuffer.AsSpan(offset, PacketSize);
                        if (!TsPacketParser.TryParse(packet, out var info))
                            throw new TsRemuxException(TsRemuxErrorCode.SyncLost, bytesProcessed + offset);

                        // 探测只读取文件前部，因此输出阶段再次检查整个文件，防止后段才出现的真实加扰被漏掉。
                        if (!info.TransportError && info.HasPayload && info.ScramblingControl >= 2 &&
                            plan.FullPayloadSourcePids.Contains(info.Pid))
                        {
                            throw new TsRemuxException(
                                TsRemuxErrorCode.EncryptedServiceUnsupported,
                                plan.SourcePidServiceIds.GetValueOrDefault(info.Pid));
                        }

                        var wrote = false;
                        if (plan.StaticSectionsBySourcePid.TryGetValue(info.Pid, out var staticSection))
                        {
                            var targetPid = info.Pid == 0
                                ? 0
                                : plan.SourcePidMap.TryGetValue(info.Pid, out var mapped) ? mapped : info.Pid;
                            if (preserveSectionStates is not null)
                            {
                                await WriteSectionSlotAsync(
                                    info.Pid, targetPid, info.PayloadStart, preserveSectionStates[info.Pid])
                                    .ConfigureAwait(false);
                                wrote = true;
                            }
                            else if (info.PayloadStart)
                            {
                                await WriteSectionAsync(targetPid, staticSection).ConfigureAwait(false);
                                if (info.Pid == 0 && plan.InjectSdtAfterPat &&
                                    plan.StaticSectionsBySourcePid.TryGetValue(0x0011, out var injectedSdt))
                                {
                                    await WriteSectionAsync(0x0011, injectedSdt).ConfigureAwait(false);
                                }
                                wrote = true;
                            }
                        }
                        else if (info.Pid == 0x0012 && plan.PreserveEitPackets)
                        {
                            await WritePacketAsync(inputBuffer, offset).ConfigureAwait(false);
                            wrote = true;
                        }
                        else if (info.Pid == 0x0012 && eitAssembler is not null && eitState is not null)
                        {
                            if (info.HasPayload)
                            {
                                // 一个输入包最多只含少量 EIT section；预留固定空间后，回调只写内存，
                                // 避免在 section 回调中同步访问磁盘或为每个 EIT 分配新数组。
                                if (outputBuffer.Length - outputLength < EitRewriteState.RequiredOutputCapacity)
                                    await FlushAsync().ConfigureAwait(false);
                                eitState.OutputLength = outputLength;
                                eitState.PacketsWritten = packetsWritten;
                                eitState.WroteSection = false;
                                eitAssembler.Push(
                                    inputBuffer.AsSpan(offset + info.PayloadOffset, PacketSize - info.PayloadOffset),
                                    info.PayloadStart,
                                    ref eitState, WriteEitSection);
                                outputLength = eitState.OutputLength;
                                packetsWritten = eitState.PacketsWritten;
                                wrote = eitState.WroteSection;
                            }
                        }
                        else if (info.Pid == 0x0014)
                        {
                            await WritePacketAsync(inputBuffer, offset).ConfigureAwait(false);
                            wrote = true;
                        }
                        else if (plan.FullPayloadSourcePids.Contains(info.Pid))
                        {
                            packet.CopyTo(packetScratch);
                            RewritePid(packetScratch, plan.SourcePidMap[info.Pid]);
                            await WritePacketAsync(packetScratch, 0).ConfigureAwait(false);
                            wrote = true;
                        }
                        else if (plan.PcrOnlySourcePids.Contains(info.Pid) && info.HasPcr && !info.TransportError)
                        {
                            var targetPid = plan.SourcePidMap[info.Pid];
                            BuildPcrOnlyPacket(packet, packetScratch, targetPid, 0);
                            await WritePacketAsync(packetScratch, 0).ConfigureAwait(false);
                            wrote = true;
                        }

                        if (!wrote && plan.OutputMode == TsRemuxOutputMode.PreservePacketCount)
                            await WriteNullPacketAsync().ConfigureAwait(false);
                    }

                    bytesProcessed += completeLength;
                    buffered -= completeLength;
                    if (buffered > 0)
                        inputBuffer.AsSpan(completeLength, buffered).CopyTo(inputBuffer);

                    if (stopwatch.ElapsedTicks - lastProgressTicks >= Stopwatch.Frequency / 10)
                    {
                        lastProgressTicks = stopwatch.ElapsedTicks;
                        progress?.Report(new TsRemuxProgress(
                            Math.Min(bytesProcessed, plan.Catalog.FileSize), plan.Catalog.FileSize,
                            bytesWritten + outputLength, packetsWritten,
                            Math.Max(0, bytesProcessed - plan.Catalog.SyncOffset) /
                            Math.Max(0.001, stopwatch.Elapsed.TotalSeconds), stopwatch.Elapsed));
                    }
                }
                if (preserveSectionStates is not null)
                {
                    foreach (var item in preserveSectionStates)
                    {
                        if (item.Value.IsIncomplete)
                            throw new TsRemuxException(TsRemuxErrorCode.InsufficientPsiPacketSlots, FormatPid(item.Key));
                    }
                }
                await FlushAsync().ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                continuityErrors = validator.ContinuityErrors;
                transportErrors = validator.TransportErrors;
            }
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

        var result = new TsRemuxResult
        {
            BytesProcessed = Math.Min(bytesProcessed, plan.Catalog.FileSize),
            BytesWritten = bytesWritten,
            PacketsWritten = packetsWritten,
            ContinuityErrors = continuityErrors,
            TransportErrors = transportErrors,
            Elapsed = stopwatch.Elapsed
        };
        progress?.Report(new TsRemuxProgress(
            result.BytesProcessed, plan.Catalog.FileSize, result.BytesWritten, result.PacketsWritten,
            Math.Max(0, result.BytesProcessed - plan.Catalog.SyncOffset) /
            Math.Max(0.001, result.Elapsed.TotalSeconds), result.Elapsed));
        return result;
    }

    private static void WriteEitSection(Span<byte> section, ref EitRewriteState state)
    {
        if (!TryRewriteEitSection(section, state.Plan))
            return;
        const int targetPid = 0x0012;
        if (state.Continuity[targetPid] < 0)
            state.Continuity[targetPid] = 0;
        var sectionOffset = 0;
        var first = true;
        while (sectionOffset < section.Length)
        {
            var packet = state.OutputBuffer.AsSpan(state.OutputLength, PacketSize);
            packet.Fill(0xFF);
            packet[0] = 0x47;
            packet[1] = (byte)((first ? 0x40 : 0) | ((targetPid >> 8) & 0x1F));
            packet[2] = (byte)targetPid;
            packet[3] = (byte)(0x10 | state.Continuity[targetPid]);
            state.Continuity[targetPid] = (state.Continuity[targetPid] + 1) & 0x0F;
            var payloadOffset = 4;
            if (first)
                packet[payloadOffset++] = 0;
            var length = Math.Min(PacketSize - payloadOffset, section.Length - sectionOffset);
            section.Slice(sectionOffset, length).CopyTo(packet[payloadOffset..]);
            sectionOffset += length;
            state.OutputLength += PacketSize;
            state.PacketsWritten++;
            first = false;
        }
        state.WroteSection = true;
    }

    private static bool TryRewriteEitSection(Span<byte> section, TsRemuxPlan plan)
    {
        if (section.Length < 14 || section[0] != 0x4E && section[0] is < 0x50 or > 0x5F ||
            ((section[8] << 8) | section[9]) != plan.Catalog.TransportStreamId ||
            !TsPsiSectionBuilder.HasValidCrc(section))
            return false;
        var serviceId = (section[3] << 8) | section[4];
        if (!plan.ServiceIdMap.TryGetValue(serviceId, out var outputServiceId))
            return false;
        section[3] = (byte)(outputServiceId >> 8);
        section[4] = (byte)outputServiceId;
        TsPsiSectionBuilder.WriteCrc(section);
        return true;
    }

    private static byte[] BuildServiceDescriptors(
        TsServiceSummary? source,
        TsRemuxServiceConfiguration configuration)
    {
        var sourceDescriptors = source?.Descriptors ?? [];
        var sourceHasDescriptor = TryFindServiceDescriptor(
            sourceDescriptors, out var sourceServiceType, out var sourceProvider, out var sourceName);
        var outputServiceType = configuration.OutputServiceType ??
                                (sourceHasDescriptor && sourceServiceType > 0 ? sourceServiceType : (byte)0x01);
        var unchanged = configuration.WriteServiceName == (sourceHasDescriptor && sourceName.Length > 0) &&
                        configuration.WriteProviderName == (sourceHasDescriptor && sourceProvider.Length > 0) &&
                        (!configuration.WriteServiceName || configuration.ServiceName == sourceName) &&
                        (!configuration.WriteProviderName || configuration.ProviderName == sourceProvider) &&
                        (configuration.OutputServiceType is null || outputServiceType == sourceServiceType);
        if (unchanged)
            return sourceDescriptors;

        var output = new List<byte>(sourceDescriptors.Length + 64);
        var malformedTailOffset = -1;
        for (var offset = 0; offset + 2 <= sourceDescriptors.Length;)
        {
            var length = sourceDescriptors[offset + 1];
            if (offset + 2 + length > sourceDescriptors.Length)
            {
                malformedTailOffset = offset;
                break;
            }
            if (sourceDescriptors[offset] != 0x48)
            {
                for (var index = 0; index < 2 + length; index++)
                    output.Add(sourceDescriptors[offset + index]);
            }
            offset += 2 + length;
        }

        if (configuration.OutputServiceType is not null ||
            configuration.WriteServiceName || configuration.WriteProviderName)
        {
            var provider = configuration.WriteProviderName
                ? TsDvbTextCodec.Encode(configuration.ProviderName)
                : [];
            var name = configuration.WriteServiceName
                ? TsDvbTextCodec.Encode(configuration.ServiceName)
                : [];
            var bodyLength = 3 + provider.Length + name.Length;
            if (bodyLength > byte.MaxValue)
                throw new TsRemuxException(TsRemuxErrorCode.SdtTooLarge);
            output.Add(0x48);
            output.Add((byte)bodyLength);
            output.Add(outputServiceType);
            output.Add((byte)provider.Length);
            output.AddRange(provider);
            output.Add((byte)name.Length);
            output.AddRange(name);
        }
        if (malformedTailOffset >= 0)
        {
            // 损坏尾部放在新 service_descriptor 之后：既保留厂商原始数据，也避免解析器
            // 在抵达用户新写入的名称和提供商之前就因非法长度停止。
            for (var index = malformedTailOffset; index < sourceDescriptors.Length; index++)
                output.Add(sourceDescriptors[index]);
        }
        return output.ToArray();
    }

    internal static bool TryFindServiceDescriptor(
        ReadOnlySpan<byte> descriptors,
        out byte serviceType,
        out string provider,
        out string name)
    {
        serviceType = 0;
        provider = string.Empty;
        name = string.Empty;
        for (var offset = 0; offset + 2 <= descriptors.Length;)
        {
            var tag = descriptors[offset];
            var length = descriptors[offset + 1];
            if (offset + 2 + length > descriptors.Length)
                return false;
            if (tag == 0x48 && length >= 3)
            {
                var body = descriptors.Slice(offset + 2, length);
                serviceType = body[0];
                var providerLength = body[1];
                if (2 + providerLength >= body.Length)
                    return false;
                provider = TsDvbTextCodec.Decode(body.Slice(2, providerLength));
                var nameLengthOffset = 2 + providerLength;
                var nameLength = body[nameLengthOffset];
                if (nameLengthOffset + 1 + nameLength <= body.Length)
                    name = TsDvbTextCodec.Decode(body.Slice(nameLengthOffset + 1, nameLength));
                return true;
            }
            offset += 2 + length;
        }
        return false;
    }

    private static void BuildPcrOnlyPacket(
        ReadOnlySpan<byte> source,
        Span<byte> target,
        int targetPid,
        byte continuityCounter)
    {
        target.Fill(0xFF);
        target[0] = 0x47;
        target[1] = (byte)((targetPid >> 8) & 0x1F);
        target[2] = (byte)targetPid;
        // adaptation-only 包不消耗连续计数器；固定计数可避免删去媒体负载后产生人为 CC 跳变。
        target[3] = (byte)(0x20 | (continuityCounter & 0x0F));
        target[4] = 183;
        var adaptationLength = source[4];
        source.Slice(5, adaptationLength).CopyTo(target[5..]);
    }

    private static void RewritePid(Span<byte> packet, int targetPid)
    {
        packet[1] = (byte)((packet[1] & 0xE0) | ((targetPid >> 8) & 0x1F));
        packet[2] = (byte)targetPid;
    }

    private static void ValidateOutputPid(int pid)
    {
        if (pid is < 0x0020 or > 0x1FFE || ReservedOutputPids.Contains(pid))
            throw new TsRemuxException(TsRemuxErrorCode.InvalidPid, FormatPid(pid));
    }

    private static string FormatPid(int pid) => pid is >= 0 and <= 0x1FFF ? $"0x{pid:X4}" : pid.ToString();

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 清理失败不覆盖真正的重封装异常。
        }
    }

    private sealed class SectionSlotState(byte[] section)
    {
        private int _offset = section.Length;

        public bool IsActive => _offset < section.Length;
        public bool IsIncomplete => _offset > 0 && _offset < section.Length;
        public bool IsFirstPacket => _offset == 0;
        public int RemainingLength => section.Length - _offset;

        public void Start() => _offset = 0;

        public void CopyNext(Span<byte> destination)
        {
            section.AsSpan(_offset, destination.Length).CopyTo(destination);
            _offset += destination.Length;
        }
    }

    private sealed class EitRewriteState(
        TsRemuxPlan plan,
        byte[] outputBuffer,
        int[] continuity)
    {
        // 单个 PSI section 最多占 23 包；额外空间用于同一输入包中紧随其后的短 section。
        public const int RequiredOutputCapacity = PacketSize * 48;
        public TsRemuxPlan Plan { get; } = plan;
        public byte[] OutputBuffer { get; } = outputBuffer;
        public int[] Continuity { get; } = continuity;
        public int OutputLength { get; set; }
        public long PacketsWritten { get; set; }
        public bool WroteSection { get; set; }
    }

    private sealed class ContinuityValidator
    {
        private readonly int[] _lastCounters = new int[8192];
        private readonly bool[] _hasCounter = new bool[8192];
        public long ContinuityErrors { get; private set; }
        public long TransportErrors { get; private set; }

        public void Process(ReadOnlySpan<byte> packets)
        {
            for (var offset = 0; offset < packets.Length; offset += PacketSize)
            {
                var packet = packets.Slice(offset, PacketSize);
                var info = TsPacketParser.Parse(packet);
                if (!info.IsValid)
                    continue;
                if (info.TransportError)
                {
                    TransportErrors++;
                    // TEI 包的 PID/CC 也可能不可靠，不能让它成为连续性基线或制造级联误报。
                    _hasCounter[info.Pid] = false;
                    continue;
                }
                if (!info.HasPayload || info.Pid == 0x1FFF || info.Discontinuity)
                {
                    if (info.Discontinuity)
                        _hasCounter[info.Pid] = false;
                    continue;
                }
                if (_hasCounter[info.Pid] && info.ContinuityCounter != ((_lastCounters[info.Pid] + 1) & 0x0F) &&
                    info.ContinuityCounter != _lastCounters[info.Pid])
                {
                    ContinuityErrors++;
                }
                _hasCounter[info.Pid] = true;
                _lastCounters[info.Pid] = info.ContinuityCounter;
            }
        }
    }
}
