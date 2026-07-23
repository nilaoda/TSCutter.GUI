using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class TsStreamAnalyzerTests
{
    [Fact]
    public async Task TransportErrorPacketsDoNotProduceCascadingHeaderClockOrContinuityErrors()
    {
        var packets = new List<byte[]>
        {
            CreatePacket(0x0100, 0, pcrBase: 90_000),
            CreatePacket(0x0100, 1, pcrBase: 93_000),
            CreatePacket(0x0100, 2),
            CreatePacket(0x0100, 3),
            CreatePacket(0x0100, 12, transportError: true, adaptationControl: 0),
            CreatePacket(0x0100, 14, transportError: true, pcrBase: 4_000_000_000),
            CreatePacket(0x0100, 6, pcrBase: 108_000),
            CreatePacket(0x0100, 7)
        };

        var result = await AnalyzeAsync(packets);

        Assert.Equal(2, Count(result, TsCheckEventType.TransportError));
        Assert.Equal(0, Count(result, TsCheckEventType.ContinuityGap));
        Assert.Equal(0, Count(result, TsCheckEventType.InvalidPacketHeader));
        Assert.Equal(0, Count(result, TsCheckEventType.SyncLoss));
        Assert.Equal(0, Count(result, TsCheckEventType.PcrBackward));
        Assert.Equal(0, Count(result, TsCheckEventType.PcrJump));
    }

    [Fact]
    public async Task ContinuityGapWithoutTransportErrorIsStillReported()
    {
        var packets = new[]
        {
            CreatePacket(0x0101, 0),
            CreatePacket(0x0101, 1),
            CreatePacket(0x0101, 2),
            CreatePacket(0x0101, 4),
            CreatePacket(0x0101, 5),
            CreatePacket(0x0101, 6)
        };

        var result = await AnalyzeAsync(packets);

        Assert.Equal(1, Count(result, TsCheckEventType.ContinuityGap));
    }

    [Fact]
    public async Task ReliablePcrJumpAfterTransportErrorIsStillReported()
    {
        var packets = new[]
        {
            CreatePacket(0x0102, 0, pcrBase: 90_000),
            CreatePacket(0x0102, 1, pcrBase: 93_000),
            CreatePacket(0x0102, 2),
            CreatePacket(0x0102, 3),
            CreatePacket(0x0102, 4, transportError: true),
            CreatePacket(0x0102, 5, pcrBase: 2_000_000),
            CreatePacket(0x0102, 6)
        };

        var result = await AnalyzeAsync(packets);

        Assert.Equal(1, Count(result, TsCheckEventType.TransportError));
        Assert.Equal(1, Count(result, TsCheckEventType.PcrJump));
    }

    [Fact]
    public async Task InterleavedTransportErrorsAreMergedPerPid()
    {
        var packets = new[]
        {
            CreatePacket(0x0100, 0),
            CreatePacket(0x0100, 1),
            CreatePacket(0x0100, 2, transportError: true),
            CreatePacket(0x0101, 0, transportError: true),
            CreatePacket(0x0100, 3, transportError: true),
            CreatePacket(0x0101, 1, transportError: true),
            CreatePacket(0x0100, 4),
            CreatePacket(0x0101, 2)
        };

        var result = await AnalyzeAsync(packets);
        var events = result.Events.Where(item => item.Type == TsCheckEventType.TransportError).ToArray();

        Assert.Equal(4, events.Sum(item => item.Occurrences));
        Assert.Equal(2, events.Length);
        Assert.Equal(2, events.Single(item => item.Pid == 0x0100).Occurrences);
        Assert.Equal(2, events.Single(item => item.Pid == 0x0101).Occurrences);
    }

    [Fact]
    public async Task PtsBeforeFirstTimelinePcrDoesNotCreateEstimatedTimeline()
    {
        const int pmtPid = 0x0100;
        const int videoPid = 0x0101;
        var packets = new[]
        {
            CreatePsiPacket(0x0000, 0, BuildPatSection(pmtPid)),
            CreatePsiPacket(pmtPid, 0, BuildPmtSection(videoPid)),
            CreatePesPacket(videoPid, 0, 200_000),
            CreatePacket(videoPid, 1, pcrBase: 199_000),
            CreatePacket(videoPid, 2, pcrBase: 199_300),
            CreatePacket(videoPid, 3, pcrBase: 199_600),
            CreatePacket(videoPid, 4, pcrBase: 199_900),
            CreatePacket(videoPid, 5, pcrBase: 200_200)
        };

        var result = await AnalyzeAsync(packets);

        Assert.Equal(videoPid, result.TimelineReferencePcrPid);
        Assert.False(result.TimelineUsesEstimatedClock);
        Assert.Equal(0, Count(result, TsCheckEventType.PcrBackward));
        Assert.Equal(0, Count(result, TsCheckEventType.PcrJump));
    }

    private static int Count(TsCheckResult result, TsCheckEventType type) => result.Events
        .Where(item => item.Type == type)
        .Sum(item => item.Occurrences);

    private static async Task<TsCheckResult> AnalyzeAsync(IEnumerable<byte[]> packets)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ts-check-{Guid.NewGuid():N}.ts");
        try
        {
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                foreach (var packet in packets)
                    await stream.WriteAsync(packet);
            }
            return await new TsStreamAnalyzer().AnalyzeAsync(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] CreatePacket(
        int pid,
        int continuityCounter,
        bool transportError = false,
        int adaptationControl = 1,
        long? pcrBase = null)
    {
        var packet = new byte[TsStreamAnalyzer.PacketSize];
        Array.Fill(packet, (byte)0x55);
        packet[0] = 0x47;
        packet[1] = (byte)((transportError ? 0x80 : 0) | ((pid >> 8) & 0x1F));
        packet[2] = (byte)pid;
        packet[3] = (byte)((adaptationControl << 4) | (continuityCounter & 0x0F));
        if (pcrBase is not { } pcr)
            return packet;

        packet[3] = (byte)(0x30 | (continuityCounter & 0x0F));
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

    private static byte[] BuildPmtSection(int pcrPid) => AppendCrc([
        0x02, 0xB0, 0x12, 0x00, 0x01, 0xC1, 0x00, 0x00,
        (byte)(0xE0 | (pcrPid >> 8)), (byte)pcrPid, 0xF0, 0x00,
        0x1B, (byte)(0xE0 | (pcrPid >> 8)), (byte)pcrPid, 0xF0, 0x00
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
}
