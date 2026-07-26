using TSCutter.GUI.Models;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class TimelineViewportStateTests
{
    [Fact]
    public void MaximumZoomKeepsUsefulKeyFrameContext()
    {
        var state = new TimelineViewportState();
        state.Reset(3_600, 2);

        state.SetZoomLevel(1, 1_800);

        Assert.Equal(40, state.ViewDuration, 6);
        Assert.Equal(90, state.ZoomFactor, 6);
        Assert.Equal(1_780, state.ViewStart, 6);
    }

    [Fact]
    public void MouseAnchorKeepsItsRelativePositionWhileZooming()
    {
        var state = new TimelineViewportState();
        state.Reset(1_000, 2);
        state.SetZoomLevel(0.35, 500);
        state.ViewStart = 200;
        var anchor = state.ViewStart + state.ViewDuration * 0.25;

        state.SetZoomLevel(0.7, anchor);

        Assert.Equal(0.25, (anchor - state.ViewStart) / state.ViewDuration, 6);
    }

    [Fact]
    public void PanningAndFittingStayInsideTheFullTimeline()
    {
        var state = new TimelineViewportState();
        state.Reset(600, 2);
        state.SetZoomLevel(0.8, 300);

        state.ViewStart = double.MaxValue;
        Assert.Equal(state.ScrollMaximum, state.ViewStart, 6);

        state.ViewStart = double.MinValue;
        Assert.Equal(0, state.ViewStart, 6);

        state.Fit();
        Assert.Equal(0, state.ZoomLevel);
        Assert.Equal(0, state.ViewStart);
        Assert.Equal(600, state.ViewDuration);
        Assert.Equal("1×", state.ZoomFactorText);
    }

    [Fact]
    public void PlayheadNavigationMovesTheViewportOnlyAfterLeavingIt()
    {
        var state = new TimelineViewportState();
        state.Reset(1_000, 2);
        state.SetZoomLevel(0.8, 500);
        var initialStart = state.ViewStart;

        state.SetPlayhead(state.ViewStart + state.ViewDuration * 0.8);
        Assert.Equal(initialStart, state.ViewStart, 6);

        var target = state.ViewEnd + 10;
        state.SetPlayhead(target);
        Assert.True(state.ViewStart > initialStart);
        Assert.Equal(target, state.ViewStart + state.ViewDuration * 0.9, 6);
    }

    [Fact]
    public void VeryShortTimelineCanStillZoomWithoutInvalidBounds()
    {
        var state = new TimelineViewportState();
        state.Reset(0.0005, 0);

        state.SetZoomLevel(1, 0.00025);

        Assert.Equal(0.0005, state.ViewDuration, 8);
        Assert.Equal(0, state.ViewStart, 8);
    }
}
