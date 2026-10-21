using System;
using System.Collections.Generic;

namespace TSCutter.GUI.Models;

public sealed class CutterProject
{
    public int Version { get; set; } = 4;
    public ProjectSource? Source { get; set; }
    public PreviewStreamSelection? PreviewSelection { get; set; }
    public bool ExportCurrentProgramOnly { get; set; }
    public RemuxExportSettings? RemuxSettings { get; set; }
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
    public bool IsSelected { get; set; }
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
    public int? ProgramId { get; set; }
    public string OutputFilePath { get; set; } = string.Empty;
    public double StartTime { get; set; }
    public double EndTime { get; set; }
    public long StartPosition { get; set; }
    public long EndPosition { get; set; }
}
