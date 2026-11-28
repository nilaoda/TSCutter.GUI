using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Utils.Editing;

namespace TSCutter.GUI.Models.Application;

public class AppConfig : ObservableObject
{
    private string _themeName = ThemeModel.AllThemes[0].Name;
    private string _darkThemeName = ThemeModel.AllDarkThemes[0].Name;
    private bool _showImageNavigator = true;
    private bool _showKeyFrameStrip = true;
    private bool _showAudioWaveform = true;
    private KeyFrameStripThumbnailSize _keyFrameStripThumbnailSize = KeyFrameStripThumbnailSize.Medium;
    private bool _enableSdrToneMapping = true;
    private bool _preferHardwareDecoding = IsHardwareDecodingSupported;
    private bool _showTimelineSegments = true;
    private InputPreferences _input = new();
    private List<RecentItem> _recentItems = [];
    private string? _lastImportFolder;
    private string? _lastExportFolder;
    private bool _autoSaveRecovery = true;
    private int _autoSaveIntervalSeconds = DefaultAutoSaveIntervalSeconds;

    // 系统当前是否处于深色模式
    [JsonIgnore]
    public static bool IsSystemDarkMode { get; set; }
    [JsonIgnore]
    public ThemeModel ThemeModel => ThemeModel.AllThemes.FirstOrDefault(x => x.Name == ThemeName) ?? ThemeModel.AllThemes[0];
    [JsonIgnore]
    public ThemeModel DarkThemeModel => ThemeModel.AllDarkThemes.FirstOrDefault(x => x.Name == DarkThemeName) ?? ThemeModel.AllDarkThemes[0];

    public string Language { get; set; } = "en-US";
    public string ThemeName
    {
        get => _themeName;
        set => SetProperty(ref _themeName, value);
    }
    public string DarkThemeName
    {
        get => _darkThemeName;
        set => SetProperty(ref _darkThemeName, value);
    }
    public ThemeVariantMode ThemeVariantMode { get; set; } = ThemeVariantMode.Automatic;
    public bool AutoDetectLanguage { get; set; } = true;
    public bool AutoCheckForUpdates { get; set; } = true;
    // 关闭后仍会提示新版本，但只保留原有的浏览器下载方式。
    public bool EnableInPlaceUpdates { get; set; } = true;
    // 用户选择跳过的版本标签；安装更新后清空，不参与版本比较。
    public string? SkippedUpdateVersion { get; set; }
    public bool ShowImageNavigator
    {
        get => _showImageNavigator;
        set => SetProperty(ref _showImageNavigator, value);
    }
    public bool ShowKeyFrameStrip
    {
        get => _showKeyFrameStrip;
        set => SetProperty(ref _showKeyFrameStrip, value);
    }
    public bool ShowAudioWaveform
    {
        get => _showAudioWaveform;
        set => SetProperty(ref _showAudioWaveform, value);
    }
    // 导航条缩略图大小；配置文件中未知的取值回落到“中”。
    [JsonConverter(typeof(KeyFrameStripThumbnailSizeJsonConverter))]
    public KeyFrameStripThumbnailSize KeyFrameStripThumbnailSize
    {
        get => _keyFrameStripThumbnailSize;
        set => SetProperty(ref _keyFrameStripThumbnailSize, KeyFrameStripThumbnailSizeJsonConverter.Normalize(value));
    }
    public bool EnableSdrToneMapping
    {
        get => _enableSdrToneMapping;
        set => SetProperty(ref _enableSdrToneMapping, value);
    }
    // 时间轴上方的章节/节目带；关闭时不读取章节和 EPG。
    public bool ShowTimelineSegments
    {
        get => _showTimelineSegments;
        set => SetProperty(ref _showTimelineSegments, value);
    }
    public string ClipOutputTemplate { get; set; } = Utils.Editing.ClipOutputNaming.DefaultTemplate;
    public bool TsBatchCheckSaveLogs { get; set; }
    public bool ExportCurrentProgramOnly { get; set; }
    // 上次选择的剪辑模式；默认关键帧剪辑。
    public Models.Editing.ClipCutMode ClipCutMode { get; set; }
    // 上次选择的片段模式（保留或删除）；默认保留。
    public Models.Editing.ClipListMode ClipListMode { get; set; }
    // 单个导出完成后显示结果窗口（耗时、大小、路径与检测结论）；队列等批量导出不显示。导出失败时总是显示。
    public bool ShowExportResult { get; set; } = true;
    // 在任务栏按钮（Windows）或 Dock 图标（macOS）上显示导出等长时间操作的进度；默认开启。
    public bool ShowSystemProgress { get; set; } = true;
    // 导出过程中对最终输出字节同步执行 TS 检测；默认开启，可在设置中关闭。
    public bool TsCheckOnExport { get; set; } = true;
    // TS 导出整体平移时间戳、让输出从 0 开始；默认关闭。
    public bool TsZeroStartTime { get; set; }
    // TS 导出删去空包（PID 0x1FFF）；默认关闭。
    public bool TsRemoveNullPackets { get; set; }
    // 普通 TS 导出删去不承载数据或标志的纯填充包；默认关闭。
    public bool TsRemoveStuffingPackets { get; set; }
    public bool PreferHardwareDecoding
    {
        get => IsHardwareDecodingSupported && _preferHardwareDecoding;
        set => _preferHardwareDecoding = NormalizeHardwareDecodingPreference(
            value,
            IsHardwareDecodingSupported);
    }
    // 硬件解码使用的设备 Id；null 表示自动选择。设备已移除时解码沿用自动选择，保留原值以便设备恢复后继续使用。
    public string? HardwareDecodingDevice { get; set; }
    public string? FFmpegRootPath { get; set; }
    // 网页剪辑的工作区目录和桌面端打开、导出对话框的记忆目录各自保存。
    [JsonIgnore]
    public string? WebUiInputDirectory { get; set; }
    [JsonIgnore]
    public string? WebUiOutputDirectory { get; set; }
    [JsonIgnore]
    public int WebUiPort { get; set; } = 5360;
    [JsonIgnore]
    public bool WebUiAllowLan { get; set; } = true;
    [JsonIgnore]
    public bool WebUiEnableIpv6 { get; set; }
    [JsonIgnore]
    public int WebUiMaxWorkers { get; set; } = 4;
    [JsonIgnore]
    public List<WebUiAccount> WebUiAccounts { get; set; } = [];
    public ThumbnailSheetPreferences ThumbnailSheetPreferences { get; set; } = new();
    // 最近打开的视频、光盘和工程；关闭记录时同时清空。
    public bool RememberRecentItems { get; set; } = true;
    public List<RecentItem> RecentItems
    {
        get => _recentItems;
        set => _recentItems = RecentItemList.Normalize(value);
    }
    // 打开类与保存类对话框上次所在的目录，分别记忆、互不影响；只整理字符串，不检查目录是否存在。
    public string? LastImportFolder
    {
        get => _lastImportFolder;
        set => _lastImportFolder = DialogFolderPaths.Normalize(value);
    }
    public string? LastExportFolder
    {
        get => _lastExportFolder;
        set => _lastExportFolder = DialogFolderPaths.Normalize(value);
    }
    // 保存和导出对话框默认定位到源文件所在目录；默认关闭，即定位到上次导出的目录。
    public bool SaveDialogsStartInSourceFolder { get; set; }
    // 定时把未保存的剪辑状态写入应用数据目录中的恢复文件，异常退出后下次启动可以恢复；默认开启。
    public bool AutoSaveRecovery
    {
        get => _autoSaveRecovery;
        set => SetProperty(ref _autoSaveRecovery, value);
    }
    // 写入恢复文件的间隔（秒）；读入时限制在允许的范围内。
    public int AutoSaveIntervalSeconds
    {
        get => _autoSaveIntervalSeconds;
        set => SetProperty(ref _autoSaveIntervalSeconds, NormalizeAutoSaveInterval(value));
    }
    // 鼠标滚轮行为；整体替换以便界面通过属性通知同步，读入时把取值改回允许的范围。
    public InputPreferences Input
    {
        get => _input;
        set => SetProperty(ref _input, (value ?? new InputPreferences()).Normalize());
    }

    [JsonIgnore]
    public static bool IsHardwareDecodingSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public const int DefaultAutoSaveIntervalSeconds = 60;
    public const int MinAutoSaveIntervalSeconds = 15;
    public const int MaxAutoSaveIntervalSeconds = 3600;

    internal static int NormalizeAutoSaveInterval(int seconds) => seconds <= 0
        ? DefaultAutoSaveIntervalSeconds
        : Math.Clamp(seconds, MinAutoSaveIntervalSeconds, MaxAutoSaveIntervalSeconds);

    internal static bool NormalizeHardwareDecodingPreference(bool requested, bool supported) => requested && supported;
}
