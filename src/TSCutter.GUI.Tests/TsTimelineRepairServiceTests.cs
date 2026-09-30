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
    [Theory]
    [InlineData(1_800)]
    [InlineData(3_600)]
    public void StableClockWithVariablePacketSpacingDoesNotCreateRepairPlan(long interval90k)
    {
        long packet = 0;
        var samples = new List<TsTimelineRepairService.PcrSample>();
        for (var index = 0; index < 500; index++)
        {
            packet += index % 3 == 0 ? 5_862 : index % 3 == 1 ? 1 : 253;
            // 40ms 的流同时模拟时基换算带来的一个 tick 抖动。
            var pcr = index * interval90k + (interval90k == 3_600 ? index % 2 : 0);
            samples.Add(new(packet, pcr, false, pcr + 7_200));
        }

        Assert.Empty(BuildAnalysis(samples).Issues);
    }

    [Theory]
    [InlineData(450_000L)]
    [InlineData(22_500L)]
    public void ForwardMediaGapIsNotCompressedIntoContinuousClock(long gap90k)
    {
        var samples = Enumerable.Range(0, 200).Select(index =>
        {
            var pcr = index * 9_000L + (index >= 50 ? gap90k : 0);
            return new TsTimelineRepairService.PcrSample(index * 10, pcr, false, pcr + 7_200);
        }).ToList();

        Assert.Empty(BuildAnalysis(samples).Issues);
    }

    [Fact]
    public void PcrSampleUsesExistingAlignmentSpaceForDamageAndDecodeOffset()
    {
        Assert.Equal(24, System.Runtime.CompilerServices.Unsafe.SizeOf<TsTimelineRepairService.PcrSample>());
    }

    [Theory]
    [InlineData(TsCheckEventType.ContinuityGap)]
    [InlineData(TsCheckEventType.ConflictingDuplicate)]
    [InlineData(TsCheckEventType.TransportError)]
    [InlineData(TsCheckEventType.SyncLoss)]
    public void TransportDamageWithoutSamePacketDtsDoesNotCompressMissingTime(TsCheckEventType type)
    {
        var samples = Enumerable.Range(0, 200).Select(index =>
            new TsTimelineRepairService.PcrSample(index * 10,
                index * 9_000L + (index >= 50 ? 450_000 : 0), false)).ToList();
        var check = CreateTransportDamageCheck(type, type == TsCheckEventType.SyncLoss ? -1 : 0x0100);

        Assert.Empty(BuildAnalysis(samples, check).Issues);
        Assert.Equal(450_000, samples[^1].Pcr90k - 199 * 9_000L);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TransportDamageOnlyProtectsCorrespondingProgram(bool sameProgram)
    {
        var samples = Enumerable.Range(0, 200).Select(index =>
            new TsTimelineRepairService.PcrSample(index * 10,
                index * 9_000L + (index >= 50 ? 450_000 : 0), false)).ToList();
        var check = CreateTransportDamageCheck(TsCheckEventType.ContinuityGap, 0x0101);
        var program = new TsCheckProgramSummary
        {
            ProgramNumber = 1, PmtPid = 0x0080, PcrPid = sameProgram ? 0x0100 : 0x0200
        };
        program.Streams.Add(0x0101, TsStreamTypes.Aac);
        check.Programs.Add(1, program);

        var analysis = BuildAnalysis(samples, check);
        if (sameProgram)
            Assert.Empty(analysis.Issues);
        else
            Assert.Single(analysis.Issues);
    }

    [Fact]
    public void MissingMediaDoesNotHideLaterIndependentClockDamage()
    {
        var samples = Enumerable.Range(0, 200).Select(index =>
            new TsTimelineRepairService.PcrSample(index * 10,
                index * 9_000L + (index >= 50 ? 450_000 : 0) + (index >= 100 ? 450_000 : 0), false)).ToList();
        var analysis = BuildAnalysis(samples, CreateTransportDamageCheck(TsCheckEventType.ContinuityGap, 0x0100));

        Assert.Single(analysis.Issues);
        Assert.Equal(0, analysis.Segments.Sum(item => item.GetCorrection90k(500)));
        Assert.Equal(-450_000, analysis.Segments.Sum(item => item.GetCorrection90k(1_000)));
    }

    [Fact]
    public void OmittedTransportEventsPreventAutomaticCompressionOfUnprovenGap()
    {
        var samples = Enumerable.Range(0, 200).Select(index =>
            new TsTimelineRepairService.PcrSample(index * 10,
                index * 9_000L + (index >= 50 ? 450_000 : 0), false)).ToList();
        var check = CreateTransportDamageCheck(TsCheckEventType.ContinuityGap, 0x0200);
        check.OmittedEventCount = 1;

        Assert.Empty(BuildAnalysis(samples, check).Issues);
    }

    [Fact]
    public void PlanThatWouldSeparateMediaClockIsRejected()
    {
        var samples = Enumerable.Range(0, 200).Select(index =>
        {
            var pcr = index * 9_000L - (index >= 50 ? 450_000 : 0);
            return new TsTimelineRepairService.PcrSample(index * 10, pcr, false, pcr + 7_200);
        }).ToList();

        // 没有匹配的媒体时间戳校正依据时，不能只修 PCR 而留下 DTS。
        Assert.Empty(BuildAnalysis(samples).Issues);
    }

    [Fact]
    public void DiscontinuityEndsPersistentCorrection()
    {
        var samples = Enumerable.Range(0, 200).Select(index =>
            new TsTimelineRepairService.PcrSample(index * 10,
                index * 9_000L + (index is >= 50 and < 100 ? 450_000 : 0), index == 100)).ToList();
        var analysis = BuildAnalysis(samples);
        var segment = Assert.Single(analysis.Segments);

        Assert.Equal(1_000, segment.EndPacketExclusive);
        Assert.Equal(-450_000, segment.GetCorrection90k(990));
        Assert.Equal(0, segment.GetCorrection90k(1_000));
    }

    [Fact]
    public void MultiplePersistentAndFiniteCorrectionsEndAtTheirOwnBoundaries()
    {
        var samples = Enumerable.Range(0, 600).Select(index =>
        {
            long offset = index is >= 50 and < 400 ? 450_000 : 0;
            if (index is >= 100 and < 400)
                offset += 900_000;
            if (index is >= 150 and < 180)
                offset += 450_000;
            if (index is >= 220 and < 270)
                offset += (index - 220) * 9_000;
            if (index >= 450)
                offset -= 450_000;
            return new TsTimelineRepairService.PcrSample(index * 10, index * 9_000L + offset, index == 400);
        }).ToList();
        var analysis = BuildAnalysis(samples);

        Assert.Equal(5, analysis.Issues.Count);
        AssertCorrectedCadence(samples, analysis);
    }

    [Theory]
    [InlineData(false, 250)]
    [InlineData(true, 499)]
    public void ManyCorrectionsPreserveAllIndependentSteps(bool persistent, int expectedIssues)
    {
        var samples = Enumerable.Range(0, 10_000).Select(index =>
            new TsTimelineRepairService.PcrSample(index * 10,
                index * 9_000L + (persistent ? index / 20 : index / 20 % 2) * 450_000L, false)).ToList();
        var analysis = BuildAnalysis(samples);

        Assert.Equal(expectedIssues, analysis.Issues.Count);
        // 抽查进入、退出和最后未闭合的持续段，避免测试自身遍历 N×方案数。
        foreach (var index in new[] { 0, 19, 20, 39, 40, 4_999, 5_000, 9_979, 9_980, 9_999 })
        {
            var sample = samples[index];
            Assert.Equal(index * 9_000L, sample.Pcr90k +
                analysis.Segments.Sum(segment => segment.GetCorrection90k(sample.PacketIndex)));
        }
    }

    [Fact]
    public void MultipleSynchronizedBackwardStepsExpireAtDiscontinuity()
    {
        var samples = Enumerable.Range(0, 500).Select(index =>
        {
            var pcr = index * 9_000L - (index is >= 50 and < 400 ? 450_000 : 0) -
                      (index is >= 100 and < 400 ? 900_000 : 0);
            return new TsTimelineRepairService.PcrSample(index * 10, pcr, index == 400, pcr + 7_200);
        }).ToList();
        var check = new TsCheckResult
        {
            FilePath = Path.Combine(Path.GetTempPath(), "timeline-clock-test.ts"),
            FileSize = 5_000 * TsStreamAnalyzer.PacketSize, SyncOffset = 0
        };
        var program = new TsCheckProgramSummary { ProgramNumber = 1, PmtPid = 0x80, PcrPid = 0x0100 };
        program.Streams.Add(0x0100, TsStreamTypes.Hevc);
        check.Programs.Add(1, program);
        foreach (var (packet, seconds) in new[] { (500L, 4.9), (1_000L, 9.9) })
            check.Events.Add(new TsCheckEvent
            {
                Severity = TsCheckSeverity.Error, Type = TsCheckEventType.DtsBackward, Pid = 0x0100,
                StartPacket = packet, FileOffset = packet * TsStreamAnalyzer.PacketSize,
                MessageCode = TsCheckMessageCode.DtsBackward, MessageArguments = [seconds]
            });
        var analysis = BuildAnalysis(samples, check);

        Assert.Equal(2, analysis.Issues.Count);
        Assert.All(analysis.Issues, issue => Assert.True(issue.AffectsStreamTimestamps));
        AssertCorrectedCadence(samples, analysis);
    }

    [Fact]
    public void TemporaryOffsetDoesNotInterpolateVideoBitrate()
    {
        long packet = 0;
        var samples = Enumerable.Range(0, 200).Select(index =>
        {
            packet += index % 3 == 0 ? 200 : 10;
            return new TsTimelineRepairService.PcrSample(packet,
                index * 9_000L + (index is >= 50 and < 80 ? 450_000 : 0), false);
        }).ToList();
        var analysis = BuildAnalysis(samples);
        Assert.Equal(TsTimelineIssueKind.TemporaryPcrOffset, Assert.Single(analysis.Issues).Kind);
        AssertCorrectedCadence(samples, analysis);
    }

    [Fact]
    public void GradualCorrectionAddsToExistingPersistentCorrection()
    {
        long packet = 0;
        var samples = Enumerable.Range(0, 200).Select(index =>
        {
            packet += index % 3 == 0 ? 200 : 10;
            var offset = index >= 50 ? 450_000 : 0;
            if (index is >= 100 and < 150)
                offset += (index - 100) * 9_000;
            return new TsTimelineRepairService.PcrSample(packet, index * 9_000L + offset, false);
        }).ToList();
        var analysis = BuildAnalysis(samples);

        Assert.Equal(2, analysis.Issues.Count);
        Assert.Contains(analysis.Issues, item => item.Kind == TsTimelineIssueKind.PersistentClockDiscontinuity);
        Assert.Contains(analysis.Issues, item => item.Kind == TsTimelineIssueKind.GradualPcrDrift);
        AssertCorrectedCadence(samples, analysis);
    }

    [Fact]
    public void ExtraShortPcrSamplesDoNotHideGradualDrift()
    {
        var samples = Enumerable.Range(0, 2_000).Select(index =>
            new TsTimelineRepairService.PcrSample(index * 10,
                index * 9_000L - index / 50 * 8_500L +
                (index is >= 1_000 and < 1_400 ? (index - 1_000) * 900L : 0), false)).ToList();
        var analysis = BuildAnalysis(samples);
        Assert.Equal(TsTimelineIssueKind.GradualPcrDrift, Assert.Single(analysis.Issues).Kind);
        long? previous = null;
        foreach (var sample in samples)
        {
            var corrected = sample.Pcr90k + analysis.Segments.Sum(segment => segment.GetCorrection90k(sample.PacketIndex));
            if (previous is { } clock)
                Assert.InRange(corrected - clock, 1, 45_000);
            previous = corrected;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TimestampCorrectionRequiresMatchingProgram(bool matchingProgram)
    {
        var samples = Enumerable.Range(0, 200).Select(index =>
        {
            var pcr = index * 9_000L - (index >= 50 ? 450_000 : 0);
            return new TsTimelineRepairService.PcrSample(index * 10, pcr, false, pcr + 7_200);
        }).ToList();
        var check = new TsCheckResult
        {
            FilePath = Path.Combine(Path.GetTempPath(), "timeline-clock-test.ts"),
            FileSize = (samples[^1].PacketIndex + 1) * TsStreamAnalyzer.PacketSize,
            SyncOffset = 0
        };
        var program = new TsCheckProgramSummary
        {
            ProgramNumber = 1, PmtPid = 0x0080, PcrPid = matchingProgram ? 0x0100 : 0x0101
        };
        program.Streams.Add(0x0100, TsStreamTypes.Hevc);
        check.Programs.Add(1, program);
        check.Events.Add(new TsCheckEvent
        {
            Severity = TsCheckSeverity.Error, Type = TsCheckEventType.DtsBackward,
            Pid = 0x0100, StartPacket = 500, FileOffset = 500 * TsStreamAnalyzer.PacketSize,
            MessageCode = TsCheckMessageCode.DtsBackward, MessageArguments = [4.9]
        });
        var analysis = BuildAnalysis(samples, check);
        if (matchingProgram)
            Assert.True(Assert.Single(analysis.Issues).AffectsStreamTimestamps);
        else
            Assert.Empty(analysis.Issues);
    }

    [Fact]
    public async Task PersistentAndGradualRepairOutputKeepsContinuousPcr()
    {
        var fixture = await CreateFixtureAsync(static sample =>
            (sample >= 50 ? 450_000 : 0) + (sample is >= 100 and < 150 ? (sample - 100) * 9_000L : 0));
        var output = fixture.Path + ".fixed.ts";
        try
        {
            var analysis = await AnalyzeAsync(fixture);
            Assert.Equal(2, analysis.Issues.Count);
            var result = await new TsTimelineRepairService().RepairAsync(analysis, output, true);
            Assert.Equal(0, result.RemainingPcrErrorCount);
            Assert.Equal(0, result.RemainingPcrWarningCount);
            var verification = await new TsStreamAnalyzer().AnalyzeAsync(output);
            Assert.DoesNotContain(verification.Events,
                item => item.Type is TsCheckEventType.PcrBackward or TsCheckEventType.PcrJump or TsCheckEventType.PcrGap);
            AssertOnlyPcrBaseChanged(await File.ReadAllBytesAsync(fixture.Path), await File.ReadAllBytesAsync(output));
        }
        finally
        {
            DeleteFixture(fixture);
            File.Delete(output);
        }
    }

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
    public async Task OutputPacketRewriterSkipsTimelineBoundaryWhenPreviousPcrAnchorWasReplaced()
    {
        var fixture = await CreateFixtureAsync(static sample => sample >= 50 ? 450_000 : 0);
        try
        {
            var analysis = await AnalyzeAsync(fixture);
            var issue = Assert.Single(analysis.Issues);
            var segment = Assert.Single(analysis.Segments);
            var anchorOffset = segment.StartAnchorPacket * TsStreamAnalyzer.PacketSize;
            var rewriter = new TsTimelineRepairService.OutputPacketRewriter(
                analysis,
                [(issue.PcrPid, anchorOffset, anchorOffset + TsStreamAnalyzer.PacketSize)]);
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
    public async Task ElementaryPayloadReplacementDoesNotSuppressTimelineRepair()
    {
        var fixture = await CreateFixtureAsync(static sample => sample >= 50 ? 450_000 : 0);
        try
        {
            var timelineAnalysis = await AnalyzeAsync(fixture);
            var segment = Assert.Single(timelineAnalysis.Segments);
            var anchorOffset = segment.StartAnchorPacket * TsStreamAnalyzer.PacketSize;
            var catalog = new TsCheckResult
            {
                FilePath = fixture.Path,
                FileSize = fixture.Length,
                SyncOffset = 0
            };
            var reference = new TsRepairSourceAnalysis
            {
                FilePath = fixture.Path,
                Catalog = catalog,
                IsReference = true,
                TimelineAnalysis = timelineAnalysis
            };
            var multiSourceAnalysis = new TsMultiSourceAnalysisResult
            {
                ReferenceSource = reference
            };
            multiSourceAnalysis.Sources.Add(reference);
            var plan = new TsRepairOutputPlan
            {
                Analysis = multiSourceAnalysis,
                RepairTimelineOnOutput = true
            };
            plan.Replacements.Add(new TsPacketReplacement
            {
                SourcePath = fixture.Path,
                SourcePid = 0x0100,
                TargetPid = 0x0100,
                ReferenceStartOffset = anchorOffset,
                ReferenceEndOffset = anchorOffset + TsStreamAnalyzer.PacketSize,
                StartContinuityCounter = 0,
                ReferencePacketCount = 1,
                SourceStartOffset = anchorOffset,
                SourceEndOffset = anchorOffset + TsStreamAnalyzer.PacketSize,
                PacketCount = 1,
                TimestampOffset90k = 0,
                PcrTimestampOffset90k = 0,
                ElementaryPayloadOnly = true,
                ElementaryLength = 1
            });
            plan.Replacements.Add(new TsPacketReplacement
            {
                SourcePath = fixture.Path,
                SourcePid = 0x0101,
                TargetPid = 0x0101,
                ReferenceStartOffset = 0,
                ReferenceEndOffset = TsStreamAnalyzer.PacketSize,
                StartContinuityCounter = 0,
                ReferencePacketCount = 1,
                SourceStartOffset = 0,
                SourceEndOffset = TsStreamAnalyzer.PacketSize,
                PacketCount = 1,
                TimestampOffset90k = 0,
                PcrTimestampOffset90k = 0
            });

            var replacedRanges = TsMultiSourceRepairService.BuildReplacedTimelineRanges(plan);
            var replacedRange = Assert.Single(replacedRanges);
            Assert.Equal(0x0101, replacedRange.Pid);
            Assert.Equal(0, replacedRange.StartOffset);
            Assert.Equal(TsStreamAnalyzer.PacketSize, replacedRange.EndOffset);
            var rewriter = new TsTimelineRepairService.OutputPacketRewriter(
                timelineAnalysis, replacedRanges);
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

    private static TsTimelineRepairAnalysis BuildAnalysis(
        List<TsTimelineRepairService.PcrSample> samples, TsCheckResult? check = null) =>
        TsTimelineRepairService.BuildAnalysisFromPcrSamples(
            Path.Combine(Path.GetTempPath(), "timeline-clock-test.ts"),
            check ?? new TsCheckResult
            {
                FilePath = Path.Combine(Path.GetTempPath(), "timeline-clock-test.ts"),
                FileSize = (samples[^1].PacketIndex + 1) * TsStreamAnalyzer.PacketSize,
                SyncOffset = 0
            }, new Dictionary<int, List<TsTimelineRepairService.PcrSample>> { [0x0100] = samples });

    private static TsCheckResult CreateTransportDamageCheck(TsCheckEventType type, int pid)
    {
        var check = new TsCheckResult
        {
            FilePath = Path.Combine(Path.GetTempPath(), "timeline-clock-test.ts"),
            FileSize = 2_000 * TsStreamAnalyzer.PacketSize,
            SyncOffset = 0
        };
        check.Events.Add(new TsCheckEvent
        {
            Severity = TsCheckSeverity.Error, Type = type, Pid = pid,
            StartPacket = 495, EndPacket = 500, FileOffset = 495 * TsStreamAnalyzer.PacketSize,
            MessageCode = TsCheckMessageCode.ContinuityGap
        });
        return check;
    }

    private static void AssertCorrectedCadence(
        List<TsTimelineRepairService.PcrSample> samples, TsTimelineRepairAnalysis analysis)
    {
        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            var correction = analysis.Segments.Sum(segment => segment.GetCorrection90k(sample.PacketIndex));
            Assert.Equal(index * 9_000L, sample.Pcr90k + correction);
        }
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
