using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Platform;
using Avalonia.Threading;
using TSCutter.GUI.Themes;
using Xunit;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Tests.UI;

namespace TSCutter.GUI.Tests.Application;

[Collection("Avalonia UI")]
public sealed class SystemThemeMonitorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticModeFollowsSystemChangesInBothDirections(bool initiallyDark)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var previous = AppConfig.IsSystemDarkMode;
            try
            {
                var config = CreateConfiguration();
                var settings = TestPlatformSettings.Create(initiallyDark);
                AppConfig.IsSystemDarkMode = !initiallyDark;
                using var monitor = new SystemThemeMonitor(settings.Settings, config);
                Assert.Equal(initiallyDark, AppConfig.IsSystemDarkMode);

                foreach (var dark in new[] { !initiallyDark, initiallyDark })
                {
                    // 平台通知可以来自后台线程，主题变更应回到 UI 线程处理。
                    Task.Run(() => settings.SetDark(dark)).GetAwaiter().GetResult();
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(dark, AppConfig.IsSystemDarkMode);
                    Assert.Equal(dark ? CustomTheme.Windows11Dark : CustomTheme.LunaSilver,
                        Avalonia.Application.Current!.RequestedThemeVariant);
                    Assert.Equal("Luna Silver", config.CurrentConfig.ThemeName);
                    Assert.Equal("Windows 11 (Dark)", config.CurrentConfig.DarkThemeName);
                }
            }
            finally
            {
                AppConfig.IsSystemDarkMode = previous;
            }
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(ThemeVariantMode.Light)]
    [InlineData(ThemeVariantMode.Dark)]
    public async Task ManualModeKeepsItsThemeAndRecordsCurrentSystemAppearance(ThemeVariantMode mode)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch(() =>
        {
            var previous = AppConfig.IsSystemDarkMode;
            try
            {
                var config = CreateConfiguration();
                config.CurrentConfig.ThemeVariantMode = mode;
                config.ApplyTheme(mode == ThemeVariantMode.Dark
                    ? config.CurrentConfig.DarkThemeName : config.CurrentConfig.ThemeName);
                var manualTheme = Avalonia.Application.Current!.RequestedThemeVariant;
                var settings = TestPlatformSettings.Create(false);
                using var monitor = new SystemThemeMonitor(settings.Settings, config);

                foreach (var dark in new[] { true, false })
                {
                    settings.SetDark(dark);
                    Assert.Equal(dark, AppConfig.IsSystemDarkMode);
                    Assert.Equal(manualTheme, Avalonia.Application.Current.RequestedThemeVariant);
                }

                monitor.Dispose();
                settings.SetDark(true);
                Assert.False(AppConfig.IsSystemDarkMode);
            }
            finally
            {
                AppConfig.IsSystemDarkMode = previous;
            }
        }, CancellationToken.None);
    }

    private static ConfigurationService CreateConfiguration()
    {
        var config = new ConfigurationService(new TestLocalization());
        config.CurrentConfig.ThemeName = "Luna Silver";
        config.CurrentConfig.DarkThemeName = "Windows 11 (Dark)";
        return config;
    }

    // Avalonia 的平台接口限制直接实现，代理仅模拟系统配色查询及变更事件。
    public class TestPlatformSettings : DispatchProxy
    {
        private bool _dark;
        private event EventHandler<PlatformColorValues>? Changed;
        public IPlatformSettings Settings => (IPlatformSettings)(object)this;

        public static TestPlatformSettings Create(bool dark)
        {
            var proxy = (TestPlatformSettings)DispatchProxy.Create<IPlatformSettings, TestPlatformSettings>();
            proxy._dark = dark;
            return proxy;
        }

        private PlatformColorValues Colors => new()
        {
            ThemeVariant = _dark ? PlatformThemeVariant.Dark : PlatformThemeVariant.Light
        };

        public void SetDark(bool value)
        {
            _dark = value;
            Changed?.Invoke(this, Colors);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case nameof(IPlatformSettings.GetColorValues):
                    return Colors;
                case "add_ColorValuesChanged":
                    Changed += (EventHandler<PlatformColorValues>)args![0]!;
                    return null;
                case "remove_ColorValuesChanged":
                    Changed -= (EventHandler<PlatformColorValues>)args![0]!;
                    return null;
                default:
                    throw new NotSupportedException(targetMethod.Name);
            }
        }
    }

    private sealed class TestLocalization : ILocalizationService
    {
        public List<SupportedLang> SupportedLanguages { get; } = [];
        public string CurrentLanguageCode => "en-US";
        public void SwitchLanguage(string code) { }
        public string GetString(string key) => key;
        public event Action? LanguageChanged { add { } remove { } }
    }
}
