using System.Reflection;
using System.Threading;
using Avalonia.Headless;
using TSCutter.GUI.Controls;
using TSCutter.GUI.Utils.Media;
using Xunit;

namespace TSCutter.GUI.Tests.UI;

public class TimelineTimecodeTests
{
    [Fact]
    public async Task RulerUsesTimecodeOnlyWhenEnabled()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var control = new TimelineControl
            {
                UseTimecode = true,
                Timecode = TimecodeUtil.TryParse("17:30:47:10", 25)!.Value
            };
            var format = typeof(TimelineControl).GetMethod("FormatRulerTime",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Equal("17:30:47:10", (string)format.Invoke(control, [0.0])!);
            Assert.Equal("17:30:48:10", (string)format.Invoke(control, [1.0])!);

            control.UseTimecode = false;
            Assert.Equal("00:00", (string)format.Invoke(control, [0.0])!);
        }, CancellationToken.None);
    }
}
