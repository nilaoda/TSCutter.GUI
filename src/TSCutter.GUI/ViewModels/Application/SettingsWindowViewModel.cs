using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Editing;
using TSCutter.GUI.Utils.Media;
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
    // 通过生成的属性读取文字，资源键改名或删除时编译即会报错。
    private readonly LocalizationManager _strings;

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
    private bool _showAudioWaveform;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyFrameStripThumbnailSizeIndex))]
    private KeyFrameStripThumbnailSize _keyFrameStripThumbnailSize = KeyFrameStripThumbnailSize.Medium;
    [ObservableProperty]
    private bool _preferHardwareDecoding;
    [ObservableProperty]
    private HardwareDecodingDeviceOption? _selectedHardwareDecodingDevice;
    [ObservableProperty]
    private bool _enableSdrToneMapping;
    [ObservableProperty]
    private bool _tsCheckOnExport;
    [ObservableProperty]
    private bool _showExportResult;
    private bool _updatingThemePreset;

    // 鼠标滚轮设置；修改后快捷键列表中的鼠标操作说明随之更新。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WheelStepUnitIndex), nameof(ShortcutGroups), nameof(HasNoShortcutMatch))]
    private WheelStepUnit _wheelStepUnit;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WheelStepIndex), nameof(ShortcutGroups), nameof(HasNoShortcutMatch))]
    private int _wheelStep = InputPreferences.MinWheelStep;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShiftWheelStepIndex), nameof(ShortcutGroups), nameof(HasNoShortcutMatch))]
    private int _shiftWheelStep = InputPreferences.DefaultShiftWheelStep;
    // 步长下拉框的文字只写数量（“n 个单位”），不随移动单位变化；切换语言后重新生成。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WheelStepIndex))]
    private IReadOnlyList<string> _wheelStepTexts = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShiftWheelStepIndex))]
    private IReadOnlyList<string> _shiftWheelStepTexts = [];
    [ObservableProperty]
    private bool _reverseWheel;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewWheelActionIndex), nameof(ShortcutGroups), nameof(HasNoShortcutMatch))]
    private PreviewWheelAction _previewWheelAction;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimelineWheelActionIndex), nameof(ShortcutGroups), nameof(HasNoShortcutMatch))]
    private TimelineWheelAction _timelineWheelAction;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShortcutGroups), nameof(HasNoShortcutMatch))]
    private string _shortcutSearchText = "";

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
    /// <summary>步长下拉框提供的档位；配置文件中不在档位里的取值会额外列出，打开设置不会改掉它。</summary>
    public static IReadOnlyList<int> WheelStepPresets { get; } = [1, 2, 3, 5, 10];
    public static IReadOnlyList<int> ShiftWheelStepPresets { get; } = [5, 10, 20, 30, 60];

    /// <summary>步长下拉框各项对应的数量，与 <see cref="WheelStepTexts"/> 一一对应。</summary>
    public IReadOnlyList<int> WheelStepValues { get; private set; } = WheelStepPresets;
    public IReadOnlyList<int> ShiftWheelStepValues { get; private set; } = ShiftWheelStepPresets;

    // 下拉框按序号选择；条目重建时控件可能短暂写回 -1，忽略。
    public int WheelStepIndex
    {
        get => IndexOf(WheelStepValues, WheelStep);
        set
        {
            if (value >= 0 && value < WheelStepValues.Count)
                WheelStep = WheelStepValues[value];
        }
    }

    public int ShiftWheelStepIndex
    {
        get => IndexOf(ShiftWheelStepValues, ShiftWheelStep);
        set
        {
            if (value >= 0 && value < ShiftWheelStepValues.Count)
                ShiftWheelStep = ShiftWheelStepValues[value];
        }
    }

    // 下拉框按枚举顺序排列选项。
    public int WheelStepUnitIndex
    {
        get => (int)WheelStepUnit;
        set => WheelStepUnit = Enum.IsDefined((WheelStepUnit)value) ? (WheelStepUnit)value : WheelStepUnit.KeyFrame;
    }

    public int KeyFrameStripThumbnailSizeIndex
    {
        get => (int)KeyFrameStripThumbnailSize;
        set => KeyFrameStripThumbnailSize = Enum.IsDefined((KeyFrameStripThumbnailSize)value)
            ? (KeyFrameStripThumbnailSize)value : KeyFrameStripThumbnailSize.Medium;
    }

    public int PreviewWheelActionIndex
    {
        get => (int)PreviewWheelAction;
        set => PreviewWheelAction = Enum.IsDefined((PreviewWheelAction)value) ? (PreviewWheelAction)value : PreviewWheelAction.Zoom;
    }

    public int TimelineWheelActionIndex
    {
        get => (int)TimelineWheelAction;
        set => TimelineWheelAction = Enum.IsDefined((TimelineWheelAction)value) ? (TimelineWheelAction)value : TimelineWheelAction.None;
    }

    /// <summary>按搜索关键字过滤后的快捷键列表；鼠标操作说明按窗口中尚未保存的滚轮设置生成。</summary>
    public IReadOnlyList<ShortcutGroup> ShortcutGroups =>
        ShortcutCatalog.Filter(ShortcutCatalog.Build(_strings, CurrentInputPreferences()), ShortcutSearchText);

    public bool HasNoShortcutMatch => ShortcutGroups.Count == 0;
    public bool IsHardwareDecodingAvailable => AppConfig.IsHardwareDecodingSupported;
    /// <summary>“自动”加当前可选的解码设备；已保存但当前不可用的设备也列出，避免打开设置后被静默改回自动。</summary>
    public IReadOnlyList<HardwareDecodingDeviceOption> HardwareDecodingDeviceOptions { get; }
    /// <summary>只有自动一项时（macOS、Linux 或未找到显卡）不可选择。</summary>
    public bool CanSelectHardwareDecodingDevice => HardwareDecodingDeviceOptions.Count > 1;
    public IReadOnlyList<ThemePickerItem> ThemePresetOptions { get; } = ThemePickerItem.CreatePresets(ThemeModel.AllThemes);
    public IReadOnlyList<ThemePickerItem> ThemeOptions { get; } = ThemePickerItem.Create(ThemeModel.AllThemes);
    public IReadOnlyList<ThemePickerItem> DarkThemeOptions { get; } = ThemePickerItem.Create(ThemeModel.AllDarkThemes);
    public List<SupportedLang> Languages => _locService.SupportedLanguages;

    public SettingsWindowViewModel(IConfigurationService configurationService, ILocalizationService localizationService)
    {
        _configService = configurationService;
        _locService = localizationService;
        _strings = new LocalizationManager(localizationService);
        ClipOutputTemplate = Utils.Editing.ClipOutputNaming.IsValidTemplate(_configService.CurrentConfig.ClipOutputTemplate)
            ? _configService.CurrentConfig.ClipOutputTemplate : Utils.Editing.ClipOutputNaming.DefaultTemplate;
        AutoDetectLanguage = _configService.CurrentConfig.AutoDetectLanguage;
        AutoCheckForUpdates = _configService.CurrentConfig.AutoCheckForUpdates;
        ShowImageNavigator = _configService.CurrentConfig.ShowImageNavigator;
        ShowKeyFrameStrip = _configService.CurrentConfig.ShowKeyFrameStrip;
        ShowAudioWaveform = _configService.CurrentConfig.ShowAudioWaveform;
        KeyFrameStripThumbnailSize = _configService.CurrentConfig.KeyFrameStripThumbnailSize;
        PreferHardwareDecoding = _configService.CurrentConfig.PreferHardwareDecoding;
        HardwareDecodingDeviceOptions = CreateHardwareDecodingDeviceOptions(
            _configService.CurrentConfig.HardwareDecodingDevice, HardwareDecodingDevices.Enumerate());
        SelectedHardwareDecodingDevice = HardwareDecodingDeviceOptions.FirstOrDefault(option =>
            option.Id == _configService.CurrentConfig.HardwareDecodingDevice) ?? HardwareDecodingDeviceOptions[0];
        EnableSdrToneMapping = _configService.CurrentConfig.EnableSdrToneMapping;
        TsCheckOnExport = _configService.CurrentConfig.TsCheckOnExport;
        ShowExportResult = _configService.CurrentConfig.ShowExportResult;
        LoadInputPreferences(_configService.CurrentConfig.Input);
        WheelStepValues = WithCurrent(WheelStepPresets, WheelStep);
        ShiftWheelStepValues = WithCurrent(ShiftWheelStepPresets, ShiftWheelStep);
        RefreshWheelStepTexts();
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

    private void LoadInputPreferences(InputPreferences input)
    {
        input = input.Normalize();
        WheelStepUnit = input.WheelStepUnit;
        WheelStep = input.WheelStep;
        ShiftWheelStep = input.ShiftWheelStep;
        ReverseWheel = input.ReverseWheel;
        PreviewWheelAction = input.PreviewWheelAction;
        TimelineWheelAction = input.TimelineWheelAction;
    }

    private InputPreferences CurrentInputPreferences() => new InputPreferences
    {
        WheelStepUnit = WheelStepUnit,
        WheelStep = WheelStep,
        ShiftWheelStep = ShiftWheelStep,
        ReverseWheel = ReverseWheel,
        PreviewWheelAction = PreviewWheelAction,
        TimelineWheelAction = TimelineWheelAction
    }.Normalize();

    private static IReadOnlyList<int> WithCurrent(IReadOnlyList<int> presets, int current) =>
        presets.Append(current).Distinct().Order().ToArray();

    private static int IndexOf(IReadOnlyList<int> values, int value)
    {
        for (var index = 0; index < values.Count; index++)
            if (values[index] == value)
                return index;
        return -1;
    }

    /// <summary>保留已保存但当前不可用的设备，避免打开设置就丢失原选择。</summary>
    internal static IReadOnlyList<HardwareDecodingDeviceOption> CreateHardwareDecodingDeviceOptions(string? savedId,
        IReadOnlyList<HardwareDecodingDevice> devices)
    {
        var options = new List<HardwareDecodingDeviceOption> { new(null, null) };
        options.AddRange(devices.Select(device => new HardwareDecodingDeviceOption(device.Id, device.Name)));
        if (!string.IsNullOrEmpty(savedId) && devices.All(device => device.Id != savedId))
            options.Add(new(savedId, null, IsMissing: true));
        return options;
    }

    /// <summary>按当前语言生成步长下拉框的文字。</summary>
    private void RefreshWheelStepTexts()
    {
        WheelStepTexts = WheelStepValues.Select(value => ShortcutCatalog.UnitCountText(_strings, value)).ToArray();
        ShiftWheelStepTexts = ShiftWheelStepValues.Select(value => ShortcutCatalog.UnitCountText(_strings, value)).ToArray();
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
        _configService.CurrentConfig.ShowAudioWaveform = ShowAudioWaveform;
        // 先改大小再改显示：同时打开预览条并改大小时只按新尺寸解码一次。
        _configService.CurrentConfig.KeyFrameStripThumbnailSize = KeyFrameStripThumbnailSize;
        _configService.CurrentConfig.ShowKeyFrameStrip = ShowKeyFrameStrip;
        _configService.CurrentConfig.PreferHardwareDecoding = PreferHardwareDecoding;
        _configService.CurrentConfig.HardwareDecodingDevice = SelectedHardwareDecodingDevice?.Id;
        _configService.CurrentConfig.EnableSdrToneMapping = EnableSdrToneMapping;
        _configService.CurrentConfig.TsCheckOnExport = TsCheckOnExport;
        _configService.CurrentConfig.ShowExportResult = ShowExportResult;
        _configService.CurrentConfig.Input = CurrentInputPreferences();
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
        // 列表与步长档位的文字取自当前语言，切换语言后重新生成。
        RefreshWheelStepTexts();
        OnPropertyChanged(nameof(ShortcutGroups));
        OnPropertyChanged(nameof(HasNoShortcutMatch));

        // 保存
        _configService.Save();
    }
}

/// <summary>解码设备下拉项：Id 为 null 表示自动选择；IsMissing 表示已保存但当前找不到的设备。</summary>
public sealed record HardwareDecodingDeviceOption(string? Id, string? Name, bool IsMissing = false)
{
    public bool IsAutomatic => Id is null;
    public bool HasName => Name is not null;
}
