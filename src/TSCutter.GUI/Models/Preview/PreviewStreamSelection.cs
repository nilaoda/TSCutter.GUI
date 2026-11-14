using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.Models.Preview;

// TS 按 PID 恢复轨道；其他容器按流索引恢复，并校验流 ID。
public sealed record PreviewStreamSelection(int StreamIndex, int StreamId, int? ProgramId);

public sealed record PreviewVideoTrackOption(PreviewStreamSelection Selection, string DisplayText);

/// <param name="dolbyEProgram">杜比 E 轨按声道组展开后的声道组序号（从 1 开始）；普通音轨为 null。</param>
/// <param name="dolbyELayout">杜比 E 声道组的声道布局文字，如“5.1”。</param>
/// <summary>多轨合并试听的一个来源：音轨 PID 与左右是否互换（按前置、中置与低音、环绕的顺序）。</summary>
public sealed record PreviewChannelMergeSource(int Pid, bool Swap);

public sealed class PreviewAudioTrackOption(PreviewStreamSelection selection, string label, bool canDecode = true,
    int? dolbyEProgram = null, string? dolbyELayout = null, IReadOnlyList<PreviewChannelMergeSource>? channelMerge = null)
    : ObservableObject
{
    /// <summary>所选音轨；多轨合并试听时为前置来源（时间以它为准）。</summary>
    public PreviewStreamSelection Selection { get; } = selection;
    public bool CanDecode { get; } = canDecode;
    public int? DolbyEProgram { get; } = dolbyEProgram;
    /// <summary>多轨合并试听：三条来源合成 5.1 后下混播放；普通音轨为 null。</summary>
    public IReadOnlyList<PreviewChannelMergeSource>? ChannelMerge { get; } = channelMerge;
    private string Label => DolbyEProgram is { } program
        ? $"{label} · {string.Format(LocalizationManager.Instance.String_DolbyE_TrackProgram, program, dolbyELayout)}"
        : label;
    public string DisplayText => CanDecode ? Label : $"{Label} · {LocalizationManager.Instance.String_Playback_AudioTrackUnsupported}";
    public void RefreshLocalizedText() => OnPropertyChanged(nameof(DisplayText));
}

public sealed class PreviewProgramOption(int? id, string name,
    IReadOnlyList<PreviewVideoTrackOption> videoTracks, IReadOnlyList<string> audioTracks) : ObservableObject
{
    public int? Id { get; } = id;
    public IReadOnlyList<PreviewVideoTrackOption> VideoTracks { get; } = videoTracks;
    public string DisplayText => Id is { } number
        ? string.IsNullOrWhiteSpace(name)
            ? string.Format(LocalizationManager.Instance.String_PreviewTrack_ProgramFallback, number)
            : $"{name} ({number})"
        : LocalizationManager.Instance.String_PreviewTrack_AllStreams;
    public string AudioDescription => audioTracks.Count == 0
        ? LocalizationManager.Instance.String_PreviewTrack_NoAudio
        : string.Join(" / ", audioTracks.Distinct());

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(AudioDescription));
    }
}
