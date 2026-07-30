using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Services;

internal static class TsPacketParser
{
    private const int PacketSize = TsUtil.TsPacketSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryParse(ReadOnlySpan<byte> packet, out TsPacketInfo info)
    {
        info = Parse(packet);
        return info.IsValid;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TsPacketInfo Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != PacketSize)
            return Invalid(TsPacketParseError.InvalidSize);
        var header = BinaryPrimitives.ReadUInt32BigEndian(packet);
        var initialError = (byte)(header >> 24) == TsUtil.TsSyncByte
            ? TsPacketParseError.None
            : TsPacketParseError.InvalidSyncByte;
        var adaptationControl = (int)(header >> 4) & 0x03;
        if (adaptationControl == 0)
        {
            return new TsPacketInfo(
                initialError == TsPacketParseError.None
                    ? TsPacketParseError.ReservedAdaptationControl
                    : initialError,
                header, 4, 0, 0);
        }

        var payloadOffset = 4;
        var adaptationLength = 0;
        byte adaptationFlags = 0;
        if ((adaptationControl & 0x02) != 0)
        {
            adaptationLength = packet[4];
            // adaptation_field_length 不得让字段越过当前 188 字节包。
            if (adaptationLength > 183)
                return new TsPacketInfo(
                    initialError == TsPacketParseError.None
                        ? TsPacketParseError.InvalidAdaptationLength
                        : initialError,
                    header, PacketSize, adaptationLength, 0);
            payloadOffset += adaptationLength + 1;
            if (adaptationLength > 0)
                adaptationFlags = packet[5];
        }

        return new TsPacketInfo(initialError, header, payloadOffset, adaptationLength, adaptationFlags);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TsPacketInfo Invalid(TsPacketParseError error) => new(error, 0, 0, 0, 0);
}
