using System;
using System.Runtime.CompilerServices;

namespace TSCutter.GUI.Utils;

/// <summary>
/// 集中处理 TS 包内 PCR、PTS 和 DTS 的读取、回绕展开与安全改写。
/// 上层服务只负责决定修正量，避免扫描、过滤和修复策略重复实现位字段操作。
/// </summary>
internal static class TsTimestampFieldCodec
{
    private const int PacketSize = TsUtil.TsPacketSize;
    private const long TimestampWrap = 1L << 33;

    public static bool TryReadPcr(
        ReadOnlySpan<byte> packet,
        out long rawPcr90k,
        out int pcrOffset,
        out bool discontinuity)
    {
        rawPcr90k = 0;
        pcrOffset = 0;
        discontinuity = false;
        if (packet.Length < PacketSize)
            return false;

        var adaptationControl = (packet[3] >> 4) & 0x03;
        if ((adaptationControl & 0x02) == 0 || packet[4] < 1)
            return false;

        var flags = packet[5];
        discontinuity = (flags & 0x80) != 0;
        if ((flags & 0x10) == 0 || packet[4] < 7)
            return false;

        pcrOffset = 6;
        rawPcr90k = ReadPcrBase(packet[6..11]);
        return true;
    }

    public static bool RewritePcr(Span<byte> packet, long correction90k)
    {
        if (correction90k == 0 ||
            !TryReadPcr(packet, out var rawPcr90k, out var pcrOffset, out _))
        {
            return false;
        }

        WritePcrBase(packet.Slice(pcrOffset, 5), rawPcr90k + correction90k);
        return true;
    }

    public static int RewritePesTimestamps(
        Span<byte> packet,
        long correction90k,
        bool preserveFirstMarkerBit = true)
    {
        if ((correction90k == 0 && preserveFirstMarkerBit) ||
            !TryLocatePesTimestamps(packet, out var ptsOffset, out var dtsOffset))
        {
            return 0;
        }

        return RewritePesTimestamps(
            packet, correction90k, ptsOffset, dtsOffset, preserveFirstMarkerBit);
    }

    public static bool TryLocatePesTimestamps(
        ReadOnlySpan<byte> packet,
        out int ptsOffset,
        out int dtsOffset)
    {
        ptsOffset = -1;
        dtsOffset = -1;
        if (packet.Length < PacketSize || (packet[1] & 0x40) == 0)
            return false;

        var adaptationControl = (packet[3] >> 4) & 0x03;
        if ((adaptationControl & 0x01) == 0)
            return false;

        var payloadOffset = 4;
        if ((adaptationControl & 0x02) != 0)
            payloadOffset += packet[4] + 1;
        if (payloadOffset + 14 > PacketSize)
            return false;

        var payload = packet[payloadOffset..];
        if (payload.Length < 9 || payload[0] != 0 || payload[1] != 0 || payload[2] != 1)
            return false;

        var flags = (payload[7] >> 6) & 0x03;
        if ((flags & 0x02) != 0 && payload.Length >= 14)
            ptsOffset = payloadOffset + 9;
        if (flags == 0x03 && payload.Length >= 19)
            dtsOffset = payloadOffset + 14;
        return ptsOffset >= 0;
    }

    public static int RewritePesTimestamps(
        Span<byte> packet,
        long correction90k,
        int ptsOffset,
        int dtsOffset,
        bool preserveFirstMarkerBit = true)
    {
        if ((correction90k == 0 && preserveFirstMarkerBit) ||
            ptsOffset < 0 || ptsOffset + 5 > packet.Length)
            return 0;

        var pts = packet.Slice(ptsOffset, 5);
        WritePesTimestamp(
            pts, ReadPesTimestamp(pts) + correction90k, preserveFirstMarkerBit);
        var count = 1;
        if (dtsOffset >= 0 && dtsOffset + 5 <= packet.Length)
        {
            var dts = packet.Slice(dtsOffset, 5);
            WritePesTimestamp(
                dts, ReadPesTimestamp(dts) + correction90k, preserveFirstMarkerBit);
            count++;
        }
        return count;
    }

    public static void WritePcrBase(Span<byte> value, long pcr90k)
    {
        var raw = ModuloTimestamp(pcr90k);
        value[0] = (byte)(raw >> 25);
        value[1] = (byte)(raw >> 17);
        value[2] = (byte)(raw >> 9);
        value[3] = (byte)(raw >> 1);
        // PCR extension 与保留位保持原值，只改写 33 位 PCR base。
        value[4] = (byte)((value[4] & 0x7F) | (byte)((raw & 1) << 7));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadPcrBase(ReadOnlySpan<byte> value) =>
        ((long)value[0] << 25) |
        ((long)value[1] << 17) |
        ((long)value[2] << 9) |
        ((long)value[3] << 1) |
        ((long)value[4] >> 7);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long ReadPesTimestamp(ReadOnlySpan<byte> value) =>
        ((long)(value[0] & 0x0E) << 29) |
        ((long)value[1] << 22) |
        ((long)(value[2] & 0xFE) << 14) |
        ((long)value[3] << 7) |
        ((long)value[4] >> 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long UnwrapTimestamp(long rawTimestamp, long lastRawTimestamp, long wrapOffset)
    {
        // 只有跨越半个 33 位周期才判定为回绕，不能吞掉普通的时间戳倒退异常。
        if (lastRawTimestamp == long.MinValue)
            return rawTimestamp + wrapOffset;
        if (lastRawTimestamp - rawTimestamp > TimestampWrap / 2)
            wrapOffset += TimestampWrap;
        else if (rawTimestamp - lastRawTimestamp > TimestampWrap / 2)
            wrapOffset -= TimestampWrap;
        return rawTimestamp + wrapOffset;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long UnwrapTimestamp(
        long rawTimestamp,
        ref long lastRawTimestamp,
        ref long wrapOffset)
    {
        var unwrapped = UnwrapTimestamp(rawTimestamp, lastRawTimestamp, wrapOffset);
        lastRawTimestamp = rawTimestamp;
        wrapOffset = unwrapped - rawTimestamp;
        return unwrapped;
    }

    private static void WritePesTimestamp(
        Span<byte> value,
        long timestamp90k,
        bool preserveFirstMarkerBit)
    {
        var raw = ModuloTimestamp(timestamp90k);
        // 时间轴修复默认保留首个 marker bit，以免顺带掩盖源文件中的格式错误；
        // 由封包器生成输出时则可显式要求把该位规范为 1。
        var preserved = preserveFirstMarkerBit
            ? value[0] & 0xF1
            : (value[0] & 0xF0) | 1;
        value[0] = (byte)(preserved | (byte)(((raw >> 30) & 7) << 1));
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
}
