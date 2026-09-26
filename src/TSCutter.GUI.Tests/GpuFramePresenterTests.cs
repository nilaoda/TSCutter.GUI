using TSCutter.GUI.Rendering;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class GpuFramePresenterTests
{
    [Fact]
    public void NullPresenter_AlwaysFallsBackWithoutOwningFrame()
    {
        using var presenter = new NullGpuFramePresenter("test");
        var accepted = presenter.TryPresent(null!, out var lease);

        Assert.False(accepted);
        Assert.Null(lease);
        Assert.False(presenter.Capabilities.IsAvailable);
        Assert.Equal("test", presenter.Capabilities.UnavailableReason);
    }
}
