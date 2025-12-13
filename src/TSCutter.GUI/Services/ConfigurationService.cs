using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using Avalonia;
using Avalonia.Styling;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Services;

public class ConfigurationService : IConfigurationService
{
    // 配置文件路径
    private readonly string _configPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
        "TSCutter.GUI", 
        "config.json");

    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = AppJsonContext.Default
    };
    
    private readonly ILocalizationService _locService;

    // 构造函数注入
    public ConfigurationService(ILocalizationService locService)
    {
        _locService = locService;
    }

    public AppConfig CurrentConfig { get; private set; } = new();

    // 加载配置
    public void Load()
    {
        if (!File.Exists(_configPath))
        {
            ApplyAll();
            return;
        }
        try
        {
            var json = File.ReadAllText(_configPath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, AppJsonContext.Default.AppConfig);
            if (config != null)
            {
                CurrentConfig = config;
            }

            ApplyAll();
            Console.WriteLine($"Loaded {_configPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to load config: {ex.Message}, use default config instead.");
        }
    }

    // 保存配置
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir!);

            var json = JsonSerializer.Serialize(CurrentConfig, _serializerOptions);
            File.WriteAllText(_configPath, json);
            Console.WriteLine($"Saved {_configPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to save config: {ex.Message}");
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
        var theme = CurrentConfig.ThemeName;
        if (CurrentConfig.AutoDetectDarkMode)
        {
            var actualThemeVariant = Application.Current?.ActualThemeVariant;
            var isDarkMode = actualThemeVariant == ThemeVariant.Dark
                             || actualThemeVariant?.InheritVariant == ThemeVariant.Dark;
            if (isDarkMode)
            {
                theme = AppConfig.DarkTheme.Name;
            }
        }
        ApplyTheme(theme);
    }

    public void ApplyLanguage(string language)
    {
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo(language);
        Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(language);
        _locService.SwitchLanguage(language);
        CurrentConfig.Language = language;
    }

    public void ApplyTheme(string theme)
    {
        var themeModel = AppConfig.AllThemes.Find(x => x.Name == theme);
        if (themeModel == null) return;

        Application.Current!.RequestedThemeVariant = themeModel.Variant;
        CurrentConfig.ThemeName = theme;
    }

    private static string AutoDetectLanguage()
    {
        var currLoc = Thread.CurrentThread.CurrentUICulture.Name;
        var loc = currLoc switch
        {
            "zh-CN" or "zh-SG" => "zh-CN",
            _ when currLoc.StartsWith("zh-") => "zh-TW",
            _ => "en-US"
        };
        return loc;
    }
}