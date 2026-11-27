using TSCutter.GUI.Utils.Media;
using Xunit;

namespace TSCutter.GUI.Tests.Media;

public class TimecodeUtilTests
{
    [Fact]
    public void ParsesMediaTimecodeAndMapsElapsedTime()
    {
        var timecode = TimecodeUtil.TryParse("17:30:47:10", 25);
        Assert.NotNull(timecode);
        Assert.Equal("17:30:47:10", timecode.Value.StartText);
        Assert.Equal("17:30:47:10", timecode.Value.Format(0));
        Assert.Equal("17:30:48:10", timecode.Value.Format(1));
        Assert.Equal("17:34:01:06", timecode.Value.Format(193.84));
        Assert.Equal("00:03:13:21", timecode.Value.FormatDuration(193.84));
    }

    [Fact]
    public void AcceptsSemicolonFrameSeparatorAndFractionalRate()
    {
        var timecode = TimecodeUtil.TryParse("01:00:00;29", 30000.0 / 1001);
        Assert.NotNull(timecode);
        Assert.Equal("01:00:00;29", timecode.Value.StartText);
        Assert.Equal(30, timecode.Value.FramesPerSecond);
    }

    [Fact]
    public void HandlesDropFrameTimecode()
    {
        var timecode = TimecodeUtil.TryParse("00:00:00;00", 30000.0 / 1001)!.Value;
        Assert.True(timecode.DropFrame);
        Assert.Equal("00:00:00;00", timecode.StartText);
        Assert.Equal("00:01:00;02", timecode.Format(60.06));
        Assert.Equal("01:00:00;00", timecode.Format(3600.0));
        Assert.True(TimecodeUtil.TryParseElapsed("00:01:00;02", timecode, out var elapsed));
        Assert.Equal(60.06, elapsed, 2);
    }

    [Fact]
    public void ConvertsEnteredTimecodeBackToElapsedSeconds()
    {
        var timecode = TimecodeUtil.TryParse("17:30:47:10", 25)!.Value;
        Assert.True(TimecodeUtil.TryParseElapsed("17:30:48:10", timecode, out var elapsed));
        Assert.Equal(1.0, elapsed, 6);
        Assert.True(TimecodeUtil.TryParseElapsed("17:34:01:06", timecode, out elapsed));
        Assert.Equal(193.84, elapsed, 6);
        Assert.False(TimecodeUtil.TryParseElapsed("17:30:46:10", timecode, out _));
        Assert.False(TimecodeUtil.TryParseElapsed("00:00:00.000", timecode, out _));
    }

    [Theory]
    [InlineData("17:30:47", 25)]
    [InlineData("17:30:47:99", 25)]
    [InlineData("bad", 25)]
    [InlineData("", 25)]
    [InlineData("17:30:47:10", 0)]
    public void RejectsInvalidTimecode(string text, double frameRate) =>
        Assert.Null(TimecodeUtil.TryParse(text, frameRate));
}
