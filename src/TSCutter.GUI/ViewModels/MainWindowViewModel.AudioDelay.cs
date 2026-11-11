using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Preview;

namespace TSCutter.GUI.ViewModels;

// 音频延迟：当前文件的音频相对视频推后（正数）或提前（负数）的毫秒数。
// 只有勾选启用后才生效：导出时改写音轨时间戳（TS 原包复制时只改 PES 头中的时间戳），预览播放与导航条波形按同样的延迟平移；
// 未勾选时导出仍是原样复制，输入框中的数值保留。
public partial class MainWindowViewModel
{
    [ObservableProperty]
    private int _audioDelayMs;

    [ObservableProperty]
    private bool _audioDelayEnabled;

    // 已应用到封装设置、波形与播放的延迟，生效值不变时不重复应用。
    private int appliedAudioDelayMs;
    // 正在从工程恢复延迟：工程记录的导出结果就是按这个延迟生成的，不重置片段的导出状态。
    private bool restoringAudioDelay;

    /// <summary>实际生效的延迟：未启用时为 0。</summary>
    public int EffectiveAudioDelayMs => AudioDelayEnabled ? AudioDelayMs : 0;

    /// <summary>延迟作用的节目：所编辑（预览）的节目，它的全部音轨一起平移；没有节目信息时为全部音轨。</summary>
    private int? AudioDelayProgramId => _activePreviewSelection?.ProgramId;

    public int AudioDelayMinimum => -AudioDelay.MaxMilliseconds;
    public int AudioDelayMaximum => AudioDelay.MaxMilliseconds;

    partial void OnAudioDelayMsChanged(int value)
    {
        var clamped = AudioDelay.Clamp(value);
        if (clamped != value)
        {
            AudioDelayMs = clamped;
            return;
        }
        ApplyAudioDelay();
    }

    partial void OnAudioDelayEnabledChanged(bool value) => ApplyAudioDelay();

    private void ApplyAudioDelay()
    {
        OnPropertyChanged(nameof(EffectiveAudioDelayMs));
        OnPropertyChanged(nameof(HasUnsavedProjectChanges));
        var effective = EffectiveAudioDelayMs;
        if (effective == appliedAudioDelayMs)
            return;
        appliedAudioDelayMs = effective;
        // 封装导出的设置随工程与导出任务快照保存，延迟随设置一起带入任务。
        if (_remuxExportSettings is { } settings)
            settings.AudioDelayMs = effective;
        // 已导出的片段是按旧延迟生成的，与更换容器、音轨一样重新标为待导出；进行中的导出完成后也不再标为已导出。
        if (!restoringAudioDelay)
            InvalidateClipExports();
        KeyFrameStripWaveform.SetOffset(effective / 1000.0);
        RequestStripWaveform();
        // 播放会话按启动时的延迟输出声音：正在播放时按新延迟重新开始，暂停保留的会话也不再沿用。
        var resume = IsPlaybackActive;
        var end = playbackSession?.EndSeconds;
        StopPlaybackSession();
        if (resume)
            _ = StartPlaybackWithOptions(CurrentTime, end);
    }

    [RelayCommand]
    private void ResetAudioDelay() => AudioDelayMs = 0;

    // 关闭或更换视频时恢复默认：不启用、数值为 0。
    private void ResetAudioDelayState()
    {
        AudioDelayEnabled = false;
        AudioDelayMs = 0;
    }

    private void RestoreAudioDelay(int milliseconds, bool enabled)
    {
        restoringAudioDelay = true;
        try
        {
            AudioDelayMs = milliseconds;
            AudioDelayEnabled = enabled;
        }
        finally { restoringAudioDelay = false; }
    }

    private RemuxExportSettings CreateRemuxExportSettings(VideoInstance video)
    {
        var settings = video.CreateExportSettings();
        settings.AudioDelayMs = EffectiveAudioDelayMs;
        return settings;
    }

    private FrameAccurateExportSettings CreateFrameAccurateExportSettings(VideoInstance video)
    {
        var settings = video.CreateFrameAccurateSettings(CurrentRemuxSettings);
        settings.AudioDelayMs = EffectiveAudioDelayMs;
        return settings;
    }

    // 读取工程或队列时，导出设置中的延迟以记录的生效值为准（未启用即为 0），不沿用设置里单独保存的数值。
    private static RemuxExportSettings? WithAudioDelay(RemuxExportSettings? settings, int effectiveMs)
    {
        if (settings is not null)
            settings.AudioDelayMs = effectiveMs;
        return settings;
    }

    private static FrameAccurateExportSettings? WithAudioDelay(FrameAccurateExportSettings? settings, int effectiveMs)
    {
        if (settings is not null)
            settings.AudioDelayMs = effectiveMs;
        return settings;
    }
}
