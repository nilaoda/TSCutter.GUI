using System.Collections.Generic;
using TSCutter.GUI.Models.Preview;

namespace TSCutter.GUI.Models.Editing;

public sealed class ClipPointFile
{
    public string Format { get; set; } = "TSCutter.ClipPoints";
    public int Version { get; set; } = 1;
    public ProjectSource Source { get; set; } = new();
    public PreviewStreamSelection? PreviewSelection { get; set; }
    public double Duration { get; set; }
    public long FirstFramePts { get; set; }
    public int TimeBaseNumerator { get; set; }
    public int TimeBaseDenominator { get; set; }
    public bool TimeBasedSource { get; set; }
    public ClipCutMode CutMode { get; set; }
    public ClipListMode ClipMode { get; set; }
    public List<ProjectClip> Clips { get; set; } = [];
}

internal readonly record struct ClipPointRange(ProjectClip Clip, double Start, double End, bool InRange);
