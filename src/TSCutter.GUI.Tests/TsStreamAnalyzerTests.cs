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
}
