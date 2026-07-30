using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class TsPacketViewerServiceTests
{
    [Fact]
    public void SharedPacketInfoRemainsCompactAndAllocationFree()
    {
        var packet = CreatePacket(0x0201, 7, payloadStart: true, pcrBase: 180_000);
        _ = TsPacketParser.Parse(packet);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var checksum = 0;

        for (var index = 0; index < 100_000; index++)
            checksum += TsPacketParser.Parse(packet).Pid;

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.Equal(0, allocated);
        Assert.Equal(0x0201 * 100_000, checksum);
        Assert.Equal(8, Unsafe.SizeOf<TsPacketInfo>());
    }

    [Fact]
    public void ParserExposesSharedHeaderAndAdaptationFields()
    {
        var packet = CreatePacket(0x0201, 7, payloadStart: true, pcrBase: 180_000);

        var parsed = TsPacketParser.Parse(packet);

        Assert.True(parsed.IsValid);
        Assert.Equal(0x0201, parsed.Pid);
        Assert.Equal(7, parsed.ContinuityCounter);
        Assert.True(parsed.PayloadStart);
        Assert.True(parsed.HasAdaptation);
        Assert.True(parsed.HasPcr);
        Assert.Equal(12, parsed.PayloadOffset);

        var fields = TsPacketFieldBuilder.Build(packet, parsed);
        var pcr = fields.SelectMany(item => item.Children)
            .Single(item => item.Kind == TsPacketFieldKind.Pcr);
        Assert.Equal(6, pcr.StartByte);
        Assert.Equal(6, pcr.ByteLength);
    }

    [Fact]
    public void ParserPreservesAllHeaderFlagsInCompactRepresentation()
    {
        var packet = CreatePacket(0x1234, 11, payloadStart: true);
        packet[1] |= 0xA0;
        packet[3] = 0xBB;

        var parsed = TsPacketParser.Parse(packet);

        Assert.Equal(0x1234, parsed.Pid);
        Assert.Equal(11, parsed.ContinuityCounter);
        Assert.True(parsed.TransportError);
        Assert.True(parsed.PayloadStart);
        Assert.True(parsed.TransportPriority);
        Assert.Equal(2, parsed.ScramblingControl);
        Assert.Equal(3, parsed.AdaptationControl);
    }

    [Fact]
    public void FieldBuilderPreservesRawPcrFlagsWhenAdaptationDataIsTruncated()
    {
        var packet = CreatePacket(0x0201, 3);
        packet[3] = 0x33;
        packet[4] = 1;
        packet[5] = 0x18;

        var parsed = TsPacketParser.Parse(packet);
        var adaptation = TsPacketFieldBuilder.Build(packet, parsed)
            .Single(item => item.Kind == TsPacketFieldKind.Adaptation);

        Assert.True(parsed.PcrFlag);
        Assert.True(parsed.OpcrFlag);
        Assert.False(parsed.HasPcr);
        Assert.False(parsed.HasOpcr);
        Assert.Equal("1", adaptation.Children.Single(item => item.Kind == TsPacketFieldKind.PcrFlag).Value);
        Assert.Equal("1", adaptation.Children.Single(item => item.Kind == TsPacketFieldKind.OpcrFlag).Value);
        Assert.DoesNotContain(adaptation.Children, item => item.Kind == TsPacketFieldKind.Pcr);
    }

    [Fact]
    public void FieldBuilderDoesNotInventOptionalHeaderForExceptionalPesStreamIds()
    {
        var packet = CreatePacket(0x0201, 3, payloadStart: true);
        packet[7] = 0xBE;
        packet[11] = 0x80;
        packet[12] = 0x05;

        var parsed = TsPacketParser.Parse(packet);
        var pes = TsPacketFieldBuilder.Build(packet, parsed)
            .SelectMany(item => item.Children)
            .Single(item => item.Kind == TsPacketFieldKind.PesHeader);

        Assert.Equal("0xBE", pes.Children.Single(item => item.Kind == TsPacketFieldKind.StreamId).Value);
        Assert.Equal(6, pes.ByteLength);
        Assert.DoesNotContain(pes.Children, item => item.Kind == TsPacketFieldKind.PesFlags);
        Assert.DoesNotContain(pes.Children, item => item.Kind == TsPacketFieldKind.PesHeaderLength);
    }

    [Fact]
    public async Task ViewerUsesSyncOffsetAndNavigatesSamePidWithoutWholeFileIndex()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ts-packet-viewer-{Guid.NewGuid():N}.ts");
        try
        {
            var prefix = new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 };
            var packets = new[]
            {
                CreatePacket(0x0100, 0),
                CreatePacket(0x0101, 0),
                CreatePacket(0x0100, 1),
                CreatePacket(0x0102, 0),
                CreatePacket(0x0100, 2),
                CreatePacket(0x0101, 1)
            };
            await using (var output = File.Create(path))
            {
                await output.WriteAsync(prefix);
                foreach (var packet in packets)
                    await output.WriteAsync(packet);
            }

            await using var viewer = new TsPacketViewerService();
            var session = await viewer.OpenAsync(path);
            Assert.Equal(prefix.Length, session.SyncOffset);
            Assert.Equal(packets.Length, session.TotalPackets);

            var rows = await viewer.ReadWindowAsync(1, 3);
            Assert.Equal([1L, 2L, 3L], rows.Select(item => item.PacketIndex));
            Assert.Equal(prefix.Length + 188, rows[0].FileOffset);
            Assert.Equal(0x0101, rows[0].Info.Pid);

            Assert.Equal(2, await viewer.FindSamePidAsync(0, 0x0100, true));
            Assert.Equal(2, await viewer.FindSamePidAsync(4, 0x0100, false));
            Assert.Null(await viewer.FindSamePidAsync(4, 0x0102, true));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SamePidNavigationCrossesSearchChunksWithOneReusableBuffer()
    {
        const int farPacketIndex = 65_540;
        var path = Path.Combine(Path.GetTempPath(), $"ts-packet-viewer-far-{Guid.NewGuid():N}.ts");
        try
        {
            var targetPacket = CreatePacket(0x0101, 0);
            var fillerPacket = CreatePacket(0x0100, 0);
            var fillerBlock = new byte[188 * 1024];
            for (var offset = 0; offset < fillerBlock.Length; offset += 188)
                fillerPacket.CopyTo(fillerBlock, offset);

            await using (var output = File.Create(path))
            {
                await output.WriteAsync(targetPacket);
                var remaining = farPacketIndex - 1;
                while (remaining > 0)
                {
                    var count = Math.Min(1024, remaining);
                    await output.WriteAsync(fillerBlock.AsMemory(0, count * 188));
                    remaining -= count;
                }
                await output.WriteAsync(targetPacket);
            }

            await using var viewer = new TsPacketViewerService();
            await viewer.OpenAsync(path);

            Assert.Equal(farPacketIndex, await viewer.FindSamePidAsync(0, 0x0101, true));
            Assert.Equal(0, await viewer.FindSamePidAsync(farPacketIndex, 0x0101, false));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] CreatePacket(int pid, int continuity, bool payloadStart = false, long? pcrBase = null)
    {
        var packet = new byte[188];
        packet.AsSpan().Fill(0xFF);
        packet[0] = 0x47;
        packet[1] = (byte)((payloadStart ? 0x40 : 0) | ((pid >> 8) & 0x1F));
        packet[2] = (byte)pid;
        packet[3] = (byte)((pcrBase is null ? 0x10 : 0x30) | (continuity & 0x0F));
        var payloadOffset = 4;
        if (pcrBase is { } pcr)
        {
            packet[4] = 7;
            packet[5] = 0x10;
            packet[6] = (byte)(pcr >> 25);
            packet[7] = (byte)(pcr >> 17);
            packet[8] = (byte)(pcr >> 9);
            packet[9] = (byte)(pcr >> 1);
            packet[10] = (byte)((pcr & 1) << 7);
            packet[11] = 0;
            payloadOffset = 12;
        }
        packet[payloadOffset] = 0;
        packet[payloadOffset + 1] = 0;
        packet[payloadOffset + 2] = 1;
        packet[payloadOffset + 3] = 0xE0;
        return packet;
    }
}
