using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class TsTimelineRepairServiceTests
{
    [Fact]
    public async Task CleanPcrDoesNotCreateRepairPlan()
    {
        var fixture = await CreateFixtureAsync(static _ => 0);
        try
        {
            var analysis = await AnalyzeAsync(fixture);
            Assert.Empty(analysis.Issues);
        }
        finally
        {
            DeleteFixture(fixture);
        }
    }

    [Fact]
    public async Task TemporaryPcrStepIsRepairedWithoutChangingFileSize()
    {
        var fixture = await CreateFixtureAsync(static sample => sample is >= 50 and < 80 ? 450_000 : 0);
        var output = fixture.Path + ".fixed.ts";
        try
        {
            var service = new TsTimelineRepairService();
            var analysis = await AnalyzeAsync(fixture);
            var issue = Assert.Single(analysis.Issues);
            Assert.Equal(TsTimelineIssueKind.TemporaryPcrOffset, issue.Kind);

            var result = await service.RepairAsync(analysis, output, true);
            Assert.Equal(fixture.Length, result.FileSize);
            Assert.True(result.RewrittenPcrCount > 0);
            Assert.Equal(0, result.RemainingPcrErrorCount);
            Assert.Equal(0, result.RemainingPcrWarningCount);
            AssertOnlyPcrBaseChanged(await File.ReadAllBytesAsync(fixture.Path),
                await File.ReadAllBytesAsync(output));
        }
        finally
        {
            DeleteFixture(fixture);
            File.Delete(output);
        }
    }

    [Fact]
    public async Task GradualPcrDriftUsesInterpolatedRepair()
    {
        var fixture = await CreateFixtureAsync(static sample => sample switch
        {
            >= 50 and < 100 => (sample - 50) * 9_000L,
            _ => 0
        });
        var output = fixture.Path + ".fixed.ts";
        try
        {
            var service = new TsTimelineRepairService();
            var analysis = await AnalyzeAsync(fixture);
            Assert.Equal(TsTimelineIssueKind.GradualPcrDrift, Assert.Single(analysis.Issues).Kind);

            var result = await service.RepairAsync(analysis, output, true);
            Assert.Equal(0, result.RemainingPcrErrorCount);
            Assert.Equal(0, result.RemainingPcrWarningCount);
        }
        finally
        {
            DeleteFixture(fixture);
            File.Delete(output);
        }
    }

    [Fact]
    public async Task VirtualTimestampCorrectionIgnoresPcrOnlySegments()
    {
        var fixture = await CreateFixtureAsync(static sample => sample >= 50 ? 450_000 : 0);
        try
        {
            var analysis = await AnalyzeAsync(fixture);
            var issue = Assert.Single(analysis.Issues);
            Assert.Equal(TsTimelineIssueKind.PersistentClockDiscontinuity, issue.Kind);
            Assert.False(issue.AffectsStreamTimestamps);

            Assert.Equal(0, TsTimelineRepairService.GetVirtualTimestampCorrection90k(
                analysis, issue.PcrPid, issue.StartPacket + 1));
        }
        finally
        {
            DeleteFixture(fixture);
        }
    }

    [Fact]
    public async Task CancelledRepairDeletesIncompleteOutput()
    {
        var fixture = await CreateFixtureAsync(static sample => sample >= 50 ? 450_000 : 0);
        var output = fixture.Path + ".cancelled.ts";
        try
        {
            var service = new TsTimelineRepairService();
            var analysis = await AnalyzeAsync(fixture);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.RepairAsync(analysis, output, true, cancellationToken: cancellation.Token));
            Assert.False(File.Exists(output));
        }
        finally
        {
            DeleteFixture(fixture);
            File.Delete(output);
        }
    }

    [Fact]
    public async Task RepairPreservesUnrelatedTransportError()
    {
        var fixture = await CreateFixtureAsync(static sample => sample is >= 50 and < 80 ? 450_000 : 0);
        var output = fixture.Path + ".fixed.ts";
        try
        {
            const int damagedPacket = 777;
            var source = await File.ReadAllBytesAsync(fixture.Path);
            source[damagedPacket * TsStreamAnalyzer.PacketSize + 1] |= 0x80;
            await File.WriteAllBytesAsync(fixture.Path, source);

            var service = new TsTimelineRepairService();
            var analysis = await AnalyzeAsync(fixture);
            await service.RepairAsync(analysis, output, true);

            var repaired = await File.ReadAllBytesAsync(output);
            Assert.NotEqual(0, repaired[damagedPacket * TsStreamAnalyzer.PacketSize + 1] & 0x80);
            var verification = await new TsStreamAnalyzer().AnalyzeAsync(output, options: new TsStreamAnalyzeOptions
            {
                Features = TsStreamAnalyzeFeatures.ContinuityValidation |
                           TsStreamAnalyzeFeatures.DetailedEvents
            });
            Assert.Contains(verification.Events, item => item.Type == TsCheckEventType.TransportError);
        }
        finally
        {
            DeleteFixture(fixture);
            File.Delete(output);
        }
    }

    [Fact]
    public async Task OutputPacketRewriterRepairsReferencePacketsWithoutRewritingMappedDonorAgain()
    {
        var fixture = await CreateFixtureAsync(static sample => sample is >= 50 and < 80 ? 450_000 : 0);
        try
        {
            var analysis = await AnalyzeAsync(fixture);
            Assert.Single(analysis.Issues);
            var original = await File.ReadAllBytesAsync(fixture.Path);
            var source = original.ToArray();
            var rewriter = new TsTimelineRepairService.OutputPacketRewriter(analysis);

            for (var offset = 0; offset < source.Length; offset += TsStreamAnalyzer.PacketSize)
            {
                rewriter.ProcessPacket(
                    source.AsSpan(offset, TsStreamAnalyzer.PacketSize), offset,
                    applyPcrCorrection: true, applyTimestampCorrection: true);
            }

            Assert.True(rewriter.RewrittenPcrCount > 0);
            Assert.Equal(0, rewriter.RemainingPcrErrorCount);
            Assert.Equal(0, rewriter.RemainingPcrWarningCount);

            var mappedDonorPacket = original.AsSpan(
                600 * TsStreamAnalyzer.PacketSize, TsStreamAnalyzer.PacketSize).ToArray();
            var donorRewriter = new TsTimelineRepairService.OutputPacketRewriter(analysis);
            donorRewriter.ProcessPacket(
                mappedDonorPacket, 600L * TsStreamAnalyzer.PacketSize,
                applyPcrCorrection: true, applyTimestampCorrection: false);

            Assert.Equal(
                source.AsSpan(600 * TsStreamAnalyzer.PacketSize, TsStreamAnalyzer.PacketSize).ToArray(),
                mappedDonorPacket);
            Assert.Equal(1, donorRewriter.RewrittenPcrCount);
        }
        finally
        {
            DeleteFixture(fixture);
        }
    }

    [Fact]
    public async Task OutputPacketRewriterSkipsTimelineBoundaryCoveredByLongGapReplacement()
    {
        var fixture = await CreateFixtureAsync(static sample => sample >= 50 ? 450_000 : 0);
        try
        {
            var analysis = await AnalyzeAsync(fixture);
            var issue = Assert.Single(analysis.Issues);
            var boundaryOffset = issue.StartPacket * TsStreamAnalyzer.PacketSize;
            var rewriter = new TsTimelineRepairService.OutputPacketRewriter(
                analysis,
                [(issue.PcrPid, boundaryOffset - TsStreamAnalyzer.PacketSize,
                    boundaryOffset)]);
            var source = await File.ReadAllBytesAsync(fixture.Path);

            for (var offset = 0; offset < source.Length; offset += TsStreamAnalyzer.PacketSize)
            {
                rewriter.ProcessPacket(
                    source.AsSpan(offset, TsStreamAnalyzer.PacketSize), offset,
                    applyPcrCorrection: true, applyTimestampCorrection: true);
            }

            Assert.Equal(0, rewriter.RepairedIssueCount);
            Assert.Equal(0, rewriter.RewrittenPcrCount);
        }
        finally
        {
            DeleteFixture(fixture);
        }
    }

    [Fact]
    public async Task OutputPacketRewriterDoesNotSuppressTimelineForDifferentReplacedPid()
    {
        var fixture = await CreateFixtureAsync(static sample => sample >= 50 ? 450_000 : 0);
        try
        {
            var analysis = await AnalyzeAsync(fixture);
            var issue = Assert.Single(analysis.Issues);
            var boundaryOffset = issue.StartPacket * TsStreamAnalyzer.PacketSize;
            var rewriter = new TsTimelineRepairService.OutputPacketRewriter(
                analysis,
                [(issue.PcrPid + 1, boundaryOffset - TsStreamAnalyzer.PacketSize,
                    boundaryOffset)]);
            var source = await File.ReadAllBytesAsync(fixture.Path);

            for (var offset = 0; offset < source.Length; offset += TsStreamAnalyzer.PacketSize)
            {
                rewriter.ProcessPacket(
                    source.AsSpan(offset, TsStreamAnalyzer.PacketSize), offset,
                    applyPcrCorrection: true, applyTimestampCorrection: true);
            }

            Assert.Equal(1, rewriter.RepairedIssueCount);
            Assert.True(rewriter.RewrittenPcrCount > 0);
            Assert.Equal(0, rewriter.RemainingPcrErrorCount);
        }
        finally
        {
            DeleteFixture(fixture);
        }
    }

    [Fact]
    public async Task OutputPacketRewriterSkipsGradualRepairEndingAtLongGapBoundary()
    {
        var fixture = await CreateFixtureAsync(static sample => sample switch
        {
            >= 50 and < 100 => (sample - 50) * 9_000L,
            _ => 0
        });
        try
        {
            var analysis = await AnalyzeAsync(fixture);
            var issue = Assert.Single(analysis.Issues);
            Assert.Equal(TsTimelineIssueKind.GradualPcrDrift, issue.Kind);
            var boundaryOffset = issue.EndPacket * TsStreamAnalyzer.PacketSize;
            var rewriter = new TsTimelineRepairService.OutputPacketRewriter(
                analysis, [(issue.PcrPid, boundaryOffset, boundaryOffset)]);
            var source = await File.ReadAllBytesAsync(fixture.Path);

            for (var offset = 0; offset < source.Length; offset += TsStreamAnalyzer.PacketSize)
            {
                rewriter.ProcessPacket(
                    source.AsSpan(offset, TsStreamAnalyzer.PacketSize), offset,
                    applyPcrCorrection: true, applyTimestampCorrection: true);
            }

            Assert.Equal(0, rewriter.RepairedIssueCount);
            Assert.Equal(0, rewriter.RewrittenPcrCount);
        }
        finally
        {
            DeleteFixture(fixture);
        }
    }

    private static async Task<TsTimelineRepairAnalysis> AnalyzeAsync(Fixture fixture)
    {
        var existingResult = new TsCheckResult
        {
            FilePath = fixture.Path,
            FileSize = fixture.Length,
            SyncOffset = 0
        };
        return await new TsTimelineRepairService().AnalyzeAsync(fixture.Path, existingResult);
    }

    private static async Task<Fixture> CreateFixtureAsync(Func<int, long> offset90k)
    {
        var path = Path.Combine(Path.GetTempPath(), $"timeline-repair-{Guid.NewGuid():N}.ts");
        var packets = new List<byte[]>(2_000);
        var pcrSample = 0;
        for (var packetIndex = 0; packetIndex < 2_000; packetIndex++)
        {
            long? pcr = null;
            if (packetIndex % 10 == 0)
            {
                pcr = pcrSample * 9_000L + offset90k(pcrSample);
                pcrSample++;
            }
            packets.Add(CreatePacket(packetIndex & 0x0F, pcr));
        }

        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            foreach (var packet in packets)
                await stream.WriteAsync(packet);
        }
        return new Fixture(path, new FileInfo(path).Length);
    }

    private static byte[] CreatePacket(int continuityCounter, long? pcr90k)
    {
        var packet = Enumerable.Repeat((byte)0x55, TsStreamAnalyzer.PacketSize).ToArray();
        packet[0] = 0x47;
        packet[1] = 0x01;
        packet[2] = 0x00;
        packet[3] = (byte)((pcr90k.HasValue ? 0x30 : 0x10) | continuityCounter);
        if (pcr90k is not { } pcr)
            return packet;

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

    private static void DeleteFixture(Fixture fixture) => File.Delete(fixture.Path);

    private static void AssertOnlyPcrBaseChanged(byte[] source, byte[] output)
    {
        Assert.Equal(source.Length, output.Length);
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == output[index])
                continue;
            var packetOffset = index % TsStreamAnalyzer.PacketSize;
            Assert.InRange(packetOffset, 6, 10);
        }
    }

    private readonly record struct Fixture(string Path, long Length);
}
