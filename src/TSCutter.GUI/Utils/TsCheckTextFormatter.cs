using System;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.Utils;

public sealed class TsCheckTextFormatter
{
    public LocalizationManager Strings { get; } = LocalizationManager.Instance;

    public string FormatEventMessage(TsCheckEvent item)
    {
        var format = item.MessageCode switch
        {
            TsCheckMessageCode.NoSync => Strings.String_TsCheck_Message_NoSync,
            TsCheckMessageCode.TrailingBytes => Strings.String_TsCheck_Message_TrailingBytes,
            TsCheckMessageCode.SyncRecovered => Strings.String_TsCheck_Message_SyncRecovered,
            TsCheckMessageCode.TransportError => Strings.String_TsCheck_Message_TransportError,
            TsCheckMessageCode.InvalidAdaptationLength => Strings.String_TsCheck_Message_InvalidAdaptationLength,
            TsCheckMessageCode.DuplicatePacket => Strings.String_TsCheck_Message_DuplicatePacket,
            TsCheckMessageCode.ConflictingDuplicate => Strings.String_TsCheck_Message_ConflictingDuplicate,
            TsCheckMessageCode.ContinuityGap => Strings.String_TsCheck_Message_ContinuityGap,
            TsCheckMessageCode.PcrBackward => Strings.String_TsCheck_Message_PcrBackward,
            TsCheckMessageCode.PcrJump => Strings.String_TsCheck_Message_PcrJump,
            TsCheckMessageCode.PcrGap => Strings.String_TsCheck_Message_PcrGap,
            TsCheckMessageCode.PsiCrcError => Strings.String_TsCheck_Message_PsiCrcError,
            TsCheckMessageCode.PmtInfoLength => Strings.String_TsCheck_Message_PmtInfoLength,
            TsCheckMessageCode.DtsBackward => Strings.String_TsCheck_Message_DtsBackward,
            TsCheckMessageCode.DtsAfterPts => Strings.String_TsCheck_Message_DtsAfterPts,
            TsCheckMessageCode.PtsBackward => Strings.String_TsCheck_Message_PtsBackward,
            TsCheckMessageCode.PtsJump => Strings.String_TsCheck_Message_PtsJump,
            TsCheckMessageCode.AudioGap => Strings.String_TsCheck_Message_AudioGap,
            TsCheckMessageCode.AvSyncDrift => Strings.String_TsCheck_Message_AvSyncDrift,
            TsCheckMessageCode.SyncLostAtEnd => Strings.String_TsCheck_Message_SyncLostAtEnd,
            TsCheckMessageCode.MissingPat => Strings.String_TsCheck_Message_MissingPat,
            TsCheckMessageCode.MissingPmt => Strings.String_TsCheck_Message_MissingPmt,
            TsCheckMessageCode.MissingPcr => Strings.String_TsCheck_Message_MissingPcr,
            TsCheckMessageCode.MissingTimestamp => Strings.String_TsCheck_Message_MissingTimestamp,
            TsCheckMessageCode.InvalidAdaptationControl => Strings.String_TsCheck_Message_InvalidAdaptationControl,
            _ => throw new ArgumentOutOfRangeException(nameof(item.MessageCode))
        };
        return string.Format(format, item.MessageArguments);
    }

    public string FormatEventType(TsCheckEvent item) => item.Type switch
    {
        TsCheckEventType.SyncLoss => Strings.String_TsCheck_Type_SyncLoss,
        TsCheckEventType.TrailingBytes => Strings.String_TsCheck_Type_TrailingBytes,
        TsCheckEventType.TransportError => Strings.String_TsCheck_Type_TransportError,
        TsCheckEventType.ContinuityGap => Strings.String_TsCheck_Type_ContinuityGap,
        TsCheckEventType.DuplicatePacket => Strings.String_TsCheck_Type_DuplicatePacket,
        TsCheckEventType.ConflictingDuplicate => Strings.String_TsCheck_Type_ConflictingDuplicate,
        TsCheckEventType.PsiCrcError => Strings.String_TsCheck_Type_PsiCrcError,
        TsCheckEventType.PsiMalformed => Strings.String_TsCheck_Type_PsiMalformed,
        TsCheckEventType.PcrBackward => Strings.String_TsCheck_Type_PcrBackward,
        TsCheckEventType.PcrJump => Strings.String_TsCheck_Type_PcrJump,
        TsCheckEventType.PcrGap => Strings.String_TsCheck_Type_PcrGap,
        TsCheckEventType.PtsBackward => Strings.String_TsCheck_Type_PtsBackward,
        TsCheckEventType.PtsJump => Strings.String_TsCheck_Type_PtsJump,
        TsCheckEventType.DtsBackward => Strings.String_TsCheck_Type_DtsBackward,
        TsCheckEventType.DtsAfterPts => Strings.String_TsCheck_Type_DtsAfterPts,
        TsCheckEventType.AvSyncDrift => Strings.String_TsCheck_Type_AvSyncDrift,
        TsCheckEventType.StreamGap => Strings.String_TsCheck_Type_StreamGap,
        TsCheckEventType.MissingProgramTable => Strings.String_TsCheck_Type_MissingProgramTable,
        TsCheckEventType.MissingPcr => Strings.String_TsCheck_Type_MissingPcr,
        TsCheckEventType.MissingTimestamp => Strings.String_TsCheck_Type_MissingTimestamp,
        TsCheckEventType.InvalidPacketHeader => Strings.String_TsCheck_Type_InvalidPacketHeader,
        _ => Strings.String_TsCheck_Type_Unknown
    };

    public string FormatSeverity(TsCheckSeverity severity) => severity switch
    {
        TsCheckSeverity.Error => Strings.String_TsCheck_Severity_Error,
        TsCheckSeverity.Warning => Strings.String_TsCheck_Severity_Warning,
        _ => Strings.String_TsCheck_Severity_Info
    };

    // 这里优先使用一次性 MPEG Audio 帧头探测结果，否则回退到 PMT 的 stream_type，避免深度解析。
    public string FormatStreamType(byte? streamType, TsMpegAudioLayer? mpegAudioLayer = null)
    {
        if (mpegAudioLayer is not null)
        {
            return mpegAudioLayer switch
            {
                TsMpegAudioLayer.LayerI => Strings.String_TsCheck_Stream_Mp1,
                TsMpegAudioLayer.LayerII => Strings.String_TsCheck_Stream_Mp2,
                TsMpegAudioLayer.LayerIII => Strings.String_TsCheck_Stream_Mp3,
                _ => throw new ArgumentOutOfRangeException(nameof(mpegAudioLayer))
            };
        }

        return streamType switch
        {
            TsStreamTypes.Mpeg1Video => Strings.String_TsCheck_Stream_Mpeg1Video,
            TsStreamTypes.Mpeg2Video => Strings.String_TsCheck_Stream_Mpeg2Video,
            TsStreamTypes.Mpeg1Audio => Strings.String_TsCheck_Stream_Mpeg1Audio,
            TsStreamTypes.Mpeg2Audio => Strings.String_TsCheck_Stream_Mpeg2Audio,
            TsStreamTypes.PrivateData => Strings.String_TsCheck_Stream_PrivateData,
            TsStreamTypes.Aac => Strings.String_TsCheck_Stream_Aac,
            TsStreamTypes.Mpeg4Video => Strings.String_TsCheck_Stream_Mpeg4Video,
            TsStreamTypes.AacLatm => Strings.String_TsCheck_Stream_AacLatm,
            TsStreamTypes.Mpeg4Systems => Strings.String_TsCheck_Stream_Mpeg4Systems,
            TsStreamTypes.Metadata => Strings.String_TsCheck_Stream_Metadata,
            TsStreamTypes.H264 => Strings.String_TsCheck_Stream_H264,
            TsStreamTypes.Mpeg4Audio => Strings.String_TsCheck_Stream_Mpeg4Audio,
            TsStreamTypes.Mvc => Strings.String_TsCheck_Stream_Mvc,
            TsStreamTypes.Jpeg2000 => Strings.String_TsCheck_Stream_Jpeg2000,
            TsStreamTypes.Hevc => Strings.String_TsCheck_Stream_Hevc,
            TsStreamTypes.JpegXs => Strings.String_TsCheck_Stream_JpegXs,
            TsStreamTypes.Vvc => Strings.String_TsCheck_Stream_Vvc,
            TsStreamTypes.Lcevc => Strings.String_TsCheck_Stream_Lcevc,
            TsStreamTypes.Cavs => Strings.String_TsCheck_Stream_Cavs,
            TsStreamTypes.BluRayPcm => Strings.String_TsCheck_Stream_BluRayPcm,
            TsStreamTypes.Ac3 => Strings.String_TsCheck_Stream_Ac3,
            TsStreamTypes.Dts => Strings.String_TsCheck_Stream_Dts,
            TsStreamTypes.TrueHd => Strings.String_TsCheck_Stream_TrueHd,
            TsStreamTypes.Eac3 => Strings.String_TsCheck_Stream_Eac3,
            TsStreamTypes.DtsHd => Strings.String_TsCheck_Stream_DtsHd,
            TsStreamTypes.DtsHdMaster => Strings.String_TsCheck_Stream_DtsHdMaster,
            TsStreamTypes.Eac3Atsc => Strings.String_TsCheck_Stream_Eac3,
            TsStreamTypes.Eac3Secondary => Strings.String_TsCheck_Stream_Eac3Secondary,
            TsStreamTypes.Dirac => Strings.String_TsCheck_Stream_Dirac,
            TsStreamTypes.Avs2 => Strings.String_TsCheck_Stream_Avs2,
            TsStreamTypes.Avs3 => Strings.String_TsCheck_Stream_Avs3,
            TsStreamTypes.Av3a => Strings.String_TsCheck_Stream_Av3a,
            TsStreamTypes.Vc1 => Strings.String_TsCheck_Stream_Vc1,
            null => Strings.String_TsCheck_Stream_Unknown,
            _ => string.Format(Strings.String_TsCheck_Stream_Other, streamType)
        };
    }

    public string FormatEventSourceTime(TsCheckEvent item) => FormatTime(item.SourceTimeSeconds, item.IsEstimatedTime);

    public string FormatEventZeroBasedTime(TsCheckEvent item) => FormatTime(item.TimeSeconds, item.IsEstimatedTime);

    public string FormatPidDescription(TsCheckEvent item, TsCheckResult? result)
    {
        TsCheckProgramSummary? pmtProgram = null;
        if (result is not null)
        {
            foreach (var program in result.Programs.Values)
            {
                if (program.PmtPid == item.Pid)
                {
                    pmtProgram = program;
                    break;
                }
            }
        }

        if (result?.Pids.TryGetValue(item.Pid, out var pid) == true)
            return FormatPidDescription(
                item.Pid, pid.ProgramNumber, pid.StreamType, pid.MpegAudioLayer, pid.IsPcrPid, pid.IsPmtPid);

        return FormatPidDescription(
            item.Pid, pmtProgram?.ProgramNumber, null, null, false, pmtProgram is not null);
    }

    public string FormatPidDescription(
        int pid, int? programNumber, byte? streamType, TsMpegAudioLayer? mpegAudioLayer,
        bool isPcrPid, bool isPmtPid, bool isGlobal = false)
    {
        if (isGlobal)
            return Strings.String_TsCheck_Pid_Global;
        if (pid < 0)
            return "-";
        if (pid == 0)
            return Strings.String_TsCheck_Pid_Pat;
        if (pid == 0x1FFF)
            return Strings.String_TsCheck_Pid_Null;

        var program = programNumber is { } number
            ? string.Format(Strings.String_TsCheck_Pid_Program, number)
            : null;
        var stream = streamType is not null ? FormatStreamType(streamType, mpegAudioLayer) : null;
        var pmt = isPmtPid ? Strings.String_TsCheck_Pid_Pmt : null;
        var pcr = isPcrPid ? Strings.String_TsCheck_Pid_Pcr : null;
        var description = JoinParts(program, stream, pmt, pcr);
        return description.Length > 0 ? description : Strings.String_TsCheck_Pid_Other;
    }

    private string FormatTime(double? seconds, bool estimated) => seconds is { } value
        ? $"{TsCheckEvent.FormatTime(value)}{(estimated ? "*" : string.Empty)}"
        : Strings.String_TsCheck_Time_Unknown;

    private static string JoinParts(params string?[] parts)
    {
        var result = string.Empty;
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part))
                continue;
            result = result.Length == 0 ? part : result + " · " + part;
        }
        return result;
    }

    public string FormatVerdict(TsCheckResult result) => result.Verdict switch
    {
        TsCheckVerdict.Pass => Strings.String_TsCheck_Verdict_Pass,
        TsCheckVerdict.Warning => Strings.String_TsCheck_Verdict_Warning,
        TsCheckVerdict.Error => Strings.String_TsCheck_Verdict_Error,
        _ => Strings.String_TsCheck_Verdict_Incomplete
    };
}
