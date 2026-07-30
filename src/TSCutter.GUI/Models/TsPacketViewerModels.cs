using System;
using System.Collections.Generic;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Models;

public enum TsPacketParseError : byte
{
    None,
    InvalidSize,
    InvalidSyncByte,
    ReservedAdaptationControl,
    InvalidAdaptationLength
}

public enum TsPacketFieldKind
{
    Header,
    SyncByte,
    TransportErrorIndicator,
    PayloadUnitStartIndicator,
    TransportPriority,
    Pid,
    ScramblingControl,
    AdaptationControl,
    ContinuityCounter,
    Adaptation,
    AdaptationLength,
    DiscontinuityIndicator,
    RandomAccessIndicator,
    ElementaryStreamPriority,
    PcrFlag,
    OpcrFlag,
    SplicingPointFlag,
    PrivateDataFlag,
    AdaptationExtensionFlag,
    Pcr,
    Payload,
    PesHeader,
    StartCodePrefix,
    StreamId,
    PesPacketLength,
    PesFlags,
    PesHeaderLength,
    Pts,
    Dts,
    PointerField,
    TableId,
    SectionLength
}

internal enum TsPacketFieldValueKind
{
    None,
    AdaptationReserved,
    PayloadOnly,
    AdaptationOnly,
    AdaptationAndPayload
}

public readonly struct TsPacketInfo
{
    private readonly uint _header;
    private readonly byte _payloadOffset;
    private readonly byte _adaptationLength;
    private readonly byte _adaptationFlags;
    private readonly byte _error;

    internal TsPacketInfo(
        TsPacketParseError error,
        uint header,
        int payloadOffset,
        int adaptationLength,
        byte adaptationFlags)
    {
        _header = header;
        _payloadOffset = (byte)payloadOffset;
        _adaptationLength = (byte)adaptationLength;
        _adaptationFlags = adaptationFlags;
        _error = (byte)error;
    }

    public TsPacketParseError Error => (TsPacketParseError)_error;
    public int Pid => (int)(_header >> 8) & 0x1FFF;
    public int ContinuityCounter => (int)_header & 0x0F;
    public int PayloadOffset => _payloadOffset;
    public bool PayloadStart => (_header & 0x0040_0000) != 0;
    public bool TransportError => (_header & 0x0080_0000) != 0;
    public bool TransportPriority => (_header & 0x0020_0000) != 0;
    public int ScramblingControl => (int)(_header >> 6) & 0x03;
    public int AdaptationControl => (int)(_header >> 4) & 0x03;
    public int AdaptationLength => _adaptationLength;
    public byte AdaptationFlags => _adaptationFlags;
    public bool IsValid => Error == TsPacketParseError.None;
    public bool HasPayload => (AdaptationControl & 0x01) != 0 && PayloadOffset < TsUtil.TsPacketSize;
    public bool HasAdaptation => (AdaptationControl & 0x02) != 0;
    public bool Discontinuity => HasAdaptation && AdaptationLength > 0 && (AdaptationFlags & 0x80) != 0;
    public bool RandomAccess => HasAdaptation && AdaptationLength > 0 && (AdaptationFlags & 0x40) != 0;
    public bool ElementaryStreamPriority => HasAdaptation && AdaptationLength > 0 && (AdaptationFlags & 0x20) != 0;
    public bool PcrFlag => HasAdaptation && AdaptationLength > 0 && (AdaptationFlags & 0x10) != 0;
    public bool OpcrFlag => HasAdaptation && AdaptationLength > 0 && (AdaptationFlags & 0x08) != 0;
    public bool HasPcr => PcrFlag && AdaptationLength >= 7;
    public bool HasOpcr => OpcrFlag && AdaptationLength >= (PcrFlag ? 13 : 7);
    public bool HasSplicingPoint => HasAdaptation && AdaptationLength > 0 && (AdaptationFlags & 0x04) != 0;
    public bool HasPrivateData => HasAdaptation && AdaptationLength > 0 && (AdaptationFlags & 0x02) != 0;
    public bool HasAdaptationExtension => HasAdaptation && AdaptationLength > 0 && (AdaptationFlags & 0x01) != 0;
}

internal sealed class TsPacketViewerSession
{
    public required string FilePath { get; init; }
    public required long FileSize { get; init; }
    public required long SyncOffset { get; init; }
    public required long TotalPackets { get; init; }
    public required TsCheckResult Catalog { get; init; }
}

internal sealed class TsPacketData
{
    public required long PacketIndex { get; init; }
    public required long FileOffset { get; init; }
    public required byte[] Data { get; init; }
    public required TsPacketInfo Info { get; init; }
    public required string TimestampText { get; init; }
}

public sealed class TsPacketViewerRow
{
    public required TsPacketInfo Info { get; init; }
    public required long PacketIndex { get; init; }
    public required long FileOffset { get; init; }
    public required byte[] Data { get; init; }
    public required string StreamText { get; set; }
    public required string TimestampText { get; init; }
    public TsCheckSeverity Severity { get; init; }
    public string PacketText => PacketIndex.ToString("N0");
    public string OffsetText => $"0x{FileOffset:X}";
    public string PidText => $"0x{Info.Pid:X4}";
    public string TeiText => Info.TransportError ? "1" : "0";
    public string PusiText => Info.PayloadStart ? "1" : "0";
    public string ContinuityText => Info.ContinuityCounter.ToString();
    public string AdaptationText { get; set; } = string.Empty;
    public bool IsInvalid => !Info.IsValid;
    public bool HasError => Severity == TsCheckSeverity.Error;
    public bool HasWarning => Severity == TsCheckSeverity.Warning;
}

internal sealed class TsPacketFieldDefinition
{
    public required TsPacketFieldKind Kind { get; init; }
    public string Value { get; init; } = string.Empty;
    public TsPacketFieldValueKind ValueKind { get; init; }
    public required int StartByte { get; init; }
    public required int ByteLength { get; init; }
    public int? HighBit { get; init; }
    public int? LowBit { get; init; }
    public List<TsPacketFieldDefinition> Children { get; } = [];
}

public sealed class TsPacketFieldItem
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public required string RangeText { get; init; }
    public required int StartByte { get; init; }
    public required int ByteLength { get; init; }
    public IReadOnlyList<TsPacketFieldItem> Children { get; init; } = [];
}
