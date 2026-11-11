using System;

namespace TSCutter.GUI.Models.Application;

/// <summary>滚轮定位每格移动的单位。</summary>
public enum WheelStepUnit
{
    /// <summary>按关键帧移动。</summary>
    KeyFrame,
    /// <summary>智能剪辑模式下逐帧移动；其他模式按关键帧移动。</summary>
    Frame,
    /// <summary>按秒移动：智能剪辑模式下落在目标时间的那一帧，其他模式与点击时间轴相同落在目标时间附近的关键帧，且至少移动一个关键帧。</summary>
    Second
}

/// <summary>在预览画面上不按修饰键滚动滚轮时的动作。</summary>
public enum PreviewWheelAction
{
    /// <summary>缩放画面。</summary>
    Zoom,
    /// <summary>按滚轮定位的步长前后移动；按住 Ctrl（macOS 为 ⌘）滚动时仍缩放画面。</summary>
    Navigate
}

/// <summary>在时间轴上不按修饰键滚动滚轮时的动作；Ctrl（macOS 为 ⌘）加滚轮始终缩放时间轴。</summary>
public enum TimelineWheelAction
{
    /// <summary>不处理。</summary>
    None,
    /// <summary>按滚轮定位的步长前后移动。</summary>
    Navigate,
    /// <summary>时间轴已放大时左右平移，与按住 Shift 滚动相同。</summary>
    Pan
}

/// <summary>
/// 鼠标滚轮相关的行为设置。默认值保持原有行为：关键帧预览条每格 1 个关键帧、预览画面缩放、时间轴不处理普通滚轮；
/// 按住 Shift 滚动预览条时按 Shift+方向键的步长加速。
/// </summary>
public sealed record InputPreferences
{
    public const int MinWheelStep = 1;
    public const int MaxWheelStep = 20;
    public const int MaxShiftWheelStep = 100;
    public const int DefaultShiftWheelStep = 10;

    /// <summary>滚轮定位每格移动的单位。</summary>
    public WheelStepUnit WheelStepUnit { get; set; } = WheelStepUnit.KeyFrame;
    /// <summary>滚轮定位时每格移动多少个单位。</summary>
    public int WheelStep { get; set; } = MinWheelStep;
    /// <summary>按住 Shift 滚动时每格移动多少个单位，默认与 Shift+方向键的 10 个关键帧相同。</summary>
    public int ShiftWheelStep { get; set; } = DefaultShiftWheelStep;
    /// <summary>反转滚轮定位方向：默认向下滚前进、向上滚后退。</summary>
    public bool ReverseWheel { get; set; }
    public PreviewWheelAction PreviewWheelAction { get; set; } = PreviewWheelAction.Zoom;
    public TimelineWheelAction TimelineWheelAction { get; set; } = TimelineWheelAction.None;

    /// <summary>把配置文件中超出范围或未知的取值改回允许的范围。</summary>
    public InputPreferences Normalize() => this with
    {
        WheelStep = Math.Clamp(WheelStep, MinWheelStep, MaxWheelStep),
        ShiftWheelStep = Math.Clamp(ShiftWheelStep, MinWheelStep, MaxShiftWheelStep),
        WheelStepUnit = Enum.IsDefined(WheelStepUnit) ? WheelStepUnit : WheelStepUnit.KeyFrame,
        PreviewWheelAction = Enum.IsDefined(PreviewWheelAction) ? PreviewWheelAction : PreviewWheelAction.Zoom,
        TimelineWheelAction = Enum.IsDefined(TimelineWheelAction) ? TimelineWheelAction : TimelineWheelAction.None
    };
}
