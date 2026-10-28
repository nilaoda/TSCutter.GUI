using Xunit;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.TransportStream.Analysis;

namespace TSCutter.GUI.Tests.Files;

public sealed class CommonUtilCopyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ByteRangeCopyRunsOutsideCallerContextAndReportsExactBytes(bool check)
    {
        var path = Path.Combine(Path.GetTempPath(), $"raw-copy-{Guid.NewGuid():N}");
        var output = path + ".out";
        try
        {
            var data = new byte[8 * 1024 * 1024 + 17];
            new Random(42).NextBytes(data);
            await File.WriteAllBytesAsync(path, data);
            var callerContext = new SynchronizationContext();
            var previousContext = SynchronizationContext.Current;
            long reportedBytes = 0;
            var usedCallerContext = false;
            Task copy;
            var session = check ? new ExportTsCheckSession(output) : null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(callerContext);
                copy = CommonUtil.CopyFileAsync(new FileInfo(path), output, 7, data.Length - 3,
                    (_, bytes) =>
                    {
                        usedCallerContext |= ReferenceEquals(SynchronizationContext.Current, callerContext);
                        reportedBytes += bytes;
                    }, checkSession: session);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
            await copy;
            session?.Complete();
            if (session is not null)
            {
                Assert.Equal(TsExportCheckState.Completed, session.State);
                Assert.Equal(data.Length - 10, session.Result!.FileSize);
            }

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
    public async Task CancellingAfterTheFirstBlockStopsWritingWithoutACompleteCheck()
    {
        var path = Path.Combine(Path.GetTempPath(), $"export-cancel-{Guid.NewGuid():N}.ts");
        var output = path + ".out";
        var session = new ExportTsCheckSession(output);
        try
        {
            var data = new byte[9 * 1024 * 1024 + 17];
            new Random(42).NextBytes(data);
            await File.WriteAllBytesAsync(path, data);
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                CommonUtil.CopyFileAsync(new FileInfo(path), output,
                    progress: (_, _) => cancellation.Cancel(), cancellationToken: cancellation.Token, checkSession: session));
            session.Abort();
            Assert.Equal(TsExportCheckState.Aborted, session.State);
            Assert.Equal(4 * 1024 * 1024, new FileInfo(output).Length);
            Assert.Equal(data[..(4 * 1024 * 1024)], await File.ReadAllBytesAsync(output));
            Assert.Equal(4 * 1024 * 1024, session.Result!.FileSize);
        }
        finally
        {
            File.Delete(path);
            File.Delete(output);
        }
    }

    [Fact]
    public async Task ShortenedSourceDoesNotBecomeASuccessfulPartialCopy()
    {
        var path = Path.Combine(Path.GetTempPath(), $"short-copy-{Guid.NewGuid():N}");
        var output = path + ".out";
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            await File.WriteAllBytesAsync(output, [4, 5, 6]);
            var session = new ExportTsCheckSession(output);
            await Assert.ThrowsAsync<EndOfStreamException>(() => CommonUtil.CopyFileAsync(
                new FileInfo(path), output, 0, 10, checkSession: session));
            session.Abort();
            Assert.Equal(TsExportCheckState.Aborted, session.State);
            Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(output));
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
