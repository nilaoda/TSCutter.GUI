using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models;
using TSCutter.GUI.Services;

namespace TSCutter.GUI.ViewModels;

public partial class SettingsWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    private readonly IConfigurationService _configService;
    private readonly ILocalizationService _locService;

    public bool? DialogResult { get; }
    public event Action? RequestClose;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLanguageEnable))]
    private bool _autoDetectLanguage;
    [ObservableProperty]
    private bool _autoCheckForUpdates;
    [ObservableProperty]
    private ThemeModel _selectedTheme;
    [ObservableProperty]
    private ThemeModel _selectedDarkTheme;
    [ObservableProperty]
    private SupportedLang _selectedLanguage;
    [ObservableProperty]
    private ThemeVariantMode _selectedThemeVariantMode;

    public bool IsLanguageEnable => !AutoDetectLanguage;
    public List<ThemeModel> Themes => ThemeModel.AllThemes;
    public List<ThemeModel> DarkThemes => ThemeModel.AllDarkThemes;
    public List<SupportedLang> Languages => _locService.SupportedLanguages;

    public SettingsWindowViewModel(IConfigurationService configurationService, ILocalizationService localizationService)
    {
        _configService = configurationService;
        _locService = localizationService;
        AutoDetectLanguage = _configService.CurrentConfig.AutoDetectLanguage;
        AutoCheckForUpdates = _configService.CurrentConfig.AutoCheckForUpdates;
        SelectedTheme = _configService.CurrentConfig.ThemeModel;
        SelectedDarkTheme = _configService.CurrentConfig.DarkThemeModel;
        SelectedThemeVariantMode = _configService.CurrentConfig.ThemeVariantMode;
        var selectedLanguage = _locService.SupportedLanguages.Find(x => x.Code == _configService.CurrentConfig.Language);
        if (selectedLanguage != null)
            SelectedLanguage = selectedLanguage;
    }
    
    [RelayCommand]
    private void Apply()
    {
        // 仅提交，不关闭窗口
        CommitChanges();
    }

    [RelayCommand]
    private void Ok()
    {
        // 提交并关闭
        CommitChanges();
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        // 不提交，直接关闭
        RequestClose?.Invoke();
    }
    
    private void CommitChanges()
    {
        // 更新配置
        _configService.CurrentConfig.AutoDetectLanguage = AutoDetectLanguage;
        _configService.CurrentConfig.AutoCheckForUpdates = AutoCheckForUpdates;
        _configService.CurrentConfig.ThemeVariantMode = SelectedThemeVariantMode;

        // 应用主题
        switch (SelectedThemeVariantMode)
        {
            case ThemeVariantMode.Light:
                _configService.ApplyTheme(SelectedTheme.Name);
                break;
            case ThemeVariantMode.Dark:
                _configService.ApplyTheme(SelectedDarkTheme.Name);
                break;
            case ThemeVariantMode.Automatic:
                _configService.ApplyTheme(AppConfig.IsSystemDarkMode ? SelectedDarkTheme.Name : SelectedTheme.Name);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        // 应用语言
        _configService.ApplyLanguage(AutoDetectLanguage ? "" : SelectedLanguage.Code);

        // 保存
        _configService.Save();
    }
}