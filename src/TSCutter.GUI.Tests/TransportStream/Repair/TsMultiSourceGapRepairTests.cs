using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using TSCutter.GUI.Models.TransportStream.Analysis;
using TSCutter.GUI.Models.TransportStream.Repair;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Services.TransportStream.Repair;

namespace TSCutter.GUI.Tests.TransportStream.Repair;

/// <summary>
/// 多源修复中 CC 小缺口、ES 级缺口、多音轨映射、广播时间锚点和输出错误路径的测试。
/// </summary>
public sealed class TsMultiSourceGapRepairTests
{
    private const int PacketSize = TsStreamAnalyzer.PacketSize;
    private const int PmtPid = 0x0100;
    private const int VideoPid = 0x0101;
    private const int AudioPid = 0x0102;
    private const int SecondAudioPid = 0x0103;
    private const int DonorAudioPid = 0x0202;
    private const int SecondDonorAudioPid = 0x0203;
    private const int TimeTablePid = 0x0014;

    public enum AudioFrameFormat
    {
        MpegAudio,
        Adts,
        Latm
    }

    [Fact]
    public async Task SmallContinuityGapIsFilledWithDonorPackets()
    {
        using var directory = new TemporaryDirectory("ts-small-gap");
        var donor = BuildVideoStream(out var donorFrames);
        var reference = BuildVideoStream(out var referenceFrames);
        reference.Drop(referenceFrames[60]);
        reference.Drop(referenceFrames[61]);
        await donor.WriteAsync(directory.DonorPath);
        await reference.WriteAsync(directory.ReferencePath);

        var service = new TsMultiSourceRepairService();
        var analysis = await service.AnalyzeAsync(
            [directory.ReferencePath, directory.DonorPath], directory.ReferencePath,
            normalizeTimeline: false);

        Assert.Empty(analysis.LargeGaps);
        var track = Assert.Single(analysis.Tracks);
        Assert.Equal(1, track.ContinuityErrorCount);
        Assert.Equal(1, track.RepairableGapCount);
        var gap = Assert.Single(track.Gaps);
        Assert.Equal(reference.OffsetOf(referenceFrames[62]), gap.ReferenceInsertOffset);
        Assert.Equal(2, gap.MissingPacketModulo);
        var candidate = Assert.Single(gap.Candidates);
        Assert.Equal(directory.DonorPath, candidate.SourcePath);
        Assert.Equal(VideoPid, candidate.SourcePid);
        Assert.Equal(
            new[] { donor.OffsetOf(donorFrames[60]), donor.OffsetOf(donorFrames[61]) },
            candidate.SourcePacketOffsets);
        Assert.Null(candidate.ElementaryPayload);
        var match = Assert.Single(track.Matches);
        Assert.Equal(TsRepairMatchConfidence.PacketFingerprint, match.Confidence);

        var plan = service.BuildOutputPlan(
            analysis, new HashSet<int> { VideoPid }, includeServiceInformation: true);
        Assert.Equal(1, plan.RepairedGapCount);
        Assert.Equal(2, plan.RepairedPacketCount);
        var insertion = Assert.Single(Assert.Single(plan.Insertions).Value);
        Assert.Equal(gap.ExpectedContinuityCounter, insertion.StartContinuityCounter);

        var result = await service.OutputAsync(plan, directory.OutputPath);
        Assert.Equal(1, result.RepairedGapCount);
        Assert.Equal(1, result.ReferenceErrorCount);
        Assert.Equal(0, result.RemainingErrorCount);
        // 输出会重写 PMT 版本号；视频 PID 的全部包（含补回的两个）应与无损的辅助源逐字节一致。
        Assert.Equal(
            ExtractPackets(await File.ReadAllBytesAsync(directory.DonorPath), VideoPid),
            ExtractPackets(await File.ReadAllBytesAsync(directory.OutputPath), VideoPid));
    }

    [Fact]
    public async Task ElementaryGapIsRebuiltWhenDonorPacketizationDiffers()
    {
        using var directory = new TemporaryDirectory("ts-elementary-gap");
        // 辅助源 PES 头多 8 字节填充，所有 TS 包的负载都与参考源错位，包级指纹无法命中，
        // 只能按 ES 字节锚点补齐。
        var donor = BuildAudioStream(TsStreamTypes.Ac3, 0xBD, headerStuffing: 8, out _, out var elementary);
        var reference = BuildAudioStream(TsStreamTypes.Ac3, 0xBD, headerStuffing: 0, out var layout, out _);
        var dropped = layout[20][3];
        reference.Drop(dropped.Index);
        await donor.WriteAsync(directory.DonorPath);
        await reference.WriteAsync(directory.ReferencePath);

        var service = new TsMultiSourceRepairService();
        var analysis = await service.AnalyzeAsync(
            [directory.ReferencePath, directory.DonorPath], directory.ReferencePath,
            normalizeTimeline: false);

        var track = Assert.Single(analysis.Tracks);
        var gap = Assert.Single(track.Gaps);
        Assert.Equal(TsRepairElementaryKind.Audio, gap.ElementaryKind);
        Assert.Equal(1, gap.MissingPacketModulo);
        var candidate = Assert.Single(gap.Candidates);
        Assert.Equal(AudioPid, candidate.SourcePid);
        Assert.Empty(candidate.SourcePacketOffsets);
        Assert.Equal(1, candidate.SynthesizedPacketCount);
        Assert.Equal(
            elementary[20].AsSpan(dropped.ElementaryStart, dropped.ElementaryLength).ToArray(),
            candidate.ElementaryPayload);

        var plan = service.BuildOutputPlan(
            analysis, new HashSet<int> { AudioPid }, includeServiceInformation: true);
        Assert.Equal(1, plan.RepairedGapCount);
        var result = await service.OutputAsync(plan, directory.OutputPath);
        Assert.Equal(0, result.RemainingErrorCount);
        var output = await File.ReadAllBytesAsync(directory.OutputPath);
        Assert.Equal(elementary.SelectMany(item => item).ToArray(),
            ExtractElementaryStream(output, AudioPid));
        var verification = await new TsStreamAnalyzer().AnalyzeAsync(directory.OutputPath);
        Assert.Equal(0, verification.Pids[AudioPid].ContinuityErrors);
    }

    [Theory]
    [InlineData(AudioFrameFormat.MpegAudio, false)]
    [InlineData(AudioFrameFormat.MpegAudio, true)]
    [InlineData(AudioFrameFormat.Adts, false)]
    [InlineData(AudioFrameFormat.Adts, true)]
    [InlineData(AudioFrameFormat.Latm, false)]
    [InlineData(AudioFrameFormat.Latm, true)]
    public async Task FramedAudioGapIsRebuiltOnlyWithinOneFrame(
        AudioFrameFormat format,
        bool crossesFrameBoundary)
    {
        using var directory = new TemporaryDirectory("ts-framed-audio-gap");
        var (streamType, frameLength) = GetFrameShape(format);
        var donor = BuildAudioStream(streamType, 0xC0, headerStuffing: 8, out _, out var elementary,
            index => CreateAudioFrames(format, index));
        var reference = BuildAudioStream(streamType, 0xC0, headerStuffing: 0, out var layout, out _,
            index => CreateAudioFrames(format, index));
        // 每个 PES 恰好由整数个帧组成，帧边界可直接由 PES 内 ES 偏移推算。
        var (pesIndex, dropped) = layout
            .SelectMany((packets, index) => packets.Select(packet => (PesIndex: index, Packet: packet)))
            .First(item =>
            {
                var packet = item.Packet;
                var end = packet.ElementaryStart + packet.ElementaryLength;
                var frameEnd = (packet.ElementaryStart / frameLength + 1) * frameLength;
                return item.PesIndex >= 10 && packet.ElementaryLength == 184 &&
                       packet.ElementaryStart >= 256 &&
                       elementary[item.PesIndex].Length - end >= 256 &&
                       end > frameEnd == crossesFrameBoundary;
            });
        reference.Drop(dropped.Index);
        await donor.WriteAsync(directory.DonorPath);
        await reference.WriteAsync(directory.ReferencePath);

        var service = new TsMultiSourceRepairService();
        var analysis = await service.AnalyzeAsync(
            [directory.ReferencePath, directory.DonorPath], directory.ReferencePath,
            normalizeTimeline: false);

        var track = Assert.Single(analysis.Tracks);
        var gap = Assert.Single(track.Gaps);
        if (crossesFrameBoundary)
        {
            // 补入内容会跨越帧边界，帧结构不能证明插入后仍对齐，因此不提供 ES 候选。
            Assert.Empty(gap.Candidates);
            Assert.Equal(0, track.RepairableGapCount);
            return;
        }

        var candidate = Assert.Single(gap.Candidates);
        Assert.Empty(candidate.SourcePacketOffsets);
        Assert.Equal(
            elementary[pesIndex].AsSpan(dropped.ElementaryStart, dropped.ElementaryLength).ToArray(),
            candidate.ElementaryPayload);
        var plan = service.BuildOutputPlan(
            analysis, new HashSet<int> { AudioPid }, includeServiceInformation: true);
        var result = await service.OutputAsync(plan, directory.OutputPath);
        Assert.Equal(0, result.RemainingErrorCount);
        Assert.Equal(elementary.SelectMany(item => item).ToArray(),
            ExtractElementaryStream(await File.ReadAllBytesAsync(directory.OutputPath), AudioPid));
    }

    [Fact]
    public async Task AmbiguousAudioTracksAreMappedByElementaryContent()
    {
        using var directory = new TemporaryDirectory("ts-ambiguous-audio");
        // 两条同类型、无语言描述的音轨在辅助源中换了 PID 且顺序对调。两条轨开头的 PES
        // 内容相同，错误映射也能命中包指纹，只有 ES 内容指纹才能把每条参考轨对应到正确的辅助轨。
        var donor = BuildTwoAudioTrackStream(
            (SecondDonorAudioPid, 2), (DonorAudioPid, 1), out _);
        var reference = BuildTwoAudioTrackStream(
            (AudioPid, 1), (SecondAudioPid, 2), out var layouts);
        reference.Drop(layouts[0][50][4].Index);
        await donor.WriteAsync(directory.DonorPath);
        await reference.WriteAsync(directory.ReferencePath);

        var service = new TsMultiSourceRepairService();
        var analysis = await service.AnalyzeAsync(
            [directory.ReferencePath, directory.DonorPath], directory.ReferencePath,
            normalizeTimeline: false);

        var first = Assert.Single(analysis.Tracks, item => item.ReferencePid == AudioPid);
        var second = Assert.Single(analysis.Tracks, item => item.ReferencePid == SecondAudioPid);
        var firstMatch = Assert.Single(first.Matches);
        Assert.Equal(DonorAudioPid, firstMatch.SourcePid);
        Assert.Equal(TsRepairMatchConfidence.PacketFingerprint, firstMatch.Confidence);
        var secondMatch = Assert.Single(second.Matches);
        Assert.Equal(SecondDonorAudioPid, secondMatch.SourcePid);
        Assert.Equal(TsRepairMatchConfidence.PacketFingerprint, secondMatch.Confidence);

        var gap = Assert.Single(first.Gaps);
        var candidate = Assert.Single(gap.Candidates);
        Assert.Equal(DonorAudioPid, candidate.SourcePid);
        Assert.Single(candidate.SourcePacketOffsets);
        Assert.Empty(second.Gaps);
    }

    [Fact]
    public async Task BroadcastTimeAnchorsFollowReferenceProgramClock()
    {
        using var directory = new TemporaryDirectory("ts-broadcast-time");
        var start = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var timeTableFrames = new[] { 0, 25, 50, 75, 100 };
        var reference = BuildVideoStream(out _, frame =>
            timeTableFrames.Contains(frame)
                ? BuildTimeDateSection(start.AddSeconds(frame / 25))
                : null, out var timeTablePackets);
        await reference.WriteAsync(directory.ReferencePath);
        File.Copy(directory.ReferencePath, directory.DonorPath);

        var analysis = await new TsMultiSourceRepairService().AnalyzeAsync(
            [directory.ReferencePath, directory.DonorPath], directory.ReferencePath,
            normalizeTimeline: false);

        Assert.Equal(timeTableFrames.Length, analysis.ReferenceBroadcastTimes.Count);
        for (var index = 0; index < timeTableFrames.Length; index++)
        {
            var anchor = analysis.ReferenceBroadcastTimes[index];
            var frame = timeTableFrames[index];
            Assert.Equal(start.AddSeconds(frame / 25), anchor.UtcTime);
            Assert.Equal(1, anchor.ProgramNumber);
            Assert.Equal(reference.OffsetOf(timeTablePackets[index]), anchor.FileOffset);
            Assert.Equal(anchor.FileOffset / PacketSize, anchor.PacketIndex);
            // 时间表先于该帧出现：首个时间表等到第一个 PES 才建立锚点，
            // 其余锚点取表出现时已收到的上一帧 PTS。
            Assert.Equal(Math.Max(0, frame - 1) * 3_600L, anchor.Clock90k);
        }
    }

    [Fact]
    public async Task OutputWithoutUsableCandidateIsRejectedAndRemoved()
    {
        using var directory = new TemporaryDirectory("ts-no-improvement");
        var reference = BuildVideoStream(out var frames);
        reference.Drop(frames[60]);
        reference.Drop(frames[61]);
        await reference.WriteAsync(directory.ReferencePath);
        // 辅助源带有同样的缺口，找不到可补入的包。
        File.Copy(directory.ReferencePath, directory.DonorPath);

        var service = new TsMultiSourceRepairService();
        var analysis = await service.AnalyzeAsync(
            [directory.ReferencePath, directory.DonorPath], directory.ReferencePath,
            normalizeTimeline: false);
        var gap = Assert.Single(Assert.Single(analysis.Tracks).Gaps);
        Assert.Empty(gap.Candidates);

        var plan = service.BuildOutputPlan(
            analysis, new HashSet<int> { VideoPid }, includeServiceInformation: true);
        Assert.Empty(plan.Insertions);
        var exception = await Assert.ThrowsAsync<TsRepairException>(() =>
            service.OutputAsync(plan, directory.OutputPath));
        Assert.Equal(TsRepairErrorCode.NoImprovement, exception.Code);
        // 参数依次为参考源错误数和输出复检错误数。
        Assert.Equal([1L, 1L], exception.Arguments.Select(Convert.ToInt64));
        Assert.False(File.Exists(directory.OutputPath));
    }

    [Fact]
    public async Task OutputToSourceFileIsRejectedBeforeWriting()
    {
        using var directory = new TemporaryDirectory("ts-output-is-source");
        var donor = BuildVideoStream(out _);
        var reference = BuildVideoStream(out var frames);
        reference.Drop(frames[60]);
        await donor.WriteAsync(directory.DonorPath);
        await reference.WriteAsync(directory.ReferencePath);
        var referenceBytes = await File.ReadAllBytesAsync(directory.ReferencePath);

        var service = new TsMultiSourceRepairService();
        var analysis = await service.AnalyzeAsync(
            [directory.ReferencePath, directory.DonorPath], directory.ReferencePath,
            normalizeTimeline: false);
        var plan = service.BuildOutputPlan(
            analysis, new HashSet<int> { VideoPid }, includeServiceInformation: true);
        Assert.Equal(1, plan.RepairedGapCount);

        foreach (var sourcePath in new[] { directory.ReferencePath, directory.DonorPath })
        {
            var exception = await Assert.ThrowsAsync<TsRepairException>(() =>
                service.OutputAsync(plan, sourcePath));
            Assert.Equal(TsRepairErrorCode.OutputIsSource, exception.Code);
        }
        Assert.Equal(referenceBytes, await File.ReadAllBytesAsync(directory.ReferencePath));
    }

    private static TsTestMux BuildVideoStream(out int[] framePackets) =>
        BuildVideoStream(out framePackets, _ => null, out _);

    private static TsTestMux BuildVideoStream(
        out int[] framePackets,
        Func<int, byte[]?> timeTableBeforeFrame,
        out List<int> timeTablePackets)
    {
        var mux = new TsTestMux();
        mux.AddSection(0x0000, BuildPatSection());
        mux.AddSection(PmtPid, BuildPmtSection(VideoPid, (TsStreamTypes.H264, VideoPid)));
        framePackets = new int[120];
        timeTablePackets = [];
        for (var frame = 0; frame < framePackets.Length; frame++)
        {
            if (timeTableBeforeFrame(frame) is { } section)
                timeTablePackets.Add(mux.AddSection(TimeTablePid, section));
            // 每帧恰好一个 TS 包：14 字节 PES 头 + 170 字节 ES。
            var packets = mux.AddPes(VideoPid, 0xE0, frame * 3_600L,
                CreateRandomBytes(170, frame), declareLength: false);
            framePackets[frame] = Assert.Single(packets).Index;
        }
        return mux;
    }

    private static TsTestMux BuildAudioStream(
        byte streamType,
        byte streamId,
        int headerStuffing,
        out PesPacket[][] layout,
        out byte[][] elementary,
        Func<int, byte[]>? createElementary = null)
    {
        createElementary ??= pesIndex => CreateRandomBytes(1_200, 1_000 + pesIndex);
        var mux = new TsTestMux();
        mux.AddSection(0x0000, BuildPatSection());
        mux.AddSection(PmtPid, BuildPmtSection(AudioPid, (streamType, AudioPid)));
        layout = new PesPacket[30][];
        elementary = new byte[layout.Length][];
        for (var pesIndex = 0; pesIndex < layout.Length; pesIndex++)
        {
            elementary[pesIndex] = createElementary(pesIndex);
            layout[pesIndex] = mux.AddPes(AudioPid, streamId, pesIndex * 2_880L,
                elementary[pesIndex], headerStuffing);
        }
        return mux;
    }

    private static TsTestMux BuildTwoAudioTrackStream(
        (int Pid, int Content) first,
        (int Pid, int Content) second,
        out PesPacket[][][] layouts)
    {
        var mux = new TsTestMux();
        mux.AddSection(0x0000, BuildPatSection());
        mux.AddSection(PmtPid, BuildPmtSection(
            first.Pid, (TsStreamTypes.Ac3, first.Pid), (TsStreamTypes.Ac3, second.Pid)));
        // 参考源 ES 指纹每 64 KiB 取一个样本，需要足够长的 ES 才能得到多个独立样本。
        const int pesCount = 110;
        layouts = [new PesPacket[pesCount][], new PesPacket[pesCount][]];
        var tracks = new[] { first, second };
        for (var pesIndex = 0; pesIndex < pesCount; pesIndex++)
        {
            for (var trackIndex = 0; trackIndex < tracks.Length; trackIndex++)
            {
                var track = tracks[trackIndex];
                // 首个 PES 两轨相同，模拟各音轨共有的静音片段。
                var seed = pesIndex == 0 ? 0 : track.Content * 10_000 + pesIndex;
                layouts[trackIndex][pesIndex] = mux.AddPes(track.Pid, 0xBD, pesIndex * 2_880L,
                    CreateRandomBytes(2_048, seed));
            }
        }
        return mux;
    }

    private static (byte StreamType, int FrameLength) GetFrameShape(AudioFrameFormat format) =>
        format switch
        {
            // MPEG-1 Layer II，384 kbit/s，48 kHz：144 × 384000 / 48000 = 1152 字节。
            AudioFrameFormat.MpegAudio => (TsStreamTypes.Mpeg1Audio, 1_152),
            AudioFrameFormat.Adts => (TsStreamTypes.Aac, 700),
            AudioFrameFormat.Latm => (TsStreamTypes.AacLatm, 603),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };

    private static byte[] CreateAudioFrames(AudioFrameFormat format, int pesIndex)
    {
        const int framesPerPes = 4;
        var frameLength = GetFrameShape(format).FrameLength;
        var result = new byte[frameLength * framesPerPes];
        for (var frameIndex = 0; frameIndex < framesPerPes; frameIndex++)
        {
            var frame = result.AsSpan(frameIndex * frameLength, frameLength);
            CreateRandomBytes(frameLength, pesIndex * framesPerPes + frameIndex).CopyTo(frame);
            switch (format)
            {
                case AudioFrameFormat.MpegAudio:
                    // 同步字 + MPEG-1 Layer II 无 CRC，码率索引 14，48 kHz，无填充。
                    frame[0] = 0xFF;
                    frame[1] = 0xFD;
                    frame[2] = 0xE4;
                    frame[3] = 0x04;
                    break;
                case AudioFrameFormat.Adts:
                    // ADTS 无 CRC，AAC LC，44.1 kHz，双声道，13 位帧长含 7 字节头。
                    frame[0] = 0xFF;
                    frame[1] = 0xF1;
                    frame[2] = 0x50;
                    frame[3] = (byte)(0x80 | ((frameLength >> 11) & 0x03));
                    frame[4] = (byte)(frameLength >> 3);
                    frame[5] = (byte)(((frameLength & 0x07) << 5) | 0x1F);
                    frame[6] = 0xFC;
                    break;
                case AudioFrameFormat.Latm:
                    // LOAS 同步字 0x2B7 + 13 位 audioMuxLength（不含 3 字节头）。
                    var muxLength = frameLength - 3;
                    frame[0] = 0x56;
                    frame[1] = (byte)(0xE0 | (muxLength >> 8));
                    frame[2] = (byte)muxLength;
                    break;
            }
        }
        return result;
    }

    private static byte[] CreateRandomBytes(int length, int seed)
    {
        var result = new byte[length];
        new Random(seed).NextBytes(result);
        return result;
    }

    private static byte[] ExtractPackets(byte[] stream, int pid)
    {
        var result = new List<byte>();
        for (var offset = 0; offset + PacketSize <= stream.Length; offset += PacketSize)
        {
            var packet = stream.AsSpan(offset, PacketSize);
            if (packet[0] == 0x47 && (((packet[1] & 0x1F) << 8) | packet[2]) == pid)
                result.AddRange(packet.ToArray());
        }
        return result.ToArray();
    }

    private static byte[] ExtractElementaryStream(byte[] stream, int pid)
    {
        var result = new List<byte>();
        for (var offset = 0; offset + PacketSize <= stream.Length; offset += PacketSize)
        {
            var packet = stream.AsSpan(offset, PacketSize);
            if (packet[0] != 0x47 || (((packet[1] & 0x1F) << 8) | packet[2]) != pid)
                continue;
            var adaptationControl = (packet[3] >> 4) & 0x03;
            if ((adaptationControl & 0x01) == 0)
                continue;
            var payloadOffset = 4 + ((adaptationControl & 0x02) != 0 ? 1 + packet[4] : 0);
            var payload = packet[payloadOffset..];
            if ((packet[1] & 0x40) != 0)
                payload = payload[(9 + payload[8])..];
            result.AddRange(payload.ToArray());
        }
        return result.ToArray();
    }

    private static byte[] BuildTimeDateSection(DateTimeOffset utcTime)
    {
        var mjd = (int)(utcTime.UtcDateTime.Date - new DateTime(1858, 11, 17)).TotalDays;
        return
        [
            0x70, 0x70, 0x05, (byte)(mjd >> 8), (byte)mjd,
            ToBcd(utcTime.Hour), ToBcd(utcTime.Minute), ToBcd(utcTime.Second)
        ];

        static byte ToBcd(int value) => (byte)((value / 10 << 4) | (value % 10));
    }

    private static byte[] BuildPatSection() => AppendCrc([
        0x00, 0xB0, 0x0D, 0x00, 0x01, 0xC1, 0x00, 0x00,
        0x00, 0x01, (byte)(0xE0 | (PmtPid >> 8)), (byte)(PmtPid & 0xFF)
    ]);

    private static byte[] BuildPmtSection(int pcrPid, params (byte StreamType, int Pid)[] streams)
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
            section.Add(stream.StreamType);
            section.Add((byte)(0xE0 | (stream.Pid >> 8)));
            section.Add((byte)stream.Pid);
            section.Add(0xF0);
            section.Add(0x00);
        }
        return AppendCrc(section.ToArray());
    }

    private static byte[] AppendCrc(byte[] section)
    {
        var crc = uint.MaxValue;
        foreach (var value in section)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }
        return [.. section, (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc];
    }

    private static void WritePts(Span<byte> value, long pts)
    {
        value[0] = (byte)(0x21 | (((pts >> 30) & 0x07) << 1));
        value[1] = (byte)(pts >> 22);
        value[2] = (byte)(0x01 | (((pts >> 15) & 0x7F) << 1));
        value[3] = (byte)(pts >> 7);
        value[4] = (byte)(0x01 | ((pts & 0x7F) << 1));
    }

    /// <summary>PES 拆包后某个 TS 包在整体包序中的位置，以及它承载的 ES 字节区间。</summary>
    private readonly record struct PesPacket(int Index, int ElementaryStart, int ElementaryLength);

    /// <summary>
    /// 按 PID 自动递增 CC 的简易复用器。<see cref="Drop"/> 只是在写出时跳过该包，
    /// 后续包保留原 CC，从而在参考源中留下真实的连续性缺口。
    /// </summary>
    private sealed class TsTestMux
    {
        private readonly List<byte[]> _packets = [];
        private readonly HashSet<int> _dropped = [];
        private readonly Dictionary<int, int> _continuity = [];

        public int AddSection(int pid, byte[] section)
        {
            var packet = CreatePacket(pid, payloadStart: true);
            packet[4] = 0;
            section.CopyTo(packet.AsSpan(5));
            _packets.Add(packet);
            return _packets.Count - 1;
        }

        public PesPacket[] AddPes(
            int pid,
            byte streamId,
            long pts90k,
            ReadOnlySpan<byte> elementary,
            int headerStuffing = 0,
            bool declareLength = true)
        {
            var headerLength = 14 + headerStuffing;
            var pes = new byte[headerLength + elementary.Length];
            pes[2] = 1;
            pes[3] = streamId;
            if (declareLength)
                BinaryPrimitives.WriteUInt16BigEndian(pes.AsSpan(4), (ushort)(pes.Length - 6));
            pes[6] = 0x80;
            pes[7] = 0x80;
            pes[8] = (byte)(5 + headerStuffing);
            WritePts(pes.AsSpan(9, 5), pts90k);
            pes.AsSpan(14, headerStuffing).Fill(0xFF);
            elementary.CopyTo(pes.AsSpan(headerLength));

            var result = new List<PesPacket>();
            for (var start = 0; start < pes.Length; start += 184)
            {
                var length = Math.Min(184, pes.Length - start);
                var packet = CreatePacket(pid, payloadStart: start == 0);
                // 末包不足 184 字节时用适配域填充，保证负载紧贴包尾。
                var adaptationLength = 184 - length;
                if (adaptationLength > 0)
                {
                    packet[3] |= 0x20;
                    packet[4] = (byte)(adaptationLength - 1);
                    if (adaptationLength > 1)
                    {
                        packet[5] = 0;
                        packet.AsSpan(6, adaptationLength - 2).Fill(0xFF);
                    }
                }
                pes.AsSpan(start, length).CopyTo(packet.AsSpan(4 + adaptationLength));
                var elementaryStart = Math.Max(start, headerLength) - headerLength;
                var elementaryEnd = Math.Max(start + length, headerLength) - headerLength;
                result.Add(new PesPacket(
                    _packets.Count, elementaryStart, elementaryEnd - elementaryStart));
                _packets.Add(packet);
            }
            return result.ToArray();
        }

        public void Drop(int index) => _dropped.Add(index);

        public long OffsetOf(int index) =>
            (long)(index - _dropped.Count(item => item < index)) * PacketSize;

        public Task WriteAsync(string path) => File.WriteAllBytesAsync(path, _packets
            .Where((_, index) => !_dropped.Contains(index))
            .SelectMany(packet => packet)
            .ToArray());

        private byte[] CreatePacket(int pid, bool payloadStart)
        {
            var continuityCounter = _continuity.GetValueOrDefault(pid);
            _continuity[pid] = (continuityCounter + 1) & 0x0F;
            var packet = new byte[PacketSize];
            Array.Fill(packet, (byte)0xFF);
            packet[0] = 0x47;
            packet[1] = (byte)((payloadStart ? 0x40 : 0) | ((pid >> 8) & 0x1F));
            packet[2] = (byte)pid;
            packet[3] = (byte)(0x10 | continuityCounter);
            return packet;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path;

        public TemporaryDirectory(string prefix)
        {
            _path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_path);
        }

        public string DonorPath => Path.Combine(_path, "donor.ts");
        public string ReferencePath => Path.Combine(_path, "reference.ts");
        public string OutputPath => Path.Combine(_path, "output.ts");

        public void Dispose()
        {
            try
            {
                Directory.Delete(_path, recursive: true);
            }
            catch
            {
                // 测试清理失败不覆盖核心断言。
            }
        }
    }
}
