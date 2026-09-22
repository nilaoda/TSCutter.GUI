using System;
using System.Threading;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.Utils;

internal sealed class MediaReadDeadline(
    TimeSpan timeout,
    CancellationToken cancellationToken,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly long startedAt = (timeProvider ?? TimeProvider.System).GetTimestamp();

    public bool ShouldInterrupt => cancellationToken.IsCancellationRequested ||
        clock.GetElapsedTime(startedAt) >= timeout;

    public void ThrowIfInterrupted()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ShouldInterrupt)
            throw new MediaReadTimeoutException();
    }
}
