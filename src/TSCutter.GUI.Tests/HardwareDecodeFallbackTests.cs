using TSCutter.GUI.Models;
using Xunit;

namespace TSCutter.GUI.Tests;

public class HardwareDecodeFallbackTests
{
    [Fact]
    public void ResolveHardwareFallbackSeekPts_ForwardNavigationDoesNotUseStaleSeekPosition()
    {
        var result = VideoInstance.ResolveHardwareFallbackSeekPts(
            count: 1,
            anchorPts: 90_000,
            lastSeekPts: 0,
            failurePts: null);

        Assert.Equal(90_001, result);
    }

    [Fact]
    public void ResolveHardwareFallbackSeekPts_PrefersFailedForwardFrame()
    {
        var result = VideoInstance.ResolveHardwareFallbackSeekPts(
            count: 1,
            anchorPts: 90_000,
            lastSeekPts: 0,
            failurePts: 180_000);

        Assert.Equal(180_000, result);
    }

    [Theory]
    [InlineData(89_999, true)]
    [InlineData(90_000, false)]
    [InlineData(90_001, false)]
    public void IsDecodedFrameAtRequestedSide_BackwardRequiresEarlierFrame(long currentPts, bool expected)
    {
        Assert.Equal(
            expected,
            VideoInstance.IsDecodedFrameAtRequestedSide(
                backward: true,
                requireForwardAfterAnchor: false,
                currentPts: currentPts,
                anchorPts: 90_000));
    }

    [Theory]
    [InlineData(89_999, false)]
    [InlineData(90_000, false)]
    [InlineData(90_001, true)]
    public void IsDecodedFrameAtRequestedSide_ForwardFallbackRequiresLaterFrame(long currentPts, bool expected)
    {
        Assert.Equal(
            expected,
            VideoInstance.IsDecodedFrameAtRequestedSide(
                backward: false,
                requireForwardAfterAnchor: true,
                currentPts: currentPts,
                anchorPts: 90_000));
    }
}
