using System.Reflection;
using System.Runtime.ExceptionServices;
using TSCutter.GUI.Models.Preview;
using Xunit;

namespace TSCutter.GUI.Tests.Preview;

public sealed class VideoInstanceConcurrencyTests
{
    [Fact]
    public async Task NativeOperationsCannotEnterTheSameDecoderAtOnce()
    {
        using var video = new VideoInstance("unused.mp4");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(() => RunRead(video, () =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return true;
        }));
        Task? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = Task.Run(() =>
            {
                secondStarted.SetResult();
                RunRead(video, () => { secondEntered.SetResult(); return true; });
            });
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            Assert.False(secondEntered.Task.IsCompleted);
        }
        finally
        {
            release.Set();
            await first;
            if (second is not null) await second;
        }
        Assert.True(secondEntered.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CloseWaitsForNativeOperationsAndRejectsLaterRequests()
    {
        using var video = new VideoInstance("unused.mp4");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = Task.Run(() => RunRead(video, () =>
        {
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return true;
        }));
        Task? close = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            close = Task.Run(() => { closeStarted.SetResult(); video.Close(); });
            await closeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(100);
            Assert.False(close.IsCompleted);
        }
        finally
        {
            release.Set();
            await operation;
            if (close is not null) await close;
        }
        Assert.Throws<ObjectDisposedException>(() => RunRead(video, () => throw new Exception("Must not run")));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => video.RefreshCurrentFrameAsync());
    }

    // 用可控操作验证互斥和关闭顺序，无需依赖素材或实际进入 FFmpeg。
    private static bool RunRead(VideoInstance video, Func<bool> operation)
    {
        var method = typeof(VideoInstance).GetMethod("RunReadOperation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(bool));
        try { return (bool)method.Invoke(video, [operation, CancellationToken.None, null])!; }
        catch (TargetInvocationException exception) when (exception.InnerException is { } inner)
        {
            ExceptionDispatchInfo.Capture(inner).Throw();
            throw;
        }
    }
}
