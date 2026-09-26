using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class TsAnomalyNavigationUtilTests
{
    [Fact]
    public void SampleRangeUsesMeasuredTransportBitrate()
    {
        var result = CreateResult(100_000);
        result.Pids.Add(0x0100, new TsCheckPidSummary
        {
            Pid = 0x0100,
            Bitrate = TsUtil.TsPacketSize * 8 * 1_000
        });
        var item = CreateEvent(50_000, 50_010);

        var range = TsAnomalyNavigationUtil.CalculateSampleRange(result, item);

        Assert.Equal(35_000, range.StartPacket);
        Assert.Equal(65_010, range.EndPacket);
    }

    [Fact]
    public void SampleRangeFallsBackToTimelinePacketRate()
    {
        var result = CreateResult(100_000);
        result.Timeline.Add(new TsCheckTimelineBucket
        {
            StartSeconds = 0,
            DurationSeconds = 10,
            TotalPacketCount = 20_000,
            Segment = 0
        });
        var item = CreateEvent(50_000, 50_010);

        var range = TsAnomalyNavigationUtil.CalculateSampleRange(result, item);

        Assert.Equal(20_000, range.StartPacket);
        Assert.Equal(80_010, range.EndPacket);
    }

    [Fact]
    public void SampleRangeCoversMergedEventAndClampsToFile()
    {
        var result = CreateResult(20_000);
        result.Pids.Add(0x0100, new TsCheckPidSummary
        {
            Pid = 0x0100,
            Bitrate = TsUtil.TsPacketSize * 8 * 1_000
        });
        var item = CreateEvent(500, 19_500);

        var range = TsAnomalyNavigationUtil.CalculateSampleRange(result, item);

        Assert.Equal(0, range.StartPacket);
        Assert.Equal(19_999, range.EndPacket);
    }

    [Fact]
    public void SampleRangeUsesBoundedByteFallbackWithoutClockData()
    {
        const long totalPackets = 500_000;
        var result = CreateResult(totalPackets);
        var item = CreateEvent(200_000, 200_010);
        var fallbackPackets = (16L * 1024 * 1024 + TsUtil.TsPacketSize - 1) / TsUtil.TsPacketSize;

        var range = TsAnomalyNavigationUtil.CalculateSampleRange(result, item);

        Assert.Equal(200_000 - fallbackPackets, range.StartPacket);
        Assert.Equal(200_010 + fallbackPackets, range.EndPacket);
    }

    [Fact]
    public void SampleRangeCanExtendPastCancelledScanPosition()
    {
        var result = CreateResult(100_000);
        result.PacketCount = 50_000;
        result.Pids.Add(0x0100, new TsCheckPidSummary
        {
            Pid = 0x0100,
            Bitrate = TsUtil.TsPacketSize * 8 * 1_000
        });
        var item = CreateEvent(49_000, 49_010);

        var range = TsAnomalyNavigationUtil.CalculateSampleRange(result, item);

        Assert.Equal(34_000, range.StartPacket);
        Assert.Equal(64_010, range.EndPacket);
    }

    private static TsCheckResult CreateResult(long packetCount) => new()
    {
        FilePath = "sample.ts",
        FileSize = packetCount * TsUtil.TsPacketSize,
        SyncOffset = 0,
        PacketCount = packetCount
    };

    private static TsCheckEvent CreateEvent(long startPacket, long endPacket) => new()
    {
        Severity = TsCheckSeverity.Error,
        Type = TsCheckEventType.ContinuityGap,
        Pid = 0x0100,
        StartPacket = startPacket,
        EndPacket = endPacket,
        FileOffset = startPacket * TsUtil.TsPacketSize,
        MessageCode = TsCheckMessageCode.ContinuityGap
    };
}
