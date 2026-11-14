using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models.Media;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Media;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Media;
using TSCutter.GUI.ViewModels.Media;

namespace TSCutter.GUI.ViewModels;

// 多轨合并：按节目记录是否把三条立体声音轨合并为一条 5.1。与杜比 E 设置一样只随工程文件和导出任务保存，
// 新打开的文件默认不合并；重新打开同一文件（切换预览轨道）时保留设置。修改后把已导出的片段标为待导出。
public partial class MainWindowViewModel
{
    private Dictionary<int, ChannelMergeSettings> _channelMergeSettings = [];
    // 当前设置所属的文件。
    private string? _channelMergePath;

    private IReadOnlyList<ChannelMergeProgramInfo> ChannelMergePrograms => _videoInstance?.ChannelMergePrograms ?? [];

    /// <summary>当前预览的节目可以合并（有三条以上采样率相同的立体声音轨）。</summary>
    private ChannelMergeProgramInfo? CurrentChannelMergeProgram
    {
        get
        {
            var programs = ChannelMergePrograms;
            if (programs.Count == 0)
                return null;
            var selected = _videoInstance?.StreamSelection?.ProgramId;
            return programs.FirstOrDefault(program => program.ProgramId == selected) ?? (selected is null ? programs[0] : null);
        }
    }

    public bool HasChannelMergeCandidates => IsVideoInitialized && CurrentChannelMergeProgram is not null;
    public bool IsChannelMergeConfigured => ActiveChannelMergeSettings.Any();

    /// <summary>当前文件中仍然有效、需要合并的设置。</summary>
    private IEnumerable<ChannelMergeSettings> ActiveChannelMergeSettings => _channelMergeSettings.Values
        .Where(settings => settings.HasWork && ChannelMergePrograms.Any(program => program.ProgramId == settings.ProgramId &&
            settings.SourcePids.All(pid => program.Tracks.Any(track => track.Pid == pid))));

    /// <summary>导出任务使用的音轨重建计划快照（杜比 E 转码与多轨合并）；都不处理时为 null。</summary>
    private AudioRebuildPlan? CurrentAudioRebuildPlan => AudioRebuildPlan.Create(ActiveDolbyESettings, ActiveChannelMergeSettings);

    public string ChannelMergeButtonTip
    {
        get
        {
            var strings = LocalizationManager.Instance;
            var lines = ActiveChannelMergeSettings.Select(settings =>
            {
                var outputs = string.Join(", ", settings.Outputs.Where(output => output.Enabled)
                    .Select(output => AudioOutputCodecInfo.Name(output.Codec)));
                return string.Format(settings.KeepOriginal ? strings.String_ChannelMerge_SummaryKeep
                    : strings.String_ChannelMerge_SummaryReplace, outputs);
            }).ToArray();
            return lines.Length == 0 ? strings.String_ChannelMerge_ButtonTip
                : strings.String_ChannelMerge_ButtonTip + "\n" + string.Join("\n", lines);
        }
    }

    private bool CanConfigureChannelMerge => CanEditVideo && HasChannelMergeCandidates;

    /// <summary>打开文件后：换了文件时按新文件重建（默认不合并），重新打开同一文件时保留设置。</summary>
    private void ResetChannelMergeSettings()
    {
        var path = ChannelMergePrograms.Count > 0 ? VideoPath : null;
        var changed = path != _channelMergePath && _channelMergeSettings.Values.Any(settings => settings.HasWork);
        if (path != _channelMergePath)
            _channelMergeSettings = [];
        _channelMergePath = path;
        foreach (var program in ChannelMergePrograms.Where(program => !_channelMergeSettings.ContainsKey(program.ProgramId)))
            _channelMergeSettings[program.ProgramId] = ChannelMergeDefaults.Create(program);
        NotifyChannelMergeChanged(changed);
        UpdateChannelMergePreviewTrack();
    }

    /// <summary>从工程恢复：只恢复当前文件中仍存在的节目，来源音轨都还在时才恢复映射。</summary>
    private void RestoreChannelMergeSettings(IEnumerable<ChannelMergeSettings>? saved)
    {
        foreach (var settings in saved ?? [])
        {
            var program = ChannelMergePrograms.FirstOrDefault(item => item.ProgramId == settings.ProgramId);
            if (program is null || !settings.IsValid ||
                !settings.SourcePids.All(pid => program.Tracks.Any(track => track.Pid == pid)))
                continue;
            _channelMergeSettings[settings.ProgramId] = settings.Snapshot();
        }
        _channelMergePath = ChannelMergePrograms.Count > 0 ? VideoPath : null;
        NotifyChannelMergeChanged();
        UpdateChannelMergePreviewTrack();
    }

    private List<ChannelMergeSettings>? CaptureChannelMergeSettings()
    {
        var saved = _channelMergeSettings.Values.Where(settings => settings.Merge)
            .OrderBy(settings => settings.ProgramId).Select(settings => settings.Snapshot()).ToList();
        return saved.Count > 0 ? saved : null;
    }

    private void NotifyChannelMergeChanged(bool projectChanged = true)
    {
        OnPropertyChanged(nameof(HasChannelMergeCandidates));
        OnPropertyChanged(nameof(IsChannelMergeConfigured));
        OnPropertyChanged(nameof(ChannelMergeButtonTip));
        if (projectChanged)
            OnPropertyChanged(nameof(HasUnsavedProjectChanges));
        ConfigureChannelMergeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanConfigureChannelMerge))]
    private async Task ConfigureChannelMergeAsync()
    {
        var video = _videoInstance;
        if (CurrentChannelMergeProgram is not { } program)
            return;
        var path = VideoPath;
        var disc = video?.Disc;
        var syncOffset = video?.TsSyncOffset ?? -1;
        var dialog = _dialogService.CreateViewModel<ChannelMergeWindowViewModel>();
        var current = _channelMergeSettings.TryGetValue(program.ProgramId, out var settings)
            ? settings.Snapshot() : ChannelMergeDefaults.Create(program);
        dialog.Initialize(program, current, (pids, selected, progress, token) => Task.Run(() =>
            ChannelMergeAnalyzer.Analyze(path, disc, syncOffset, pids, selected, progress, token), token));
        await _dialogService.ShowDialogAsync(this, dialog);
        if (dialog.DialogResult != true || !ReferenceEquals(video, _videoInstance))
            return;
        var before = CurrentAudioRebuildPlan;
        _channelMergeSettings[program.ProgramId] = dialog.Result;
        // 导出结果取决于重建计划；计划有变化时已导出的片段需要重新导出。
        if (AudioRebuildPlan.Signature(before) != AudioRebuildPlan.Signature(CurrentAudioRebuildPlan))
            InvalidateClipExports();
        NotifyChannelMergeChanged();
        UpdateChannelMergePreviewTrack();
    }

    /// <summary>
    /// 试听：当前节目设置了合并时，音频下拉末尾加一项“合并 5.1（下混试听）”，播放与波形按合并结果下混为立体声；
    /// 取消合并时去掉。正在试听时映射改变，按新映射重新开始。
    /// </summary>
    private void UpdateChannelMergePreviewTrack()
    {
        var tracks = PreviewAudioTracks.Where(track => track.ChannelMerge is null).ToList();
        var settings = CurrentChannelMergeProgram is { } program &&
                       _channelMergeSettings.TryGetValue(program.ProgramId, out var current) &&
                       ActiveChannelMergeSettings.Contains(current) ? current : null;
        PreviewAudioTrackOption? merged = null;
        if (settings is not null && tracks.FirstOrDefault(track => track.DolbyEProgram is null &&
                                                                   track.Selection.StreamId == settings.AnchorPid) is { } anchor)
        {
            merged = new PreviewAudioTrackOption(anchor.Selection,
                string.Format(LocalizationManager.Instance.String_ChannelMerge_PreviewTrack,
                    string.Join(" + ", settings.SourcePids.Select(pid => $"0x{pid:X4}"))),
                channelMerge: settings.Sources.Select(source => new PreviewChannelMergeSource(source.Pid, source.Swap)).ToArray());
            tracks.Add(merged);
        }
        var existing = PreviewAudioTracks.FirstOrDefault(track => track.ChannelMerge is not null);
        if (merged is null && existing is null)
            return;
        // 映射没有变化时保留原来的选项，不打断试听。
        if (merged is not null && existing is not null && existing.ChannelMerge!.SequenceEqual(merged.ChannelMerge!))
            return;
        var previous = SelectedPreviewAudioTrack;
        updatingPreviewAudio = true;
        try
        {
            PreviewAudioTracks = tracks;
            SelectedPreviewAudioTrack = previous?.ChannelMerge is not null
                ? merged ?? tracks.FirstOrDefault(track => track.CanDecode) ?? tracks.FirstOrDefault()
                : previous;
            OnPropertyChanged(nameof(HasPreviewAudioTracks));
            OnPropertyChanged(nameof(CanSelectPreviewAudio));
        }
        finally { updatingPreviewAudio = false; }
        if (!ReferenceEquals(previous, SelectedPreviewAudioTrack))
            RestartPreviewAudio();
    }
}
