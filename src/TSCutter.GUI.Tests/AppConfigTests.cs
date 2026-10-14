using System.Text.Json;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;
using Xunit;

namespace TSCutter.GUI.Tests;

public class AppConfigTests
{
    [Fact]
    public void ImageNavigatorDefaultsToEnabledForExistingConfigurations()
    {
        var config = JsonSerializer.Deserialize("{}", AppJsonContext.Default.AppConfig)!;

        Assert.True(config.ShowImageNavigator);
    }

    [Fact]
    public void ImageNavigatorPreferenceNotifiesBindingsAndSurvivesSerialization()
    {
        var config = new AppConfig();
        string? changedProperty = null;
        config.PropertyChanged += (_, args) => changedProperty = args.PropertyName;
        config.ShowImageNavigator = false;
        var json = JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig)!;

        Assert.Equal(nameof(AppConfig.ShowImageNavigator), changedProperty);
        Assert.False(restored.ShowImageNavigator);
    }

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
