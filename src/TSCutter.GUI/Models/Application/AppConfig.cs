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
    // 导出过程中对最终输出字节同步执行 TS 检测；默认开启，可在设置中关闭。
    public bool TsCheckOnExport { get; set; } = true;
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
    public ThumbnailSheetPreferences ThumbnailSheetPreferences { get; set; } = new();
    // 最近打开的视频、光盘和工程；关闭记录时同时清空。
    public bool RememberRecentItems { get; set; } = true;
    public List<RecentItem> RecentItems
    {
        get => _recentItems;
        set => _recentItems = RecentItemList.Normalize(value);
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

    internal static bool NormalizeHardwareDecodingPreference(bool requested, bool supported) => requested && supported;
}
