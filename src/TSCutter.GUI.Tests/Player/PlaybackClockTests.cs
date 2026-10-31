using System;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Player;
using Xunit;

namespace TSCutter.GUI.Tests.Player;

/// <summary>主时钟的暂停冻结、恢复重锚与等待语义。</summary>
public class PlaybackClockTests
{
    [Fact]
    public async Task PauseFreezesTimeAndResumeRebasesWithoutPausedWallTime()
    {
        var clock = new PlaybackClock();
        clock.Start(0);
        await Task.Delay(120);
        var beforePause = clock.CurrentSeconds;
        Assert.InRange(beforePause, 0.05, 0.4);

        clock.Pause();
        var pausedAt = clock.CurrentSeconds;
        await Task.Delay(100);
        Assert.Equal(pausedAt, clock.CurrentSeconds, 6);

        clock.Resume();
        await Task.Delay(60);
        Assert.True(clock.CurrentSeconds > pausedAt);
        // 暂停期间的时间不计入媒体时间：恢复后不超出暂停值太多。
        Assert.True(clock.CurrentSeconds - pausedAt < 0.3);
    }

    [Fact]
    public void StartReanchorsToTheGivenMediaTime()
    {
        var clock = new PlaybackClock();
        clock.Start(10);
        Assert.Equal(10, clock.CurrentSeconds, 3);
        clock.Start(42.5);
        Assert.Equal(42.5, clock.CurrentSeconds, 3);
        clock.Stop();
        Assert.Equal(42.5, clock.CurrentSeconds, 3);
    }

    [Fact]
    public async Task WaitUntilReturnsWhenDueOrImmediatelyForPastTimes()
    {
        var clock = new PlaybackClock();
        clock.Start(0);
        var pastWait = clock.WaitUntilAsync(-1, CancellationToken.None);
        var timeout = Task.Delay(50);
        Assert.Equal(pastWait, await Task.WhenAny(pastWait, timeout));

        var dueWait = clock.WaitUntilAsync(0.15, CancellationToken.None);
        var completed = await Task.WhenAny(dueWait, Task.Delay(2000));
        Assert.Equal(dueWait, completed);
    }

    [Fact]
    public async Task WaitUntilDoesNotPassWhilePausedAndHonorsCancellation()
    {
        var clock = new PlaybackClock();
        clock.Start(0);
        clock.Pause();
        using var cancellation = new CancellationTokenSource(200);
        var wait = clock.WaitUntilAsync(5, cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }
}
