using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using Avalonia;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.Services.Application;

public class ConfigurationService : IConfigurationService
{
    private readonly string _configPath;
    private readonly string _remoteConfigPath;
    private string _savedRemoteJson;
    
    private readonly ILocalizationService _locService;
    private readonly bool _persistent;

    // 构造函数注入
    public ConfigurationService(ILocalizationService locService, bool persistent = true, AppConfig? initialConfig = null)
        : this(locService, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TSCutter.GUI", "config.json"), persistent, initialConfig) { }

    internal ConfigurationService(ILocalizationService locService, string configPath,
        bool persistent = true, AppConfig? initialConfig = null)
    {
        _locService = locService;
        _persistent = persistent;
        _configPath = configPath;
        _remoteConfigPath = Path.Combine(Path.GetDirectoryName(configPath)!, "remote-editing.json");
        CurrentConfig = initialConfig ?? new AppConfig();
        _savedRemoteJson = SerializeRemote();
    }

    public AppConfig CurrentConfig { get; private set; }

    // 加载配置；远程设置单独读取，不依赖普通配置是否存在或完整。
    public void Load()
    {
        if (!_persistent)
        {
            ApplyAll();
            return;
        }
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);
                CurrentConfig = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig) ?? CurrentConfig;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load config: {ex.Message}, use default config instead.");
        }
        try
        {
            if (File.Exists(_remoteConfigPath))
            {
                var json = File.ReadAllText(_remoteConfigPath);
                var remote = JsonSerializer.Deserialize(json, AppJsonContext.Default.RemoteEditingConfig)
                    ?? throw new InvalidDataException("The remote editing configuration is empty.");
                remote.ApplyTo(CurrentConfig);
            }
            _savedRemoteJson = SerializeRemote();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load remote editing config: {ex.Message}");
        }
        try
        {
            ApplyAll();
            Console.WriteLine($"Loaded {_configPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to apply config: {ex.Message}");
        }
    }

    // 保存配置
    public void Save()
    {
        if (!_persistent)
            return;

        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir!);

            var remote = SerializeRemote();
            // 普通客户端保存其他偏好时，不用其启动时的旧快照覆盖远程宿主设置。
            if (remote != _savedRemoteJson)
            {
                WriteConfig(_remoteConfigPath, remote);
                _savedRemoteJson = remote;
            }
            var json = JsonSerializer.Serialize(CurrentConfig, AppJsonContext.Default.AppConfig);
            WriteConfig(_configPath, json);
            Console.WriteLine($"Saved {_configPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save config: {ex.Message}");
        }
    }

    private string SerializeRemote() =>
        JsonSerializer.Serialize(RemoteEditingConfig.From(CurrentConfig), AppJsonContext.Default.RemoteEditingConfig);

    private static void WriteConfig(string path, string json)
    {
        // 同目录临时文件写完后替换，其他客户端不会读到只写了一半的 JSON。
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private void ApplyAll()
    {
        // 语言
        var language = CurrentConfig.Language;
        if (CurrentConfig.AutoDetectLanguage)
        {
            language = AutoDetectLanguage();
        }
        ApplyLanguage(language);

        // 主题
        var theme = CurrentConfig.ThemeVariantMode switch
        {
            ThemeVariantMode.Automatic when AppConfig.IsSystemDarkMode => CurrentConfig.DarkThemeName,
            ThemeVariantMode.Dark => CurrentConfig.DarkThemeName,
            _ => CurrentConfig.ThemeName
        };
        ApplyTheme(theme);
    }

    public void ApplyLanguage(string language)
    {
        if (string.IsNullOrEmpty(language))
            language = AutoDetectLanguage();
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo(language);
        Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(language);
        _locService.SwitchLanguage(language);
        CurrentConfig.Language = language;
    }

    public void ApplyTheme(string theme)
    {
        if (string.IsNullOrWhiteSpace(theme)) return;

        var themeModel = ThemeModel.AllThemes.Concat(ThemeModel.AllDarkThemes).FirstOrDefault(x => x.Name == theme);

        if (themeModel == null) return;

        Avalonia.Application.Current!.RequestedThemeVariant = themeModel.Variant;

        if (_persistent)
            Web.WebUiLauncher.UpdateTheme(theme);

        if (themeModel.IsDarkTheme)
        {
            CurrentConfig.DarkThemeName = theme;
        }
        else
        {
            CurrentConfig.ThemeName = theme;
        }
    }

    private static string AutoDetectLanguage()
    {
        foreach (var languageName in GetPreferredLanguageNames())
        {
            var language = ResolveLanguageName(languageName);
            if (language != null)
                return language;
        }

        return "en-US";
    }

    private static string? ResolveLanguageName(string? languageName)
    {
        if (string.IsNullOrWhiteSpace(languageName))
            return null;

        var normalized = NormalizeLanguageName(languageName);
        if (normalized.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("-TW", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("-HK", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("-MO", StringComparison.OrdinalIgnoreCase))
            return "zh-TW";

        if (normalized.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return "zh-CN";

        return normalized.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "en-US"
            : null;
    }

    private static IEnumerable<string> GetPreferredLanguageNames()
    {
        if (OperatingSystem.IsMacOS())
        {
            foreach (var languageName in ReadMacOSAppleLanguages())
                yield return languageName;
        }

        yield return CultureInfo.CurrentUICulture.Name;
        yield return CultureInfo.CurrentCulture.Name;

        foreach (var variable in new[] { "LC_ALL", "LC_MESSAGES", "LANG" })
            yield return Environment.GetEnvironmentVariable(variable) ?? string.Empty;
    }

    [SupportedOSPlatform("macos")]
    private static IReadOnlyList<string> ReadMacOSAppleLanguages()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/defaults",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("read");
            startInfo.ArgumentList.Add("-g");
            startInfo.ArgumentList.Add("AppleLanguages");

            using var process = Process.Start(startInfo);
            if (process == null || !process.WaitForExit(1000))
                return [];

            var languageNames = new List<string>();
            foreach (var line in process.StandardOutput.ReadToEnd().Split('\n'))
            {
                var languageName = line.Trim().Trim(',', '"');
                if (languageName.Length > 0 && languageName is not "(" and not ")")
                    languageNames.Add(languageName);
            }

            return languageNames;
        }
        catch
        {
            return [];
        }
    }

    private static string NormalizeLanguageName(string languageName)
    {
        var normalized = languageName.Trim().Trim('"').Replace('_', '-');
        var dotIndex = normalized.IndexOf('.', StringComparison.Ordinal);
        return dotIndex >= 0 ? normalized[..dotIndex] : normalized;
    }
}