using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Services.Editing.AudioRebuild;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Utils.TransportStream.Packets;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

/// <summary>TS 输出处理（起始时间归零、去除空包）：用生成的传输包核对改写结果。</summary>
public sealed class TsOutputPostProcessorTests
{
    private const int PacketSize = 188;
    private const long Wrap = 1L << 33;
    private const int Program1Pmt = 0x1000, Program1Pcr = 0x1FF, Program1Video = 0x100, Program1Audio = 0x101;
    private const int Program2Pmt = 0x1010, Program2Video = 0x200, Program2Audio = 0x201;
    private const int UnlistedPid = 0x300;
    // 节目 1 的时钟在开始后 1 秒回绕；节目 2 使用另一个时钟。
    private const long Clock1 = Wrap - 90_000;
    private const long Clock2 = 5_000_000;
    private const int PcrExtension = 123;
    private const int Steps = 100;
    private const long Step = 3_600;

    /// <summary>
    /// 两个节目各自从 0 开始：节目 1 的 PCR 在独立 PID 上，跨越 33 位回绕；节目 2 的 PCR 与视频同 PID。
    /// PCR 扩展位保持不变；未列入节目表的 PID 按全部时间戳的最小值平移；空包全部删去。
    /// 生成的时长超过暂存长度，暂存期间与之后改写的结果一致。
    /// </summary>
    [Fact]
    public void ZeroStartShiftsEachClockAndRemovesNullPackets()
    {
        var source = BuildMultiProgramStream();
        var output = Process(source, TsOutputProcessing.ZeroStartTime | TsOutputProcessing.RemoveNullPackets, out var processor);

        Assert.True(processor.OffsetsDecided);
        Assert.Equal(Steps, processor.NullPacketsRemoved);
        Assert.Equal(Clock1, processor.GroupOffsets[Program1Pcr]);
        Assert.Equal(Clock2, processor.GroupOffsets[Program2Video]);
        Assert.Equal(Clock1, processor.GlobalOffset);
        Assert.Equal(source.Length - Steps * PacketSize, output.Length);

        var packets = Packets(output).ToList();
        Assert.DoesNotContain(packets, packet => Pid(packet) == 0x1FFF);
        Assert.Equal(Enumerable.Range(0, Steps).Select(k => (k * Step, PcrExtension)),
            packets.Where(packet => Pid(packet) == Program1Pcr).Select(ReadPcr));
        Assert.Equal(Enumerable.Range(0, Steps).Select(k => (k * Step + 45_000 + Step, (long?)(k * Step + 45_000))),
            packets.Where(packet => Pid(packet) == Program1Video).Select(ReadPts));
        Assert.Equal(Enumerable.Range(0, Steps).Select(k => (k * Step + 30_000, (long?)null)),
            packets.Where(packet => Pid(packet) == Program1Audio).Select(ReadPts));
        Assert.Equal(Enumerable.Range(0, Steps).Select(k => (k * Step, PcrExtension)),
            packets.Where(packet => Pid(packet) == Program2Video).Select(ReadPcr));
        Assert.Equal(Enumerable.Range(0, Steps).Select(k => (k * Step + 20_000, (long?)null)),
            packets.Where(packet => Pid(packet) == Program2Video).Select(ReadPts));
        // 独立音频 PID 的 OPCR 跟随所属节目的平移量，且不参与当前时钟的归零。
        Assert.Equal(Enumerable.Range(0, Steps).Select(k => ((k * Step - 9_000 + Wrap) % Wrap, PcrExtension)),
            packets.Where(packet => Pid(packet) == Program2Audio).Select(ReadPcr));
        Assert.Equal(Enumerable.Range(0, Steps).Select(k => (k * Step + 1_000, (long?)null)),
            packets.Where(packet => Pid(packet) == UnlistedPid).Select(ReadPts));
        // 节目表原样保留。
        Assert.Equal(Packets(source).Where(packet => Pid(packet) is 0 or Program1Pmt or Program2Pmt).Select(Convert.ToHexString),
            packets.Where(packet => Pid(packet) is 0 or Program1Pmt or Program2Pmt).Select(Convert.ToHexString));
    }

    /// <summary>只去除空包：其余包逐字节不变；M2TS 连同附加头整包删去，其他包的附加头不变。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingNullPacketsKeepsOtherPacketsUnchanged(bool m2ts)
    {
        var bodies = Packets(BuildMultiProgramStream()).ToList();
        var source = m2ts ? ToM2ts(bodies) : bodies.SelectMany(packet => packet).ToArray();
        var output = Process(source, TsOutputProcessing.RemoveNullPackets, out var processor, m2ts ? 192 : PacketSize);

        Assert.Equal(Steps, processor.NullPacketsRemoved);
        var kept = m2ts
            ? ToM2ts(bodies).Chunk(192).Where(packet => Pid(packet.AsSpan(4)) != 0x1FFF).SelectMany(packet => packet)
            : bodies.Where(packet => Pid(packet) != 0x1FFF).SelectMany(packet => packet);
        Assert.Equal(kept.ToArray(), output);
    }

    /// <summary>M2TS 起始时间归零：时间戳按包体改写，附加头中的到达时间戳不变。</summary>
    [Fact]
    public void ZeroStartKeepsM2tsArrivalTimestamps()
    {
        var bodies = Packets(BuildMultiProgramStream()).ToList();
        var source = ToM2ts(bodies);
        var output = Process(source, TsOutputProcessing.ZeroStartTime, out _, 192);

        Assert.Equal(source.Length, output.Length);
        var headers = (byte[] data) => data.Chunk(192).Select(packet => Convert.ToHexString(packet, 0, 4));
        Assert.Equal(headers(source), headers(output));
        Assert.Equal((0L, PcrExtension), output.Chunk(192).Select(packet => packet[4..])
            .Where(packet => Pid(packet) == Program1Pcr).Select(ReadPcr).First());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingPureStuffingKeepsPacketsWithInformation(bool m2ts)
    {
        var stuffing = PureStuffingPacket(Program1Video);
        var nonStandardPadding = PureStuffingPacket(Program1Video);
        nonStandardPadding[^1] = 0xAA;
        var discontinuity = PureStuffingPacket(Program1Video);
        discontinuity[5] = 0x80;
        var privateData = PureStuffingPacket(Program1Audio);
        privateData[5] = 0x02;
        privateData[6] = 1;
        privateData[7] = 0x09;
        var transportError = PureStuffingPacket(Program1Audio);
        transportError[1] |= 0x80;
        var kept = new[]
        {
            PcrPacket(Program1Video, Clock1),
            PcrPacket(Program1Audio, Clock1, original: true),
            Pes(Program1Video, Clock1 + Step, null, 1),
            NullPacket(), discontinuity, privateData, nonStandardPadding, transportError
        };
        var bodies = kept.Prepend(stuffing).Append(PureStuffingPacket(Program1Audio)).ToArray();
        var source = m2ts ? ToM2ts(bodies) : bodies.SelectMany(packet => packet).ToArray();
        var output = Process(source, TsOutputProcessing.RemoveStuffingPackets, out var processor, m2ts ? 192 : PacketSize);

        Assert.Equal(m2ts ? source : kept.SelectMany(packet => packet).ToArray(), output);
        Assert.Equal(m2ts ? 0 : 2, processor.StuffingPacketsRemoved);
        Assert.Equal(0, processor.NullPacketsRemoved);
    }

    internal static byte[] PureStuffingPacket(int pid)
    {
        var packet = Header(pid, payloadStart: false, continuity: 0, control: 0x20);
        packet[4] = 183;
        packet.AsSpan(6).Fill(0xFF);
        return packet;
    }

    /// <summary>任意切块写入与一次写入的结果相同，不足一包的尾部原样写出。</summary>
    [Fact]
    public void ChunkedWritesMatchSingleWrite()
    {
        var source = BuildMultiProgramStream().Concat(PureStuffingPacket(Program1Video))
            .Concat(new byte[] { 0x47, 1, 2 }).ToArray();
        var options = TsOutputProcessing.ZeroStartTime | TsOutputProcessing.RemoveNullPackets |
                      TsOutputProcessing.RemoveStuffingPackets;
        var expected = Process(source, options, out _);
        var random = new Random(7);
        var chunks = new List<byte>();
        var processor = new TsOutputPostProcessor(options, PacketSize, (buffer, offset, count) =>
            chunks.AddRange(buffer.AsSpan(offset, count).ToArray()));
        for (var offset = 0; offset < source.Length;)
        {
            var count = Math.Min(source.Length - offset, random.Next(1, 1000));
            processor.Write(source.AsSpan(offset, count));
            offset += count;
        }
        processor.Complete();
        Assert.Equal(expected, chunks.ToArray());
        Assert.Equal(Steps, processor.NullPacketsRemoved);
        Assert.Equal(1, processor.StuffingPacketsRemoved);
        Assert.Equal(new byte[] { 0x47, 1, 2 }, expected[^3..]);
    }

    /// <summary>
    /// PES 头跨包：后续分片到齐后改写，重复的分片包一并改写；带传输错误标志的包和加扰包的 PES 头保持原样。
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(16)]
    public void SplitHeadersAreRewrittenAndDamagedPacketsAreKept(int firstPayloadBytes)
    {
        const int pid = Program1Video;
        var packets = new List<byte[]> { Pat(), Pmt1(), Pmt2() };
        packets.Add(PcrPacket(Program1Pcr, Clock1));
        var split = SplitPes(pid, Clock1 + 9_000, Clock1 + 6_000, firstPayloadBytes, continuity: 0);
        packets.Add(split[0]);
        packets.Add(NullPacket());
        packets.Add(split[1]);
        packets.Add(split[1].ToArray());
        var damaged = Pes(Program1Audio, Clock1 + 12_000, null, continuity: 0);
        damaged[1] |= 0x80;
        packets.Add(damaged);
        var scrambled = Pes(Program1Audio, Clock1 + 15_000, null, continuity: 1);
        scrambled[3] |= 0x80;
        packets.Add(scrambled);
        packets.Add(Pes(Program1Audio, Clock1 + 18_000, null, continuity: 2));
        packets.Add(PcrPacket(Program1Pcr, Clock1 + TsOutputPostProcessor.LookaheadTicks));
        packets.Add(split[1].ToArray());
        var source = packets.SelectMany(packet => packet).ToArray();

        var output = Packets(Process(source, TsOutputProcessing.ZeroStartTime, out _)).ToList();

        var header = output[4].AsSpan(PayloadOffset(output[4])).ToArray().Concat(output[6][PayloadOffset(output[6])..]).ToArray();
        Assert.Equal(9_000, TsTimestampFieldCodec.ReadPesTimestamp(header.AsSpan(9, 5)));
        Assert.Equal(6_000, TsTimestampFieldCodec.ReadPesTimestamp(header.AsSpan(14, 5)));
        Assert.Equal(output[6], output[7]);
        Assert.Equal(damaged, output[8]);
        Assert.Equal(scrambled, output[9]);
        Assert.Equal((18_000L, (long?)null), ReadPts(output[10]));
        Assert.Equal(output[6], output[^1]);
    }

    /// <summary>跨包 PES 头的后续分片一直没有出现时放弃改写该头，不会一直暂存后面的输出。</summary>
    [Fact]
    public void UnfinishedSplitHeaderDoesNotHoldTheOutput()
    {
        var first = SplitPes(Program1Video, Clock1 + 9_000, Clock1 + 6_000, 10, continuity: 0)[0];
        var packets = new List<byte[]> { Pat(), Pmt1(), Pmt2(), first };
        for (var k = 0; k < 8_000; k++)
            packets.Add(PcrPacket(Program1Pcr, Clock1 + k * Step, k));
        var source = packets.SelectMany(packet => packet).ToArray();
        var output = new List<byte>();
        var processor = new TsOutputPostProcessor(TsOutputProcessing.ZeroStartTime, PacketSize, (buffer, offset, count) =>
            output.AddRange(buffer.AsSpan(offset, count).ToArray()));
        processor.Write(source);
        Assert.NotEmpty(output);
        processor.Complete();
        Assert.Equal(source.Length, output.Count);
        Assert.Equal(first, output.Skip(3 * PacketSize).Take(PacketSize));
    }

    /// <summary>经输出文件工厂写出时，检测会话收到的是处理后的字节，输出流记下处理结果。</summary>
    [Fact]
    public async Task OutputStreamFeedsTheCheckWithProcessedBytes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tscutter-output-processing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source.ts");
            var output = Path.Combine(directory, "output.ts");
            var data = BuildMultiProgramStream();
            await File.WriteAllBytesAsync(source, data);
            var session = new ExportTsCheckSession(output);
            var setup = new AudioRebuildSetup(null, source, output, session, null,
                TsOutputProcessing.ZeroStartTime | TsOutputProcessing.RemoveNullPackets);
            TsOutputFile.Current = setup;
            try
            {
                await using var stream = TsOutputFile.Create(output, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                    FileOptions.None);
                for (var offset = 0; offset < data.Length; offset += 5_000)
                    await stream.WriteAsync(data.AsMemory(offset, Math.Min(5_000, data.Length - offset)));
            }
            finally
            {
                TsOutputFile.Current = null;
            }
            session.Complete();

            var written = await File.ReadAllBytesAsync(output);
            Assert.Null(setup.Error);
            Assert.Equal(1, setup.ProcessedStreamCount);
            Assert.Equal(Steps, setup.NullPacketsRemoved);
            Assert.Equal(data.Length - Steps * PacketSize, written.Length);
            Assert.Equal(written.Length / PacketSize, session.Result!.PacketCount);
            Assert.Equal((0L, PcrExtension), Packets(written).Where(packet => Pid(packet) == Program1Pcr).Select(ReadPcr).First());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ---- 生成素材 ----

    private static byte[] BuildMultiProgramStream()
    {
        var packets = new List<byte[]> { Pat(), Pmt1(), Pmt2() };
        for (var k = 0; k < Steps; k++)
        {
            var t1 = Clock1 + k * Step;
            var t2 = Clock2 + k * Step;
            var cc = k & 15;
            packets.Add(PcrPacket(Program1Pcr, t1, cc));
            packets.Add(Pes(Program1Video, t1 + 45_000 + Step, t1 + 45_000, cc));
            packets.Add(Pes(Program1Audio, t1 + 30_000, null, cc));
            packets.Add(Pes(Program2Video, t2 + 20_000, null, cc, pcr: t2));
            packets.Add(PcrPacket(Program2Audio, t2 - 9_000, cc, original: true));
            packets.Add(Pes(UnlistedPid, t1 + 1_000, null, cc));
            packets.Add(NullPacket());
            if (k % 10 == 9)
                packets.AddRange([Pat(), Pmt1(), Pmt2()]);
        }
        return packets.SelectMany(packet => packet).ToArray();
    }

    private static byte[] Process(byte[] source, TsOutputProcessing options, out TsOutputPostProcessor processor,
        int packetSize = PacketSize)
    {
        var output = new List<byte>();
        processor = new TsOutputPostProcessor(options, packetSize, (buffer, offset, count) =>
            output.AddRange(buffer.AsSpan(offset, count).ToArray()));
        processor.Write(source);
        processor.Complete();
        return output.ToArray();
    }

    private static byte[] Pat() => Section(0, [0x00, 0xB0, 0, 0, 1, 0xC1, 0, 0,
        0, 1, (byte)(0xE0 | Program1Pmt >> 8), Program1Pmt & 0xFF,
        0, 2, (byte)(0xE0 | Program2Pmt >> 8), Program2Pmt & 0xFF]);

    private static byte[] Pmt1() => Section(Program1Pmt, [0x02, 0xB0, 0, 0, 1, 0xC1, 0, 0,
        (byte)(0xE0 | Program1Pcr >> 8), Program1Pcr & 0xFF, 0xF0, 0,
        0x1B, (byte)(0xE0 | Program1Video >> 8), Program1Video & 0xFF, 0xF0, 0,
        0x0F, (byte)(0xE0 | Program1Audio >> 8), Program1Audio & 0xFF, 0xF0, 0]);

    private static byte[] Pmt2() => Section(Program2Pmt, [0x02, 0xB0, 0, 0, 2, 0xC1, 0, 0,
        (byte)(0xE0 | Program2Video >> 8), Program2Video & 0xFF, 0xF0, 0,
        0x1B, (byte)(0xE0 | Program2Video >> 8), Program2Video & 0xFF, 0xF0, 0,
        0x0F, (byte)(0xE0 | Program2Audio >> 8), Program2Audio & 0xFF, 0xF0, 0]);

    private static byte[] Section(int pid, byte[] header)
    {
        var section = new byte[header.Length + 4];
        header.CopyTo(section, 0);
        var length = section.Length - 3;
        section[1] = (byte)(0xB0 | length >> 8);
        section[2] = (byte)length;
        TsPsiSectionBuilder.WriteCrc(section);
        var packet = Header(pid, payloadStart: true, continuity: 0);
        packet[4] = 0;
        section.CopyTo(packet, 5);
        packet.AsSpan(5 + section.Length).Fill(0xFF);
        return packet;
    }

    private static byte[] Header(int pid, bool payloadStart, int continuity, int control = 0x10)
    {
        var packet = new byte[PacketSize];
        packet[0] = 0x47;
        packet[1] = (byte)((payloadStart ? 0x40 : 0) | pid >> 8);
        packet[2] = (byte)pid;
        packet[3] = (byte)(control | continuity & 15);
        return packet;
    }

    private static byte[] PcrPacket(int pid, long pcr, int continuity = 0, bool original = false)
    {
        var packet = Header(pid, payloadStart: false, continuity, control: 0x20);
        packet[4] = 183;
        packet[5] = original ? (byte)0x08 : (byte)0x10;
        WritePcr(packet.AsSpan(6, 6), pcr);
        packet.AsSpan(12).Fill(0xFF);
        return packet;
    }

    /// <summary>一个完整的 PES 包：可选 PCR 放在适配字段里，PES 头之后以填充数据补满。</summary>
    private static byte[] Pes(int pid, long pts, long? dts, int continuity, long? pcr = null)
    {
        var pes = PesHeader(pts, dts);
        var packet = Header(pid, payloadStart: true, continuity, control: 0x30);
        var adaptation = pcr is null ? 1 : 7;
        packet[4] = (byte)adaptation;
        packet[5] = (byte)(pcr is null ? 0 : 0x10);
        if (pcr is { } value)
            WritePcr(packet.AsSpan(6, 6), value);
        pes.CopyTo(packet, 5 + adaptation);
        packet.AsSpan(5 + adaptation + pes.Length).Fill(0xAA);
        return packet;
    }

    /// <summary>PES 头被拆成两个包：第一个包只放 firstPayloadBytes 字节（其余为适配字段填充）。</summary>
    private static byte[][] SplitPes(int pid, long pts, long dts, int firstPayloadBytes, int continuity)
    {
        var pes = PesHeader(pts, dts).Concat(Enumerable.Repeat((byte)0xAA, 40)).ToArray();
        var first = Header(pid, payloadStart: true, continuity, control: 0x30);
        first[4] = (byte)(183 - firstPayloadBytes);
        first[5] = 0;
        first.AsSpan(6, first[4] - 1).Fill(0xFF);
        pes.AsSpan(0, firstPayloadBytes).CopyTo(first.AsSpan(PacketSize - firstPayloadBytes));
        var rest = pes[firstPayloadBytes..];
        var second = Header(pid, payloadStart: false, continuity + 1, control: 0x30);
        second[4] = (byte)(183 - rest.Length);
        second[5] = 0;
        second.AsSpan(6, second[4] - 1).Fill(0xFF);
        rest.CopyTo(second.AsSpan(PacketSize - rest.Length));
        return [first, second];
    }

    private static byte[] PesHeader(long pts, long? dts)
    {
        var header = new byte[dts is null ? 14 : 19];
        header[2] = 1;
        header[3] = 0xE0;
        header[6] = 0x80;
        header[7] = (byte)(dts is null ? 0x80 : 0xC0);
        header[8] = (byte)(header.Length - 9);
        WriteTimestamp(header.AsSpan(9, 5), pts, dts is null ? 2 : 3);
        if (dts is { } value)
            WriteTimestamp(header.AsSpan(14, 5), value, 1);
        return header;
    }

    private static byte[] NullPacket()
    {
        var packet = Header(0x1FFF, payloadStart: false, continuity: 0);
        packet.AsSpan(4).Fill(0xFF);
        return packet;
    }

    private static byte[] ToM2ts(IEnumerable<byte[]> bodies) => bodies.SelectMany((body, index) =>
        new byte[] { 0x40, 0, (byte)(index >> 8), (byte)index }.Concat(body)).ToArray();

    private static void WritePcr(Span<byte> bytes, long pcrBase)
    {
        var value = pcrBase & (Wrap - 1);
        bytes[0] = (byte)(value >> 25);
        bytes[1] = (byte)(value >> 17);
        bytes[2] = (byte)(value >> 9);
        bytes[3] = (byte)(value >> 1);
        bytes[4] = (byte)((value & 1) << 7 | 0x7E | PcrExtension >> 8);
        bytes[5] = PcrExtension & 0xFF;
    }

    private static void WriteTimestamp(Span<byte> bytes, long value, int prefix)
    {
        value &= Wrap - 1;
        bytes[0] = (byte)(prefix << 4 | (int)(value >> 29 & 0x0E) | 1);
        bytes[1] = (byte)(value >> 22);
        bytes[2] = (byte)(value >> 14 & 0xFE | 1);
        bytes[3] = (byte)(value >> 7);
        bytes[4] = (byte)(value << 1 & 0xFE | 1);
    }

    // ---- 读取输出 ----

    private static IEnumerable<byte[]> Packets(byte[] data) => data.Chunk(PacketSize);

    private static int Pid(byte[] packet) => Pid(packet.AsSpan());

    private static int Pid(ReadOnlySpan<byte> packet) => (packet[1] & 0x1F) << 8 | packet[2];

    private static int PayloadOffset(byte[] packet) => 4 + ((packet[3] & 0x20) != 0 ? packet[4] + 1 : 0);

    private static (long Base, int Extension) ReadPcr(byte[] packet) =>
        (TsTimestampFieldCodec.ReadPcrBase(packet.AsSpan(6, 5)), (packet[10] & 1) << 8 | packet[11]);

    private static (long Pts, long? Dts) ReadPts(byte[] packet)
    {
        var header = packet.AsSpan(PayloadOffset(packet));
        var pts = TsTimestampFieldCodec.ReadPesTimestamp(header.Slice(9, 5));
        return (pts, header[7] >> 6 == 3 ? TsTimestampFieldCodec.ReadPesTimestamp(header.Slice(14, 5)) : null);
    }
}
