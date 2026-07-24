using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TSCutter.GUI.Services;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class TsMultiSourceLargeGapRepairTests
{
    [Fact]
    public async Task MissingPesIntervalCanBeRestoredFromCompleteDonor()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"ts-large-gap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var donorPath = Path.Combine(directory, "donor.ts");
        var referencePath = Path.Combine(directory, "reference.ts");
        var outputPath = Path.Combine(directory, "output.ts");
        try
        {
            await WriteStreamAsync(donorPath, missingStart: -1, missingCount: 0);
            await WriteStreamAsync(referencePath, missingStart: 40, missingCount: 40);

            var service = new TsMultiSourceRepairService();
            var analysis = await service.AnalyzeAsync(
                [referencePath, donorPath], referencePath, normalizeTimeline: false);

            var largeGap = Assert.Single(analysis.LargeGaps);
            Assert.Empty(largeGap.Candidates);
            await service.MatchLargeGapsAsync(analysis);
            var candidate = Assert.Single(largeGap.Candidates);
            Assert.Single(candidate.Tracks);

            var selectedPids = analysis.Tracks
                .Select(track => track.ReferencePid).ToHashSet();
            var plan = service.BuildOutputPlan(
                analysis, selectedPids, includeServiceInformation: true,
                new HashSet<long> { largeGap.ReferenceInsertOffset });
            var result = await service.OutputAsync(plan, outputPath);

            Assert.Equal(1, result.RepairedLargeGapCount);
            Assert.Equal(0, result.RemainingErrorCount);
            var verification = await new TsStreamAnalyzer().AnalyzeAsync(outputPath);
            Assert.Equal(0, verification.Pids[VideoPid].ContinuityErrors);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 测试清理失败不覆盖核心修复断言。
            }
        }
    }

    [Fact]
    public async Task TransportDamageBeforeLargeGapIsReplacedAsSingleIncident()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"ts-large-gap-incident-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var donorPath = Path.Combine(directory, "donor.ts");
        var referencePath = Path.Combine(directory, "reference.ts");
        var outputPath = Path.Combine(directory, "output.ts");
        try
        {
            await WriteStreamAsync(donorPath, missingStart: -1, missingCount: 0);
            await WriteStreamAsync(
                referencePath, missingStart: 40, missingCount: 40, damagedFrame: 10);

            var service = new TsMultiSourceRepairService();
            var analysis = await service.AnalyzeAsync(
                [referencePath, donorPath], referencePath, normalizeTimeline: false);

            var largeGap = Assert.Single(analysis.LargeGaps);
            Assert.True(largeGap.IncludesPrecedingDamage);
            Assert.True(largeGap.ReferenceMissingStartPts90k <
                        largeGap.ReferenceOriginalMissingStartPts90k);
            await service.MatchLargeGapsAsync(analysis);
            var candidate = Assert.Single(largeGap.Candidates);
            Assert.Single(candidate.Tracks);
            Assert.Equal(70, candidate.Tracks[0].SourcePacketCount);

            var selectedPids = analysis.Tracks
                .Select(track => track.ReferencePid).ToHashSet();
            var plan = service.BuildOutputPlan(
                analysis, selectedPids, includeServiceInformation: true,
                new HashSet<long> { largeGap.ReferenceInsertOffset });
            var insertion = Assert.Single(Assert.Single(plan.LargeGapInsertions).Value);
            var trackInsertion = Assert.Single(insertion.Tracks);
            Assert.Empty(plan.Insertions);
            Assert.Empty(plan.Replacements);
            Assert.Equal(largeGap.ReferenceInsertOffset,
                trackInsertion.ReferenceDiscardStartOffset);
            Assert.True(trackInsertion.ReferenceDiscardEndOffset >
                        trackInsertion.ReferenceDiscardStartOffset);

            var result = await service.OutputAsync(plan, outputPath);

            Assert.Equal(1, result.RepairedLargeGapCount);
            Assert.Equal(0, result.RemainingErrorCount);
            Assert.Equal(new FileInfo(donorPath).Length, new FileInfo(outputPath).Length);
            var verification = await new TsStreamAnalyzer().AnalyzeAsync(outputPath);
            Assert.Equal(0, verification.Pids[VideoPid].ContinuityErrors);
            Assert.Equal(0, verification.Pids[VideoPid].TransportErrors);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 测试清理失败不覆盖核心修复断言。
            }
        }
    }

    [Fact]
    public async Task IsolatedEarlierDamageIsNotMergedIntoLargeGap()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"ts-large-gap-isolated-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var donorPath = Path.Combine(directory, "donor.ts");
        var referencePath = Path.Combine(directory, "reference.ts");
        try
        {
            await WriteStreamAsync(
                donorPath, missingStart: -1, missingCount: 0, frameCount: 300);
            await WriteStreamAsync(
                referencePath, missingStart: 200, missingCount: 40,
                damagedFrame: 10, frameCount: 300);

            var service = new TsMultiSourceRepairService();
            var analysis = await service.AnalyzeAsync(
                [referencePath, donorPath], referencePath, normalizeTimeline: false);

            var largeGap = Assert.Single(analysis.LargeGaps);
            Assert.False(largeGap.IncludesPrecedingDamage);
            Assert.Equal(
                largeGap.ReferenceOriginalMissingStartPts90k,
                largeGap.ReferenceMissingStartPts90k);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 测试清理失败不覆盖核心修复断言。
            }
        }
    }

    [Fact]
    public async Task CorrelatedMultiTrackDamageCanBridgeLongIntervalBeforeLargeGap()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"ts-large-gap-correlated-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var donorPath = Path.Combine(directory, "donor.ts");
        var referencePath = Path.Combine(directory, "reference.ts");
        var outputPath = Path.Combine(directory, "output.ts");
        try
        {
            const int frameCount = 1_800;
            const int damagedFrame = 350;
            const int missingStart = 1_500;
            const int missingCount = 100;
            await WriteMultiTrackStreamAsync(
                donorPath, damaged: false, frameCount, missingStart, missingCount,
                damagedFrame, damagedFrame);
            await WriteMultiTrackStreamAsync(
                referencePath, damaged: true, frameCount, missingStart, missingCount,
                damagedFrame, damagedFrame);

            var service = new TsMultiSourceRepairService();
            var analysis = await service.AnalyzeAsync(
                [referencePath, donorPath], referencePath, normalizeTimeline: false);

            var largeGap = Assert.Single(analysis.LargeGaps);
            Assert.True(largeGap.IncludesPrecedingDamage);
            Assert.True(
                largeGap.ReferenceOriginalMissingStartPts90k -
                largeGap.ReferenceMissingStartPts90k > 40L * 90_000);

            await service.MatchLargeGapsAsync(analysis);
            var candidate = Assert.Single(largeGap.Candidates);
            Assert.Equal(3, candidate.Tracks.Count);
            var selectedPids = analysis.Tracks
                .Select(track => track.ReferencePid).ToHashSet();
            var plan = service.BuildOutputPlan(
                analysis, selectedPids, includeServiceInformation: true,
                new HashSet<long> { largeGap.ReferenceInsertOffset });
            var result = await service.OutputAsync(plan, outputPath);

            Assert.Equal(0, result.RemainingErrorCount);
            Assert.Equal(new FileInfo(donorPath).Length, new FileInfo(outputPath).Length);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 测试清理失败不覆盖核心修复断言。
            }
        }
    }

    [Fact]
    public async Task MultiTrackDamageUsesEarliestIncidentBoundaryForWholeProgram()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"ts-large-gap-multitrack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var donorPath = Path.Combine(directory, "donor.ts");
        var referencePath = Path.Combine(directory, "reference.ts");
        var outputPath = Path.Combine(directory, "output.ts");
        try
        {
            await WriteMultiTrackStreamAsync(donorPath, damaged: false);
            await WriteMultiTrackStreamAsync(referencePath, damaged: true);

            var service = new TsMultiSourceRepairService();
            var analysis = await service.AnalyzeAsync(
                [referencePath, donorPath], referencePath, normalizeTimeline: false);
            var largeGap = Assert.Single(analysis.LargeGaps);
            Assert.True(largeGap.IncludesPrecedingDamage);
            Assert.Equal(3, largeGap.Tracks.Count);

            await service.MatchLargeGapsAsync(analysis);
            var candidate = Assert.Single(largeGap.Candidates);
            Assert.Equal(3, candidate.Tracks.Count);

            var selectedPids = analysis.Tracks
                .Select(track => track.ReferencePid).ToHashSet();
            var plan = service.BuildOutputPlan(
                analysis, selectedPids, includeServiceInformation: true,
                new HashSet<long> { largeGap.ReferenceInsertOffset });
            var insertion = Assert.Single(Assert.Single(plan.LargeGapInsertions).Value);
            Assert.Equal(3, insertion.Tracks.Count);
            Assert.All(insertion.Tracks, item =>
                Assert.Equal(largeGap.ReferenceInsertOffset, item.ReferenceDiscardStartOffset));
            Assert.Empty(plan.Insertions);
            Assert.Empty(plan.Replacements);

            var result = await service.OutputAsync(plan, outputPath);
            Assert.Equal(0, result.RemainingErrorCount);
            Assert.Equal(new FileInfo(donorPath).Length, new FileInfo(outputPath).Length);
            var verification = await new TsStreamAnalyzer().AnalyzeAsync(outputPath);
            foreach (var pid in new[] { VideoPid, AudioPid1, AudioPid2 })
            {
                Assert.Equal(0, verification.Pids[pid].ContinuityErrors);
                Assert.Equal(0, verification.Pids[pid].TransportErrors);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // 测试清理失败不覆盖核心修复断言。
            }
        }
    }

    private const int PmtPid = 0x0100;
    private const int VideoPid = 0x0101;
    private const int AudioPid1 = 0x0102;
    private const int AudioPid2 = 0x0103;

    private static async Task WriteStreamAsync(
        string path,
        int missingStart,
        int missingCount,
        int damagedFrame = -1,
        int frameCount = 120)
    {
        var packets = new List<byte[]>
        {
            CreatePsiPacket(0x0000, 0, BuildPatSection(PmtPid)),
            CreatePsiPacket(PmtPid, 0, BuildPmtSection(VideoPid))
        };
        for (var frame = 0; frame < frameCount; frame++)
        {
            if (frame >= missingStart && frame < missingStart + missingCount)
                continue;
            var packet = CreateVideoPesPacket(
                VideoPid, frame & 0x0F, frame * 3_600L, frame);
            if (frame == damagedFrame)
                packet[1] |= 0x80;
            packets.Add(packet);
        }
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        foreach (var packet in packets)
            await stream.WriteAsync(packet);
    }

    private static async Task WriteMultiTrackStreamAsync(
        string path,
        bool damaged,
        int frameCount = 120,
        int missingStart = 40,
        int missingCount = 40,
        int audioDamagedFrame = 10,
        int videoDamagedFrame = 20)
    {
        var packets = new List<byte[]>
        {
            CreatePsiPacket(0x0000, 0, BuildPatSection(PmtPid)),
            CreatePsiPacket(PmtPid, 0, BuildPmtSection(
                VideoPid, (0x1B, VideoPid), (0x03, AudioPid1), (0x03, AudioPid2)))
        };
        for (var frame = 0; frame < frameCount; frame++)
        {
            if (damaged && frame >= missingStart && frame < missingStart + missingCount)
                continue;
            var video = CreatePesPacket(
                VideoPid, frame & 0x0F, frame * 3_600L, frame, 0xE0);
            var audio1 = CreatePesPacket(
                AudioPid1, frame & 0x0F, frame * 3_600L, frame + 31, 0xC0);
            var audio2 = CreatePesPacket(
                AudioPid2, frame & 0x0F, frame * 3_600L, frame + 67, 0xC0);
            if (damaged && frame == audioDamagedFrame)
            {
                audio1[1] |= 0x80;
                audio2[1] |= 0x80;
            }
            if (damaged && frame == videoDamagedFrame)
                video[1] |= 0x80;
            packets.Add(video);
            packets.Add(audio1);
            packets.Add(audio2);
        }
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        foreach (var packet in packets)
            await stream.WriteAsync(packet);
    }

    private static byte[] CreatePsiPacket(int pid, int continuityCounter, byte[] section)
    {
        var packet = CreatePacket(pid, continuityCounter);
        packet[1] |= 0x40;
        packet[4] = 0;
        section.CopyTo(packet.AsSpan(5));
        packet.AsSpan(5 + section.Length).Fill(0xFF);
        return packet;
    }

    private static byte[] CreateVideoPesPacket(
        int pid,
        int continuityCounter,
        long pts90k,
        int frame)
    {
        return CreatePesPacket(pid, continuityCounter, pts90k, frame, 0xE0);
    }

    private static byte[] CreatePesPacket(
        int pid,
        int continuityCounter,
        long pts90k,
        int frame,
        byte streamId)
    {
        var packet = CreatePacket(pid, continuityCounter);
        packet[1] |= 0x40;
        var payload = packet.AsSpan(4);
        payload[0] = 0;
        payload[1] = 0;
        payload[2] = 1;
        payload[3] = streamId;
        payload[4] = 0;
        payload[5] = 0;
        payload[6] = 0x80;
        payload[7] = 0x80;
        payload[8] = 5;
        WritePts(payload.Slice(9, 5), pts90k);
        for (var index = 14; index < payload.Length; index++)
            payload[index] = (byte)(frame * 17 + index);
        BinaryPrimitives.WriteInt32BigEndian(payload.Slice(14, sizeof(int)), frame);
        return packet;
    }

    private static byte[] CreatePacket(int pid, int continuityCounter)
    {
        var packet = new byte[TsStreamAnalyzer.PacketSize];
        Array.Fill(packet, (byte)0xFF);
        packet[0] = 0x47;
        packet[1] = (byte)((pid >> 8) & 0x1F);
        packet[2] = (byte)pid;
        packet[3] = (byte)(0x10 | (continuityCounter & 0x0F));
        return packet;
    }

    private static byte[] BuildPatSection(int pmtPid) => AppendCrc([
        0x00, 0xB0, 0x0D, 0x00, 0x01, 0xC1, 0x00, 0x00,
        0x00, 0x01, (byte)(0xE0 | (pmtPid >> 8)), (byte)pmtPid
    ]);

    private static byte[] BuildPmtSection(int pcrPid) => AppendCrc([
        0x02, 0xB0, 0x12, 0x00, 0x01, 0xC1, 0x00, 0x00,
        (byte)(0xE0 | (pcrPid >> 8)), (byte)pcrPid, 0xF0, 0x00,
        0x1B, (byte)(0xE0 | (pcrPid >> 8)), (byte)pcrPid, 0xF0, 0x00
    ]);

    private static byte[] BuildPmtSection(
        int pcrPid,
        params (int StreamType, int Pid)[] streams)
    {
        var sectionLength = 9 + streams.Length * 5 + 4;
        var section = new List<byte>
        {
            0x02, (byte)(0xB0 | (sectionLength >> 8)), (byte)sectionLength,
            0x00, 0x01, 0xC1, 0x00, 0x00,
            (byte)(0xE0 | (pcrPid >> 8)), (byte)pcrPid, 0xF0, 0x00
        };
        foreach (var stream in streams)
        {
            section.Add((byte)stream.StreamType);
            section.Add((byte)(0xE0 | (stream.Pid >> 8)));
            section.Add((byte)stream.Pid);
            section.Add(0xF0);
            section.Add(0x00);
        }
        return AppendCrc(section.ToArray());
    }

    private static byte[] AppendCrc(byte[] section)
    {
        uint crc = uint.MaxValue;
        foreach (var value in section)
            crc = UpdateCrc(crc, value);
        return [.. section, (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc];
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= (uint)value << 24;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        return crc;
    }

    private static void WritePts(Span<byte> value, long pts)
    {
        value[0] = (byte)(0x21 | (((pts >> 30) & 0x07) << 1));
        value[1] = (byte)(pts >> 22);
        value[2] = (byte)(0x01 | (((pts >> 15) & 0x7F) << 1));
        value[3] = (byte)(pts >> 7);
        value[4] = (byte)(0x01 | ((pts & 0x7F) << 1));
    }
}
