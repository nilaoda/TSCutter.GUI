using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.ViewModels.Editing;

public partial class RemuxExportTrackChoice : ObservableObject
{
    public int Index { get; init; }
    public string Description { get; init; } = "";
    public bool CanChange { get; init; } = true;
    public bool IsTextSubtitle { get; init; }
    /// <summary>分组标题（TS 多节目时的节目名），只在每组第一条轨道上；没有分组时为空。</summary>
    public string GroupHeader { get; init; } = "";
    public bool HasGroupHeader => GroupHeader.Length > 0;
    [ObservableProperty] private bool _isSelected;
}

public partial class RemuxExportOptionsViewModel : ViewModelBase, IModalDialogViewModel
{
    public IReadOnlyList<string> Containers { get; private set; } = ["mp4", "mkv"];
    public bool IsVobOutput => Container == "vob";
    /// <summary>TS 原包导出：封装与源文件相同，只能选择轨道（<see cref="RemuxExportTrackChoice.Index"/> 为 PID）。</summary>
    public bool IsTsOutput { get; private set; }
    public bool IsRemuxOutput => !IsTsOutput;
    public string TsContainerText { get; private set; } = "";
    /// <summary>当前为智能剪辑：只输出预览视频所在节目的音视频和字幕。</summary>
    public bool ShowsFrameAccurateHint { get; private set; }
    partial void OnContainerChanged(string value) => OnPropertyChanged(nameof(IsVobOutput));
    [ObservableProperty] private string _container = "mp4";
    [ObservableProperty] private bool _fastSubtitleSeek = true;
    public List<RemuxExportTrackChoice> Tracks { get; } = [];
    private bool _isMatroskaSource;
    public bool CanUseFastSubtitleSeek => _isMatroskaSource && Tracks.Any(track => track.IsTextSubtitle && track.IsSelected);

    internal void Initialize(RemuxExportSettings settings, IEnumerable<RemuxExportTrackChoice> tracks, bool isMatroskaSource)
    {
        if (settings.Disc is { } disc)
        {
            Containers = [disc.Kind == Models.Discs.DiscKind.Dvd ? "vob" : "m2ts", "mkv", "mp4"];
            OnPropertyChanged(nameof(Containers));
        }
        Container = settings.Container;
        FastSubtitleSeek = settings.FastSubtitleSeek;
        _isMatroskaSource = isMatroskaSource;
        foreach (var choice in tracks)
        {
            choice.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(RemuxExportTrackChoice.IsSelected))
                    OnPropertyChanged(nameof(CanUseFastSubtitleSeek));
            };
            Tracks.Add(choice);
        }
        OnPropertyChanged(nameof(CanUseFastSubtitleSeek));
    }

    internal void InitializeTs(string container, IEnumerable<RemuxExportTrackChoice> tracks, bool frameAccurate)
    {
        IsTsOutput = true;
        TsContainerText = string.Format(Utils.Application.LocalizationManager.Instance.String_TsTracks_SameAsSource,
            container.ToUpperInvariant());
        ShowsFrameAccurateHint = frameAccurate;
        Tracks.AddRange(tracks);
    }

    public bool? DialogResult { get; private set; }
    public event Action? RequestClose;

    [RelayCommand] private void Confirm() { DialogResult = true; RequestClose?.Invoke(); }
    [RelayCommand] private void Cancel() => RequestClose?.Invoke();

    internal RemuxExportSettings Apply(RemuxExportSettings source)
    {
        var result = source.Snapshot();
        result.Container = Container;
        result.FastSubtitleSeek = FastSubtitleSeek;
        result.StreamIndices = Tracks.Where(track => track.IsSelected).Select(track => track.Index).ToList();
        return result;
    }
}
