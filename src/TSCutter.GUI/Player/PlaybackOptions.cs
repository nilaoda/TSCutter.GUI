using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Models.Discs;

namespace TSCutter.GUI.Player;

/// <summary>播放会话配置。</summary>
public sealed record PlaybackOptions
{
    /// <summary>必需。媒体文件路径。</summary>
    public required string FilePath { get; init; }
    // 诊断仅读短片段，解码器不输出帧时也不继续扫描整片。
    internal double? InputReadEndSeconds { get; init; }
    internal bool CollectReadTimings { get; init; }
    internal bool EnableGpuPresentation { get; init; } = true;
    internal bool EnableHardwareSurfacePresentation { get; init; } = true;
    internal bool RenderAtRequestedSize { get; init; }
    /// <summary>开始播放的时间线秒数（0 = 片头）。</summary>
    public double StartSeconds { get; init; }
    public DiscInput? Disc { get; init; }
    /// <summary>时间线原点（首关键帧包 PTS 对应的秒数），来自导航预览，保证时间轴一致。</summary>
    public double TimelineOriginSeconds { get; init; }
    /// <summary>音频相对视频的延迟秒数（正数推后），与导出改写音轨时间戳的方向一致。</summary>
    public double AudioDelaySeconds { get; init; }
    /// <summary>估算的关键帧间隔秒数，用于预滚锚点的向后余量。</summary>
    public double EstimatedKeyFrameIntervalSeconds { get; init; }
    /// <summary>预滚至少回退的秒数：逐行刷新码流从恢复点起需数秒才完整，来自导航预览的检测。</summary>
    public double MinimumPrerollSeconds { get; init; }
    /// <summary>MPEG-2 帧内刷新：没有 I 帧，按导航预览的检测把带序列头的 P 帧补标为关键包。</summary>
    public bool Mpeg2RefreshPoints { get; init; }
    /// <summary>停止播放的时间线秒数；null 表示播到文件尾。</summary>
    public double? EndSeconds { get; init; }
    /// <summary>与导航预览一致的节目/视频轨选择；null 表示默认视频轨。</summary>
    public PreviewStreamSelection? StreamSelection { get; init; }
    public PreviewStreamSelection? AudioSelection { get; init; }
    /// <summary>TS 同步字节偏移（损坏前缀跳过），与导航预览一致；-1 表示不偏移。</summary>
    public long TsSyncOffset { get; init; } = -1;
    /// <summary>呈现位图的最大宽高（按预览区域换算），0 表示原始尺寸。</summary>
    public int MaxPresentWidth { get; init; }
    public int MaxPresentHeight { get; init; }
    /// <summary>是否启用 SDR 色调映射（与预览设置一致）。</summary>
    public bool EnableSdrToneMapping { get; init; } = true;
    /// <summary>预览音量（0..1），只作用于预览输出；1 为不变音量。</summary>
    public double Volume { get; init; } = 1.0;
    /// <summary>优先硬件解码（VideoToolbox/D3D11VA），运行期失效自动回退软件。</summary>
    public bool PreferHardwareDecoding { get; init; }
    /// <summary>硬件解码使用的显卡 Id；null 或显卡已移除时按系统默认选择。</summary>
    public string? HardwareDecodingDevice { get; init; }
}
