using System.Text.Json;
using Xunit;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.Tests.Application;

public class AppConfigTests
{
    [Theory]
    [InlineData("Removed Theme", "Removed Dark Theme")]
    [InlineData("macOS", "macOS (Dark)")]
    public void UnavailableThemesFallBackWithoutDiscardingSavedNames(string light, string dark)
    {
        var config = new AppConfig { ThemeName = light, DarkThemeName = dark };
        var defaults = new AppConfig();
        var nativeAvailable = OperatingSystem.IsMacOS() && light == "macOS";
        Assert.Equal(nativeAvailable ? light : defaults.ThemeName, config.ThemeModel.Name);
        Assert.Equal(nativeAvailable ? dark : defaults.DarkThemeName, config.DarkThemeModel.Name);
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig),
            AppJsonContext.Default.AppConfig)!;
        Assert.Equal(light, restored.ThemeName);
        Assert.Equal(dark, restored.DarkThemeName);
    }

    [Fact]
    public void SdrMappingDefaultsToEnabledAndDisabledPreferenceSurvivesSerialization()
    {
        var config = JsonSerializer.Deserialize("{}", AppJsonContext.Default.AppConfig)!;
        Assert.True(config.EnableSdrToneMapping);
        config.EnableSdrToneMapping = false;
        var json = JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig);
        Assert.False(JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig)!.EnableSdrToneMapping);
    }

    [Fact]
    public void KeyFrameStripDefaultsToEnabledAndKeepsDisabledPreference()
    {
        var config = JsonSerializer.Deserialize("{}", AppJsonContext.Default.AppConfig)!;
        Assert.True(config.ShowKeyFrameStrip);
        string? changedProperty = null;
        config.PropertyChanged += (_, args) => changedProperty = args.PropertyName;
        config.ShowKeyFrameStrip = false;
        Assert.Equal(nameof(AppConfig.ShowKeyFrameStrip), changedProperty);
        var json = JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig);
        var restored = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig)!;
        Assert.False(restored.ShowKeyFrameStrip);
    }

    [Fact]
    public void AudioWaveformDefaultsToEnabledAndKeepsDisabledPreference()
    {
        var config = JsonSerializer.Deserialize("{}", AppJsonContext.Default.AppConfig)!;
        Assert.True(config.ShowAudioWaveform);
        string? changedProperty = null;
        config.PropertyChanged += (_, args) => changedProperty = args.PropertyName;
        config.ShowAudioWaveform = false;
        Assert.Equal(nameof(AppConfig.ShowAudioWaveform), changedProperty);
        var json = JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig);
        Assert.False(JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig)!.ShowAudioWaveform);
    }

    [Theory]
    [InlineData("{}", KeyFrameStripThumbnailSize.Medium)]
    [InlineData("""{"KeyFrameStripThumbnailSize":"Large"}""", KeyFrameStripThumbnailSize.Large)]
    [InlineData("""{"KeyFrameStripThumbnailSize":"Huge","ShowKeyFrameStrip":false}""", KeyFrameStripThumbnailSize.Medium)]
    [InlineData("""{"KeyFrameStripThumbnailSize":7,"ShowKeyFrameStrip":false}""", KeyFrameStripThumbnailSize.Medium)]
    [InlineData("""{"KeyFrameStripThumbnailSize":null,"ShowKeyFrameStrip":false}""", KeyFrameStripThumbnailSize.Medium)]
    public void KeyFrameStripThumbnailSizeDefaultsToMediumAndInvalidValuesFallBack(string json, KeyFrameStripThumbnailSize expected)
    {
        var config = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig)!;
        Assert.Equal(expected, config.KeyFrameStripThumbnailSize);
        // 非法值只影响这一项，其他设置照常读取。
        Assert.Equal(!json.Contains("ShowKeyFrameStrip"), config.ShowKeyFrameStrip);
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig),
            AppJsonContext.Default.AppConfig)!;
        Assert.Equal(expected, restored.KeyFrameStripThumbnailSize);
    }

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
