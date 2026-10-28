using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.TransportStream.Analysis;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.TransportStream.Packets;

namespace TSCutter.GUI.Tests.TransportStream.Analysis;

/// <summary>
/// 导出期增量检测与文件扫描的等价性、分块一致性及取消/失败边界。
/// 允许在测试中扫描文件作对照；生产流程不读回输出文件的约束由实现保证。
/// </summary>
public sealed class ExportTsCheckSessionTests
{
    private const int PacketSize = TsStreamAnalyzer.PacketSize;
    private const int PmtPid = 0x0100;
    private const int VideoPid = 0x0101;
    private const int AudioPid = 0x0102;

    [Fact]
    public async Task FileScanAndIncrementalSessionProduceEquivalentResults()
    {
        var data = BuildStream();
        var fileResult = await AnalyzeFileAsync(data);
        var sessionResult = RunSession(data, out _);

        AssertResultsEquivalent(fileResult, sessionResult);
        Assert.Equal(TsCheckVerdict.Error, sessionResult.Verdict);
        Assert.Equal(1, Count(sessionResult, TsCheckEventType.TransportError));
        // 合法的显式 discontinuity 不产生 PCR/PTS 倒退或跳变错误。
        Assert.Equal(0, Count(sessionResult, TsCheckEventType.PcrJump));
        Assert.Equal(0, Count(sessionResult, TsCheckEventType.PcrBackward));
        Assert.Equal(1, Count(sessionResult, TsCheckEventType.TrailingBytes));
        Assert.Equal(data.Length, sessionResult.FileSize);
        Assert.Equal(data.Length, sessionResult.BytesScanned);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(188)]
    [InlineData(189)]
    [InlineData(1 << 20)]
    public void IncrementalSessionIsIndependentOfChunkSize(int chunkSize)
    {
        var data = BuildStream();
        if (chunkSize >= 1 << 20)
            data = Enumerable.Repeat(data, 180).SelectMany(block => block).ToArray();
        var wholeResult = RunSession(data, out _);
        var chunkedResult = RunSession(data, out _, chunkSize);

        AssertResultsEquivalent(wholeResult, chunkedResult);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(191)]
    [InlineData(65536)]
    public async Task M2tsArrivalPrefixIsHandledByBothPaths(int chunkSize)
    {
        var packets = BuildPacketList();
        // 每包前加 4 字节到达头，构成 192 字节 M2TS；到达时间戳递增。
        var data = new byte[packets.Sum(packet => PacketSize + 4)];
        var offset = 0;
        uint arrival = 100;
        foreach (var packet in packets)
        {
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset, 4), arrival);
            arrival += 3600;
            packet.CopyTo(data, offset + 4);
            offset += PacketSize + 4;
        }

        var fileResult = await AnalyzeFileAsync(data);
        var sessionResult = RunSession(data, out _, chunkSize);

        AssertResultsEquivalent(fileResult, sessionResult);
        Assert.Equal(TsPacketLayout.M2tsPacketSize, sessionResult.PacketSize);
        // M2TS 的 SyncOffset 以附加头起点为基准，与文件扫描一致。
        Assert.Equal(0, sessionResult.SyncOffset);
    }

    [Fact]
    public void AbortedSessionIsMarkedIncompleteAndNeverPasses()
    {
        var data = BuildStream();
        using var analyzer = new TsStreamAnalyzer();
        analyzer.BeginSession("out.ts");
        analyzer.AppendOutput(data.AsSpan(0, data.Length / 2));
        Assert.Throws<OperationCanceledException>(() => analyzer.AppendOutput(data, new CancellationToken(true)));
        var result = analyzer.AbortSession();

        Assert.True(result.WasCancelled);
        Assert.Equal(TsCheckVerdict.Incomplete, result.Verdict);
        Assert.Equal(data.Length / 2, result.FileSize);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyPathFeedsDetectorWithoutChangingOutputBytes(bool programOnly)
    {
        var source = Path.Combine(Path.GetTempPath(), $"tscutter-check-src-{Guid.NewGuid():N}.ts");
        var destination = Path.Combine(Path.GetTempPath(), $"tscutter-check-dst-{Guid.NewGuid():N}.ts");
        try
        {
            var data = BuildStream();
            await File.WriteAllBytesAsync(source, data);

            var session = new ExportTsCheckSession(destination);
            session.Begin();
            if (programOnly)
                await new TsProgramClipExportService().ExportAsync(source, destination, 0,
                    data.Length / PacketSize * PacketSize, 1, null, default, session);
            else
                await CommonUtil.CopyFileAsync(new FileInfo(source), destination,
                    cancellationToken: default, checkSession: session);
            session.Complete();

            Assert.Equal(TsExportCheckState.Completed, session.State);
            // 检测开关不改变导出内容：正常 TS 复制可直接比较字节。
            if (!programOnly)
                Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(destination));
            // 增量结论与对同一输出文件的扫描对照一致（测试中允许扫文件）。
            var reference = await new TsStreamAnalyzer().AnalyzeAsync(destination);
            AssertResultsEquivalent(reference, session.Result!);
        }
        finally
        {
            File.Delete(source);
            File.Delete(destination);
        }
    }

    [Fact]
    public void SyncRecoveryAndDamagedTailKeepOffsetsAcrossChunks()
    {
        var packets = BuildStream()[..^6];
        byte[] data = [.. packets, .. new byte[2000],
            .. Enumerable.Repeat(packets, 5).SelectMany(block => block), .. new byte[2000]];
        var whole = RunSession(data, out _, data.Length);
        var chunked = RunSession(data, out _, 189);
        AssertResultsEquivalent(whole, chunked);
        var lostAtEnd = Assert.Single(whole.Events, item => item.MessageCode == TsCheckMessageCode.SyncLostAtEnd);
        Assert.Equal(data.Length - 2000, lostAtEnd.FileOffset);
        Assert.Equal(2000L, Convert.ToInt64(lostAtEnd.MessageArguments[0]));
    }

    [Fact]
    public async Task AnalyzerCanBeReusedAfterOpenFailureAndRecognizesAShortInventory()
    {
        using var analyzer = new TsStreamAnalyzer();
        await Assert.ThrowsAsync<FileNotFoundException>(() => analyzer.AnalyzeAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".ts")));
        var path = Path.Combine(Path.GetTempPath(), $"tscutter-short-{Guid.NewGuid():N}.ts");
        try
        {
            await File.WriteAllBytesAsync(path, BuildStream()[..(PacketSize * 2)]);
            var result = await analyzer.AnalyzeAsync(path, options: new TsStreamAnalyzeOptions { InventoryOnly = true });
            Assert.Equal(2, result.PacketCount);
            Assert.Equal(2 * PacketSize, result.BytesScanned);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InventoryStopsEarlyAndTheAnalyzerCanBeReused()
    {
        using var analyzer = new TsStreamAnalyzer();
        var data = Enumerable.Repeat(BuildStream(), 180).SelectMany(block => block).ToArray();
        analyzer.BeginSession("inventory.ts", new TsStreamAnalyzeOptions
        {
            InventoryOnly = true, MinimumBytes = PacketSize * 10, StablePacketCount = 5
        });
        analyzer.AppendOutput(data);
        var result = analyzer.CompleteSession();
        Assert.InRange(result.PacketCount, 10, 100);
        analyzer.BeginSession("second.ts");
        analyzer.AppendOutput(data);
        AssertResultsEquivalent(RunSession(data, out _), analyzer.CompleteSession());
    }

    [Fact]
    public async Task ManagedWriteFailureDoesNotFeedUnwrittenBytesOrProduceACompleteCheck()
    {
        var session = new ExportTsCheckSession("failed.ts");
        session.Begin();
        await using (var output = new TsDetectingWriteStream(new FailingOutputStream(), session))
            await Assert.ThrowsAsync<IOException>(async () => await output.WriteAsync(BuildStream().AsMemory()));
        session.Abort();
        Assert.Equal(TsExportCheckState.Aborted, session.State);
        Assert.Equal(0, session.Result!.FileSize);
        Assert.Equal(TsCheckVerdict.Incomplete, session.Result.Verdict);
    }

    private sealed class FailingOutputStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Simulated write failure"));
    }

    private static TsCheckResult RunSession(byte[] data, out ExportTsCheckSession session, int chunkSize = 1 << 16)
    {
        session = new ExportTsCheckSession("out.ts");
        session.Begin();
        for (var offset = 0; offset < data.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, data.Length - offset);
            session.Append(data.AsSpan(offset, length));
        }
        session.Complete();
        Assert.Equal(TsExportCheckState.Completed, session.State);
        Assert.NotNull(session.Result);
        return session.Result!;
    }

    private static async Task<TsCheckResult> AnalyzeFileAsync(byte[] data)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tscutter-equiv-{Guid.NewGuid():N}.ts");
        try
        {
            await File.WriteAllBytesAsync(path, data);
            return await new TsStreamAnalyzer().AnalyzeAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertResultsEquivalent(TsCheckResult expected, TsCheckResult actual)
    {
        Assert.Equal(expected.Verdict, actual.Verdict);
        Assert.Equal(expected.FileSize, actual.FileSize);
        Assert.Equal(expected.BytesScanned, actual.BytesScanned);
        Assert.Equal(expected.PacketCount, actual.PacketCount);
        Assert.Equal(expected.SyncOffset, actual.SyncOffset);
        Assert.Equal(expected.PacketSize, actual.PacketSize);
        Assert.Equal(expected.TotalErrorCount, actual.TotalErrorCount);
        Assert.Equal(expected.TotalWarningCount, actual.TotalWarningCount);
        Assert.Equal(expected.GlobalErrorCount, actual.GlobalErrorCount);
        Assert.Equal(expected.GlobalWarningCount, actual.GlobalWarningCount);
        Assert.Equal(expected.OmittedEventCount, actual.OmittedEventCount);

        Assert.Equal(
            expected.Events.Select(item => (item.Severity, item.Type, item.Pid, item.StartPacket,
                item.EndPacket, item.Occurrences, item.MessageCode, item.FileOffset)),
            actual.Events.Select(item => (item.Severity, item.Type, item.Pid, item.StartPacket,
                item.EndPacket, item.Occurrences, item.MessageCode, item.FileOffset)));

        Assert.Equal(expected.Pids.Keys, actual.Pids.Keys);
        foreach (var (pid, summary) in expected.Pids)
        {
            var other = actual.Pids[pid];
            Assert.Equal(summary.PacketCount, other.PacketCount);
            Assert.Equal(summary.ErrorCount, other.ErrorCount);
            Assert.Equal(summary.WarningCount, other.WarningCount);
            Assert.Equal(summary.TransportErrors, other.TransportErrors);
            Assert.Equal(summary.ContinuityErrors, other.ContinuityErrors);
            Assert.Equal(summary.PayloadPacketCount, other.PayloadPacketCount);
        }
        Assert.Equal(expected.Timeline.Count, actual.Timeline.Count);
        for (var index = 0; index < expected.Timeline.Count; index++)
        {
            Assert.Equal(expected.Timeline[index].Segment, actual.Timeline[index].Segment);
            Assert.Equal(expected.Timeline[index].StartSeconds, actual.Timeline[index].StartSeconds, 9);
            Assert.Equal(expected.Timeline[index].DurationSeconds, actual.Timeline[index].DurationSeconds, 9);
            Assert.Equal(expected.Timeline[index].TotalPacketCount, actual.Timeline[index].TotalPacketCount, 6);
        }
    }

    private static int Count(TsCheckResult result, TsCheckEventType type) =>
        result.Events.Where(item => item.Type == type).Sum(item => item.Occurrences);

    /// <summary>
    /// 组成带 PAT/PMT、PCR、PTS、TEI 坏包、合法 discontinuity 与尾部残余的 TS 流。
    /// </summary>
    private static byte[] BuildStream()
    {
        using var output = new MemoryStream();
        foreach (var packet in BuildPacketList())
            output.Write(packet);
        // 尾部残余字节：不足一个完整包。
        output.Write([0x47, 0x1F, 0xFF, 0x10, 0x11, 0x22]);
        return output.ToArray();
    }

    private static List<byte[]> BuildPacketList()
    {
        var packets = new List<byte[]>
        {
            CreatePsiPacket(0x0000, 0, BuildPatSection(PmtPid)),
            CreatePsiPacket(PmtPid, 0, BuildPmtSection(VideoPid,
                (0x1B, VideoPid), (0x03, AudioPid)))
        };
        var videoContinuity = 1;
        var audioContinuity = 1;
        for (var index = 0; index < 24; index++)
        {
            var pts = 900_000 + index * 3_000;
            packets.Add(CreatePesPacket(VideoPid, videoContinuity++ & 0x0F, pts));
            packets.Add(CreatePacket(VideoPid, videoContinuity++ & 0x0F, pcrBase: pts));
            packets.Add(CreatePesPacket(AudioPid, audioContinuity++ & 0x0F, pts + 1_000));
            if (index == 5)
            {
                // TEI 坏包：应报告一个 TransportError，不产生级联误报。
                packets.Add(CreatePacket(AudioPid, audioContinuity++ & 0x0F, transportError: true));
            }
            if (index == 11)
            {
                // 合法显式 discontinuity：标志重置时间基线，PCR 保持原有节奏，不产生倒退或跳变。
                packets.Add(CreatePacket(VideoPid, videoContinuity++ & 0x0F, pcrBase: pts,
                    discontinuity: true));
            }
        }
        return packets;
    }

    private static byte[] CreatePacket(
        int pid,
        int continuityCounter,
        bool transportError = false,
        long? pcrBase = null,
        bool discontinuity = false)
    {
        var packet = new byte[PacketSize];
        Array.Fill(packet, (byte)0x55);
        packet[0] = 0x47;
        packet[1] = (byte)((transportError ? 0x80 : 0) | ((pid >> 8) & 0x1F));
        packet[2] = (byte)pid;
        packet[3] = (byte)(0x10 | (continuityCounter & 0x0F));
        if (pcrBase is not { } pcr)
            return packet;

        packet[3] = (byte)(0x30 | (continuityCounter & 0x0F));
        packet[4] = 7;
        packet[5] = (byte)(discontinuity ? 0x90 : 0x10);
        packet[6] = (byte)(pcr >> 25);
        packet[7] = (byte)(pcr >> 17);
        packet[8] = (byte)(pcr >> 9);
        packet[9] = (byte)(pcr >> 1);
        packet[10] = (byte)(((pcr & 1) << 7) | 0x7E);
        packet[11] = 0;
        return packet;
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

    private static byte[] CreatePesPacket(int pid, int continuityCounter, long pts)
    {
        var packet = CreatePacket(pid, continuityCounter);
        packet[1] |= 0x40;
        var payload = packet.AsSpan(4);
        payload[0] = 0;
        payload[1] = 0;
        payload[2] = 1;
        payload[3] = 0xE0;
        payload[4] = 0;
        payload[5] = 0;
        payload[6] = 0x80;
        payload[7] = 0x80;
        payload[8] = 5;
        WritePts(payload.Slice(9, 5), pts);
        return packet;
    }

    private static byte[] BuildPatSection(int pmtPid) => AppendCrc([
        0x00, 0xB0, 0x0D, 0x00, 0x01, 0xC1, 0x00, 0x00,
        0x00, 0x01, (byte)(0xE0 | (pmtPid >> 8)), (byte)pmtPid
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
}
