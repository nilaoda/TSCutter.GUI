using Avalonia.Headless;
using TSCutter.GUI.Services.Application.SystemProgress;
using TSCutter.GUI.Tests.UI;
using Xunit;

namespace TSCutter.GUI.Tests.Application;

[Collection("Avalonia UI")]
public class SystemProgressTrackerTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void IdleTrackerShowsNothing()
    {
        var tracker = new SystemProgressTracker();
        Assert.True(tracker.IsIdle);
        Assert.Equal(SystemProgressSnapshot.None, tracker.GetSnapshot());
    }

    [Fact]
    public void NewOperationIsIndeterminateUntilFirstReport()
    {
        var tracker = new SystemProgressTracker();
        using var operation = tracker.Begin();
        Assert.Equal(SystemProgressState.Indeterminate, tracker.GetSnapshot().State);

        operation.ReportPercent(25);
        Assert.Equal(new SystemProgressSnapshot(SystemProgressState.Normal, 0.25), tracker.GetSnapshot());

        operation.SetIndeterminate();
        Assert.Equal(SystemProgressState.Indeterminate, tracker.GetSnapshot().State);
    }

    [Fact]
    public void ReportClampsAndIgnoresNaN()
    {
        var tracker = new SystemProgressTracker();
        using var operation = tracker.Begin();
        operation.Report(1.5);
        Assert.Equal(1, tracker.GetSnapshot().Value);
        operation.Report(-1);
        Assert.Equal(0, tracker.GetSnapshot().Value);
        operation.Report(double.NaN);
        Assert.Equal(0, tracker.GetSnapshot().Value);
    }

    [Fact]
    public void ConcurrentOperationsAverageDeterminateValues()
    {
        var tracker = new SystemProgressTracker();
        using var first = tracker.Begin();
        using var second = tracker.Begin();
        using var unknown = tracker.Begin();
        first.Report(0.2);
        second.Report(0.6);

        var snapshot = tracker.GetSnapshot();
        Assert.Equal(SystemProgressState.Normal, snapshot.State);
        Assert.Equal(0.4, snapshot.Value, 6);
    }

    [Fact]
    public void CompletingOrCancellingClearsImmediately()
    {
        var tracker = new SystemProgressTracker();
        var completed = tracker.Begin();
        completed.Report(0.5);
        completed.Complete();
        Assert.True(tracker.IsIdle);

        var cancelled = tracker.Begin();
        cancelled.Report(0.5);
        cancelled.Cancel();
        Assert.Equal(SystemProgressSnapshot.None, tracker.GetSnapshot());

        var disposed = tracker.Begin();
        disposed.Dispose();
        Assert.True(tracker.IsIdle);
    }

    [Fact]
    public void FirstOutcomeWins()
    {
        var tracker = new SystemProgressTracker(isAppActive: () => false);
        var operation = tracker.Begin();
        operation.Complete();
        operation.Fail();
        operation.Report(0.5);
        Assert.True(tracker.IsIdle);
    }

    [Fact]
    public void FailureInBackgroundStaysUntilAppIsActivated()
    {
        var time = new ManualTime();
        var tracker = new SystemProgressTracker(time, () => false);
        var operation = tracker.Begin();
        operation.Report(0.7);
        operation.Fail();

        time.Now += TimeSpan.FromHours(1);
        Assert.Equal(new SystemProgressSnapshot(SystemProgressState.Error, 0.7), tracker.GetSnapshot());
        Assert.False(tracker.IsIdle);

        tracker.NotifyAppActivated();
        Assert.True(tracker.IsIdle);
    }

    [Fact]
    public void FailureInForegroundExpiresAfterShortDelay()
    {
        var time = new ManualTime();
        var tracker = new SystemProgressTracker(time, () => true);
        tracker.Begin().Fail();
        Assert.Equal(SystemProgressState.Error, tracker.GetSnapshot().State);

        time.Now += SystemProgressTracker.ForegroundFailureDuration - TimeSpan.FromMilliseconds(1);
        Assert.Equal(SystemProgressState.Error, tracker.GetSnapshot().State);

        time.Now += TimeSpan.FromMilliseconds(1);
        Assert.Equal(SystemProgressSnapshot.None, tracker.GetSnapshot());
    }

    [Fact]
    public void ShortOperationsStayHiddenUntilShowDelayPasses()
    {
        var time = new ManualTime();
        var tracker = new SystemProgressTracker(time, () => false, TimeSpan.FromMilliseconds(500));
        var quick = tracker.Begin();
        quick.Report(0.5);
        Assert.Equal(SystemProgressSnapshot.None, tracker.GetSnapshot());
        Assert.False(tracker.IsIdle);
        quick.Complete();
        Assert.True(tracker.IsIdle);

        using var slow = tracker.Begin();
        slow.Report(0.4);
        time.Now += TimeSpan.FromMilliseconds(500);
        Assert.Equal(new SystemProgressSnapshot(SystemProgressState.Normal, 0.4), tracker.GetSnapshot());

        // 失败不受延迟影响，即使操作很快就失败也要显示。
        tracker.Begin().Fail();
        Assert.Equal(SystemProgressState.Error, tracker.GetSnapshot().State);
    }

    [Fact]
    public void FailWithCancellationExceptionCountsAsCancel()
    {
        var tracker = new SystemProgressTracker(isAppActive: () => false);
        tracker.Begin().Fail(new OperationCanceledException());
        Assert.True(tracker.IsIdle);

        tracker.Begin().Fail(new TaskCanceledException());
        Assert.True(tracker.IsIdle);

        tracker.Begin().Fail(new IOException());
        Assert.Equal(SystemProgressState.Error, tracker.GetSnapshot().State);
    }

    [Fact]
    public void FailureWithoutProgressShowsFullErrorBar()
    {
        var tracker = new SystemProgressTracker(isAppActive: () => false);
        tracker.Begin().Fail();
        Assert.Equal(new SystemProgressSnapshot(SystemProgressState.Error, 1), tracker.GetSnapshot());
    }

    [Fact]
    public void NewOperationClearsPendingFailures()
    {
        var tracker = new SystemProgressTracker(isAppActive: () => false);
        tracker.Begin().Fail();
        using var next = tracker.Begin();
        next.Report(0.1);
        Assert.Equal(new SystemProgressSnapshot(SystemProgressState.Normal, 0.1), tracker.GetSnapshot());
    }

    [Fact]
    public void ErrorWhileRunningKeepsAdvancing()
    {
        var tracker = new SystemProgressTracker();
        using var batch = tracker.Begin();
        batch.Report(0.3);
        batch.MarkError();
        Assert.Equal(new SystemProgressSnapshot(SystemProgressState.Error, 0.3), tracker.GetSnapshot());

        batch.Report(0.8);
        Assert.Equal(new SystemProgressSnapshot(SystemProgressState.Error, 0.8), tracker.GetSnapshot());
    }

    [Fact]
    public void ErrorTakesPriorityOverOtherOperations()
    {
        var tracker = new SystemProgressTracker(isAppActive: () => false);
        using var running = tracker.Begin();
        running.Report(0.5);
        var failing = tracker.Begin();
        failing.Fail();
        Assert.Equal(new SystemProgressSnapshot(SystemProgressState.Error, 0.5), tracker.GetSnapshot());
    }

    [Fact]
    public void PausedOnlyWhenEveryOperationIsPaused()
    {
        var tracker = new SystemProgressTracker();
        using var first = tracker.Begin();
        using var second = tracker.Begin();
        first.Report(0.2);
        second.Report(0.4);
        first.SetPaused(true);
        Assert.Equal(SystemProgressState.Normal, tracker.GetSnapshot().State);

        second.SetPaused(true);
        var snapshot = tracker.GetSnapshot();
        Assert.Equal(SystemProgressState.Paused, snapshot.State);
        Assert.Equal(0.3, snapshot.Value, 6);

        first.SetPaused(false);
        Assert.Equal(SystemProgressState.Normal, tracker.GetSnapshot().State);
    }

    [Fact]
    public void OwnerSnapshotOnlyIncludesThatOwner()
    {
        var tracker = new SystemProgressTracker(isAppActive: () => false);
        var ownerA = new object();
        var ownerB = new object();
        using var a = tracker.Begin(ownerA);
        using var b = tracker.Begin(ownerB);
        a.Report(0.2);
        b.Report(0.8);

        Assert.Equal(new SystemProgressSnapshot(SystemProgressState.Normal, 0.2), tracker.GetSnapshot(ownerA));
        Assert.Equal(new SystemProgressSnapshot(SystemProgressState.Normal, 0.8), tracker.GetSnapshot(ownerB));
        Assert.Equal(SystemProgressSnapshot.None, tracker.GetSnapshot(new object()));

        b.Fail();
        Assert.Equal(SystemProgressState.Normal, tracker.GetSnapshot(ownerA).State);
        Assert.Equal(SystemProgressState.Error, tracker.GetSnapshot(ownerB).State);
        Assert.Equal(SystemProgressState.Error, tracker.GetSnapshot().State);
    }

    [Fact]
    public void ChangedIsRaisedForStateChangesButNotValueUpdates()
    {
        var tracker = new SystemProgressTracker();
        var count = 0;
        tracker.Changed += () => count++;

        var operation = tracker.Begin();
        Assert.Equal(1, count);
        operation.Report(0.1);
        Assert.Equal(2, count);
        operation.Report(0.2);
        operation.Report(0.3);
        Assert.Equal(2, count);
        operation.MarkError();
        operation.MarkError();
        Assert.Equal(3, count);
        operation.Complete();
        Assert.Equal(4, count);
    }

    [Fact]
    public void ConcurrentReportsFromManyThreadsAreSafe()
    {
        var tracker = new SystemProgressTracker();
        var operations = Enumerable.Range(0, 8).Select(_ => tracker.Begin()).ToArray();
        Parallel.For(0, 8, index =>
        {
            for (var step = 0; step <= 1000; step++)
            {
                operations[index].Report(step / 1000.0);
                _ = tracker.GetSnapshot();
            }
            operations[index].Complete();
        });
        Assert.True(tracker.IsIdle);
    }

    [Fact]
    public async Task DockBarRendersDistinctImagesPerState()
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        var images = await session.Dispatch(() => new[]
        {
            DockProgressBarRenderer.Render(new SystemProgressSnapshot(SystemProgressState.Normal, 0.3), 128),
            DockProgressBarRenderer.Render(new SystemProgressSnapshot(SystemProgressState.Normal, 0.8), 128),
            DockProgressBarRenderer.Render(new SystemProgressSnapshot(SystemProgressState.Error, 0.8), 128),
            DockProgressBarRenderer.Render(new SystemProgressSnapshot(SystemProgressState.Paused, 0.8), 128),
            DockProgressBarRenderer.Render(new SystemProgressSnapshot(SystemProgressState.Indeterminate, 0), 128),
        }, CancellationToken.None);

        byte[] pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        Assert.All(images, image => Assert.Equal(pngSignature, image[..8]));
        Assert.Equal(images.Length, images.Select(Convert.ToBase64String).Distinct().Count());
    }

    [Fact]
    public void SnapshotComparisonIgnoresTinyChanges()
    {
        var a = new SystemProgressSnapshot(SystemProgressState.Normal, 0.5000);
        Assert.True(a.LooksSameAs(new SystemProgressSnapshot(SystemProgressState.Normal, 0.50004)));
        Assert.False(a.LooksSameAs(new SystemProgressSnapshot(SystemProgressState.Normal, 0.502)));
        Assert.False(a.LooksSameAs(new SystemProgressSnapshot(SystemProgressState.Error, 0.5)));
    }
}
