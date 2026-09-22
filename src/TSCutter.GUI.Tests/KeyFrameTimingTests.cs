using Sdcb.FFmpeg.Raw;
using TSCutter.GUI.Models;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class KeyFrameTimingTests
{
    [Theory]
    [InlineData(126_000)]
    [InlineData(-90_000)]
    public void MissingSamplesUseStreamStartAndOneSecondInterval(long start)
    {
        Assert.Equal((start, 90_000L), VideoInstance.ResolveKeyFrameTiming([], start, 1, 90_000));
    }

    [Fact]
    public void UnknownStartFallsBackToZero()
    {
        Assert.Equal((0L, 90_000L), VideoInstance.ResolveKeyFrameTiming([], ffmpeg.AV_NOPTS_VALUE, 1, 90_000));
    }

    [Fact]
    public void PartialSamplesKeepFirstKeyFrameTimestamp()
    {
        Assert.Equal((180_000L, 90_000L), VideoInstance.ResolveKeyFrameTiming([180_000], 0, 1, 90_000));
    }

    [Fact]
    public void LongGopIntervalIsPreserved()
    {
        Assert.Equal((126_000L, 90_000_000L),
            VideoInstance.ResolveKeyFrameTiming([126_000, 90_126_000], 0, 1, 90_000));
    }

    [Theory]
    [InlineData(1, 1_000, 1_000)]
    [InlineData(1_001, 30_000, 30)]
    [InlineData(0, 0, 1)]
    public void DuplicateTimestampsUseNonzeroIntervalInStreamTimeBase(int numerator, int denominator, long gap)
    {
        Assert.Equal((126_000L, gap),
            VideoInstance.ResolveKeyFrameTiming([126_000, 126_000], 0, numerator, denominator));
    }
}
