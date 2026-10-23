using TSCutter.GUI.Services.Preview;
using TSCutter.GUI.ViewModels.Preview;

namespace TSCutter.GUI.ViewModels.Editing;

public partial class MainWindowViewModel
{
    private bool keyFrameStripScrubbing;
    public KeyFrameStripViewModel KeyFrameStrip { get; } = new();

    public bool IsKeyFrameStripVisible => ShowKeyFrameStrip && IsVideoInitialized;

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
        KeyFrameStrip.SetSource(new KeyFrameStripSource(VideoPath, _activePreviewSelection, EnableSdrToneMapping));
        KeyFrameStrip.RequestAt(time, immediate, currentPts);
    }

    private void ResetKeyFrameStrip()
    {
        keyFrameStripScrubbing = false;
        KeyFrameStrip.SetSource(null);
    }
}
