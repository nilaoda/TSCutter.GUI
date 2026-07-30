using System;
using System.Globalization;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Services;

internal static class TsPacketFieldBuilder
{
    public static TsPacketFieldDefinition[] Build(
        ReadOnlySpan<byte> packet,
        TsPacketInfo info,
        bool parsePsiPayload = false)
    {
        if (packet.Length != TsUtil.TsPacketSize)
            return [];

        // 每个字段都携带包内字节范围，UI 选择字段后可直接映射到 Hex 高亮区域。
        var header = Group(TsPacketFieldKind.Header, 0, 4);
        header.Children.Add(Leaf(TsPacketFieldKind.SyncByte, $"0x{packet[0]:X2}", 0, 1));
        header.Children.Add(Bit(TsPacketFieldKind.TransportErrorIndicator, Bool(info.TransportError), 1, 7, 7));
        header.Children.Add(Bit(TsPacketFieldKind.PayloadUnitStartIndicator, Bool(info.PayloadStart), 1, 6, 6));
        header.Children.Add(Bit(TsPacketFieldKind.TransportPriority, Bool(info.TransportPriority), 1, 5, 5));
        header.Children.Add(new TsPacketFieldDefinition
        {
            Kind = TsPacketFieldKind.Pid,
            Value = $"0x{info.Pid:X4} ({info.Pid})",
            StartByte = 1,
            ByteLength = 2,
            HighBit = 12,
            LowBit = 0
        });
        header.Children.Add(Bit(TsPacketFieldKind.ScramblingControl, info.ScramblingControl.ToString(), 3, 7, 6));
        header.Children.Add(new TsPacketFieldDefinition
        {
            Kind = TsPacketFieldKind.AdaptationControl,
            ValueKind = info.AdaptationControl switch
            {
                0 => TsPacketFieldValueKind.AdaptationReserved,
                1 => TsPacketFieldValueKind.PayloadOnly,
                2 => TsPacketFieldValueKind.AdaptationOnly,
                _ => TsPacketFieldValueKind.AdaptationAndPayload
            },
            StartByte = 3,
            ByteLength = 1,
            HighBit = 5,
            LowBit = 4
        });
        header.Children.Add(Bit(TsPacketFieldKind.ContinuityCounter, info.ContinuityCounter.ToString(), 3, 3, 0));

        var roots = new System.Collections.Generic.List<TsPacketFieldDefinition> { header };
        if (info.HasAdaptation && info.Error != TsPacketParseError.InvalidAdaptationLength)
            roots.Add(BuildAdaptation(packet, info));
        if (info.HasPayload)
            roots.Add(BuildPayload(packet, info, parsePsiPayload));
        return roots.ToArray();
    }

    private static TsPacketFieldDefinition BuildAdaptation(ReadOnlySpan<byte> packet, TsPacketInfo info)
    {
        var group = Group(TsPacketFieldKind.Adaptation, 4, info.AdaptationLength + 1);
        group.Children.Add(Leaf(TsPacketFieldKind.AdaptationLength, info.AdaptationLength.ToString(), 4, 1));
        if (info.AdaptationLength == 0)
            return group;

        group.Children.Add(Bit(TsPacketFieldKind.DiscontinuityIndicator, Bool(info.Discontinuity), 5, 7, 7));
        group.Children.Add(Bit(TsPacketFieldKind.RandomAccessIndicator, Bool(info.RandomAccess), 5, 6, 6));
        group.Children.Add(Bit(TsPacketFieldKind.ElementaryStreamPriority, Bool(info.ElementaryStreamPriority), 5, 5, 5));
        group.Children.Add(Bit(TsPacketFieldKind.PcrFlag, Bool(info.PcrFlag), 5, 4, 4));
        group.Children.Add(Bit(TsPacketFieldKind.OpcrFlag, Bool(info.OpcrFlag), 5, 3, 3));
        group.Children.Add(Bit(TsPacketFieldKind.SplicingPointFlag, Bool(info.HasSplicingPoint), 5, 2, 2));
        group.Children.Add(Bit(TsPacketFieldKind.PrivateDataFlag, Bool(info.HasPrivateData), 5, 1, 1));
        group.Children.Add(Bit(TsPacketFieldKind.AdaptationExtensionFlag, Bool(info.HasAdaptationExtension), 5, 0, 0));
        if (info.HasPcr)
        {
            var pcrBase = TsTimestampFieldCodec.ReadPcrBase(packet[6..11]);
            var extension = ((packet[10] & 0x01) << 8) | packet[11];
            group.Children.Add(Leaf(
                TsPacketFieldKind.Pcr,
                $"{pcrBase} ({TsCheckEvent.FormatTime(pcrBase / 90_000.0)}), ext {extension}",
                6, 6));
        }
        return group;
    }

    private static TsPacketFieldDefinition BuildPayload(
        ReadOnlySpan<byte> packet,
        TsPacketInfo info,
        bool parsePsiPayload)
    {
        var payloadLength = packet.Length - info.PayloadOffset;
        var group = Group(TsPacketFieldKind.Payload, info.PayloadOffset, payloadLength);
        if (!info.PayloadStart || payloadLength <= 0)
            return group;

        var payload = packet[info.PayloadOffset..];
        if (payload.Length >= 6 && payload[0] == 0 && payload[1] == 0 && payload[2] == 1)
        {
            var hasOptionalHeader = payload.Length >= 9 && HasOptionalPesHeader(payload[3]);
            var pesHeaderLength = hasOptionalHeader ? 9 + payload[8] : 6;
            var pes = Group(
                TsPacketFieldKind.PesHeader,
                info.PayloadOffset,
                Math.Min(payload.Length, pesHeaderLength));
            pes.Children.Add(Leaf(TsPacketFieldKind.StartCodePrefix, "0x000001", info.PayloadOffset, 3));
            pes.Children.Add(Leaf(TsPacketFieldKind.StreamId, $"0x{payload[3]:X2}", info.PayloadOffset + 3, 1));
            pes.Children.Add(Leaf(
                TsPacketFieldKind.PesPacketLength,
                ((payload[4] << 8) | payload[5]).ToString(CultureInfo.InvariantCulture),
                info.PayloadOffset + 4, 2));
            if (hasOptionalHeader)
            {
                pes.Children.Add(Leaf(TsPacketFieldKind.PesFlags, $"0x{payload[7]:X2}", info.PayloadOffset + 7, 1));
                pes.Children.Add(Leaf(TsPacketFieldKind.PesHeaderLength, payload[8].ToString(), info.PayloadOffset + 8, 1));
            }
            if (TsTimestampFieldCodec.TryLocatePesTimestamps(packet, out var ptsOffset, out var dtsOffset))
            {
                var pts = TsTimestampFieldCodec.ReadPesTimestamp(packet[ptsOffset..]);
                pes.Children.Add(Leaf(TsPacketFieldKind.Pts, $"{pts} ({TsCheckEvent.FormatTime(pts / 90_000.0)})", ptsOffset, 5));
                if (dtsOffset >= 0)
                {
                    var dts = TsTimestampFieldCodec.ReadPesTimestamp(packet[dtsOffset..]);
                    pes.Children.Add(Leaf(TsPacketFieldKind.Dts, $"{dts} ({TsCheckEvent.FormatTime(dts / 90_000.0)})", dtsOffset, 5));
                }
            }
            group.Children.Add(pes);
            return group;
        }

        if (!parsePsiPayload)
            return group;

        var pointer = payload[0];
        group.Children.Add(Leaf(TsPacketFieldKind.PointerField, pointer.ToString(), info.PayloadOffset, 1));
        var sectionOffset = info.PayloadOffset + 1 + pointer;
        if (sectionOffset + 3 <= packet.Length)
        {
            group.Children.Add(Leaf(TsPacketFieldKind.TableId, $"0x{packet[sectionOffset]:X2}", sectionOffset, 1));
            var sectionLength = ((packet[sectionOffset + 1] & 0x0F) << 8) | packet[sectionOffset + 2];
            group.Children.Add(new TsPacketFieldDefinition
            {
                Kind = TsPacketFieldKind.SectionLength,
                Value = sectionLength.ToString(CultureInfo.InvariantCulture),
                StartByte = sectionOffset + 1,
                ByteLength = 2,
                HighBit = 11,
                LowBit = 0
            });
        }
        return group;
    }

    private static TsPacketFieldDefinition Group(TsPacketFieldKind kind, int start, int length) => new()
    {
        Kind = kind,
        StartByte = start,
        ByteLength = length
    };

    private static TsPacketFieldDefinition Leaf(TsPacketFieldKind kind, string value, int start, int length) => new()
    {
        Kind = kind,
        Value = value,
        StartByte = start,
        ByteLength = length
    };

    private static TsPacketFieldDefinition Bit(TsPacketFieldKind kind, string value, int start, int high, int low) => new()
    {
        Kind = kind,
        Value = value,
        StartByte = start,
        ByteLength = 1,
        HighBit = high,
        LowBit = low
    };

    private static string Bool(bool value) => value ? "1" : "0";

    private static bool HasOptionalPesHeader(byte streamId) => streamId is not (
        0xBC or 0xBE or 0xBF or 0xF0 or 0xF1 or 0xF2 or 0xF8 or 0xFF);
}
