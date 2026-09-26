using System;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.Utils;

internal static class TsAnomalyNavigationUtil
{
    internal const double SampleContextSeconds = 15;
    private const long FallbackContextBytes = 16L * 1024 * 1024;

    public static TsPacketRange CalculateSampleRange(TsCheckResult result, TsCheckEvent item)
    {
        var packetsFromFileSize = result.SyncOffset >= 0
            ? Math.Max(0, (result.FileSize - result.SyncOffset) / TsUtil.TsPacketSize)
            : 0;
        var totalPackets = Math.Max(result.PacketCount, packetsFromFileSize);
        if (totalPackets == 0)
            return new TsPacketRange(0, 0);

        var lastPacket = totalPackets - 1;
        var eventStart = Math.Clamp(item.StartPacket, 0, lastPacket);
        var eventEnd = Math.Clamp(Math.Max(item.StartPacket, item.EndPacket), eventStart, lastPacket);
        var packetsPerSecond = EstimatePacketsPerSecond(result);
        var contextPackets = packetsPerSecond > 0
            ? (long)Math.Min(totalPackets, Math.Ceiling(packetsPerSecond * SampleContextSeconds))
            : Math.Min(totalPackets, (FallbackContextBytes + TsUtil.TsPacketSize - 1) / TsUtil.TsPacketSize);

        // 合并后的异常可能横跨大量包，范围必须覆盖完整事件，再分别向前、向后补充上下文。
        return new TsPacketRange(
            Math.Max(0, eventStart - contextPackets),
            Math.Min(lastPacket, eventEnd + contextPackets));
    }

    private static double EstimatePacketsPerSecond(TsCheckResult result)
    {
        double totalBitrate = 0;
        foreach (var pid in result.Pids.Values)
        {
            if (double.IsFinite(pid.Bitrate) && pid.Bitrate > 0)
                totalBitrate += pid.Bitrate;
        }
        if (totalBitrate > 0)
            return totalBitrate / (TsUtil.TsPacketSize * 8.0);

        double timelinePackets = 0;
        double timelineSeconds = 0;
        foreach (var bucket in result.Timeline)
        {
            if (!double.IsFinite(bucket.DurationSeconds) || bucket.DurationSeconds <= 0 ||
                !double.IsFinite(bucket.TotalPacketCount) || bucket.TotalPacketCount <= 0)
            {
                continue;
            }
            timelinePackets += bucket.TotalPacketCount;
            timelineSeconds += bucket.DurationSeconds;
        }
        return timelineSeconds > 0 ? timelinePackets / timelineSeconds : 0;
    }
}

internal readonly record struct TsPacketRange(long StartPacket, long EndPacket);
