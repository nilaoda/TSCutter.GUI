using TSCutter.GUI.Models;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class VideoInstanceDurationTests
{
    [Fact]
    public void UsesVideoStreamDurationWhenAvailable()
    {
        var duration = VideoInstance.ResolveTimelineDuration(
            streamDuration: 900_000,
            timeBaseNumerator: 1,
            timeBaseDenominator: 90_000,
            containerDuration: 12_000_000);

        Assert.Equal(10, duration.Seconds, 6);
        Assert.Equal(900_000, duration.StreamPts);
    }

    [Fact]
    public void FallsBackToContainerDurationInStreamTimeBase()
    {
        var duration = VideoInstance.ResolveTimelineDuration(
            streamDuration: 0,
            timeBaseNumerator: 1,
            timeBaseDenominator: 90_000,
            containerDuration: 462_720_000);

        Assert.Equal(462.72, duration.Seconds, 6);
        Assert.Equal(41_644_800, duration.StreamPts);
    }

    [Theory]
    [InlineData(0, 90_000, 10_000_000)]
    [InlineData(1, 0, 10_000_000)]
    [InlineData(1, 90_000, 0)]
    public void ReturnsZeroWhenNoUsableDurationExists(
        int timeBaseNumerator,
        int timeBaseDenominator,
        long containerDuration)
    {
        var duration = VideoInstance.ResolveTimelineDuration(
            streamDuration: 0,
            timeBaseNumerator,
            timeBaseDenominator,
            containerDuration);

        Assert.Equal(0, duration.Seconds);
        Assert.Equal(0, duration.StreamPts);
    }
}
