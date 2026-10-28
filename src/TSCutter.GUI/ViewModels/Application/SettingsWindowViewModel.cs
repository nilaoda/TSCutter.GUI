using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Utils.Editing;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.ViewModels.Application;

public partial class SettingsWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOutputTemplateValid), nameof(OutputNamePreview))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(OkCommand))]
    private string _clipOutputTemplate = Utils.Editing.ClipOutputNaming.DefaultTemplate;

    public bool IsOutputTemplateValid => Utils.Editing.ClipOutputNaming.IsValidTemplate(ClipOutputTemplate);
    public string OutputNamePreview => IsOutputTemplateValid
        ? Utils.Editing.ClipOutputNaming.Generate(ClipOutputTemplate, "Recording.ts", "Highlight", 1, 10, 20, "ts")
        : "";

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
    private bool _showImageNavigator;
    [ObservableProperty]
    private bool _showKeyFrameStrip;
    [ObservableProperty]
    private bool _preferHardwareDecoding;
    [ObservableProperty]
    private bool _enableSdrToneMapping;
    [ObservableProperty]
    private bool _tsCheckOnExport;
    private bool _updatingThemePreset;

    [ObservableProperty]
    private int _selectedTabIndex;
    [ObservableProperty]
    private ThemePickerItem? _selectedThemePreset;
    [ObservableProperty]
    private ThemeModel _selectedTheme;
    [ObservableProperty]
    private ThemeModel _selectedDarkTheme;
    [ObservableProperty]
    private SupportedLang _selectedLanguage;
    [ObservableProperty]
    private ThemeVariantMode _selectedThemeVariantMode;

    public bool IsLanguageEnable => !AutoDetectLanguage;
    public bool IsHardwareDecodingAvailable => AppConfig.IsHardwareDecodingSupported;
    public IReadOnlyList<ThemePickerItem> ThemePresetOptions { get; } = ThemePickerItem.CreatePresets(ThemeModel.AllThemes);
    public IReadOnlyList<ThemePickerItem> ThemeOptions { get; } = ThemePickerItem.Create(ThemeModel.AllThemes);
    public IReadOnlyList<ThemePickerItem> DarkThemeOptions { get; } = ThemePickerItem.Create(ThemeModel.AllDarkThemes);
    public List<SupportedLang> Languages => _locService.SupportedLanguages;

    public SettingsWindowViewModel(IConfigurationService configurationService, ILocalizationService localizationService)
    {
        _configService = configurationService;
        _locService = localizationService;
        ClipOutputTemplate = Utils.Editing.ClipOutputNaming.IsValidTemplate(_configService.CurrentConfig.ClipOutputTemplate)
            ? _configService.CurrentConfig.ClipOutputTemplate : Utils.Editing.ClipOutputNaming.DefaultTemplate;
        AutoDetectLanguage = _configService.CurrentConfig.AutoDetectLanguage;
        AutoCheckForUpdates = _configService.CurrentConfig.AutoCheckForUpdates;
        ShowImageNavigator = _configService.CurrentConfig.ShowImageNavigator;
        ShowKeyFrameStrip = _configService.CurrentConfig.ShowKeyFrameStrip;
        PreferHardwareDecoding = _configService.CurrentConfig.PreferHardwareDecoding;
        EnableSdrToneMapping = _configService.CurrentConfig.EnableSdrToneMapping;
        TsCheckOnExport = _configService.CurrentConfig.TsCheckOnExport;
        SelectedTheme = _configService.CurrentConfig.ThemeModel;
        SelectedDarkTheme = _configService.CurrentConfig.DarkThemeModel;
        SelectedThemeVariantMode = _configService.CurrentConfig.ThemeVariantMode;
        var selectedLanguage = _locService.SupportedLanguages.Find(x => x.Code == _configService.CurrentConfig.Language);
        if (selectedLanguage != null)
            SelectedLanguage = selectedLanguage;
    }
    
    partial void OnSelectedThemePresetChanged(ThemePickerItem? value)
    {
        if (_updatingThemePreset || value?.Theme is not { } light)
            return;
        var dark = ThemeModel.GetPairedDarkTheme(light);
        if (dark is null)
            return;

        _updatingThemePreset = true;
        try
        {
            SelectedTheme = light;
            SelectedDarkTheme = dark;
        }
        finally
        {
            _updatingThemePreset = false;
        }
    }

    partial void OnSelectedThemeChanged(ThemeModel value) => UpdateThemePreset();
    partial void OnSelectedDarkThemeChanged(ThemeModel value) => UpdateThemePreset();

    private void UpdateThemePreset()
    {
        if (_updatingThemePreset || SelectedTheme is null || SelectedDarkTheme is null)
            return;

        _updatingThemePreset = true;
        try
        {
            SelectedThemePreset = ThemeModel.GetPairedDarkTheme(SelectedTheme) == SelectedDarkTheme
                ? ThemePresetOptions.Single(item => item.Theme == SelectedTheme)
                : ThemePresetOptions[0];
        }
        finally
        {
            _updatingThemePreset = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsOutputTemplateValid))]
    private void Apply()
    {
        // 仅提交，不关闭窗口
        CommitChanges();
    }

    [RelayCommand(CanExecute = nameof(IsOutputTemplateValid))]
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
        if (!IsOutputTemplateValid)
            return;
        // 自定义项允许直接编辑；提交时按最终组合重新识别，与菜单的勾选保持一致。
        UpdateThemePreset();
        _configService.CurrentConfig.ClipOutputTemplate = ClipOutputTemplate.Trim();
        // 更新配置
        _configService.CurrentConfig.AutoDetectLanguage = AutoDetectLanguage;
        _configService.CurrentConfig.AutoCheckForUpdates = AutoCheckForUpdates;
        _configService.CurrentConfig.ShowImageNavigator = ShowImageNavigator;
        _configService.CurrentConfig.ShowKeyFrameStrip = ShowKeyFrameStrip;
        _configService.CurrentConfig.PreferHardwareDecoding = PreferHardwareDecoding;
        _configService.CurrentConfig.EnableSdrToneMapping = EnableSdrToneMapping;
        _configService.CurrentConfig.TsCheckOnExport = TsCheckOnExport;
        _configService.CurrentConfig.ThemeVariantMode = SelectedThemeVariantMode;
        // 两套配色都需保存，即使其中一套当前未启用。
        _configService.CurrentConfig.ThemeName = SelectedTheme.Name;
        _configService.CurrentConfig.DarkThemeName = SelectedDarkTheme.Name;

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
