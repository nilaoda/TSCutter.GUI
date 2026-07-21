using System;
using System.Collections.Generic;

namespace TSCutter.GUI.Models;

public enum TsCheckSeverity
{
    Info,
    Warning,
    Error
}

public enum TsCheckVerdict
{
    Pass,
    Warning,
    Error,
    Incomplete
}

public enum TsCheckEventType
{
    SyncLoss,
    TrailingBytes,
    TransportError,
    ContinuityGap,
    PesSizeMismatch,
    DuplicatePacket,
    ConflictingDuplicate,
    PsiCrcError,
    PsiMalformed,
    PcrBackward,
    PcrJump,
    PcrGap,
    PtsBackward,
    PtsJump,
    DtsBackward,
    DtsAfterPts,
    AvSyncDrift,
    StreamGap,
    MissingProgramTable,
    MissingPcr,
    MissingTimestamp,
    InvalidPacketHeader
}

public enum TsCheckMessageCode
{
    NoSync,
    TrailingBytes,
    SyncRecovered,
    TransportError,
    InvalidAdaptationLength,
    DuplicatePacket,
    ConflictingDuplicate,
    ContinuityGap,
    PesSizeMismatch,
    PcrBackward,
    PcrJump,
    PcrGap,
    PsiCrcError,
    PmtInfoLength,
    DtsBackward,
    DtsAfterPts,
    PtsBackward,
    PtsJump,
    AudioGap,
    AvSyncDrift,
    SyncLostAtEnd,
    MissingPat,
    MissingPmt,
    MissingPcr,
    MissingTimestamp,
    InvalidAdaptationControl
}

public enum TsMpegAudioLayer
{
    LayerI,
    LayerII,
    LayerIII
}

public enum TsSupplementaryStreamType
{
    DvbSubtitle,
    DvbTeletext,
    AribCaption,
    Ac4,
    Opus,
    Smpte302M,
    Dra,
    SmpteKlv,
    Smpte2038,
    TimedId3
}

public sealed class TsCheckEvent
{
    public required TsCheckSeverity Severity { get; init; }
    public required TsCheckEventType Type { get; init; }
    public required int Pid { get; init; }
    public required long StartPacket { get; init; }
    public long EndPacket { get; set; }
    public required long FileOffset { get; init; }
    public double? SourceTimeSeconds { get; init; }
    public double? TimeSeconds { get; init; }
    public bool IsEstimatedTime { get; init; }
    public required TsCheckMessageCode MessageCode { get; init; }
    public object[] MessageArguments { get; init; } = [];
    public int Occurrences { get; set; } = 1;

    public string PidText => Pid >= 0 ? $"0x{Pid:X4}" : "-";

    public static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }
}

public sealed class TsCheckPidSummary
{
    public required int Pid { get; init; }
    public long PacketCount { get; set; }
    public long PayloadPacketCount { get; set; }
    public long ContinuityErrors { get; set; }
    public long TransportErrors { get; set; }
    public long PesSizeErrors { get; set; }
    public long DuplicatePackets { get; set; }
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public byte? StreamType { get; set; }
    public TsMpegAudioLayer? MpegAudioLayer { get; set; }
    public TsSupplementaryStreamType? SupplementaryStreamType { get; set; }
    public string? Language { get; set; }
    public int? ProgramNumber { get; set; }
    public bool IsPcrPid { get; set; }
    public bool IsPmtPid { get; set; }
    public string PidText => $"0x{Pid:X4}";
}

public sealed class TsCheckProgramSummary
{
    public required int ProgramNumber { get; init; }
    public required int PmtPid { get; set; }
    public int PcrPid { get; set; } = -1;
    public byte PmtVersion { get; set; }
    public byte[] ProgramDescriptors { get; set; } = [];
    public Dictionary<int, byte> Streams { get; } = [];
    public Dictionary<int, TsStreamDefinition> StreamDefinitions { get; } = [];
}

public sealed class TsServiceSummary
{
    public required int ServiceId { get; init; }
    public string ServiceName { get; set; } = string.Empty;
    public string ProviderName { get; set; } = string.Empty;
    public byte ServiceType { get; set; }
    public byte SdtVersion { get; set; }
    public int OriginalNetworkId { get; set; }
    public bool EitSchedule { get; set; }
    public bool EitPresentFollowing { get; set; }
    public byte RunningStatus { get; set; }
    public bool FreeCaMode { get; set; }
    public byte[] Descriptors { get; set; } = [];
}

public sealed class TsStreamDefinition
{
    public required byte StreamType { get; init; }
    public byte[] Descriptors { get; init; } = [];
}

public sealed class TsCheckResult
{
    public required string FilePath { get; init; }
    public required long FileSize { get; init; }
    public int TransportStreamId { get; set; } = 1;
    public byte PatVersion { get; set; }
    public long SyncOffset { get; set; }
    public long PacketCount { get; set; }
    public long BytesScanned { get; set; }
    public TimeSpan Elapsed { get; set; }
    public bool WasCancelled { get; set; }
    public long OmittedEventCount { get; set; }
    public int TotalErrorCount { get; set; }
    public int TotalWarningCount { get; set; }
    public int GlobalErrorCount { get; set; }
    public int GlobalWarningCount { get; set; }
    public List<TsCheckEvent> Events { get; } = [];
    public List<TsCheckTimelineBucket> Timeline { get; } = [];
    public Dictionary<int, TsCheckPidSummary> Pids { get; } = [];
    public Dictionary<int, TsCheckProgramSummary> Programs { get; } = [];
    public Dictionary<int, TsServiceSummary> Services { get; } = [];
    public TsBroadcastTimeAnchor? FirstBroadcastTime { get; set; }
    public TsBroadcastTimeAnchor? LastBroadcastTime { get; set; }
    public int TimelineReferencePcrPid { get; set; } = -1;
    public int ErrorCount => TotalErrorCount;
    public int WarningCount => TotalWarningCount;
    public TsCheckVerdict Verdict => WasCancelled
        ? TsCheckVerdict.Incomplete
        : ErrorCount > 0
            ? TsCheckVerdict.Error
            : WarningCount > 0
                ? TsCheckVerdict.Warning
                : TsCheckVerdict.Pass;
}

public readonly record struct TsBroadcastTimeAnchor(
    DateTimeOffset UtcTime,
    long Clock90k,
    long PacketIndex,
    long FileOffset,
    int ProgramNumber = -1);

public readonly record struct TsCheckTimelinePidSample(int Pid, double PacketCount);

public sealed class TsCheckTimelineBucket
{
    private const int TransportPacketBits = 188 * 8;

    public required double StartSeconds { get; init; }
    public required double DurationSeconds { get; init; }
    public required double TotalPacketCount { get; init; }
    public required int Segment { get; init; }
    public TsCheckTimelinePidSample[] Pids { get; init; } = [];
    public double EndSeconds => StartSeconds + DurationSeconds;
    public double TotalBitrate => DurationSeconds > 0
        ? TotalPacketCount * TransportPacketBits / DurationSeconds
        : 0;

    public double GetPidBitrate(int pid)
    {
        if (DurationSeconds <= 0)
            return 0;
        // 每个桶的 PID 采样已排序，绘图时使用二分查找，避免长时间轴反复线性扫描全部 PID。
        var low = 0;
        var high = Pids.Length - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var sample = Pids[middle];
            if (sample.Pid == pid)
                return sample.PacketCount * TransportPacketBits / DurationSeconds;
            if (sample.Pid < pid)
                low = middle + 1;
            else
                high = middle - 1;
        }
        return 0;
    }
}

public sealed class TsStreamAnalyzeOptions
{
    public bool InventoryOnly { get; init; }
    public bool IncludeServiceMetadata { get; init; }
    public long MaxBytes { get; init; } = long.MaxValue;
    public int StablePacketCount { get; init; } = 8_192;
    public TsStreamAnalyzeFeatures Features { get; init; } = TsStreamAnalyzeFeatures.Default;
}

[Flags]
public enum TsStreamAnalyzeFeatures
{
    None = 0,
    ContinuityValidation = 1 << 0,
    TimestampValidation = 1 << 1,
    AvSyncValidation = 1 << 2,
    DetailedEvents = 1 << 3,
    Timeline = 1 << 4,
    PesSizeValidation = 1 << 5,
    BroadcastClock = 1 << 6,
    Default = ContinuityValidation | TimestampValidation | AvSyncValidation | DetailedEvents | Timeline |
              PesSizeValidation | BroadcastClock
}

public readonly record struct TsCheckPidProgress(
    int Pid,
    long PacketCount,
    int ErrorCount,
    int WarningCount,
    int? ProgramNumber,
    byte? StreamType,
    TsMpegAudioLayer? MpegAudioLayer,
    TsSupplementaryStreamType? SupplementaryStreamType,
    string? Language,
    bool IsPcrPid,
    bool IsPmtPid,
    bool IsGlobal);

public readonly record struct TsCheckProgress(
    long BytesScanned,
    long FileSize,
    long PacketCount,
    int ErrorCount,
    int WarningCount,
    double BytesPerSecond,
    TimeSpan Elapsed,
    TsCheckPidProgress[] Pids,
    TsCheckEvent? NewEvent,
    TsCheckTimelineBucket[] Timeline)
{
    public double Percent => FileSize > 0 ? BytesScanned * 100.0 / FileSize : 0;
}

public static class TsStreamTypes
{
    // MPEG-TS PMT 的 stream_type 常量；0x80 以上多为厂商或光盘私有分配，需结合规范/描述符理解。
    public const byte Mpeg1Video = 0x01;
    public const byte Mpeg2Video = 0x02;
    public const byte Mpeg1Audio = 0x03;
    public const byte Mpeg2Audio = 0x04;
    public const byte PrivateSection = 0x05;
    public const byte PrivateData = 0x06;
    public const byte Aac = 0x0F;
    public const byte Mpeg4Video = 0x10;
    public const byte AacLatm = 0x11;
    public const byte Mpeg4Systems = 0x12;
    public const byte Mpeg4SystemsSection = 0x13;
    public const byte Metadata = 0x15;
    public const byte H264 = 0x1B;
    public const byte Mpeg4Audio = 0x1C;
    public const byte Mvc = 0x20;
    public const byte Jpeg2000 = 0x21;
    public const byte Hevc = 0x24;
    public const byte JpegXs = 0x32;
    public const byte Vvc = 0x33;
    public const byte Lcevc = 0x36;
    public const byte Cavs = 0x42;
    public const byte BluRayPcm = 0x80;
    public const byte Ac3 = 0x81;
    public const byte Dts = 0x82;
    public const byte TrueHd = 0x83;
    public const byte Eac3 = 0x84;
    public const byte DtsHd = 0x85;
    public const byte DtsHdMaster = 0x86;
    public const byte Eac3Atsc = 0x87;
    public const byte Eac3Secondary = 0xA1;
    public const byte DtsExpressSecondary = 0xA2;
    public const byte HdmvPgsSubtitle = 0x90;
    public const byte HdmvTextSubtitle = 0x92;
    public const byte Dirac = 0xD1;
    public const byte Avs2 = 0xD2;
    public const byte Avs3 = 0xD4;
    public const byte Av3a = 0xD5;
    public const byte Vc1 = 0xEA;

    public static bool IsVideo(byte streamType) => streamType is
        Mpeg1Video or Mpeg2Video or Mpeg4Video or H264 or Mvc or Jpeg2000 or Hevc or
        JpegXs or Vvc or Lcevc or Cavs or Dirac or Avs2 or Avs3 or Vc1;

    public static bool IsAudio(byte streamType, TsSupplementaryStreamType? supplementaryStreamType = null) =>
        supplementaryStreamType is TsSupplementaryStreamType.Ac4 or TsSupplementaryStreamType.Opus or
            TsSupplementaryStreamType.Smpte302M or TsSupplementaryStreamType.Dra || streamType is
        Mpeg1Audio or Mpeg2Audio or Aac or AacLatm or Mpeg4Audio or BluRayPcm or Ac3 or
        Dts or TrueHd or Eac3 or DtsHd or DtsHdMaster or Eac3Atsc or Eac3Secondary or
        DtsExpressSecondary or Av3a;

}
