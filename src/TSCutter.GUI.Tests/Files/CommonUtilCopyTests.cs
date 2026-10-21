using Xunit;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Tests.Files;

public sealed class CommonUtilCopyTests
{
    [Fact]
    public async Task ByteRangeCopyRunsOutsideCallerContextAndReportsExactBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"raw-copy-{Guid.NewGuid():N}");
        var output = path + ".out";
        try
        {
            var data = new byte[2 * 1024 * 1024 + 17];
            new Random(42).NextBytes(data);
            await File.WriteAllBytesAsync(path, data);
            var callerContext = new SynchronizationContext();
            var previousContext = SynchronizationContext.Current;
            long reportedBytes = 0;
            var usedCallerContext = false;
            Task copy;
            try
            {
                SynchronizationContext.SetSynchronizationContext(callerContext);
                copy = CommonUtil.CopyFileAsync(new FileInfo(path), output, 7, data.Length - 3,
                    (_, bytes) =>
                    {
                        usedCallerContext |= ReferenceEquals(SynchronizationContext.Current, callerContext);
                        reportedBytes += bytes;
                    });
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
            await copy;

            Assert.False(usedCallerContext);
            Assert.Equal(data.Length - 10, reportedBytes);
            Assert.Equal(data[7..^3], await File.ReadAllBytesAsync(output));
        }
        finally
        {
            File.Delete(path);
            File.Delete(output);
        }
    }

    [Fact]
    public async Task CancelledCopyDoesNotTruncateExistingOutput()
    {
        var path = Path.Combine(Path.GetTempPath(), $"raw-copy-{Guid.NewGuid():N}");
        var output = path + ".out";
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            await File.WriteAllBytesAsync(output, [4, 5, 6]);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                CommonUtil.CopyFileAsync(new FileInfo(path), output, cancellationToken: cancellation.Token));

            Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(output));
        }
        finally
        {
            File.Delete(path);
            File.Delete(output);
        }
    }
}
