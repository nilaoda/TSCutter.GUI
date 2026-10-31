using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.Models.Preview;

// TS 按 PID 恢复轨道；其他容器按流索引恢复，并校验流 ID。
public sealed record PreviewStreamSelection(int StreamIndex, int StreamId, int? ProgramId);

public sealed record PreviewVideoTrackOption(PreviewStreamSelection Selection, string DisplayText);

public sealed class PreviewAudioTrackOption(PreviewStreamSelection selection, string label, bool canDecode = true) : ObservableObject
{
    public PreviewStreamSelection Selection { get; } = selection;
    public bool CanDecode { get; } = canDecode;
    public string DisplayText => CanDecode ? label : $"{label} · {LocalizationManager.Instance.String_Playback_AudioTrackUnsupported}";
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
