using Avalonia.Controls.ApplicationLifetimes;
using TSCutter.GUI.Services.Application;
using Xunit;

namespace TSCutter.GUI.Tests.Application;

public sealed class MacOSShutdownHandlerTests
{
    [Fact]
    public void NativeRequestsAreDeferredAndCoalescedAndAllowLaterRequests()
    {
        var posted = new Queue<Action>();
        var attempts = 0;
        MacOSShutdownHandler? handler = null;
        handler = new MacOSShutdownHandler(posted.Enqueue, () =>
        {
            attempts++;
            var retry = new ShutdownRequestedEventArgs();
            handler!.OnShutdownRequested(null, retry);
            Assert.False(retry.Cancel);
        });

        for (var request = 0; request < 3; request++)
        {
            var native = new ShutdownRequestedEventArgs();
            handler.OnShutdownRequested(null, native);
            Assert.True(native.Cancel);
        }
        Assert.Equal(0, attempts);
        Assert.Single(posted);
        posted.Dequeue()();
        Assert.Equal(1, attempts);
        Assert.Empty(posted);

        var next = new ShutdownRequestedEventArgs();
        handler.OnShutdownRequested(null, next);
        Assert.True(next.Cancel);
        Assert.Single(posted);
        posted.Dequeue()();
        Assert.Equal(2, attempts);
        Assert.Empty(posted);
    }

    [Fact]
    public void PreviouslyCancelledRequestDoesNotStartAnotherShutdown()
    {
        var handler = new MacOSShutdownHandler(_ => Assert.Fail("不应安排退出。"),
            () => Assert.Fail("不应执行退出。"));
        var request = new ShutdownRequestedEventArgs { Cancel = true };

        handler.OnShutdownRequested(null, request);

        Assert.True(request.Cancel);
    }
}
