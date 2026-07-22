using System;
using System.IO;
using System.Linq;
using System.Text;
using TSCutter.GUI.Models;
using TSCutter.GUI.Services;

namespace TSCutter.GUI.Utils;

public sealed class TsCheckReportBuilder(TsCheckTextFormatter text)
{
    public string Build(TsCheckResult result)
    {
        var builder = new StringBuilder(32 * 1024);
        var strings = text.Strings;
        builder.AppendLine(strings.String_TsCheck_Report_Title);
        builder.AppendLine(new string('=', 72));
        AppendField(builder, strings.String_TsCheck_Report_File, result.FilePath);
        AppendField(builder, strings.String_TsCheck_Report_FileSize, CommonUtil.FormatFileSize(result.FileSize));
        AppendField(builder, strings.String_TsCheck_Report_Generated, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        builder.AppendLine();

        AppendSection(builder, strings.String_TsCheck_Report_Summary);
        AppendField(builder, strings.String_TsCheck_Report_Verdict, text.FormatVerdict(result));
        AppendField(builder, strings.String_TsCheck_Report_PacketSize,
            string.Format(strings.String_TsCheck_Report_Bytes, TsStreamAnalyzer.PacketSize));
        AppendField(builder, strings.String_TsCheck_Report_SyncOffset, result.SyncOffset.ToString("N0"));
        AppendField(builder, strings.String_TsCheck_Report_Packets, result.PacketCount.ToString("N0"));
        AppendField(builder, strings.String_TsCheck_Report_Errors, result.ErrorCount.ToString("N0"));
        AppendField(builder, strings.String_TsCheck_Report_Warnings, result.WarningCount.ToString("N0"));
        AppendField(builder, strings.String_TsCheck_Report_Elapsed, result.Elapsed.ToString(@"hh\:mm\:ss\.fff"));
        AppendField(builder, strings.String_TsCheck_Report_AverageSpeed,
            $"{CommonUtil.FormatFileSize(result.BytesScanned / Math.Max(0.001, result.Elapsed.TotalSeconds))}/s");
        if (result.FirstBroadcastTime is { } firstBroadcastTime)
        {
            AppendField(builder, strings.String_TsCheck_Report_BroadcastTime,
                text.FormatBroadcastTime(firstBroadcastTime, result.LastBroadcastTime));
        }
        builder.AppendLine();

        if (result.Timeline.Count > 0)
        {
            var timelineDuration = result.Timeline.Sum(item => item.DurationSeconds);
            var timelinePackets = result.Timeline.Sum(item => item.TotalPacketCount);
            var averageBitrate = timelinePackets * TsStreamAnalyzer.PacketSize * 8 /
                                 Math.Max(0.001, timelineDuration);
            var peakBitrate = result.Timeline.Max(item => item.TotalBitrate);
            AppendSection(builder, strings.String_TsCheck_Report_BitrateTimeline);
            AppendField(builder, strings.String_TsCheck_Report_ReferencePcr,
                result.TimelineReferencePcrPid >= 0 ? $"0x{result.TimelineReferencePcrPid:X4}" : "-");
            AppendField(builder, strings.String_TsCheck_Report_TimelineDuration,
                TsCheckEvent.FormatTime(result.Timeline[^1].EndSeconds));
            AppendField(builder, strings.String_TsCheck_Report_TimelineSamples,
                result.Timeline.Count.ToString("N0"));
            AppendField(builder, strings.String_TsCheck_Report_AverageBitrate,
                string.Format(strings.String_TsCheck_Report_BitrateValue, averageBitrate / 1_000_000));
            AppendField(builder, strings.String_TsCheck_Report_PeakBitrate,
                string.Format(strings.String_TsCheck_Report_BitrateValue, peakBitrate / 1_000_000));
            builder.AppendLine();
        }

        AppendSection(builder, strings.String_TsCheck_Report_Programs);
        if (result.Programs.Count == 0)
        {
            builder.AppendLine(strings.String_TsCheck_Report_NoPrograms);
        }
        else
        {
            foreach (var program in result.Programs.Values.OrderBy(item => item.ProgramNumber))
            {
                builder.AppendLine(string.Format(strings.String_TsCheck_Report_ProgramLine, program.ProgramNumber,
                    $"0x{program.PmtPid:X4}", program.PcrPid >= 0 ? $"0x{program.PcrPid:X4}" : "-"));
                foreach (var stream in program.Streams.OrderBy(item => item.Key))
                {
                    result.Pids.TryGetValue(stream.Key, out var pid);
                    builder.AppendLine(string.Format(strings.String_TsCheck_Report_StreamLine,
                        $"0x{stream.Key:X4}", text.FormatStreamType(
                            stream.Value, pid?.MpegAudioLayer, pid?.SupplementaryStreamType, pid?.Language)));
                }
            }
        }
        builder.AppendLine();

        AppendSection(builder, strings.String_TsCheck_Report_PidStatistics);
        builder.AppendLine(strings.String_TsCheck_Report_PidHeader);
        foreach (var pid in result.Pids.Values.OrderBy(item => item.Pid))
        {
            builder.AppendLine($"{pid.PidText,-8} {pid.PacketCount,14:N0} {pid.ContinuityErrors,10:N0} " +
                               $"{pid.TransportErrors,10:N0} {pid.PesSizeErrors,10:N0} {pid.DuplicatePackets,10:N0}  " +
                               text.FormatStreamType(
                                   pid.StreamType, pid.MpegAudioLayer, pid.SupplementaryStreamType, pid.Language));
        }
        builder.AppendLine();

        AppendSection(builder, strings.String_TsCheck_Report_Events);
        if (result.Events.Count == 0)
        {
            builder.AppendLine(strings.String_TsCheck_Report_NoEvents);
        }
        else
        {
            foreach (var item in result.Events)
            {
                var packetRange = item.StartPacket == item.EndPacket
                    ? item.StartPacket.ToString("N0")
                    : $"{item.StartPacket:N0}-{item.EndPacket:N0}";
                builder.AppendLine($"[{text.FormatSeverity(item.Severity)}] {text.FormatEventType(item)}");
                builder.AppendLine(string.Format(strings.String_TsCheck_Report_EventTimes,
                    text.FormatEventSourceTime(item), text.FormatEventZeroBasedTime(item)));
                builder.AppendLine(string.Format(strings.String_TsCheck_Report_EventLocation,
                    item.PidText, text.FormatPidDescription(item, result), packetRange, item.FileOffset));
                builder.AppendLine(text.FormatEventMessage(item));
                if (item.Occurrences > 1)
                    builder.AppendLine(string.Format(strings.String_TsCheck_Report_Occurrences, item.Occurrences));
                builder.AppendLine();
            }
        }

        if (result.OmittedEventCount > 0)
            builder.AppendLine(string.Format(strings.String_TsCheck_Report_Omitted, result.OmittedEventCount));

        return builder.ToString();
    }

    public async System.Threading.Tasks.Task WriteAsync(string path, TsCheckResult result)
    {
        // UTF-8 BOM 便于 Windows 记事本正确识别中英文报告。
        await File.WriteAllTextAsync(path, Build(result), new UTF8Encoding(true));
    }

    private static void AppendSection(StringBuilder builder, string title)
    {
        builder.AppendLine(title);
        builder.AppendLine(new string('-', 72));
    }

    private static void AppendField(StringBuilder builder, string name, string value) =>
        builder.AppendLine($"{name,-24}: {value}");
}
