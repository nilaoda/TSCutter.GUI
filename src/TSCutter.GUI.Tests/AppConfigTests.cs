using TSCutter.GUI.Models;
using Xunit;

namespace TSCutter.GUI.Tests;

public class AppConfigTests
{
    [Fact]
    public void PreferHardwareDecoding_DefaultsToCurrentPlatformCapability()
    {
        Assert.Equal(
            AppConfig.IsHardwareDecodingSupported,
            new AppConfig().PreferHardwareDecoding);
    }

    [Fact]
    public void PreferHardwareDecoding_CannotBeEnabledOnUnsupportedPlatform()
    {
        var config = new AppConfig { PreferHardwareDecoding = true };

        Assert.Equal(AppConfig.IsHardwareDecodingSupported, config.PreferHardwareDecoding);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void NormalizeHardwareDecodingPreference_RequiresRequestAndPlatformSupport(
        bool requested,
        bool supported,
        bool expected)
    {
        Assert.Equal(
            expected,
            AppConfig.NormalizeHardwareDecodingPreference(requested, supported));
    }
}
