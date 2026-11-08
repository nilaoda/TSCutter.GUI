using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Preview;
using TSCutter.GUI.ViewModels.Preview;

namespace TSCutter.GUI.ViewModels;

public partial class MainWindowViewModel
{
    private bool keyFrameStripScrubbing;
    public KeyFrameStripViewModel KeyFrameStrip { get; } = new();

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

    private void OnKeyFrameStripPreferenceChanged()
    {
        // 设置窗口和视图菜单共用同一配置通知；保存仍由各自的提交操作负责。
        OnPropertyChanged(nameof(ShowKeyFrameStrip));
        OnPropertyChanged(nameof(IsKeyFrameStripVisible));
        if (ShowKeyFrameStrip)
            RequestKeyFrameStrip(CurrentTime, immediate: true, currentPts: _displayedFramePts);
        else
            KeyFrameStrip.SetSource(null);
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
        // 帧精确模式下导航条显示每一帧；来源变化会清空缓存并重新解码。
        KeyFrameStrip.SetSource(new KeyFrameStripSource(VideoPath, _activePreviewSelection,
            EnableSdrToneMapping, _videoInstance?.Disc, UsesFrameAccurateCut));
        KeyFrameStrip.RequestAt(time, immediate, currentPts);
    }

    private int pendingWheelSteps;
    private bool wheelNavigationRunning;

    /// <summary>
    /// 在导航条上滚动滚轮：正数前进、负数后退，两种剪辑模式均按关键帧移动。
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
                await DrawNextFrameAsync(count, clampAtEnd: true);
            }
        }
        finally
        {
            pendingWheelSteps = 0;
            wheelNavigationRunning = false;
        }
    }

    private void ResetKeyFrameStrip()
    {
        keyFrameStripScrubbing = false;
        KeyFrameStrip.SetSource(null);
    }
}
