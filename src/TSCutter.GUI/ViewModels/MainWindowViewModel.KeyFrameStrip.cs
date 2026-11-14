using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Preview;
using TSCutter.GUI.ViewModels.Preview;

namespace TSCutter.GUI.ViewModels;

public partial class MainWindowViewModel
{
    private bool keyFrameStripScrubbing;
    public KeyFrameStripViewModel KeyFrameStrip { get; } = new();

    /// <summary>导航条下方的音频波形，跟随导航条的可见范围读取。</summary>
    public StripWaveformViewModel KeyFrameStripWaveform { get; } = new();

    // 波形使用与播放相同的音轨；导航条隐藏、没有可解码的音轨时不显示波形条。
    private void UpdateStripWaveformSource()
    {
        KeyFrameStripWaveform.SetSource(ShowKeyFrameStrip && ShowAudioWaveform && IsVideoInitialized
            && _videoInstance is { Inited: true } video && SelectedPreviewAudioTrack is { CanDecode: true } track
            ? new AudioWaveformSource(VideoPath, video.Disc, video.TsSyncOffset, _activePreviewSelection,
                track.Selection, video.TimelineOriginSeconds, track.DolbyEProgram, track.ChannelMerge)
            : null);
        RequestStripWaveform();
    }

    private void OnKeyFrameStripPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(KeyFrameStripViewModel.Items))
            RequestStripWaveform();
    }

    // 范围取导航条显示的画面：从第一张到最后一张之后一段，当前位置只决定读取优先级。
    private void RequestStripWaveform()
    {
        if (!KeyFrameStripWaveform.IsAvailable)
            return;
        var items = KeyFrameStrip.Items.Where(item => !item.IsPlaceholder).ToArray();
        if (items.Length == 0)
            return;
        var start = items[0].Timestamp.TotalSeconds;
        var last = items[^1].Timestamp.TotalSeconds;
        var end = items.Length > 1 ? last + (last - items[^2].Timestamp.TotalSeconds) : last + 1;
        KeyFrameStripWaveform.Request(start, end, CurrentTime);
    }

    public bool IsKeyFrameStripVisible => ShowKeyFrameStrip && IsVideoInitialized;

    // 删除模式的片段是要删除的范围：导航条用删除颜色标出被删除的画面，终点（恢复播放的画面）不在其中。
    public IReadOnlyList<KeyFrameRetentionRange> KeyFrameStripRanges => Clips
        .Select(clip => new KeyFrameRetentionRange(
            clip.StartTime,
            clip.EndPosition < 0 ? double.PositiveInfinity : clip.EndTime,
            clip.StartPts is 0 or long.MinValue ? null : clip.StartPts,
            clip.EndPosition < 0 || clip.EndPts is 0 or long.MinValue ? null : clip.EndPts,
            IsActive: ReferenceEquals(clip, SelectedClip), clip.Color, Removed: IsRemoveMode))
        .ToArray();

    public bool ShowKeyFrameStrip
    {
        get => _configService.CurrentConfig.ShowKeyFrameStrip;
        set
        {
            if (value == ShowKeyFrameStrip)
                return;
            _configService.CurrentConfig.ShowKeyFrameStrip = value;
            _configService.Save();
        }
    }

    public bool ShowAudioWaveform
    {
        get => _configService.CurrentConfig.ShowAudioWaveform;
        set
        {
            if (value == ShowAudioWaveform)
                return;
            _configService.CurrentConfig.ShowAudioWaveform = value;
            _configService.Save();
        }
    }

    private void OnAudioWaveformPreferenceChanged()
    {
        OnPropertyChanged(nameof(ShowAudioWaveform));
        UpdateStripWaveformSource();
    }

    public KeyFrameStripThumbnailSize KeyFrameStripThumbnailSize => _configService.CurrentConfig.KeyFrameStripThumbnailSize;

    private void OnKeyFrameStripThumbnailSizeChanged()
    {
        // 先释放旧尺寸的缓存图片与解码器，再通知导航条按新尺寸更新布局与可见数量（此时没有来源，不会用旧图片发布或启动解码），
        // 最后以新尺寸作为来源重新解码。
        KeyFrameStrip.SetSource(null);
        OnPropertyChanged(nameof(KeyFrameStripThumbnailSize));
        RequestKeyFrameStrip(CurrentTime, immediate: true, currentPts: _displayedFramePts);
    }

    private void OnKeyFrameStripPreferenceChanged()
    {
        // 设置窗口和视图菜单共用同一配置通知；保存仍由各自的提交操作负责。
        OnPropertyChanged(nameof(ShowKeyFrameStrip));
        OnPropertyChanged(nameof(IsKeyFrameStripVisible));
        if (ShowKeyFrameStrip)
            RequestKeyFrameStrip(CurrentTime, immediate: true, currentPts: _displayedFramePts);
        else
            KeyFrameStrip.SetSource(null);
        UpdateStripWaveformSource();
    }

    public void BeginKeyFrameStripScrub()
    {
        keyFrameStripScrubbing = true;
        KeyFrameStrip.Cancel();
    }

    public void EndKeyFrameStripScrub(double time)
    {
        keyFrameStripScrubbing = false;
        RequestKeyFrameStrip(time, immediate: true);
    }

    public void RequestKeyFrameStrip(double time, bool immediate = false, long? currentPts = null)
    {
        if (!ShowKeyFrameStrip || !IsVideoInitialized || DurationMax <= 0)
            return;
        // 帧精确模式下导航条显示每一帧；来源（含缩略图大小）变化会清空缓存并重新解码。
        KeyFrameStrip.SetSource(new KeyFrameStripSource(VideoPath, _activePreviewSelection,
            EnableSdrToneMapping, _videoInstance?.Disc, UsesFrameAccurateCut, KeyFrameStripThumbnailSize));
        KeyFrameStrip.RequestAt(time, immediate, currentPts);
    }

    private int pendingWheelSteps;
    private bool wheelNavigationRunning;

    /// <summary>
    /// 滚轮定位：正数前进、负数后退，单位按设置为关键帧、帧（只在智能剪辑模式下逐帧，其他模式按关键帧）或秒。
    /// 定位期间继续滚动的格数累计起来，当前定位完成后一次处理。
    /// </summary>
    public async Task ScrollKeyFrameStripAsync(int steps)
    {
        // 自己的滚轮定位进行中时累计格数；其他解码、加载进行中时忽略。
        if (!wheelNavigationRunning && !CanTogglePreviewOptions)
            return;
        pendingWheelSteps += steps;
        if (wheelNavigationRunning)
            return;
        wheelNavigationRunning = true;
        try
        {
            while (pendingWheelSteps != 0)
            {
                var count = pendingWheelSteps;
                pendingWheelSteps = 0;
                if (!IsVideoInitialized || IsDecoding || CurrentPts == long.MinValue)
                    break;
                var unit = ViewerPreferences.Input.WheelStepUnit;
                if (unit == WheelStepUnit.Frame && CanStepFrame)
                {
                    if (!await StepFramesAsync(count))
                        break;
                }
                else if (unit == WheelStepUnit.Second)
                {
                    if (!await StepSecondsAsync(count))
                        break;
                }
                else
                    await DrawNextFrameAsync(count, clampAtEnd: true);
            }
        }
        finally
        {
            pendingWheelSteps = 0;
            wheelNavigationRunning = false;
        }
    }

    /// <summary>
    /// 按秒前后移动。智能剪辑模式下精确解码目标时间的那一帧；其他模式与点击时间轴相同，落在目标时间附近的关键帧，
    /// 关键帧间隔比步长更长时至少移动一个关键帧，避免滚轮停在原地。画面没有变化（到达片头片尾）时返回 false。
    /// </summary>
    private async Task<bool> StepSecondsAsync(int seconds)
    {
        if (seconds == 0 || _videoInstance is not { Inited: true } video)
            return false;
        var before = CurrentPts;
        var target = Math.Clamp(CurrentTime + seconds, 0, Math.Max(0, DurationMax));
        if (CanStepFrame)
        {
            var targetPts = video.SecondsToPts(target);
            await DecodeExactFrameAsync(instance => instance.DecodeFrameFromPtsAsync(targetPts));
            return CurrentPts != before;
        }
        await SeekToTimeAndDrawFrameAsync(TimeSpan.FromSeconds(target));
        var moved = seconds > 0 ? CurrentPts > before : CurrentPts < before;
        if (!moved)
            await DrawNextFrameAsync(Math.Sign(seconds), clampAtEnd: true);
        return CurrentPts != before;
    }

    private void ResetKeyFrameStrip()
    {
        keyFrameStripScrubbing = false;
        KeyFrameStrip.SetSource(null);
        KeyFrameStripWaveform.SetSource(null);
    }
}
