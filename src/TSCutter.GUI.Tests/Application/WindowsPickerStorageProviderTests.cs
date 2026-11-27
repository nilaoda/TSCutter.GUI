using TSCutter.GUI.Services.Application;
using Xunit;

namespace TSCutter.GUI.Tests.Application;

public sealed class WindowsPickerStorageProviderTests
{
    [Fact]
    public async Task PickerAndNestedLongRunningWorkRunInStaAndPropagateResults()
    {
        if (!OperatingSystem.IsWindows())
            return;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            var result = await WindowsPickerStorageProvider.RunPickerAsync(() =>
            {
                Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                return Task.Factory.StartNew(() =>
                {
                    Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());
                    return 42;
                }, TaskCreationOptions.LongRunning);
            });
            Assert.Equal(42, result);
        }
        await Assert.ThrowsAsync<IOException>(() => WindowsPickerStorageProvider.RunPickerAsync<int>(() =>
            throw new IOException("picker failed")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsPickerStorageProvider.RunPickerAsync(() =>
            Task.FromCanceled<int>(cancellation.Token)));
    }
}
