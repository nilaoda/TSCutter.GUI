using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using TSCutter.GUI.Utils;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class TsRemuxServiceTests
{
    private const int SourcePmtPid = 0x0100;
    private const int VideoPid = 0x0101;
    private const int AudioPid = 0x0102;

    [Fact]
    public void MissingServiceMetadataIsNotGeneratedByDefault()
    {
        var catalog = CreateCatalog();
        var plan = new TsRemuxService().BuildPlan(catalog, CreateConfiguration());

        Assert.False(plan.NeedsSdt);
        Assert.DoesNotContain(0x0011, plan.StaticSectionsBySourcePid.Keys);
    }

    [Fact]
    public void ExistingServiceDescriptorIsPreservedByteForByte()
    {
        var descriptors = BuildServiceDescriptor("Provider", "Service");
        var catalog = CreateCatalog(descriptors);
        var plan = new TsRemuxService().BuildPlan(catalog, CreateConfiguration(
            writeServiceName: true,
            serviceName: "Service",
            writeProviderName: true,
            providerName: "Provider"));

        Assert.Equal(descriptors, Assert.Single(plan.Programs).ServiceDescriptors);
    }

    [Fact]
    public void MissingProviderIsOnlyAddedWhenExplicitlyEnabled()
    {
        var catalog = CreateCatalog();
        var plan = new TsRemuxService().BuildPlan(catalog, CreateConfiguration(
            writeServiceName: true,
            serviceName: "Service"));

        var descriptors = Assert.Single(plan.Programs).ServiceDescriptors;
        Assert.True(TsRemuxService.TryFindServiceDescriptor(
            descriptors, out _, out var provider, out var name));
        Assert.Equal(string.Empty, provider);
        Assert.Equal("Service", name);
    }

    [Fact]
    public void ServiceTypeCanBeChangedThroughTheServiceDescriptor()
    {
        var catalog = CreateCatalog(BuildServiceDescriptor("Provider", "Service"));
        var configuration = CreateConfiguration(
            writeServiceName: true,
            serviceName: "Service",
            writeProviderName: true,
            providerName: "Provider",
            outputServiceType: 0x02);

        var plan = new TsRemuxService().BuildPlan(catalog, configuration);

        Assert.True(TsRemuxService.TryFindServiceDescriptor(
            Assert.Single(plan.Programs).ServiceDescriptors,
            out var serviceType, out _, out _));
        Assert.Equal(0x02, serviceType);
    }

    [Fact]
    public void ExplicitServiceTypeCreatesMetadataWhenSourceDescriptorIsMissing()
    {
        var plan = new TsRemuxService().BuildPlan(CreateCatalog(), CreateConfiguration(outputServiceType: 0x02));

        Assert.True(TsRemuxService.TryFindServiceDescriptor(
            Assert.Single(plan.Programs).ServiceDescriptors,
            out var serviceType, out var provider, out var name));
        Assert.Equal(0x02, serviceType);
        Assert.Empty(provider);
        Assert.Empty(name);
    }

    [Fact]
    public void EditingServiceMetadataPreservesMalformedDescriptorTail()
    {
        byte[] malformedTail = [0x99, 0x05, 0x01];
        var descriptors = BuildServiceDescriptor("Provider", "Service").Concat(malformedTail).ToArray();
        var catalog = CreateCatalog(descriptors);

        var plan = new TsRemuxService().BuildPlan(catalog, CreateConfiguration(
            writeServiceName: true,
            serviceName: "Renamed",
            writeProviderName: true,
            providerName: "Provider"));

        var output = Assert.Single(plan.Programs).ServiceDescriptors;
        Assert.True(output.AsSpan().IndexOf(malformedTail) >= 0);
        Assert.True(TsRemuxService.TryFindServiceDescriptor(output, out _, out _, out var name));
        Assert.Equal("Renamed", name);
    }

    [Fact]
    public void RemovingPcrCarrierKeepsClockAsPcrOnlyPid()
    {
        var catalog = CreateCatalog();
        var plan = new TsRemuxService().BuildPlan(catalog, CreateConfiguration(
            keepVideo: false,
            videoOutputPid: 0x0201,
            audioOutputPid: 0x0202));

        Assert.Contains(VideoPid, plan.PcrOnlySourcePids);
        Assert.DoesNotContain(VideoPid, plan.FullPayloadSourcePids);
        Assert.Equal(0x0201, Assert.Single(plan.Programs).OutputPcrPid);
    }

    [Fact]
    public void PmtCaDescriptorWithoutScrambledPacketsIsAllowed()
    {
        var catalog = CreateCatalog();
        catalog.Programs[1].ProgramDescriptors = [0x09, 0x04, 0x01, 0x00, 0xE1, 0x10];

        var plan = new TsRemuxService().BuildPlan(catalog, CreateConfiguration());

        Assert.Single(plan.Programs);
    }

    [Fact]
    public void SdtFreeCaFlagWithoutScrambledPacketsIsAllowed()
    {
        var catalog = CreateCatalog(BuildServiceDescriptor("Provider", "Service"));
        catalog.Services[1].FreeCaMode = true;

        var plan = new TsRemuxService().BuildPlan(catalog, CreateConfiguration());

        Assert.Single(plan.Programs);
    }

    [Fact]
    public void ScrambledSelectedMediaPacketsAreRejected()
    {
        var catalog = CreateCatalog();
        catalog.Pids[AudioPid].ScrambledPayloadPacketCount = 1;

        var exception = Assert.Throws<TsRemuxException>(() =>
            new TsRemuxService().BuildPlan(catalog, CreateConfiguration()));

        Assert.Equal(TsRemuxErrorCode.EncryptedServiceUnsupported, exception.Code);
    }

    [Fact]
    public void ScrambledPacketsOnRemovedPcrCarrierAreAllowed()
    {
        var catalog = CreateCatalog();
        catalog.Pids[VideoPid].ScrambledPayloadPacketCount = 1;

        var plan = new TsRemuxService().BuildPlan(catalog, CreateConfiguration(keepVideo: false));

        Assert.Contains(VideoPid, plan.PcrOnlySourcePids);
    }

    [Fact]
    public void DuplicateTargetPidIsRejectedGlobally()
    {
        var catalog = CreateCatalog();

        var exception = Assert.Throws<TsRemuxException>(() => new TsRemuxService().BuildPlan(
            catalog,
            CreateConfiguration(audioOutputPid: VideoPid)));

        Assert.Equal(TsRemuxErrorCode.DuplicatePid, exception.Code);
    }

    [Fact]
    public void ExistingLanguageDescriptorIsReplacedWithoutChangingOtherDescriptors()
    {
        var catalog = CreateCatalog();
        catalog.Programs[1].StreamDefinitions[AudioPid] = new TsStreamDefinition
        {
            StreamType = 0x03,
            Descriptors = [0x52, 0x01, 0x07, 0x0A, 0x04, (byte)'e', (byte)'n', (byte)'g', 0x02]
        };

        var plan = new TsRemuxService().BuildPlan(catalog, CreateConfiguration(audioLanguage: "jpn"));

        var audio = Assert.Single(Assert.Single(plan.Programs).Streams, item => item.SourcePid == AudioPid);
        Assert.Equal([0x52, 0x01, 0x07, 0x0A, 0x04, (byte)'j', (byte)'p', (byte)'n', 0x02],
            audio.Definition.Descriptors);
    }

    [Fact]
    public void MissingLanguageDescriptorIsAppended()
    {
        var catalog = CreateCatalog();
        catalog.Programs[1].StreamDefinitions[AudioPid] = new TsStreamDefinition
        {
            StreamType = 0x03,
            Descriptors = [0x52, 0x01, 0x07]
        };

        var plan = new TsRemuxService().BuildPlan(catalog, CreateConfiguration(audioLanguage: "chi"));

        var audio = Assert.Single(Assert.Single(plan.Programs).Streams, item => item.SourcePid == AudioPid);
        Assert.Equal([0x52, 0x01, 0x07, 0x0A, 0x03, (byte)'c', (byte)'h', (byte)'i'],
            audio.Definition.Descriptors);
    }

    [Fact]
    public void EmptyLanguageRemovesOnlyLanguageDescriptor()
    {
        var catalog = CreateCatalog();
        catalog.Programs[1].StreamDefinitions[AudioPid] = new TsStreamDefinition
        {
            StreamType = 0x03,
            Descriptors = [0x52, 0x01, 0x07, 0x0A, 0x03, (byte)'e', (byte)'n', (byte)'g']
        };

        var plan = new TsRemuxService().BuildPlan(catalog, CreateConfiguration(audioLanguage: string.Empty));

        var audio = Assert.Single(Assert.Single(plan.Programs).Streams, item => item.SourcePid == AudioPid);
        Assert.Equal([0x52, 0x01, 0x07], audio.Definition.Descriptors);
    }

    [Fact]
    public void ConfiguredTrackOrderControlsPmtStreamOrder()
    {
        var plan = new TsRemuxService().BuildPlan(CreateCatalog(), CreateConfiguration(
            videoOrder: 1,
            audioOrder: 0));

        Assert.Equal([AudioPid, VideoPid], Assert.Single(plan.Programs).Streams.Select(item => item.SourcePid));
    }

    [Fact]
    public void PidAllocatorSkipsAlreadyUsedValues()
    {
        var used = new HashSet<int> { 0x0100, 0x0101, 0x0103 };

        Assert.True(TsPidAllocator.TryTakeNext(used, out var first));
        Assert.True(TsPidAllocator.TryTakeNext(used, out var second));

        Assert.Equal(0x0102, first);
        Assert.Equal(0x0104, second);
    }

    [Fact]
    public void PreservePacketCountRejectsChangedServiceIdWhileKeepingEpg()
    {
        var catalog = CreateCatalog();
        var exception = Assert.Throws<TsRemuxException>(() => new TsRemuxService().BuildPlan(
            catalog,
            CreateConfiguration(
                outputServiceId: 2,
                outputMode: TsRemuxOutputMode.PreservePacketCount,
                keepEpg: true)));

        Assert.Equal(TsRemuxErrorCode.PreserveEpgRequiresUnchangedServices, exception.Code);
    }

    [Fact]
    public async Task CompactRemuxCanRemovePcrCarrierWithoutAddingContinuityErrors()
    {
        var sourcePackets = new[]
        {
            CreatePsiPacket(0, TsPsiSectionBuilder.BuildPat(
                1, 0, [new TsPsiSectionBuilder.PatProgram(1, SourcePmtPid)])),
            CreatePsiPacket(SourcePmtPid, TsPsiSectionBuilder.BuildPmt(
                1, 0, VideoPid, [],
                [
                    new TsPsiSectionBuilder.PmtStream(VideoPid, new TsStreamDefinition { StreamType = 0x1B }),
                    new TsPsiSectionBuilder.PmtStream(AudioPid, new TsStreamDefinition { StreamType = 0x03 })
                ])),
            CreatePayloadPacket(VideoPid, 0, pcrBase: 90_000),
            CreatePayloadPacket(AudioPid, 0),
            CreatePayloadPacket(VideoPid, 1, pcrBase: 93_000),
            CreatePayloadPacket(AudioPid, 1)
        };
        var catalog = CreateCatalog(fileSize: sourcePackets.Length * TsStreamAnalyzer.PacketSize);
        var configuration = CreateConfiguration(
            keepVideo: false,
            outputPmtPid: 0x0200,
            videoOutputPid: 0x0201,
            audioOutputPid: 0x0202,
            keepEpg: false);

        var (result, output) = await RemuxAsync(sourcePackets, catalog, configuration);

        Assert.Equal(0, result.TransportErrors);
        Assert.Equal(0, result.ContinuityErrors);
        var packets = SplitPackets(output);
        Assert.DoesNotContain(packets, packet => TsPacketParser.Parse(packet).Pid == VideoPid);
        Assert.Equal(2, packets.Count(packet => TsPacketParser.Parse(packet) is { Pid: 0x0201, HasPcr: true }));
        Assert.All(
            packets.Where(packet => TsPacketParser.Parse(packet).Pid == 0x0201),
            packet => Assert.False(TsPacketParser.Parse(packet).HasPayload));
        Assert.Equal(2, packets.Count(packet => TsPacketParser.Parse(packet).Pid == 0x0202));
    }

    [Fact]
    public async Task PreservePacketCountProducesExactlyOneOutputPacketPerInputPacket()
    {
        var sourcePackets = new[]
        {
            CreatePsiPacket(0, TsPsiSectionBuilder.BuildPat(
                1, 0, [new TsPsiSectionBuilder.PatProgram(1, SourcePmtPid)])),
            CreatePsiPacket(SourcePmtPid, TsPsiSectionBuilder.BuildPmt(
                1, 0, VideoPid, [],
                [
                    new TsPsiSectionBuilder.PmtStream(VideoPid, new TsStreamDefinition { StreamType = 0x1B }),
                    new TsPsiSectionBuilder.PmtStream(AudioPid, new TsStreamDefinition { StreamType = 0x03 })
                ])),
            CreatePayloadPacket(VideoPid, 0, pcrBase: 90_000),
            CreatePayloadPacket(AudioPid, 0),
            CreatePayloadPacket(VideoPid, 1, pcrBase: 93_000),
            CreatePayloadPacket(AudioPid, 1)
        };
        var catalog = CreateCatalog(fileSize: sourcePackets.Length * TsStreamAnalyzer.PacketSize);
        var configuration = CreateConfiguration(
            keepVideo: false,
            outputMode: TsRemuxOutputMode.PreservePacketCount,
            keepEpg: false);

        var (result, output) = await RemuxAsync(sourcePackets, catalog, configuration);

        Assert.Equal(sourcePackets.Length * TsStreamAnalyzer.PacketSize, output.Length);
        Assert.Equal(sourcePackets.Length, result.PacketsWritten);
    }

    [Fact]
    public async Task ScramblingFoundAfterProbeCausesOutputToFail()
    {
        var sourcePackets = new[]
        {
            CreatePsiPacket(0, TsPsiSectionBuilder.BuildPat(
                1, 0, [new TsPsiSectionBuilder.PatProgram(1, SourcePmtPid)])),
            CreatePsiPacket(SourcePmtPid, TsPsiSectionBuilder.BuildPmt(
                1, 0, VideoPid, [],
                [
                    new TsPsiSectionBuilder.PmtStream(VideoPid, new TsStreamDefinition { StreamType = 0x1B }),
                    new TsPsiSectionBuilder.PmtStream(AudioPid, new TsStreamDefinition { StreamType = 0x03 })
                ])),
            CreatePayloadPacket(VideoPid, 0),
            CreatePayloadPacket(AudioPid, 0, scramblingControl: 2)
        };
        var catalog = CreateCatalog(fileSize: sourcePackets.Length * TsStreamAnalyzer.PacketSize);

        var exception = await Assert.ThrowsAsync<TsRemuxException>(async () =>
            await RemuxAsync(sourcePackets, catalog, CreateConfiguration()));

        Assert.Equal(TsRemuxErrorCode.EncryptedServiceUnsupported, exception.Code);
    }

    [Fact]
    public async Task TransportErrorDoesNotTurnUnreliableScramblingOrContinuityBitsIntoExtraErrors()
    {
        var damagedAudio = CreatePayloadPacket(AudioPid, 7, scramblingControl: 2);
        damagedAudio[1] |= 0x80;
        var sourcePackets = new[]
        {
            CreatePsiPacket(0, TsPsiSectionBuilder.BuildPat(
                1, 0, [new TsPsiSectionBuilder.PatProgram(1, SourcePmtPid)])),
            CreatePsiPacket(SourcePmtPid, TsPsiSectionBuilder.BuildPmt(
                1, 0, VideoPid, [],
                [
                    new TsPsiSectionBuilder.PmtStream(VideoPid, new TsStreamDefinition { StreamType = 0x1B }),
                    new TsPsiSectionBuilder.PmtStream(AudioPid, new TsStreamDefinition { StreamType = 0x03 })
                ])),
            CreatePayloadPacket(VideoPid, 0),
            CreatePayloadPacket(AudioPid, 0),
            damagedAudio,
            CreatePayloadPacket(AudioPid, 1)
        };
        var catalog = CreateCatalog(fileSize: sourcePackets.Length * TsStreamAnalyzer.PacketSize);

        var (result, _) = await RemuxAsync(sourcePackets, catalog, CreateConfiguration());

        Assert.Equal(1, result.TransportErrors);
        Assert.Equal(0, result.ContinuityErrors);
    }

    [Fact]
    public async Task CompactRemuxOutputCanBeScannedWithEditedServiceAndPidMetadata()
    {
        var sourceDescriptors = BuildServiceDescriptor("Provider", "Service");
        var sourcePackets = new[]
        {
            CreatePsiPacket(0, TsPsiSectionBuilder.BuildPat(
                1, 0, [new TsPsiSectionBuilder.PatProgram(1, SourcePmtPid)])),
            CreatePsiPacket(SourcePmtPid, TsPsiSectionBuilder.BuildPmt(
                1, 0, VideoPid, [],
                [
                    new TsPsiSectionBuilder.PmtStream(VideoPid, new TsStreamDefinition { StreamType = 0x1B }),
                    new TsPsiSectionBuilder.PmtStream(AudioPid, new TsStreamDefinition { StreamType = 0x03 })
                ])),
            CreatePsiPacket(0x0011, TsPsiSectionBuilder.BuildSdt(
                1, 0, 1,
                [new TsPsiSectionBuilder.SdtService(1, sourceDescriptors, true, true, 4, false)])),
            CreatePayloadPacket(VideoPid, 0, pcrBase: 90_000),
            CreatePayloadPacket(AudioPid, 0),
            CreatePayloadPacket(VideoPid, 1, pcrBase: 93_000),
            CreatePayloadPacket(AudioPid, 1)
        };
        var catalog = CreateCatalog(sourceDescriptors,
            sourcePackets.Length * TsStreamAnalyzer.PacketSize);
        var configuration = CreateConfiguration(
            outputServiceId: 2,
            outputPmtPid: 0x0200,
            videoOutputPid: 0x0201,
            audioOutputPid: 0x0202,
            writeServiceName: true,
            serviceName: "Renamed",
            writeProviderName: true,
            providerName: "Updated",
            keepEpg: false);

        var (_, output) = await RemuxAsync(sourcePackets, catalog, configuration);
        var scanned = await AnalyzeAsync(output, includeServiceMetadata: true);

        var program = Assert.Single(scanned.Programs).Value;
        Assert.Equal(2, program.ProgramNumber);
        Assert.Equal(0x0200, program.PmtPid);
        Assert.Equal(0x0201, program.PcrPid);
        Assert.Equal([0x0201, 0x0202], program.Streams.Keys.Order().ToArray());
        var service = Assert.Single(scanned.Services).Value;
        Assert.Equal("Renamed", service.ServiceName);
        Assert.Equal("Updated", service.ProviderName);
    }

    [Fact]
    public async Task CompactRemuxRewritesSelectedServiceIdInEit()
    {
        var sourcePackets = new[]
        {
            CreatePsiPacket(0, TsPsiSectionBuilder.BuildPat(
                1, 0, [new TsPsiSectionBuilder.PatProgram(1, SourcePmtPid)])),
            CreatePsiPacket(SourcePmtPid, TsPsiSectionBuilder.BuildPmt(
                1, 0, VideoPid, [],
                [
                    new TsPsiSectionBuilder.PmtStream(VideoPid, new TsStreamDefinition { StreamType = 0x1B }),
                    new TsPsiSectionBuilder.PmtStream(AudioPid, new TsStreamDefinition { StreamType = 0x03 })
                ])),
            CreatePsiPacket(0x0012, BuildEitSection(serviceId: 1)),
            CreatePayloadPacket(VideoPid, 0, pcrBase: 90_000),
            CreatePayloadPacket(AudioPid, 0)
        };
        var catalog = CreateCatalog(fileSize: sourcePackets.Length * TsStreamAnalyzer.PacketSize);

        var (_, output) = await RemuxAsync(sourcePackets, catalog, CreateConfiguration(outputServiceId: 2));

        var eitPacket = Assert.Single(SplitPackets(output), packet => TsPacketParser.Parse(packet).Pid == 0x0012);
        var info = TsPacketParser.Parse(eitPacket);
        var section = eitPacket.AsSpan(info.PayloadOffset + 1);
        var sectionLength = 3 + ((section[1] & 0x0F) << 8) + section[2];
        section = section[..sectionLength];
        Assert.Equal(2, (section[3] << 8) | section[4]);
        Assert.True(TsPsiSectionBuilder.HasValidCrc(section));
    }

    private static TsCheckResult CreateCatalog(byte[]? serviceDescriptors = null, long fileSize = 0)
    {
        var result = new TsCheckResult
        {
            FilePath = string.Empty,
            FileSize = fileSize,
            TransportStreamId = 1,
            PatVersion = 0,
            SyncOffset = 0
        };
        var program = new TsCheckProgramSummary
        {
            ProgramNumber = 1,
            PmtPid = SourcePmtPid,
            PcrPid = VideoPid,
            PmtVersion = 0
        };
        program.Streams[VideoPid] = 0x1B;
        program.Streams[AudioPid] = 0x03;
        program.StreamDefinitions[VideoPid] = new TsStreamDefinition { StreamType = 0x1B };
        program.StreamDefinitions[AudioPid] = new TsStreamDefinition { StreamType = 0x03 };
        result.Programs[1] = program;
        result.Pids[VideoPid] = new TsCheckPidSummary { Pid = VideoPid };
        result.Pids[AudioPid] = new TsCheckPidSummary { Pid = AudioPid };
        if (serviceDescriptors is not null)
        {
            result.Services[1] = new TsServiceSummary
            {
                ServiceId = 1,
                ServiceName = "Service",
                ProviderName = "Provider",
                ServiceType = 1,
                SdtVersion = 0,
                OriginalNetworkId = 1,
                RunningStatus = 4,
                Descriptors = serviceDescriptors
            };
            result.Pids[0x0011] = new TsCheckPidSummary { Pid = 0x0011 };
        }
        return result;
    }

    private static TsRemuxConfiguration CreateConfiguration(
        bool keepVideo = true,
        int outputServiceId = 1,
        int outputPmtPid = SourcePmtPid,
        int videoOutputPid = VideoPid,
        int audioOutputPid = AudioPid,
        bool writeServiceName = false,
        string serviceName = "",
        bool writeProviderName = false,
        string providerName = "",
        byte? outputServiceType = null,
        TsRemuxOutputMode outputMode = TsRemuxOutputMode.Compact,
        bool keepEpg = true,
        string? audioLanguage = null,
        int videoOrder = 0,
        int audioOrder = 1) => new()
    {
        KeepEpg = keepEpg,
        OutputMode = outputMode,
        Services =
        [
            new TsRemuxServiceConfiguration
            {
                SourceServiceId = 1,
                OutputServiceId = outputServiceId,
                OutputPmtPid = outputPmtPid,
                OutputServiceType = outputServiceType,
                WriteServiceName = writeServiceName,
                ServiceName = serviceName,
                WriteProviderName = writeProviderName,
                ProviderName = providerName,
                Tracks =
                [
                    new TsRemuxTrackConfiguration
                    {
                        SourcePid = VideoPid,
                        OutputPid = videoOutputPid,
                        Keep = keepVideo,
                        Order = videoOrder
                    },
                    new TsRemuxTrackConfiguration
                    {
                        SourcePid = AudioPid,
                        OutputPid = audioOutputPid,
                        Keep = true,
                        OutputLanguageCode = audioLanguage,
                        Order = audioOrder
                    }
                ]
            }
        ]
    };

    private static byte[] BuildServiceDescriptor(string provider, string name)
    {
        var providerBytes = TsDvbTextCodec.Encode(provider);
        var nameBytes = TsDvbTextCodec.Encode(name);
        return
        [
            0x48,
            (byte)(3 + providerBytes.Length + nameBytes.Length),
            0x01,
            (byte)providerBytes.Length,
            .. providerBytes,
            (byte)nameBytes.Length,
            .. nameBytes
        ];
    }

    private static byte[] BuildEitSection(int serviceId)
    {
        var section = new byte[18];
        section[0] = 0x4E;
        section[1] = 0xB0;
        section[2] = 15;
        section[3] = (byte)(serviceId >> 8);
        section[4] = (byte)serviceId;
        section[5] = 0xC1;
        section[6] = 0;
        section[7] = 0;
        section[8] = 0;
        section[9] = 1;
        section[10] = 0;
        section[11] = 1;
        section[12] = 0;
        section[13] = 0x4E;
        TsPsiSectionBuilder.WriteCrc(section);
        return section;
    }

    private static byte[] CreatePsiPacket(int pid, byte[] section)
    {
        Assert.True(section.Length <= 183);
        var packet = new byte[TsStreamAnalyzer.PacketSize];
        Array.Fill(packet, (byte)0xFF);
        packet[0] = 0x47;
        packet[1] = (byte)(0x40 | (pid >> 8));
        packet[2] = (byte)pid;
        packet[3] = 0x10;
        packet[4] = 0;
        section.CopyTo(packet, 5);
        return packet;
    }

    private static byte[] CreatePayloadPacket(
        int pid,
        int continuityCounter,
        long? pcrBase = null,
        int scramblingControl = 0)
    {
        var packet = new byte[TsStreamAnalyzer.PacketSize];
        Array.Fill(packet, (byte)0x55);
        packet[0] = 0x47;
        packet[1] = (byte)(pid >> 8);
        packet[2] = (byte)pid;
        packet[3] = (byte)((scramblingControl << 6) | 0x10 | continuityCounter);
        if (pcrBase is not { } pcr)
            return packet;

        packet[3] = (byte)((scramblingControl << 6) | 0x30 | continuityCounter);
        packet[4] = 7;
        packet[5] = 0x10;
        packet[6] = (byte)(pcr >> 25);
        packet[7] = (byte)(pcr >> 17);
        packet[8] = (byte)(pcr >> 9);
        packet[9] = (byte)(pcr >> 1);
        packet[10] = (byte)(((pcr & 1) << 7) | 0x7E);
        packet[11] = 0;
        return packet;
    }

    private static async Task<(TsRemuxResult Result, byte[] Output)> RemuxAsync(
        IReadOnlyList<byte[]> packets,
        TsCheckResult catalog,
        TsRemuxConfiguration configuration)
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"ts-remux-source-{Guid.NewGuid():N}.ts");
        var outputPath = Path.Combine(Path.GetTempPath(), $"ts-remux-output-{Guid.NewGuid():N}.ts");
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.CreateNew, FileAccess.Write))
            {
                foreach (var packet in packets)
                    await source.WriteAsync(packet);
            }
            var result = await new TsRemuxService().RemuxAsync(
                sourcePath, outputPath, catalog, configuration);
            return (result, await File.ReadAllBytesAsync(outputPath));
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    private static async Task<TsCheckResult> AnalyzeAsync(
        byte[] data, bool includeServiceMetadata)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ts-remux-scan-{Guid.NewGuid():N}.ts");
        try
        {
            await File.WriteAllBytesAsync(path, data);
            return await new TsStreamAnalyzer().AnalyzeAsync(path, options: new TsStreamAnalyzeOptions
            {
                InventoryOnly = true,
                IncludeServiceMetadata = includeServiceMetadata,
                MinimumBytes = long.MaxValue,
                Features = TsStreamAnalyzeFeatures.None
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static IReadOnlyList<byte[]> SplitPackets(byte[] data)
    {
        Assert.Equal(0, data.Length % TsStreamAnalyzer.PacketSize);
        var packets = new byte[data.Length / TsStreamAnalyzer.PacketSize][];
        for (var index = 0; index < packets.Length; index++)
            packets[index] = data.AsSpan(index * TsStreamAnalyzer.PacketSize, TsStreamAnalyzer.PacketSize).ToArray();
        return packets;
    }
}
