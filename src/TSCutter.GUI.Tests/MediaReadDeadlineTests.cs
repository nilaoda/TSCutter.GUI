using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class MediaReadDeadlineTests
{
    [Fact]
    public void MoreThanTwentyThousandPacketsDoNotExhaustTheDeadline()
    {
        var clock = new ManualTimeProvider();
        var deadline = new MediaReadDeadline(TimeSpan.FromSeconds(30), default, clock);
        for (var packet = 0; packet < 100_000; packet++)
            deadline.ThrowIfInterrupted();
        Assert.False(deadline.ShouldInterrupt);
    }

    [Fact]
    public void TimeoutDependsOnElapsedTimeAndRemainsExpiredAcrossRetries()
    {
        var clock = new ManualTimeProvider();
        var deadline = new MediaReadDeadline(TimeSpan.FromSeconds(30), default, clock);
        clock.Advance(TimeSpan.FromSeconds(29));
        deadline.ThrowIfInterrupted();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(deadline.ShouldInterrupt);
        Assert.Throws<MediaReadTimeoutException>(deadline.ThrowIfInterrupted);
        Assert.Throws<MediaReadTimeoutException>(deadline.ThrowIfInterrupted);

        var nextOperation = new MediaReadDeadline(TimeSpan.FromSeconds(30), default, clock);
        nextOperation.ThrowIfInterrupted();
        Assert.False(nextOperation.ShouldInterrupt);
    }

    [Fact]
    public void CancellationInterruptsImmediatelyAndTakesPrecedenceOverTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        var clock = new ManualTimeProvider();
        var deadline = new MediaReadDeadline(TimeSpan.FromSeconds(30), cancellation.Token, clock);
        cancellation.Cancel();
        Assert.True(deadline.ShouldInterrupt);
        Assert.Equal(cancellation.Token,
            Assert.Throws<OperationCanceledException>(deadline.ThrowIfInterrupted).CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Throws<OperationCanceledException>(deadline.ThrowIfInterrupted);
    }

    [Fact]
    public void CancelledInitializationDoesNotOpenTheFile()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var video = new VideoInstance("nonexistent.ts");
        Assert.Throws<OperationCanceledException>(() => video.InitVideo(cancellation.Token));
        Assert.False(video.Inited);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public void Advance(TimeSpan duration) => timestamp += duration.Ticks;
    }
}
