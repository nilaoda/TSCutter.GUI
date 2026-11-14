using System;
using System.Collections.Generic;
using TSCutter.GUI.Models.Preview;

namespace TSCutter.GUI.Models.Editing;

public sealed class CutterProject
{
    public int Version { get; set; } = 4;
    public ProjectSource? Source { get; set; }
    public PreviewStreamSelection? PreviewSelection { get; set; }
    public bool ExportCurrentProgramOnly { get; set; }
    public RemuxExportSettings? RemuxSettings { get; set; }
    public ClipCutMode CutMode { get; set; }
    public ClipListMode ClipMode { get; set; }
    /// <summary>音频相对视频的延迟（毫秒），见 <see cref="AudioDelay"/>。</summary>
    public int AudioDelayMs { get; set; }
    /// <summary>是否启用音频延迟；未启用时导出不改写音轨时间戳。</summary>
    public bool AudioDelayEnabled { get; set; }
    /// <summary>各杜比 E 轨（按 PID）的导出处理方式；没有杜比 E 轨时为 null。</summary>
    public List<Media.DolbyETrackSettings>? DolbyE { get; set; }
    /// <summary>TS 导出时不输出的轨道（PID）；全部保留时为 null。</summary>
    public List<int>? ExcludedTsPids { get; set; }
    public List<ProjectClip> Clips { get; set; } = [];
    public List<ProjectQueueItem> Queue { get; set; } = [];
    public int SelectedClipIndex { get; set; } = -1;
    public double CurrentTime { get; set; }
    public double TimelineZoomLevel { get; set; }
    public double TimelineViewStart { get; set; }
}

public sealed class ProjectSource
{
    public string Path { get; set; } = string.Empty;
    public long Length { get; set; }
    public List<long> AnchorOffsets { get; set; } = [];
    public string PrefixFingerprint { get; set; } = string.Empty;
}

public sealed class ProjectClip
{
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Color { get; set; } = "";
    public double StartTime { get; set; }
    public double EndTime { get; set; }
    public long StartPts { get; set; }
    public long EndPts { get; set; }
    public long StartPosition { get; set; }
    public long EndPosition { get; set; }
    /// <summary>终点画面所在包的字节位置（删除模式下 TS 关键帧剪辑从这里恢复）；旧工程为 -1。</summary>
    public long EndFramePosition { get; set; } = -1;
    /// <summary>起点、终点关键帧压缩包的签名（DVD 导出按画面定位切点）；没有时为 null。</summary>
    public string? StartSignature { get; set; }
    public string? EndSignature { get; set; }
    public bool IsSelected { get; set; }
    public bool FrameAccurate { get; set; }
    /// <summary>起止按 EPG 时间标记、尚未核对。</summary>
    public bool NeedsBoundaryReview { get; set; }
    public byte[]? StartThumbnailJpeg { get; set; }
    public byte[]? EndThumbnailJpeg { get; set; }
    public string? OutputFilePath { get; set; }
    public MergedClipOutput? MergedOutput { get; set; }
    public bool WasExported { get; set; }
}

public sealed class ProjectQueueItem
{
    public int ClipIndex { get; set; } = 1;
    public string GeneratedFileName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Color { get; set; } = "";
    public ProjectSource Source { get; set; } = new();
    public RemuxExportSettings? RemuxSettings { get; set; }
    public FrameAccurateExportSettings? FrameAccurate { get; set; }
    public long StartPts { get; set; }
    public long? EndPts { get; set; }
    public int? ProgramId { get; set; }
    /// <summary>关键帧模式 TS 任务的音频延迟（毫秒）；封装与帧精确任务的延迟在各自设置中。</summary>
    public int AudioDelayMs { get; set; }
    /// <summary>音频延迟作用的节目号（所编辑的节目）；null 表示全部音轨。</summary>
    public int? AudioDelayProgramId { get; set; }
    /// <summary>杜比 E 转码计划；不转码时为 null。</summary>
    public Media.DolbyEExportPlan? DolbyE { get; set; }
    /// <summary>TS 导出时不输出的轨道（PID）；全部保留时为 null。</summary>
    public List<int>? ExcludedPids { get; set; }
    public string OutputFilePath { get; set; } = string.Empty;
    public double StartTime { get; set; }
    public double EndTime { get; set; }
    public long StartPosition { get; set; }
    public long EndPosition { get; set; }
    public string? StartSignature { get; set; }
    public string? EndSignature { get; set; }
}
