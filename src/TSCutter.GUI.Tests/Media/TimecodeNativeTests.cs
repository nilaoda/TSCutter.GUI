using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Utils.Media;
using Xunit;

namespace TSCutter.GUI.Tests.Media;

public class TimecodeNativeTests
{
    [NativeRuntimeFact]
    public void ReadsTimecodeFromFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Application", "Fixtures", "timecode.mp4");
        using var format = FormatContext.OpenInputUrl(path);
        format.LoadStreamInfo();
        var timecode = TimecodeUtil.Read(format, format.GetVideoStream());
        Assert.NotNull(timecode);
        Assert.Equal("17:30:47:10", timecode.Value.StartText);
        Assert.Equal(25, timecode.Value.FrameRate);
        Assert.Equal("17:30:48:10", timecode.Value.Format(1.0));
    }
}
