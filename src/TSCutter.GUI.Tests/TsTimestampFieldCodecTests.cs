using System;
using TSCutter.GUI.Utils;
using Xunit;

namespace TSCutter.GUI.Tests;

/// <summary>验证各服务共享的 TS 时间戳字段编解码规则。</summary>
public sealed class TsTimestampFieldCodecTests
{
    private const int PacketSize = TsUtil.TsPacketSize;
    private const long TimestampWrap = 1L << 33;

    [Fact]
    public void RewritesPcrPtsAndDtsWithTheSameWrappedCorrection()
    {
        var packet = CreateTimestampPacket(1_000, 2_000, 1_500);

        Assert.True(TsTimestampFieldCodec.RewritePcr(packet, -3_000));
        Assert.Equal(2, TsTimestampFieldCodec.RewritePesTimestamps(packet, -3_000));

        Assert.True(TsTimestampFieldCodec.TryReadPcr(
            packet, out var pcr, out _, out var discontinuity));
        Assert.True(discontinuity);
        Assert.Equal(TimestampWrap - 2_000, pcr);
        Assert.Equal(TimestampWrap - 1_000, ReadTimestamp(packet.AsSpan(21, 5)));
        Assert.Equal(TimestampWrap - 1_500, ReadTimestamp(packet.AsSpan(26, 5)));
        Assert.Equal(0x31, packet[21] & 0xF1);
        Assert.Equal(0x11, packet[26] & 0xF1);
    }

    [Fact]
    public void DoesNotTreatOrdinaryPayloadAsPesTimestampHeader()
    {
        var packet = new byte[PacketSize];
        packet.AsSpan().Fill(0xFF);
        packet[0] = 0x47;
        packet[1] = 0x40;
        packet[3] = 0x10;

        Assert.False(TsTimestampFieldCodec.TryLocatePesTimestamps(
            packet, out _, out _));
        Assert.Equal(0, TsTimestampFieldCodec.RewritePesTimestamps(packet, 90_000));
    }

    [Fact]
    public void MarkerBitPolicyRemainsExplicitForRepairAndPacketizerCallers()
    {
        var repairPacket = CreateTimestampPacket(1_000, 2_000, 1_500);
        repairPacket[21] &= 0xFE;
        TsTimestampFieldCodec.RewritePesTimestamps(repairPacket, 9_000);
        Assert.Equal(0, repairPacket[21] & 0x01);

        var packetizerPacket = CreateTimestampPacket(1_000, 2_000, 1_500);
        packetizerPacket[21] &= 0xFE;
        TsTimestampFieldCodec.RewritePesTimestamps(
            packetizerPacket, 0, preserveFirstMarkerBit: false);
        Assert.Equal(1, packetizerPacket[21] & 0x01);
        Assert.Equal(2_000, ReadTimestamp(packetizerPacket.AsSpan(21, 5)));
    }

    [Fact]
    public void UnwrapsOnlyAcrossTheHalfCycleBoundary()
    {
        var lastRaw = TimestampWrap - 100;
        var wrapOffset = 0L;

        var unwrapped = TsTimestampFieldCodec.UnwrapTimestamp(
            50, ref lastRaw, ref wrapOffset);

        Assert.Equal(TimestampWrap + 50, unwrapped);
        Assert.Equal(50, lastRaw);
        Assert.Equal(TimestampWrap, wrapOffset);
    }

    private static byte[] CreateTimestampPacket(long pcr90k, long pts90k, long dts90k)
    {
        var packet = new byte[PacketSize];
        packet.AsSpan().Fill(0xFF);
        packet[0] = 0x47;
        packet[1] = 0x40;
        packet[3] = 0x30;
        packet[4] = 7;
        packet[5] = 0x90;
        WritePcr(packet.AsSpan(6, 6), pcr90k);

        var payload = packet.AsSpan(12);
        payload[0] = 0;
        payload[1] = 0;
        payload[2] = 1;
        payload[3] = 0xE0;
        payload[4] = 0;
        payload[5] = 0;
        payload[6] = 0x80;
        payload[7] = 0xC0;
        payload[8] = 10;
        WriteTimestamp(payload[9..14], pts90k, 0x30);
        WriteTimestamp(payload[14..19], dts90k, 0x10);
        return packet;
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

    private static void WriteTimestamp(Span<byte> value, long timestamp90k, byte prefix)
    {
        var raw = ModuloTimestamp(timestamp90k);
        value[0] = (byte)(prefix | (((raw >> 30) & 7) << 1) | 1);
        value[1] = (byte)(raw >> 22);
        value[2] = (byte)((((raw >> 15) & 0x7F) << 1) | 1);
        value[3] = (byte)(raw >> 7);
        value[4] = (byte)(((raw & 0x7F) << 1) | 1);
    }

    private static long ReadTimestamp(ReadOnlySpan<byte> value) =>
        ((long)(value[0] & 0x0E) << 29) |
        ((long)value[1] << 22) |
        ((long)(value[2] & 0xFE) << 14) |
        ((long)value[3] << 7) |
        ((long)value[4] >> 1);

    private static long ModuloTimestamp(long value)
    {
        value %= TimestampWrap;
        return value < 0 ? value + TimestampWrap : value;
    }
}
