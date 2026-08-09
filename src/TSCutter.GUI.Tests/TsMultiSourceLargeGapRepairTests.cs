using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class TsMultiSourceLargeGapRepairTests
{
    [Fact]
    public async Task SparseSubtitleDamageUsesCorrelatedVideoRepairRange()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"ts-dense-subtitle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var donorPath = Path.Combine(directory, "donor.ts");
        var referencePath = Path.Combine(directory, "reference.ts");
        var outputPath = Path.Combine(directory, "output.ts");
        try
        {
            await WriteDenseSubtitleDamageStreamAsync(donorPath, damaged: false);
            await WriteDenseSubtitleDamageStreamAsync(referencePath, damaged: true);

            var service = new TsMultiSourceRepairService();
            var analysis = await service.AnalyzeAsync(
                [referencePath, donorPath], referencePath, normalizeTimeline: false);
            Assert.InRange(
                analysis.TimelineEndPts90k - analysis.TimelineStartPts90k,
                1, 10L * 90_000);
            var subtitleTrack = Assert.Single(analysis.Tracks, item =>
                item.ReferencePid == SubtitlePid);
            var subtitleRegion = Assert.Single(subtitleTrack.PesRegions, item =>
                item.Reason == TsRepairPesRegionReason.DenseTransportDamage);
            Assert.Equal(subtitleRegion.ReferenceFirstPts90k,
                subtitleRegion.ReferenceLastPts90k);

            var selectedPids = analysis.Tracks.Select(item => item.ReferencePid).ToHashSet();
            var plan = service.BuildOutputPlan(
                analysis, selectedPids, includeServiceInformation: true);
            var videoReplacement = Assert.Single(plan.Replacements, item =>
                item.TargetPid == VideoPid);
            var subtitleReplacement = Assert.Single(plan.Replacements, item =>
                item.TargetPid == SubtitlePid);
            Assert.Equal(videoReplacement.SourceStartOffset,
                subtitleReplacement.SourceStartOffset);
            Assert.Equal(videoReplacement.SourceEndOffset,
                subtitleReplacement.SourceEndOffset);
            Assert.True(subtitleReplacement.PacketCount > 1);

            var result = await service.OutputAsync(plan, outputPath);
            Assert.Equal(0, result.RemainingErrorCount);
            var verification = await new TsStreamAnalyzer().AnalyzeAsync(outputPath);
            Assert.Equal(0, verification.ErrorCount);
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
    public async Task DenseContinuityDamageIsPromotedToBoundedRegionReplacement()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"ts-dense-damage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var donorPath = Path.Combine(directory, "donor.ts");
        var referencePath = Path.Combine(directory, "reference.ts");
        var outputPath = Path.Combine(directory, "output.ts");
        try
        {
            await WriteDenseDamageStreamAsync(donorPath, damaged: false);
            await WriteDenseDamageStreamAsync(referencePath, damaged: true);

            var service = new TsMultiSourceRepairService();
            var analysis = await service.AnalyzeAsync(
                [referencePath, donorPath], referencePath, normalizeTimeline: false);
            var track = Assert.Single(analysis.Tracks);
            var region = Assert.Single(track.PesRegions, item =>
                item.Reason == TsRepairPesRegionReason.DenseTransportDamage);
            Assert.True(region.MismatchCount >= 3);
            Assert.NotEmpty(region.Candidates);

            var plan = service.BuildOutputPlan(
                analysis, new HashSet<int> { VideoPid }, includeServiceInformation: true);
            Assert.Single(plan.Replacements);
            Assert.Empty(plan.Insertions);

            var result = await service.OutputAsync(plan, outputPath);
            Assert.Equal(0, result.RemainingErrorCount);
            var verification = await new TsStreamAnalyzer().AnalyzeAsync(outputPath);
            Assert.Equal(0, verification.Pids[VideoPid].ContinuityErrors);
            Assert.DoesNotContain(verification.Events, item =>
                item.Pid == VideoPid && item.Severity == TsCheckSeverity.Error);
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
    public void DenseCandidateSelectionRequiresMatchingDonorPid()
    {
        const string sourcePath = "donor.ts";
        var region = new TsRepairPesRegion
        {
            ReferencePid = VideoPid,
            ReferenceStartOffset = 100,
            ReferenceEndOffset = 200,
            ReferenceStartContinuityCounter = 0,
            ReferencePacketCount = 1,
            ReferenceFirstPts90k = 0,
            ReferenceLastPts90k = 3_600,
            ReferencePts90k = [0, 3_600],
            MismatchCount = 3,
            Reason = TsRepairPesRegionReason.DenseTransportDamage,
            BeforeAnchor = [],
            ReferenceSignatures = [],
            AfterAnchor = []
        };
        region.Candidates.Add(new TsRepairPesRegionCandidate
        {
            SourcePath = sourcePath,
            SourcePid = AudioPid1,
            SourceStartOffset = 1_000,
            SourceEndOffset = 2_000,
            PacketCount = 1,
            TimestampOffset90k = 0,
            ElementaryLength = 0,
            FingerprintMatches = 10
        });
        var expected = new TsRepairPesRegionCandidate
        {
            SourcePath = sourcePath,
            SourcePid = AudioPid2,
            SourceStartOffset = 3_000,
            SourceEndOffset = 4_000,
            PacketCount = 1,
            TimestampOffset90k = 90_000,
            ElementaryLength = 0,
            FingerprintMatches = 1
        };
        region.Candidates.Add(expected);
        var match = new TsRepairTrackMatch
        {
            SourcePath = sourcePath,
            SourcePid = AudioPid2,
            TimestampOffset90k = 0
        };

        var selected = TsMultiSourceRepairService.SelectDenseCandidate(
            region, match, sourcePath);

        Assert.Same(expected, selected);
    }

    [Fact]
    public async Task DirectPacketInsertionRewritesPesTimestampAtPayloadStart()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"ts-packet-insertion-timestamp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var donorPath = Path.Combine(directory, "donor.ts");
        var referencePath = Path.Combine(directory, "reference.ts");
        var outputPath = Path.Combine(directory, "output.ts");
        try
        {
            await WriteStreamAsync(referencePath, missingStart: -1, missingCount: 0,
                frameCount: 4);
            await File.WriteAllBytesAsync(
                donorPath, CreateVideoPesPacket(VideoPid, 0, 90_000, 100));

            var catalog = await new TsStreamAnalyzer().AnalyzeAsync(referencePath);
            var filter = new TsStreamFilterService();
            var filterPlan = filter.BuildPlan(
                catalog, new HashSet<int> { VideoPid }, includeServiceInformation: false);
            var insertionOffset = 2L * TsStreamAnalyzer.PacketSize;
            var insertion = new TsPacketInsertion
            {
                SourcePath = donorPath,
                SourcePid = VideoPid,
                TargetPid = VideoPid,
                // 插入包后紧接参考源 CC=0 的首包，因此从 15 开始可保持连续。
                StartContinuityCounter = 15,
                SourcePacketOffsets = [0],
                TimestampOffset90k = 9_000,
                PcrTimestampOffset90k = 0
            };

            await filter.FilterWithInsertionsAsync(
                referencePath, outputPath, catalog, filterPlan,
                new Dictionary<long, List<TsPacketInsertion>>
                {
                    [insertionOffset] = [insertion]
                },
                new Dictionary<long, List<TsLargeGapInsertion>>(),
                [], new HashSet<long>(),
                new TsRepairOutputValidator(filterPlan.EffectivePids), null);

            var output = await File.ReadAllBytesAsync(outputPath);
            var insertedPacket = output.AsSpan(
                2 * TsStreamAnalyzer.PacketSize, TsStreamAnalyzer.PacketSize);
            Assert.NotEqual(0, insertedPacket[1] & 0x40);
            Assert.Equal(99_000, ReadPts(insertedPacket.Slice(13, 5)));
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
    public async Task DenseAudioVideoReplacementPreservesDonorPacketInterleaving()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"ts-interleaved-replacement-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var donorPath = Path.Combine(directory, "donor.ts");
        var referencePath = Path.Combine(directory, "reference.ts");
        var outputPath = Path.Combine(directory, "output.ts");
        try
        {
            await WritePacketsAsync(referencePath,
            [
                CreatePsiPacket(0x0000, 0, BuildPatSection(PmtPid)),
                CreatePsiPacket(PmtPid, 0, BuildPmtSection(
                    VideoPid, (0x1B, VideoPid), (0x03, AudioPid1))),
                CreatePesPacket(VideoPid, 0, 0, 0, 0xE0),
                CreatePesPacket(AudioPid1, 0, 0, 0, 0xC0),
                // 模拟损坏参考区：各 PID 包位已失去正常的跨轨交织关系。
                CreatePesPacket(VideoPid, 1, 3_600, 1, 0xE0),
                CreatePesPacket(VideoPid, 2, 7_200, 2, 0xE0),
                CreatePesPacket(AudioPid1, 1, 3_600, 1, 0xC0),
                CreatePesPacket(AudioPid1, 2, 7_200, 2, 0xC0),
                CreatePesPacket(VideoPid, 3, 10_800, 3, 0xE0),
                CreatePesPacket(AudioPid1, 3, 10_800, 3, 0xC0)
            ]);
            await WritePacketsAsync(donorPath,
            [
                CreatePesPacket(VideoPid, 0, 3_600, 11, 0xE0),
                CreatePesPacket(AudioPid1, 0, 3_600, 21, 0xC0),
                CreatePesPacket(VideoPid, 1, 7_200, 12, 0xE0),
                CreatePesPacket(AudioPid1, 1, 7_200, 22, 0xC0)
            ]);

            var catalog = await new TsStreamAnalyzer().AnalyzeAsync(referencePath);
            var filter = new TsStreamFilterService();
            var filterPlan = filter.BuildPlan(
                catalog, new HashSet<int> { VideoPid, AudioPid1 },
                includeServiceInformation: false);
            var packetSize = TsStreamAnalyzer.PacketSize;
            var replacements = new TsPacketReplacement[]
            {
                new()
                {
                    SourcePath = donorPath,
                    SourcePid = VideoPid,
                    TargetPid = VideoPid,
                    ReferenceStartOffset = 4L * packetSize,
                    ReferenceEndOffset = 8L * packetSize,
                    StartContinuityCounter = 1,
                    ReferencePacketCount = 2,
                    SourceStartOffset = 0,
                    SourceEndOffset = 4L * packetSize,
                    PacketCount = 2,
                    TimestampOffset90k = 0,
                    PcrTimestampOffset90k = 0,
                    PreserveSourceInterleaving = true
                },
                new()
                {
                    SourcePath = donorPath,
                    SourcePid = AudioPid1,
                    TargetPid = AudioPid1,
                    ReferenceStartOffset = 6L * packetSize,
                    ReferenceEndOffset = 9L * packetSize,
                    StartContinuityCounter = 1,
                    ReferencePacketCount = 2,
                    SourceStartOffset = 0,
                    SourceEndOffset = 4L * packetSize,
                    PacketCount = 2,
                    TimestampOffset90k = 0,
                    PcrTimestampOffset90k = 0,
                    PreserveSourceInterleaving = true
                }
            };

            await filter.FilterWithInsertionsAsync(
                referencePath, outputPath, catalog, filterPlan,
                new Dictionary<long, List<TsPacketInsertion>>(),
                new Dictionary<long, List<TsLargeGapInsertion>>(),
                replacements, new HashSet<long>(),
                new TsRepairOutputValidator(filterPlan.EffectivePids), null);

            var output = await File.ReadAllBytesAsync(outputPath);
            var mediaPids = new List<int>();
            for (var offset = 0; offset < output.Length; offset += packetSize)
            {
                var pid = ((output[offset + 1] & 0x1F) << 8) | output[offset + 2];
                if (pid is VideoPid or AudioPid1)
                    mediaPids.Add(pid);
            }
            Assert.Equal(
                [VideoPid, AudioPid1, VideoPid, AudioPid1,
                    VideoPid, AudioPid1, VideoPid, AudioPid1],
                mediaPids);
            var verification = await new TsStreamAnalyzer().AnalyzeAsync(outputPath);
            Assert.Equal(0, verification.Pids[VideoPid].ContinuityErrors);
            Assert.Equal(0, verification.Pids[AudioPid1].ContinuityErrors);
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
    public async Task DuplicateDonorPidFallsBackToIndependentReplacement()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"ts-duplicate-donor-pid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var donorPath = Path.Combine(directory, "donor.ts");
        var referencePath = Path.Combine(directory, "reference.ts");
        var outputPath = Path.Combine(directory, "output.ts");
        try
        {
            await WritePacketsAsync(referencePath,
            [
                CreatePsiPacket(0x0000, 0, BuildPatSection(PmtPid)),
                CreatePsiPacket(PmtPid, 0, BuildPmtSection(
                    VideoPid, (0x1B, VideoPid), (0x03, AudioPid1))),
                CreatePesPacket(VideoPid, 0, 0, 0, 0xE0),
                CreatePesPacket(AudioPid1, 0, 0, 0, 0xC0),
                CreatePesPacket(VideoPid, 1, 3_600, 1, 0xE0),
                CreatePesPacket(VideoPid, 2, 7_200, 2, 0xE0),
                CreatePesPacket(AudioPid1, 1, 3_600, 1, 0xC0),
                CreatePesPacket(AudioPid1, 2, 7_200, 2, 0xC0),
                CreatePesPacket(VideoPid, 3, 10_800, 3, 0xE0),
                CreatePesPacket(AudioPid1, 3, 10_800, 3, 0xC0)
            ]);
            await WritePacketsAsync(donorPath,
            [
                CreatePesPacket(VideoPid, 0, 3_600, 11, 0xE0),
                CreatePesPacket(VideoPid, 1, 7_200, 12, 0xE0)
            ]);

            var catalog = await new TsStreamAnalyzer().AnalyzeAsync(referencePath);
            var filter = new TsStreamFilterService();
            var filterPlan = filter.BuildPlan(
                catalog, new HashSet<int> { VideoPid, AudioPid1 },
                includeServiceInformation: false);
            var packetSize = TsStreamAnalyzer.PacketSize;
            var replacements = new TsPacketReplacement[]
            {
                new()
                {
                    SourcePath = donorPath,
                    SourcePid = VideoPid,
                    TargetPid = VideoPid,
                    ReferenceStartOffset = 4L * packetSize,
                    ReferenceEndOffset = 8L * packetSize,
                    StartContinuityCounter = 1,
                    ReferencePacketCount = 2,
                    SourceStartOffset = 0,
                    SourceEndOffset = 2L * packetSize,
                    PacketCount = 2,
                    TimestampOffset90k = 0,
                    PcrTimestampOffset90k = 0,
                    PreserveSourceInterleaving = true
                },
                new()
                {
                    SourcePath = donorPath,
                    SourcePid = VideoPid,
                    TargetPid = AudioPid1,
                    ReferenceStartOffset = 6L * packetSize,
                    ReferenceEndOffset = 9L * packetSize,
                    StartContinuityCounter = 1,
                    ReferencePacketCount = 2,
                    SourceStartOffset = 0,
                    SourceEndOffset = 2L * packetSize,
                    PacketCount = 2,
                    TimestampOffset90k = 0,
                    PcrTimestampOffset90k = 0,
                    PreserveSourceInterleaving = true
                }
            };

            await filter.FilterWithInsertionsAsync(
                referencePath, outputPath, catalog, filterPlan,
                new Dictionary<long, List<TsPacketInsertion>>(),
                new Dictionary<long, List<TsLargeGapInsertion>>(),
                replacements, new HashSet<long>(),
                new TsRepairOutputValidator(filterPlan.EffectivePids), null);

            var verification = await new TsStreamAnalyzer().AnalyzeAsync(outputPath);
            Assert.Equal(0, verification.Pids[VideoPid].ContinuityErrors);
            Assert.Equal(0, verification.Pids[AudioPid1].ContinuityErrors);
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
    public async Task LargeGapStartsWithContinuityFromActualWrittenPackets()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"ts-large-gap-packetization-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var donorPath = Path.Combine(directory, "donor.ts");
        var referencePath = Path.Combine(directory, "reference.ts");
        var outputPath = Path.Combine(directory, "output.ts");
        try
        {
            // 相同 PES 在辅助源中拆成两个 TS 包，使缺口段包数与参考源模 16 不同。
            await WriteStreamAsync(
                donorPath, missingStart: -1, missingCount: 0,
                splitPesStart: 40, splitPesCount: 40);
            await WriteStreamAsync(referencePath, missingStart: 40, missingCount: 40);

            var service = new TsMultiSourceRepairService();
            var analysis = await service.AnalyzeAsync(
                [referencePath, donorPath], referencePath, normalizeTimeline: false);
            var largeGap = Assert.Single(analysis.LargeGaps);
            await service.MatchLargeGapsAsync(analysis);
            Assert.Single(largeGap.Candidates);

            var selectedPids = analysis.Tracks
                .Select(track => track.ReferencePid).ToHashSet();
            var plan = service.BuildOutputPlan(
                analysis, selectedPids, includeServiceInformation: true,
                new HashSet<long> { largeGap.ReferenceInsertOffset });
            var result = await service.OutputAsync(plan, outputPath);

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
    public async Task RetainedServiceInformationContinuityIsRealignedAfterLargeGap()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"ts-large-gap-si-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var donorPath = Path.Combine(directory, "donor.ts");
        var referencePath = Path.Combine(directory, "reference.ts");
        var outputPath = Path.Combine(directory, "output.ts");
        try
        {
            await WriteStreamAsync(
                donorPath, missingStart: -1, missingCount: 0,
                includeServiceInformation: true);
            await WriteStreamAsync(
                referencePath, missingStart: 40, missingCount: 40,
                includeServiceInformation: true);

            var service = new TsMultiSourceRepairService();
            var analysis = await service.AnalyzeAsync(
                [referencePath, donorPath], referencePath, normalizeTimeline: false);
            var largeGap = Assert.Single(analysis.LargeGaps);
            Assert.True(analysis.ReferenceSource.ServiceInformationContinuityErrors > 0);

            await service.MatchLargeGapsAsync(analysis);
            var selectedPids = analysis.Tracks
                .Select(track => track.ReferencePid).ToHashSet();
            var plan = service.BuildOutputPlan(
                analysis, selectedPids, includeServiceInformation: true,
                new HashSet<long> { largeGap.ReferenceInsertOffset });
            var result = await service.OutputAsync(plan, outputPath);

            var verification = await new TsStreamAnalyzer().AnalyzeAsync(outputPath);
            Assert.Equal(0, verification.Pids[0x0011].ContinuityErrors);
            Assert.Equal(0, verification.Pids[VideoPid].ContinuityErrors);
            Assert.Equal(0, result.RemainingErrorCount);
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
            Assert.True(candidate.Tracks[0].SourcePacketCount > 0);

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
            var verification = await new TsStreamAnalyzer().AnalyzeAsync(outputPath);
            Assert.Equal(0, verification.ErrorCount);
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
    public async Task DamagedDonorStartPesIsRejectedForIncidentReplacement()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), $"ts-large-gap-damaged-donor-start-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var donorPath = Path.Combine(directory, "donor.ts");
        var referencePath = Path.Combine(directory, "reference.ts");
        try
        {
            // 两端负载指纹相同，但辅助源候选起始 PES 带 TEI，不能作为整段替换的健康边界。
            await WriteStreamAsync(
                donorPath, missingStart: -1, missingCount: 0, damagedFrame: 11);
            await WriteStreamAsync(
                referencePath, missingStart: 40, missingCount: 40, damagedFrame: 10);

            var service = new TsMultiSourceRepairService();
            var analysis = await service.AnalyzeAsync(
                [referencePath, donorPath], referencePath, normalizeTimeline: false);
            var largeGap = Assert.Single(analysis.LargeGaps);
            Assert.True(largeGap.IncludesPrecedingDamage);

            await service.MatchLargeGapsAsync(analysis);

            Assert.Empty(largeGap.Candidates);
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
            var verification = await new TsStreamAnalyzer().AnalyzeAsync(outputPath);
            Assert.Equal(0, verification.ErrorCount);
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
            Assert.True(new FileInfo(outputPath).Length > 0);
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
    private const int SubtitlePid = 0x0104;

    private static async Task WritePacketsAsync(string path, IReadOnlyList<byte[]> packets)
    {
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        foreach (var packet in packets)
            await stream.WriteAsync(packet);
    }

    private static async Task WriteStreamAsync(
        string path,
        int missingStart,
        int missingCount,
        int damagedFrame = -1,
        int frameCount = 120,
        bool includeServiceInformation = false,
        int splitPesStart = -1,
        int splitPesCount = 0)
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
            if (includeServiceInformation && frame % 10 == 0)
                packets.Add(CreatePacket(0x0011, frame / 10 & 0x0F));
            var extraPacketsBeforeFrame = splitPesStart < 0
                ? 0
                : Math.Clamp(frame - splitPesStart, 0, splitPesCount);
            var continuityCounter = (frame + extraPacketsBeforeFrame) & 0x0F;
            var packet = CreateVideoPesPacket(
                VideoPid, continuityCounter, frame * 3_600L, frame);
            if (frame == damagedFrame)
                packet[1] |= 0x80;
            if (frame >= splitPesStart && frame < splitPesStart + splitPesCount)
                packets.AddRange(SplitPesPacket(packet, continuityCounter));
            else
                packets.Add(packet);
        }
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        foreach (var packet in packets)
            await stream.WriteAsync(packet);
    }

    private static async Task WriteDenseDamageStreamAsync(string path, bool damaged)
    {
        var packets = new List<byte[]>
        {
            CreatePsiPacket(0x0000, 0, BuildPatSection(PmtPid)),
            CreatePsiPacket(PmtPid, 0, BuildPmtSection(VideoPid))
        };
        for (var frame = 0; frame < 120; frame++)
        {
            var continuityCounter = frame & 0x0F;
            if (damaged && frame is >= 40 and <= 44)
                continuityCounter = (continuityCounter + frame * 3 + 5) & 0x0F;
            var packet = CreateVideoPesPacket(
                VideoPid, continuityCounter, frame * 3_600L, frame);
            if (frame == 45)
                AddAdaptationField(packet, discontinuity: damaged);
            packets.Add(packet);
        }
        await using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        foreach (var packet in packets)
            await stream.WriteAsync(packet);
    }

    private static async Task WriteDenseSubtitleDamageStreamAsync(string path, bool damaged)
    {
        var packets = new List<byte[]>
        {
            CreatePsiPacket(0x0000, 0, BuildPatSection(PmtPid)),
            CreatePsiPacket(PmtPid, 0, BuildPmtWithDvbSubtitleSection())
        };
        var subtitleContinuityCounter = 0;
        const long invalidSubtitlePts90k = (1L << 33) - 1;
        for (var frame = 0; frame < 120; frame++)
        {
            var videoContinuityCounter = frame & 0x0F;
            if (damaged && frame is >= 40 and <= 44)
                videoContinuityCounter = (videoContinuityCounter + frame * 3 + 5) & 0x0F;
            var video = CreateVideoPesPacket(
                VideoPid, videoContinuityCounter, frame * 3_600L, frame);
            if (frame == 45)
                AddAdaptationField(video, discontinuity: damaged);
            packets.Add(video);

            if (frame % 10 == 0)
            {
                packets.Add(CreatePesPacket(
                    SubtitlePid, subtitleContinuityCounter++ & 0x0F,
                    invalidSubtitlePts90k, 7, 0xBD));
            }
            if (frame is < 41 or > 44)
                continue;

            var expectedCounter = subtitleContinuityCounter++ & 0x0F;
            var subtitle = CreatePacket(SubtitlePid, expectedCounter);
            if (damaged && frame < 44)
                subtitle[3] = (byte)(0x10 | ((expectedCounter + frame - 38) & 0x0F));
            if (frame == 44)
                AddAdaptationField(subtitle, discontinuity: damaged);
            packets.Add(subtitle);
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

    private static byte[][] SplitPesPacket(byte[] source, int continuityCounter)
    {
        const int firstPayloadLength = 100;
        var sourcePayload = source.AsSpan(4);
        var first = CreatePacket(VideoPid, continuityCounter);
        var second = CreatePacket(VideoPid, (continuityCounter + 1) & 0x0F);
        WriteAdaptedPayload(first, sourcePayload[..firstPayloadLength], payloadStart: true);
        WriteAdaptedPayload(second, sourcePayload[firstPayloadLength..], payloadStart: false);
        return [first, second];
    }

    private static void WriteAdaptedPayload(byte[] packet, ReadOnlySpan<byte> payload, bool payloadStart)
    {
        if (payloadStart)
            packet[1] |= 0x40;
        packet[3] = (byte)(0x30 | (packet[3] & 0x0F));
        var adaptationLength = 183 - payload.Length;
        packet[4] = (byte)adaptationLength;
        if (adaptationLength > 0)
        {
            packet[5] = 0;
            packet.AsSpan(6, adaptationLength - 1).Fill(0xFF);
        }
        payload.CopyTo(packet.AsSpan(5 + adaptationLength));
    }

    private static void AddAdaptationField(byte[] packet, bool discontinuity)
    {
        // 辅助源和参考源保持相同负载长度；只有参考源在密集故障结束处设置
        // discontinuity_indicator，用于覆盖录制设备主动重置 CC 的真实场景。
        packet.AsSpan(4, 182).CopyTo(packet.AsSpan(6));
        packet[3] = (byte)(0x30 | (packet[3] & 0x0F));
        packet[4] = 1;
        packet[5] = discontinuity ? (byte)0x80 : (byte)0;
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

    private static byte[] BuildPmtWithDvbSubtitleSection() => AppendCrc([
        0x02, 0xB0, 0x21, 0x00, 0x01, 0xC1, 0x00, 0x00,
        (byte)(0xE0 | (VideoPid >> 8)), (byte)(VideoPid & 0xFF), 0xF0, 0x00,
        0x1B, (byte)(0xE0 | (VideoPid >> 8)), (byte)(VideoPid & 0xFF), 0xF0, 0x00,
        0x06, (byte)(0xE0 | (SubtitlePid >> 8)), (byte)(SubtitlePid & 0xFF), 0xF0, 0x0A,
        0x59, 0x08, (byte)'e', (byte)'n', (byte)'g', 0x10,
        0x00, 0x01, 0x00, 0x01
    ]);

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

    private static long ReadPts(ReadOnlySpan<byte> value) =>
        ((long)(value[0] & 0x0E) << 29) |
        ((long)value[1] << 22) |
        ((long)(value[2] & 0xFE) << 14) |
        ((long)value[3] << 7) |
        ((long)value[4] >> 1);
}
