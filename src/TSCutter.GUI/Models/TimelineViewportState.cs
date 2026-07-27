using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TSCutter.GUI.Models;

/// <summary>
/// 保存主时间轴的可见范围，并集中处理无级缩放、横向平移与播放头跟随。
/// 该状态仅影响界面映射，不改变实际剪辑时间或媒体解码行为。
/// </summary>
public sealed class TimelineViewportState : ObservableObject
{
    private const double KeyFramesPerMaximumZoom = 20;

    private double _fullDuration;
    private double _estimatedKeyFrameInterval;
    private double _zoomLevel;
    private double _viewStart;
    private double _viewDuration;
    private double _playhead;

    public double FullDuration => _fullDuration;
    public double ZoomLevel
    {
        get => _zoomLevel;
        set => SetZoomLevel(value, GetDefaultZoomAnchor());
    }

    public double ViewStart
    {
        get => _viewStart;
        set => SetViewStart(value);
    }

    public double ViewDuration => _viewDuration;
    public double ViewEnd => _viewStart + _viewDuration;
    public double ScrollMaximum => Math.Max(0, _fullDuration - _viewDuration);
    public double SmallChange => Math.Max(_viewDuration * 0.05, 0.001);
    public double LargeChange => Math.Max(_viewDuration * 0.8, 0.001);
    public double ZoomFactor => _viewDuration > 0 ? _fullDuration / _viewDuration : 1;
    public string ZoomFactorText => $"{ZoomFactor:0.#}×";
    public bool CanZoom => MaximumZoomFactor > 1.0001;
    public bool IsZoomed => _zoomLevel > 0.0001;

    private double MaximumZoomFactor
    {
        get
        {
            if (_fullDuration <= 0)
                return 1;

            // 最大缩放仍保留约 20 个关键帧间隔，避免把只支持关键帧定位的
            // 时间轴放大到没有实际编辑意义的粒度。
            var minimumVisibleDuration = _estimatedKeyFrameInterval > 0
                ? _estimatedKeyFrameInterval * KeyFramesPerMaximumZoom
                : Math.Min(60, _fullDuration);
            var lowerBound = Math.Min(0.001, _fullDuration);
            minimumVisibleDuration = Math.Clamp(minimumVisibleDuration, lowerBound, _fullDuration);
            return Math.Max(1, _fullDuration / minimumVisibleDuration);
        }
    }

    public void Reset(double fullDuration, double estimatedKeyFrameInterval)
    {
        _fullDuration = Math.Max(0, fullDuration);
        _estimatedKeyFrameInterval = Math.Max(0, estimatedKeyFrameInterval);
        _zoomLevel = 0;
        _viewStart = 0;
        _viewDuration = _fullDuration;
        _playhead = 0;
        NotifyAll();
    }

    public void Fit()
    {
        if (_zoomLevel == 0 && _viewStart == 0 && _viewDuration == _fullDuration)
            return;

        _zoomLevel = 0;
        _viewStart = 0;
        _viewDuration = _fullDuration;
        NotifyAll();
    }

    public void ZoomByWheel(double wheelDelta, double anchorTime)
    {
        if (wheelDelta == 0)
            return;
        SetZoomLevel(_zoomLevel + wheelDelta * 0.055, anchorTime);
    }

    public void PanBy(double deltaSeconds) => SetViewStart(_viewStart + deltaSeconds);

    public void SetPlayhead(double value)
    {
        _playhead = Math.Clamp(value, 0, Math.Max(0, _fullDuration));
        if (_zoomLevel <= 0 || _viewDuration <= 0)
            return;

        // 使用关键帧按钮越过可见边界时自动跟随，并保留少量上下文。
        if (_playhead < _viewStart)
            SetViewStart(_playhead - _viewDuration * 0.1);
        else if (_playhead > ViewEnd)
            SetViewStart(_playhead - _viewDuration * 0.9);
    }

    public void SetZoomLevel(double value, double anchorTime)
    {
        var nextLevel = Math.Clamp(value, 0, 1);
        var maximumFactor = MaximumZoomFactor;
        var nextDuration = maximumFactor <= 1
            ? _fullDuration
            : _fullDuration / Math.Exp(nextLevel * Math.Log(maximumFactor));

        var oldDuration = _viewDuration > 0 ? _viewDuration : _fullDuration;
        var anchor = anchorTime;
        double anchorRatio;
        if (oldDuration <= 0 || anchor < _viewStart || anchor > ViewEnd)
        {
            anchor = _viewStart + oldDuration / 2;
            anchorRatio = 0.5;
        }
        else
        {
            anchorRatio = Math.Clamp((anchor - _viewStart) / oldDuration, 0, 1);
        }

        // 鼠标所指时间在缩放前后保持原位置。
        _zoomLevel = nextLevel;
        _viewDuration = Math.Clamp(nextDuration, 0, _fullDuration);
        _viewStart = ClampViewStart(anchor - anchorRatio * _viewDuration);
        NotifyAll();
    }

    private void SetViewStart(double value)
    {
        var next = ClampViewStart(value);
        if (Math.Abs(next - _viewStart) < 0.000001)
            return;
        _viewStart = next;
        OnPropertyChanged(nameof(ViewStart));
        OnPropertyChanged(nameof(ViewEnd));
    }

    private double ClampViewStart(double value) =>
        Math.Clamp(value, 0, Math.Max(0, _fullDuration - _viewDuration));

    private double GetDefaultZoomAnchor() =>
        _playhead >= _viewStart && _playhead <= ViewEnd
            ? _playhead
            : _viewStart + _viewDuration / 2;

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(FullDuration));
        OnPropertyChanged(nameof(ZoomLevel));
        OnPropertyChanged(nameof(ViewStart));
        OnPropertyChanged(nameof(ViewDuration));
        OnPropertyChanged(nameof(ViewEnd));
        OnPropertyChanged(nameof(ScrollMaximum));
        OnPropertyChanged(nameof(SmallChange));
        OnPropertyChanged(nameof(LargeChange));
        OnPropertyChanged(nameof(ZoomFactor));
        OnPropertyChanged(nameof(ZoomFactorText));
        OnPropertyChanged(nameof(CanZoom));
        OnPropertyChanged(nameof(IsZoomed));
    }
}
