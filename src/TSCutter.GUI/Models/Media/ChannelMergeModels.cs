using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace TSCutter.GUI.Models.Media;

/// <summary>合并 5.1 的三个槽位：每个槽位取一条立体声音轨的左右两路。</summary>
public enum ChannelMergeSlot
{
    /// <summary>前置：L、R。</summary>
    Front,
    /// <summary>中置与低音：C、LFE。</summary>
    CenterLfe,
    /// <summary>环绕：Ls、Rs。</summary>
    Surround
}

/// <summary>一个槽位的来源：音轨的 PID，以及左右两路是否互换。</summary>
public sealed class ChannelMergeSource
{
    public int Pid { get; set; }
    /// <summary>左右互换：来源的右声道放到槽位的第一路（L、C、Ls）。</summary>
    public bool Swap { get; set; }

    public ChannelMergeSource Snapshot() => new() { Pid = Pid, Swap = Swap };
}

/// <summary>
/// 一个节目的多轨合并：把三条立体声音轨按 L/R、C/LFE、Ls/Rs 合并为一条 5.1 音轨。
/// 码流中没有说明这种映射的信息，由用户指定；新打开的文件默认不合并。
/// </summary>
public sealed class ChannelMergeSettings
{
    public const int SlotCount = 3;

    /// <summary>来源音轨所在的 TS 节目号。</summary>
    public int ProgramId { get; set; }
    /// <summary>false 表示不合并，各音轨原样保留。</summary>
    public bool Merge { get; set; }
    /// <summary>合并时是否同时保留来源音轨；不保留时合并结果替换它们。</summary>
    public bool KeepOriginal { get; set; }
    /// <summary>按 <see cref="ChannelMergeSlot"/> 顺序的三个来源；第一条（前置）决定新音轨的位置与时间。</summary>
    public List<ChannelMergeSource> Sources { get; set; } = [];
    public List<AudioOutput> Outputs { get; set; } = [];

    /// <summary>新音轨的位置与时间以前置来源为准。</summary>
    [JsonIgnore]
    public int AnchorPid => Sources.Count > 0 ? Sources[0].Pid : -1;

    [JsonIgnore]
    public IEnumerable<int> SourcePids => Sources.Select(source => source.Pid);

    [JsonIgnore]
    public bool IsValid => Sources.Count == SlotCount &&
                           Sources.All(source => source.Pid is > 0 and < 0x1FFF) &&
                           Sources.Select(source => source.Pid).Distinct().Count() == SlotCount;

    [JsonIgnore]
    public bool HasWork => Merge && IsValid && Outputs.Any(output => output.Enabled);

    public ChannelMergeSettings Snapshot() => new()
    {
        ProgramId = ProgramId, Merge = Merge, KeepOriginal = KeepOriginal,
        Sources = Sources.Select(source => source.Snapshot()).ToList(),
        Outputs = Outputs.Select(output => output.SnapshotOutput()).ToList()
    };

    /// <summary>比较导出结果是否相同用的文本。</summary>
    [JsonIgnore]
    public string Signature => $"M{ProgramId}:{KeepOriginal}:" +
                               string.Join(",", Sources.Select(source => $"{source.Pid}{(source.Swap ? "s" : "")}")) + ":" +
                               string.Join(";", Outputs.Where(output => output.Enabled).Select(output => output.Signature));
}
