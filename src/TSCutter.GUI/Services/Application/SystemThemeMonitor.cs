using System;
using Avalonia.Platform;
using Avalonia.Threading;
using TSCutter.GUI.Models.Application;

namespace TSCutter.GUI.Services.Application;

public sealed class SystemThemeMonitor : IDisposable
{
    private readonly IPlatformSettings _settings;
    private readonly IConfigurationService _configuration;
    private bool _disposed;

    public SystemThemeMonitor(IPlatformSettings settings, IConfigurationService configuration)
    {
        _settings = settings;
        _configuration = configuration;
        // 在加载配置前读取系统状态，不从应用已经选定的配色反推系统外观。
        AppConfig.IsSystemDarkMode = settings.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;
        _settings.ColorValuesChanged += OnColorValuesChanged;
    }

    private void OnColorValuesChanged(object? sender, PlatformColorValues colors)
    {
        if (Dispatcher.UIThread.CheckAccess())
            UpdateTheme(colors);
        else
            Dispatcher.UIThread.Post(() => UpdateTheme(colors));
    }

    private void UpdateTheme(PlatformColorValues colors)
    {
        if (_disposed)
            return;

        var isDark = colors.ThemeVariant == PlatformThemeVariant.Dark;
        if (AppConfig.IsSystemDarkMode == isDark)
            return;

        // 手动模式仍记录最新系统状态，之后切回自动模式时可立即使用。
        AppConfig.IsSystemDarkMode = isDark;
        var config = _configuration.CurrentConfig;
        if (config.ThemeVariantMode == ThemeVariantMode.Automatic)
            _configuration.ApplyTheme(isDark ? config.DarkThemeName : config.ThemeName);
    }

    public void Dispose()
    {
        _disposed = true;
        _settings.ColorValuesChanged -= OnColorValuesChanged;
    }
}
