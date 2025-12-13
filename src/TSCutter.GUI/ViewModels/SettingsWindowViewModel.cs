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
    private bool _autoDetectDarkMode;
    [ObservableProperty]
    private ThemeModel _selectedTheme;
    [ObservableProperty]
    private SupportedLang _selectedLanguage;

    public bool IsLanguageEnable => !AutoDetectLanguage;
    public List<ThemeModel> Themes => AppConfig.AllThemes;
    public List<SupportedLang> Languages => _locService.SupportedLanguages;

    public SettingsWindowViewModel(IConfigurationService configurationService, ILocalizationService localizationService)
    {
        _configService = configurationService;
        _locService = localizationService;
        AutoDetectLanguage = _configService.CurrentConfig.AutoDetectLanguage;
        AutoDetectDarkMode = _configService.CurrentConfig.AutoDetectDarkMode;
        SelectedTheme = _configService.CurrentConfig.ThemeModel;
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
        _configService.CurrentConfig.ThemeName = SelectedTheme.Name;
        _configService.CurrentConfig.Language = SelectedLanguage.Code;
        _configService.CurrentConfig.AutoDetectLanguage = AutoDetectLanguage;
        _configService.CurrentConfig.AutoDetectDarkMode = AutoDetectDarkMode;

        // 应用
        _configService.ApplyTheme(SelectedTheme.Name);
        _configService.ApplyLanguage(SelectedLanguage.Code);

        // 保存
        _configService.Save();
    }
}