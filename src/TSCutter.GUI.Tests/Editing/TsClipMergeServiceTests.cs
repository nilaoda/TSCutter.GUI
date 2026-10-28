using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using TSCutter.GUI.Models.TransportStream.Analysis;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.TransportStream.Processing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Utils.TransportStream.Packets;

namespace TSCutter.GUI.Tests.Editing;

public sealed class TsClipMergeServiceTests
{
    private const int PacketSize = TsStreamAnalyzer.PacketSize;
    private const int MediaPid = 0x0100;
    private const long TimestampWrap = 1L << 33;

    [Theory]
    [InlineData(17)]
    [InlineData(65_200)]
    [InlineData(2_000_001)]
    [InlineData(TsScramblingProbe.MaximumProbeBytes + 17)]
    public async Task NonTsSuffixAndDamagedPrefixKeepTheRealPacketAlignment(int prefix)
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"ts-merge-prefix-{Guid.NewGuid():N}.mts");
        var outputPath = sourcePath + ".merged.ts";
        try
        {
            var packets = Enumerable.Range(0, 20)
                .SelectMany(index => CreatePacket(index, index * 9_000L)).ToArray();
            var source = new byte[prefix + packets.Length];
            packets.CopyTo(source, prefix);
            await File.WriteAllBytesAsync(sourcePath, source);
            await new TsClipMergeService().MergeAsync(new TsClipMergeRequest
            {
                SourcePath = sourcePath, OutputPath = outputPath,
                Ranges = [new(prefix, prefix + 10 * PacketSize, 0, 1)]
            });
            Assert.Equal(packets[..(10 * PacketSize)], await File.ReadAllBytesAsync(outputPath));
        }
        finally { File.Delete(sourcePath); File.Delete(outputPath); }
    }

    [Fact]
    public async Task MarkedRangesKeepTheirRealOffsetsAfterBytesInsertedBetweenThem()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"ts-merge-offsets-{Guid.NewGuid():N}.ts");
        var outputPath = sourcePath + ".merged.ts";
        try
        {
            var first = Enumerable.Range(0, 10).SelectMany(index => CreatePacket(index, index * 9_000L)).ToArray();
            var second = Enumerable.Range(20, 10).SelectMany(index => CreatePacket(index, index * 9_000L)).ToArray();
            const int insertedBytes = 17;
            var secondStart = first.Length + insertedBytes;
            byte[] data = [.. first, .. new byte[insertedBytes], .. second, 0x12, 0x34];
            await File.WriteAllBytesAsync(sourcePath, data);
            var session = new ExportTsCheckSession(outputPath);
            var result = await new TsClipMergeService().MergeAsync(new TsClipMergeRequest
            {
                SourcePath = sourcePath, OutputPath = outputPath,
                Ranges = [new(PacketSize, first.Length, 0.1, 0.9), new(secondStart, -1, 2, 2.9)]
            }, checkSession: session);
            session.Complete();
            var reference = await new TsStreamAnalyzer().AnalyzeAsync(outputPath);
            Assert.Equal(reference.PacketCount, session.Result!.PacketCount);
            Assert.Equal(reference.TotalErrorCount, session.Result.TotalErrorCount);
            Assert.Equal(reference.TotalWarningCount, session.Result.TotalWarningCount);
            Assert.Equal(reference.FileSize, session.Result.FileSize);
            var output = await File.ReadAllBytesAsync(outputPath);
            Assert.Equal(19 * PacketSize, output.Length);
            Assert.Equal(2, result.SegmentCount);
            Assert.Equal(first[PacketSize..], output[..(9 * PacketSize)]);
            for (var index = 0; index < 10; index++)
            {
                // 时间戳和 CC 允许在合并接缝处改写，压缩载荷必须来自原来的真实偏移。
                Assert.Equal(second.AsSpan(index * PacketSize + 26, PacketSize - 26).ToArray(),
                    output.AsSpan((9 + index) * PacketSize + 26, PacketSize - 26).ToArray());
            }
        }
        finally { File.Delete(sourcePath); File.Delete(outputPath); }
    }

    [Fact]
    public async Task UnmarkedFileStartStillSkipsADamagedPrefix()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"ts-merge-unmarked-{Guid.NewGuid():N}.ts");
        var outputPath = sourcePath + ".merged.ts";
        try
        {
            var packets = Enumerable.Range(0, 10).SelectMany(index => CreatePacket(index, index * 9_000L)).ToArray();
            byte[] data = [.. new byte[17], .. packets];
            await File.WriteAllBytesAsync(sourcePath, data);
            await new TsClipMergeService().MergeAsync(new TsClipMergeRequest
            {
                SourcePath = sourcePath, OutputPath = outputPath, Ranges = [new(0, -1, 0, 1)]
            });
            Assert.Equal(packets, await File.ReadAllBytesAsync(outputPath));
        }
        finally { File.Delete(sourcePath); File.Delete(outputPath); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(TimestampWrap - 18_000)]
    public async Task AudioPesSpanningPastVideoEndCannotOverlapAtTheJoin(long basePts)
    {
        const int audioPid = 257;
        var sourcePath = Path.Combine(Path.GetTempPath(), $"ts-merge-audio-{Guid.NewGuid():N}.ts");
        var outputPath = sourcePath + ".merged.ts";
        try
        {
            var packets = Enumerable.Range(0, 30).SelectMany(index =>
            {
                var videoPts = index < 20 ? index * 9_000L : 900_000 + (index - 20) * 9_000L;
                var audioPts = index < 20 ? 36_000 + index * 36_000L : 880_000 + (index - 20) * 36_000L;
                var audio = CreatePacket(index, basePts + audioPts);
                audio[2] = audioPid & 255;
                audio[15] = 0xBD;
                return new[] { CreatePacket(index, basePts + videoPts), audio };
            }).ToArray();
            await File.WriteAllBytesAsync(sourcePath, packets.SelectMany(packet => packet).ToArray());
            await new TsClipMergeService().MergeAsync(new TsClipMergeRequest
            {
                SourcePath = sourcePath, OutputPath = outputPath, VideoPid = MediaPid, AudioPids = [audioPid],
                Ranges = [new(0, 10 * PacketSize, 0, 0.4), new(40 * PacketSize, 50 * PacketSize, 10, 10.4)]
            });
            var output = await File.ReadAllBytesAsync(outputPath);
            var previousAudio = output.AsSpan(9 * PacketSize, PacketSize);
            var nextVideo = output.AsSpan(10 * PacketSize, PacketSize);
            var nextAudio = output.AsSpan(11 * PacketSize, PacketSize);
            Assert.Equal(36_000, ModuloTimestamp(ReadPts(nextAudio) - ReadPts(previousAudio)));
            Assert.Equal(20_000, ModuloTimestamp(ReadPts(nextVideo) - ReadPts(nextAudio)));
            Assert.True(ModuloTimestamp(ReadPts(nextVideo) - ReadPts(output.AsSpan(8 * PacketSize, PacketSize))) > 0);
        }
        finally { File.Delete(sourcePath); File.Delete(outputPath); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(TimestampWrap - 36_000)]
    public async Task IncludedEndFrameDoesNotSharePtsWithTheNextSegment(long basePts)
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"ts-merge-inclusive-{Guid.NewGuid():N}.ts");
        var outputPath = sourcePath + ".merged.ts";
        try
        {
            await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 50)
                .SelectMany(index => CreatePacket(index, basePts + index * 9_000L)).ToArray());
            await new TsClipMergeService().MergeAsync(new TsClipMergeRequest
            {
                SourcePath = sourcePath,
                OutputPath = outputPath,
                Ranges =
                [
                    new(0, 11 * PacketSize, 0, 1),
                    new(20 * PacketSize, 31 * PacketSize, 2, 3),
                    new(40 * PacketSize, 50 * PacketSize, 4, 4.9)
                ]
            });
            var output = await File.ReadAllBytesAsync(outputPath);
            var end = output.AsSpan(10 * PacketSize, PacketSize);
            var next = output.AsSpan(11 * PacketSize, PacketSize);
            Assert.Equal(9_000, ModuloTimestamp(ReadPts(next) - ReadPts(end)));
            Assert.Equal(9_000, ModuloTimestamp(ReadPcr(next) - ReadPcr(end)));
            // 第三段继续累计实际播放跨度，不能把第二段已应用的偏移重复计入。
            end = output.AsSpan(21 * PacketSize, PacketSize);
            next = output.AsSpan(22 * PacketSize, PacketSize);
            Assert.Equal(9_000, ModuloTimestamp(ReadPts(next) - ReadPts(end)));
            Assert.Equal(9_000, ModuloTimestamp(ReadPcr(next) - ReadPcr(end)));
        }
        finally { File.Delete(sourcePath); File.Delete(outputPath); }
    }

    [Fact]
    public async Task LeadingPicturesInTheNextSegmentStartAfterTheIncludedEndFrame()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"ts-merge-leading-{Guid.NewGuid():N}.ts");
        var outputPath = sourcePath + ".merged.ts";
        try
        {
            var packets = Enumerable.Range(0, 30)
                .Select(index => CreatePacket(index, index * 9_000L)).ToArray();
            long[] nextPts = [900_000, 873_000, 882_000, 891_000, 909_000, 918_000];
            for (var index = 0; index < nextPts.Length; index++)
                packets[20 + index] = CreatePacket(20 + index, nextPts[index]);
            await File.WriteAllBytesAsync(sourcePath, packets.SelectMany(packet => packet).ToArray());
            await new TsClipMergeService().MergeAsync(new TsClipMergeRequest
            {
                SourcePath = sourcePath,
                OutputPath = outputPath,
                Ranges = [new(0, 5 * PacketSize, 0, 0.4), new(20 * PacketSize, 26 * PacketSize, 10, 10.2)]
            });
            var output = await File.ReadAllBytesAsync(outputPath);
            Assert.Equal(36_000, ReadPts(output.AsSpan(4 * PacketSize, PacketSize)));
            Assert.Equal(72_000, ReadPts(output.AsSpan(5 * PacketSize, PacketSize)));
            Assert.Equal(45_000, ReadPts(output.AsSpan(6 * PacketSize, PacketSize)));
            Assert.Equal(54_000, ReadPts(output.AsSpan(7 * PacketSize, PacketSize)));
            Assert.Equal(63_000, ReadPts(output.AsSpan(8 * PacketSize, PacketSize)));
        }
        finally { File.Delete(sourcePath); File.Delete(outputPath); }
    }

    [Fact]
    public async Task DecodeTimestampsAlsoRemainStrictlyAfterThePreviousSegment()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"ts-merge-dts-{Guid.NewGuid():N}.ts");
        var outputPath = sourcePath + ".merged.ts";
        try
        {
            var packets = Enumerable.Range(0, 30)
                .Select(index => CreatePacket(index, index * 9_000L)).ToArray();
            for (var index = 0; index < 5; index++)
                SetDts(packets[index], index * 9_000L);
            for (var index = 20; index < 25; index++)
            {
                packets[index] = CreatePacket(index, 900_000 + (index - 20) * 9_000L);
                SetDts(packets[index], 800_000 + (index - 20) * 9_000L);
            }
            await File.WriteAllBytesAsync(sourcePath, packets.SelectMany(packet => packet).ToArray());
            await new TsClipMergeService().MergeAsync(new TsClipMergeRequest
            {
                SourcePath = sourcePath,
                OutputPath = outputPath,
                Ranges = [new(0, 5 * PacketSize, 0, 0.4), new(20 * PacketSize, 25 * PacketSize, 10, 10.4)]
            });
            var output = await File.ReadAllBytesAsync(outputPath);
            var end = output.AsSpan(4 * PacketSize, PacketSize);
            var next = output.AsSpan(5 * PacketSize, PacketSize);
            Assert.Equal(45_000, Utils.TransportStream.Packets.TsTimestampFieldCodec.ReadPesTimestamp(next[26..31]));
            Assert.True(ReadPts(next) > ReadPts(end));
            // 一整段采用同一偏移，原有 PTS-DTS 差不能被接缝修复改写。
            Assert.Equal(100_000, ReadPts(next) - Utils.TransportStream.Packets.TsTimestampFieldCodec.ReadPesTimestamp(next[26..31]));
        }
        finally { File.Delete(sourcePath); File.Delete(outputPath); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(TimestampWrap - 9_000)]
    public async Task PcrPhaseAndIndependentClocksConstrainTheWholeSegment(long basePts)
    {
        const int otherPcrPid = 300;
        var stride = basePts == 0 ? 188 : 192;
        byte[] PcrOnly(int pid, long timestamp, int extension = 0, int counter = 0)
        {
            var packet = Enumerable.Repeat((byte)0xFF, PacketSize).ToArray();
            packet[0] = 0x47;
            packet[1] = (byte)(pid >> 8);
            packet[2] = (byte)pid;
            packet[3] = (byte)(0x20 | (pid == MediaPid ? 4 : counter));
            packet[4] = 183;
            packet[5] = 0x10;
            WritePcr(packet.AsSpan(6, 6), basePts + timestamp);
            packet[10] = (byte)((packet[10] & 0xFE) | (extension >> 8));
            packet[11] = (byte)extension;
            return packet;
        }
        var packets = new List<byte[]>
        {
            TableProbeData.Section(0, TableProbeData.Pat()),
            TableProbeData.Section(0x1000, TableProbeData.Pmt())
        };
        for (var index = 0; index < 5; index++)
        {
            var packet = CreatePacket(index, basePts + index * 9_000);
            WritePcr(packet.AsSpan(6, 6), basePts + index * 9_000 - 72_000);
            packets.Add(packet);
        }
        if (stride == 192)
        {
            var payload = TableProbeData.Padding();
            payload[1] = (byte)(otherPcrPid >> 8);
            payload[2] = (byte)(otherPcrPid & 255);
            payload[3] = 0x18;
            packets.Add(payload);
        }
        var oldOtherCounter = stride == 192 ? 8 : 9;
        packets.AddRange([PcrOnly(MediaPid, -33_000), PcrOnly(otherPcrPid, -70_000, counter: oldOtherCounter),
            PcrOnly(otherPcrPid, -30_000, 299, oldOtherCounter)]);
        var firstEnd = packets.Count;
        packets.AddRange(Enumerable.Range(0, 10).Select(_ => TableProbeData.Padding()));
        var secondStart = packets.Count;
        for (var index = 0; index < 5; index++)
        {
            var packet = CreatePacket(index, basePts + 900_000 + index * 9_000);
            // 片段内故意保留一次真实 PCR 倒退，接缝修复不能掩盖它。
            WritePcr(packet.AsSpan(6, 6), basePts + (index == 2 ? 827_000 : 819_000 + index * 9_000));
            packets.Add(packet);
            if (index == 1)
            {
                packets.Add(PcrOnly(otherPcrPid, 819_000, counter: 3));
                if (stride == 192)
                {
                    var payload = TableProbeData.Padding();
                    payload[1] = (byte)(otherPcrPid >> 8);
                    payload[2] = (byte)(otherPcrPid & 255);
                    payload[3] = 0x14;
                    packets.Add(payload);
                }
            }
        }
        var secondEnd = packets.Count;
        packets.AddRange(Enumerable.Range(0, 10).Select(_ => TableProbeData.Padding()));
        var thirdStart = packets.Count;
        for (var index = 0; index < 5; index++)
        {
            var packet = CreatePacket(index, basePts + 1_800_000 + index * 9_000);
            WritePcr(packet.AsSpan(6, 6), basePts + 1_770_000 + index * 9_000);
            if (index == 0 && stride == 192)
            {
                // 首包没有 adaptation field 时，应在 PES 前插入无 PCR 的切换标记。
                var payloadOnly = Enumerable.Repeat((byte)0xFF, 188).ToArray();
                packet.AsSpan(0, 4).CopyTo(payloadOnly);
                payloadOnly[3] &= 0xDF;
                packet.AsSpan(12).CopyTo(payloadOnly.AsSpan(4));
                packet = payloadOnly;
            }
            packets.Add(packet);
            if (index == 0)
            {
                var between = PcrOnly(MediaPid, 1_770_000);
                between[3] = 0x20;
                packets.Add(between);
                packets.Add(packet.ToArray());
            }
        }
        using var files = new TableProbeData.Files(packets.SelectMany((packet, index) => stride == 188
            ? packet : new byte[] { 0xC0, 0, (byte)(index >> 8), (byte)index }.Concat(packet)).ToArray());
        var session = new ExportTsCheckSession(files.Output);
        await new TsClipMergeService().MergeAsync(new TsClipMergeRequest
        {
            SourcePath = files.Source, OutputPath = files.Output, VideoPid = MediaPid,
            Ranges = [new(0, firstEnd * stride, 0, 0.4),
                new(secondStart * stride, secondEnd * stride, 10, 10.4),
                new(thirdStart * stride, packets.Count * stride, 20, 20.4)]
        }, checkSession: session);
        session.Complete();
        var output = (await File.ReadAllBytesAsync(files.Output)).Chunk(stride)
            .Select(packet => packet[(stride - PacketSize)..]).ToArray();
        var clocks = output.Where(packet => TsPacketParser.Parse(packet) is { Pid: otherPcrPid, HasPcr: true }).ToArray();
        Assert.Equal(3, clocks.Length);
        var expectedCounter = stride == 192 ? 8 : 3;
        Assert.Equal(expectedCounter, clocks[2][3] & 15);
        Assert.Contains(output, packet => TsPacketParser.Parse(packet) is
            { Pid: otherPcrPid, Discontinuity: true, HasPcr: false } && (packet[3] & 15) == expectedCounter);
        var before = clocks[1];
        var after = clocks[2];
        var delta27m = ModuloTimestamp(ReadPcr(after) - ReadPcr(before)) * 300 +
            (((after[10] & 1) << 8) | after[11]) - (((before[10] & 1) << 8) | before[11]);
        Assert.Equal(1, delta27m);
        var video = output.Where(packet => TsPacketParser.Parse(packet).Pid == MediaPid && (packet[1] & 0x40) != 0).ToArray();
        Assert.Equal(81_000, ModuloTimestamp(ReadPts(video[6]) - ReadPcr(video[6])));
        Assert.Equal(video[10], video[11]);
        Assert.Contains(output, packet => TsPacketParser.Parse(packet) is { Pid: MediaPid, Discontinuity: true });
        var verification = await new TsStreamAnalyzer().AnalyzeAsync(files.Output);
        var retained = Assert.Single(verification.Events, item => item.Type == TsCheckEventType.PcrBackward);
        Assert.Equal(1_000 / 90_000d, retained.MessageArguments[0]);
        Assert.Equal(1, verification.ErrorCount);
        Assert.Equal(verification.ErrorCount, session.Result!.ErrorCount);
        Assert.DoesNotContain(verification.Events, item => item.Type == TsCheckEventType.PcrGap);
        if (stride == 188)
        {
            var timeline = await new TSCutter.GUI.Services.TransportStream.Repair.TsTimelineRepairService()
                .AnalyzeAsync(files.Output, verification);
            Assert.Empty(timeline.Issues);
        }
    }

    private static void SetDts(byte[] packet, long dts)
    {
        packet[19] = 0xC0;
        packet[20] = 10;
        packet[21] = (byte)((packet[21] & 15) | 0x30);
        WriteTimestamp(packet.AsSpan(26, 5), dts);
        packet[26] = (byte)((packet[26] & 15) | 0x10);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MergeMakesJoinContinuousAndPreservesInternalTransportDamage(bool programOnly)
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"ts-merge-{Guid.NewGuid():N}.ts");
        var outputPath = sourcePath + ".merged.ts";
        try
        {
            var packets = Enumerable.Range(0, 30)
                .Select(index => CreatePacket(index, index * 9_000L))
                .ToArray();
            for (var index = 0; index < packets.Length; index++)
                WritePcr(packets[index].AsSpan(6, 6), index * 9_000L - (index < 20 ? 72_000 : 27_000));
            // 第二段内部故意保留一个 CC 跳号和一个 TEI；合法时钟切换不能掩盖它们。
            packets[25][3] = (byte)((packets[25][3] & 0xF0) | 0x0B);
            packets[26][1] |= 0x80;
            byte[] source = [.. TableProbeData.Section(0, TableProbeData.Pat()),
                .. TableProbeData.Section(0x1000, TableProbeData.Pmt()), .. packets.SelectMany(packet => packet)];
            await File.WriteAllBytesAsync(sourcePath, source);
            var session = new ExportTsCheckSession(outputPath);

            var result = await new TsClipMergeService().MergeAsync(new TsClipMergeRequest
            {
                SourcePath = sourcePath,
                OutputPath = outputPath,
                ProgramId = programOnly ? 1 : null,
                VideoPid = MediaPid,
                Ranges =
                [
                    new TsClipMergeRange(0, 12 * PacketSize, 0, 1),
                    new TsClipMergeRange(22 * PacketSize, 32 * PacketSize, 2, 3)
                ]
            }, checkSession: session);
            session.Complete();

            var fullOutput = await File.ReadAllBytesAsync(outputPath);
            var output = fullOutput.Chunk(PacketSize)
                .Where(packet => (((packet[1] & 31) << 8) | packet[2]) == MediaPid)
                .SelectMany(packet => packet).ToArray();
            Assert.Equal(20 * PacketSize, output.Length);
            Assert.Equal(2, result.SegmentCount);
            Assert.True(result.RewrittenPcrCount > 0);
            Assert.True(result.RewrittenTimestampCount > 0);
            Assert.True(result.RewrittenContinuityCount > 0);

            var previous = output.AsSpan(9 * PacketSize, PacketSize);
            var joined = output.AsSpan(10 * PacketSize, PacketSize);
            Assert.Equal(9, previous[3] & 0x0F);
            Assert.Equal(10, joined[3] & 0x0F);
            Assert.Equal(54_000, ReadPcr(joined) - ReadPcr(previous));
            Assert.True(TsPacketParser.Parse(joined).Discontinuity);
            Assert.Equal(9_000, ReadPts(joined) - ReadPts(previous));

            var beforeGap = output.AsSpan(14 * PacketSize, PacketSize);
            var gapPacket = output.AsSpan(15 * PacketSize, PacketSize);
            Assert.NotEqual(((beforeGap[3] & 0x0F) + 1) & 0x0F, gapPacket[3] & 0x0F);
            Assert.NotEqual(0, output[16 * PacketSize + 1] & 0x80);

            var verification = await new TsStreamAnalyzer().AnalyzeAsync(outputPath);
            Assert.Equal(TsExportCheckState.Completed, session.State);
            Assert.Equal(fullOutput.Length, session.Result!.FileSize);
            Assert.Equal(verification.PacketCount, session.Result.PacketCount);
            Assert.Equal(verification.TotalErrorCount, session.Result.TotalErrorCount);
            Assert.Equal(verification.TotalWarningCount, session.Result.TotalWarningCount);
            Assert.Equal(1, session.Result.Pids[MediaPid].ContinuityErrors);
            Assert.Equal(1, session.Result.Pids[MediaPid].TransportErrors);
            Assert.DoesNotContain(session.Result.Events, item => item.Type is TsCheckEventType.PcrBackward or TsCheckEventType.PtsBackward);
            Assert.Equal(1, verification.Pids[MediaPid].ContinuityErrors);
            Assert.Equal(1, verification.Pids[MediaPid].TransportErrors);
            Assert.Contains(verification.Events, item => item.Type == TsCheckEventType.TransportError);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Theory]
    [InlineData(188, false)]
    [InlineData(192, false)]
    [InlineData(188, true)]
    public async Task AudioPesAreCompletedWithoutJoiningOrphanPayloads(int stride, bool programOnly)
    {
        const int audioPid = 257;
        var packets = new List<byte[]>
        {
            TableProbeData.Section(0, TableProbeData.Pat()),
            TableProbeData.Section(0x1000, TsPsiSectionBuilder.BuildPmt(1, 0, MediaPid, [],
            [
                new(MediaPid, new TsStreamDefinition { StreamType = TsStreamTypes.H264 }),
                new(audioPid, new TsStreamDefinition
                {
                    StreamType = TsStreamTypes.PrivateData, Descriptors = [0x05, 4, 0x44, 0x52, 0x41, 0x31]
                })
            ]))
        };
        var ranges = new List<TsClipMergeRange>();
        var expectedPayload = new List<byte>();
        for (var segment = 0; segment < 4; segment++)
        {
            var pts = segment * 900_000L;
            var audio = new byte[368];
            audio.AsSpan().Fill((byte)(0x60 + segment));
            byte[] header = [0, 0, 1, 0xBD, 1, 0x6A, 0x80, 0x80, 5];
            header.CopyTo(audio, 0);
            WriteTimestamp(audio.AsSpan(9, 5), pts + 1_000);
            // 当前节目路径同时覆盖 PES 头跨越视频终点、首包重复及 DRA 元数据识别。
            var audioPackets = new List<byte[]>();
            for (var offset = 0; offset < audio.Length;)
            {
                var count = Math.Min(offset == 0 && programOnly ? 3 : 184, audio.Length - offset);
                var packet = new byte[188];
                packet.AsSpan().Fill(0xFF);
                packet[0] = 0x47;
                packet[1] = offset == 0 ? (byte)0x41 : (byte)1;
                packet[2] = 1;
                packet[3] = (byte)((count == 184 ? 0x10 : 0x30) | audioPackets.Count);
                if (count < 184)
                {
                    packet[4] = (byte)(183 - count);
                    if (packet[4] > 0)
                        packet[5] = 0;
                }
                audio.AsSpan(offset, count).CopyTo(packet.AsSpan(188 - count));
                audioPackets.Add(packet);
                offset += count;
            }
            var head = audioPackets[0];
            byte[] orphan = [0x47, 1, 1, 0x1F, .. new byte[184]];
            var start = packets.Count;
            packets.AddRange([CreatePacket(0, pts), orphan, orphan.ToArray(), head,
                CreatePacket(1, pts + 9_000), CreatePacket(2, pts + 18_000)]);
            if (programOnly)
                packets.Add(head.ToArray());
            packets.AddRange(audioPackets.Skip(1));
            packets.AddRange([head.ToArray(), CreatePacket(3, pts + 27_000)]);
            ranges.Add(new((segment == 0 ? 0 : start) * stride, (start + 6) * stride,
                pts / 90_000d, (pts + 18_000) / 90_000d));
            expectedPayload.AddRange(audio[14..]);
        }
        using var files = new TableProbeData.Files(packets.SelectMany((packet, index) => stride == 188
            ? packet : new byte[] { 0, 0, (byte)(index >> 8), (byte)index }.Concat(packet)).ToArray());
        var session = new ExportTsCheckSession(files.Output);
        var result = await new TsClipMergeService().MergeAsync(new TsClipMergeRequest
        {
            SourcePath = files.Source, OutputPath = files.Output, Ranges = ranges,
            VideoPid = MediaPid, AudioPids = programOnly ? [] : [audioPid], ProgramId = programOnly ? 1 : null
        }, checkSession: session);
        session.Complete();
        var bytes = await File.ReadAllBytesAsync(files.Output);
        Assert.Equal(bytes.Length, result.OutputBytes);
        var bodies = bytes.Chunk(stride).Select(packet => packet[(stride - 188)..]).ToArray();
        Assert.Equal(12, bodies.Count(packet => TsPacketParser.Parse(packet).Pid == MediaPid));
        var outputAudio = bodies.Where(packet => TsPacketParser.Parse(packet).Pid == audioPid).ToArray();
        Assert.Equal(programOnly ? 16 : 8, outputAudio.Length);
        var actualPayload = new List<byte>();
        var headerRemaining = 0;
        byte[]? previous = null;
        foreach (var packet in outputAudio)
        {
            if (previous is not null && packet.AsSpan().SequenceEqual(previous))
                continue;
            previous = packet;
            var info = TsPacketParser.Parse(packet);
            if (info.PayloadStart)
                headerRemaining = 14;
            var skipped = Math.Min(headerRemaining, 188 - info.PayloadOffset);
            headerRemaining -= skipped;
            actualPayload.AddRange(packet[(info.PayloadOffset + skipped)..]);
        }
        Assert.Equal(expectedPayload, actualPayload);
        Assert.Equal(0, session.Result!.ErrorCount);
        var verification = await new TsStreamAnalyzer().AnalyzeAsync(files.Output);
        Assert.Equal(0, verification.ErrorCount);
        Assert.Equal(session.Result.WarningCount, verification.WarningCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DolbyAudioFramesCrossingPesStayCompleteAtEveryJoin(bool enhanced)
    {
        const int audioPid = 257;
        var stride = enhanced ? 192 : 188;
        var basePts = enhanced ? TimestampWrap - 27_000 : 0;
        var frames = new List<byte[]>();
        for (var index = 0; index < 40; index++)
        {
            var frame = Enumerable.Repeat((byte)(index + 1), 128).ToArray();
            byte[] header = enhanced ? [0x0B, 0x77, 0, 63, 0x30, 0x80, 0] : [0x0B, 0x77, 0, 0, 0, 0x40, 0];
            header.CopyTo(frame, 0);
            frames.Add(frame);
            if (enhanced)
            {
                var dependent = frame.ToArray();
                dependent[2] = 0x40;
                frames.Add(dependent);
            }
        }
        var audio = frames.SelectMany(frame => frame).ToArray();
        var unitSize = enhanced ? 256 : 128;
        var packets = new List<byte[]>
        {
            TableProbeData.Section(0, TableProbeData.Pat()),
            TableProbeData.Section(0x1000, TsPsiSectionBuilder.BuildPmt(1, 0, MediaPid, [],
            [new(MediaPid, new TsStreamDefinition { StreamType = TsStreamTypes.H264 }),
             new(audioPid, new TsStreamDefinition { StreamType = enhanced ? TsStreamTypes.Eac3 : TsStreamTypes.Ac3 })]))
        };
        for (var offset = 0; offset < audio.Length; offset += 150)
        {
            var count = Math.Min(150, audio.Length - offset);
            var pes = new byte[14 + count];
            byte[] header = [0, 0, 1, 0xBD, 0, (byte)(pes.Length - 6), 0x80, 0x80, 5];
            header.CopyTo(pes, 0);
            WriteTimestamp(pes.AsSpan(9, 5), basePts + (offset + unitSize - 1) / unitSize * 2_880L);
            audio.AsSpan(offset, count).CopyTo(pes.AsSpan(14));
            var packet = Enumerable.Repeat((byte)0xFF, 188).ToArray();
            packet[0] = 0x47;
            packet[1] = 0x41;
            packet[2] = 1;
            packet[3] = (byte)(0x30 | (offset / 150 & 15));
            packet[4] = (byte)(183 - pes.Length);
            packet[5] = 0;
            pes.CopyTo(packet, 188 - pes.Length);
            packets.Add(CreatePacket(offset / 150, basePts + offset / 150 * 3_000L));
            packets.Add(packet);
        }
        using var files = new TableProbeData.Files(packets.SelectMany((packet, index) => stride == 188
            ? packet : new byte[] { 0xC0, 0, (byte)(index >> 8), (byte)index }.Concat(packet)).ToArray());
        var starts = new[] { 2, 10, 18, 26 };
        await new TsClipMergeService().MergeAsync(new TsClipMergeRequest
        {
            SourcePath = files.Source, OutputPath = files.Output, VideoPid = MediaPid, AudioPids = [audioPid],
            ProgramId = enhanced ? 1 : null,
            Ranges = starts.Select(index => new TsClipMergeRange((2 + 2 * index) * stride,
                (2 + 2 * (index + 4)) * stride, index / 30d, (index + 4) / 30d)).ToArray()
        }, addProgramTables: true);
        var output = await File.ReadAllBytesAsync(files.Output);
        var actual = new List<byte>();
        foreach (var raw in output.Chunk(stride))
        {
            if (enhanced)
                Assert.Equal(0xC0, raw[0] & 0xC0);
            var packet = raw[(stride - 188)..];
            var info = TsPacketParser.Parse(packet);
            if (info.Pid != audioPid || !info.HasPayload)
                continue;
            var offset = info.PayloadOffset;
            if (info.PayloadStart)
                offset += 9 + packet[offset + 8];
            actual.AddRange(packet[offset..]);
        }
        var expected = starts.SelectMany(index => audio.AsSpan(
            (index * 150 + unitSize - 1) / unitSize * unitSize,
            ((index + 4) * 150 + unitSize - 1) / unitSize * unitSize -
                (index * 150 + unitSize - 1) / unitSize * unitSize).ToArray());
        Assert.Equal(expected, actual);
        var result = await new TsStreamAnalyzer().AnalyzeAsync(files.Output);
        Assert.True(result.ErrorCount == 0, string.Join("; ", result.Events.Select(item =>
            $"{item.Type}: {string.Join(',', item.MessageArguments)}")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledMergeDeletesIncompleteOutput(bool addTables)
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"ts-merge-{Guid.NewGuid():N}.ts");
        var outputPath = sourcePath + ".cancelled.ts";
        try
        {
            const int packetCount = 10_000;
            byte[] tables = addTables
                ? [.. TableProbeData.Section(0, TableProbeData.Pat()), .. TableProbeData.Section(0x1000, TableProbeData.Pmt())]
                : [];
            await File.WriteAllBytesAsync(sourcePath, [.. tables, .. Enumerable.Range(0, packetCount)
                .SelectMany(index => CreatePacket(index, index * 900L))]);
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress<TsClipMergeProgress>(_ => cancellation.Cancel());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new TsClipMergeService().MergeAsync(new TsClipMergeRequest
                {
                    SourcePath = sourcePath,
                    OutputPath = outputPath,
                    Ranges = [new TsClipMergeRange(tables.Length, tables.Length + packetCount * PacketSize, 0, 100)]
                }, progress, cancellation.Token, addProgramTables: addTables));
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task OverlappingSelectionsAreWrittenOnceInTimelineOrder()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"ts-merge-{Guid.NewGuid():N}.ts");
        var outputPath = sourcePath + ".overlap.ts";
        try
        {
            await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 20)
                .SelectMany(index => CreatePacket(index, index * 9_000L)).ToArray());

            var result = await new TsClipMergeService().MergeAsync(new TsClipMergeRequest
            {
                SourcePath = sourcePath,
                OutputPath = outputPath,
                Ranges =
                [
                    new TsClipMergeRange(5 * PacketSize, 15 * PacketSize, 0.5, 1.5),
                    new TsClipMergeRange(0, 10 * PacketSize, 0, 1)
                ]
            });

            Assert.Equal(1, result.SegmentCount);
            Assert.Equal(15 * PacketSize, new FileInfo(outputPath).Length);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    private static byte[] CreatePacket(int index, long timestamp90k)
    {
        var packet = new byte[PacketSize];
        packet.AsSpan().Fill(0xFF);
        packet[0] = 0x47;
        packet[1] = (byte)(0x40 | ((MediaPid >> 8) & 0x1F));
        packet[2] = (byte)(MediaPid & 0xFF);
        packet[3] = (byte)(0x30 | (index & 0x0F));
        packet[4] = 7;
        packet[5] = 0x10;
        WritePcr(packet.AsSpan(6, 6), timestamp90k);

        var payload = packet.AsSpan(12);
        payload[0] = 0;
        payload[1] = 0;
        payload[2] = 1;
        payload[3] = 0xE0;
        payload[4] = 0;
        payload[5] = 0;
        payload[6] = 0x80;
        payload[7] = 0x80;
        payload[8] = 5;
        WriteTimestamp(payload[9..14], timestamp90k);
        return packet;
    }

    private static long ReadPcr(ReadOnlySpan<byte> packet) =>
        ((long)packet[6] << 25) |
        ((long)packet[7] << 17) |
        ((long)packet[8] << 9) |
        ((long)packet[9] << 1) |
        ((long)packet[10] >> 7);

    private static long ReadPts(ReadOnlySpan<byte> packet)
    {
        var offset = 4 + ((packet[3] & 0x20) != 0 ? packet[4] + 1 : 0);
        var value = packet[(offset + 9)..(offset + 14)];
        return ((long)(value[0] & 0x0E) << 29) |
               ((long)value[1] << 22) |
               ((long)(value[2] & 0xFE) << 14) |
               ((long)value[3] << 7) |
               ((long)value[4] >> 1);
    }

    private static void WritePcr(Span<byte> value, long timestamp90k)
    {
        var raw = ModuloTimestamp(timestamp90k);
        value[0] = (byte)(raw >> 25);
        value[1] = (byte)(raw >> 17);
        value[2] = (byte)(raw >> 9);
        value[3] = (byte)(raw >> 1);
        value[4] = (byte)(((raw & 1) << 7) | 0x7E);
        value[5] = 0;
    }

    private static void WriteTimestamp(Span<byte> value, long timestamp90k)
    {
        var raw = ModuloTimestamp(timestamp90k);
        value[0] = (byte)(0x20 | (((raw >> 30) & 7) << 1) | 1);
        value[1] = (byte)(raw >> 22);
        value[2] = (byte)((((raw >> 15) & 0x7F) << 1) | 1);
        value[3] = (byte)(raw >> 7);
        value[4] = (byte)(((raw & 0x7F) << 1) | 1);
    }

    private static long ModuloTimestamp(long value)
    {
        value %= TimestampWrap;
        return value < 0 ? value + TimestampWrap : value;
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
