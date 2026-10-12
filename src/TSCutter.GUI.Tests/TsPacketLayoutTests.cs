using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using TSCutter.GUI.Utils;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class TsPacketLayoutTests
{
    [Theory]
    [InlineData(2, 0)]
    [InlineData(3, 17)]
    [InlineData(8, 0)]
    [InlineData(8, 17)]
    public void RepeatedSyncBytesInArrivalHeadersDoNotChangePacketOffsets(int packetCount, int prefix)
    {
        var data = CreatePackets(packetCount, prefix);
        using var input = new MemoryStream(data);
        input.Position = 7;
        var layout = TsPacketLayout.Detect(input);
        Assert.True(layout.IsM2ts);
        Assert.Equal(prefix, layout.PacketOffset);
        Assert.Equal(7, input.Position);
        if (packetCount >= TsPacketLayout.SyncProbePacketCount)
            Assert.Equal(prefix, layout.FindPacketSync(data));
    }

    [Fact]
    public async Task DamagedFirstM2tsHeaderIsStillIncludedInDiagnostics()
    {
        var path = Path.Combine(Path.GetTempPath(), $"m2ts-header-{Guid.NewGuid():N}.m2ts");
        try
        {
            var data = CreatePackets(8, 0);
            for (var index = 0; index < 8; index++)
                data.AsSpan(index * TsPacketLayout.M2tsPacketSize, TsPacketLayout.M2tsHeaderSize).Clear();
            data[TsPacketLayout.M2tsHeaderSize + 3] = 0;
            await File.WriteAllBytesAsync(path, data);
            using var input = new MemoryStream(data);
            Assert.Equal(0, TsPacketLayout.Detect(input).PacketOffset);
            var result = await new TsStreamAnalyzer().AnalyzeAsync(path);
            Assert.Equal(8, result.PacketCount);
            Assert.Contains(result.Events, item => item.Type == TsCheckEventType.InvalidPacketHeader);
        }
        finally { File.Delete(path); }
    }

    private static byte[] CreatePackets(int packetCount, int prefix)
    {
        var data = new byte[prefix + packetCount * TsPacketLayout.M2tsPacketSize];
        var layout = TsPacketLayout.FromPacketOffset(TsPacketLayout.M2tsPacketSize, prefix);
        for (var index = 0; index < packetCount; index++)
        {
            var offset = prefix + index * layout.PacketSize;
            data[offset] = 0xC0;
            data[offset + 1] = 0x47;
            data[offset + 2] = (byte)(0x73 + index);
            var packet = layout.GetPacketBody(data.AsSpan(), offset);
            packet.Fill(0xFF);
            packet[0] = 0x47;
            packet[1] = 0x01;
            packet[2] = 0x00;
            packet[3] = (byte)(0x10 | index);
        }
        return data;
    }
}
