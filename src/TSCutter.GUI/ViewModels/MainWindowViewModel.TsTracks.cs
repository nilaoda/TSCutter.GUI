using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels.Editing;

namespace TSCutter.GUI.ViewModels;

// TS 导出的轨道选择：按 PID 记录取消勾选的轨道，默认全部保留（输出与原来相同）。与杜比 E 设置一样只随工程和导出任务保存，
// 打开另一个文件时恢复为全部保留；修改后把已导出的片段标为待导出。预览视频所在的 PID 始终输出。
public partial class MainWindowViewModel
{
    private HashSet<int> _excludedTsPids = [];
    // 当前选择所属的文件；切换预览轨道会重新打开同一文件，此时保留选择。
    private string? _tsTrackSelectionPath;

    private IReadOnlyList<TsExportTrack> TsExportTracks => _videoInstance?.TsExportTracks ?? [];

    /// <summary>当前文件按 TS 原包导出（不是重封装），可以选择输出的轨道。</summary>
    public bool IsTsTrackExport => _videoInstance is { Inited: true, CanRemuxClip: false, CanBinaryClip: true } &&
                                   TsExportTracks.Count > 0;

    /// <summary>“导出设置”入口：重封装选择封装与轨道，TS 原包导出只选择轨道。</summary>
    public bool ShowsExportSettings => IsRemuxExport || IsTsTrackExport;

    /// <summary>导出范围内的轨道：勾选“仅导出当前节目”时只有该节目的轨道。</summary>
    private IEnumerable<TsExportTrack> TsTracksInScope => SelectedOutputProgramId is { } program
        ? TsExportTracks.Where(track => track.Programs.Contains(program))
        : TsExportTracks;

    /// <summary>导出任务使用的快照：范围内取消勾选的 PID（不含预览视频）；全部保留时为 null。</summary>
    private IReadOnlyList<int>? CurrentExcludedPids
    {
        get
        {
            if (!IsTsTrackExport)
                return null;
            var videoPid = _videoInstance!.VideoPid;
            var excluded = TsTracksInScope.Select(track => track.Pid)
                .Where(pid => pid != videoPid && _excludedTsPids.Contains(pid)).ToArray();
            return excluded.Length > 0 ? excluded : null;
        }
    }

    public bool HasTsTrackSelection => CurrentExcludedPids is not null;

    public string TsTrackSummary
    {
        get
        {
            var total = TsTracksInScope.Count();
            var excluded = CurrentExcludedPids?.Count ?? 0;
            return excluded == 0
                ? string.Format(LocalizationManager.Instance.String_TsTracks_SummaryAll, total)
                : string.Format(LocalizationManager.Instance.String_TsTracks_Summary, total - excluded, total);
        }
    }

    /// <summary>打开文件后：换了文件时恢复为全部保留，重新打开同一文件（切换预览轨道）时保留选择。</summary>
    private void ResetTsTrackSelection()
    {
        var path = IsTsTrackExport ? VideoPath : null;
        var cleared = path != _tsTrackSelectionPath && _excludedTsPids.Count > 0;
        if (path != _tsTrackSelectionPath)
            _excludedTsPids = [];
        _tsTrackSelectionPath = path;
        // 选择没有变化时不通知工程变更，避免每次刷新预览音轨都重新序列化工程。
        NotifyTsTracksChanged(cleared);
    }

    /// <summary>从工程恢复：只恢复当前文件中仍存在的轨道。</summary>
    private void RestoreTsTrackSelection(IEnumerable<int>? saved)
    {
        var pids = TsExportTracks.Select(track => track.Pid).ToHashSet();
        _excludedTsPids = (saved ?? []).Where(pids.Contains).ToHashSet();
        _tsTrackSelectionPath = IsTsTrackExport ? VideoPath : null;
        NotifyTsTracksChanged();
    }

    private List<int>? CaptureTsTrackSelection() => IsTsTrackExport && _excludedTsPids.Count > 0
        ? _excludedTsPids.Order().ToList()
        : null;

    private void NotifyTsTracksChanged(bool selectionChanged = true)
    {
        OnPropertyChanged(nameof(IsTsTrackExport));
        OnPropertyChanged(nameof(ShowsExportSettings));
        OnPropertyChanged(nameof(HasTsTrackSelection));
        OnPropertyChanged(nameof(TsTrackSummary));
        OnPropertyChanged(nameof(ShowRawClipSizeEstimate));
        if (selectionChanged)
            OnPropertyChanged(nameof(HasUnsavedProjectChanges));
        RemuxExportOptionsCommand.NotifyCanExecuteChanged();
    }

    private async Task ConfigureTsTracksAsync()
    {
        var video = _videoInstance!;
        var dolbyE = CurrentDolbyEPlan?.Tracks.Select(track => track.Pid).ToHashSet() ?? [];
        var tracks = TsTracksInScope.ToArray();
        // 多个节目时按节目分组；同一 PID 只列在它所属的第一个节目下。
        var grouped = tracks.GroupBy(track => track.Programs.Count > 0 ? track.Programs[0] : (int?)null)
            .OrderBy(group => group.Key is null).ThenBy(group => group.Key).ToArray();
        var choices = new List<RemuxExportTrackChoice>();
        foreach (var group in grouped)
        {
            var header = grouped.Length > 1 ? ProgramTitle(group.Key) : "";
            foreach (var track in group)
            {
                choices.Add(new RemuxExportTrackChoice
                {
                    Index = track.Pid,
                    Description = track.Description +
                                  (dolbyE.Contains(track.Pid) ? " · " + LocalizationManager.Instance.String_Export_Tag_DolbyE : ""),
                    CanChange = track.Pid != video.VideoPid,
                    GroupHeader = header,
                    IsSelected = track.Pid == video.VideoPid || !_excludedTsPids.Contains(track.Pid),
                    AudioStreamIndex = track.Track.Type == AVMediaType.AVMEDIA_TYPE_AUDIO ? track.Track.Index : -1
                });
                header = "";
            }
        }
        var dialog = _dialogService.CreateViewModel<RemuxExportOptionsViewModel>();
        dialog.InitializeTs(video.ClipExtension, choices, UsesFrameAccurateCut);
        dialog.EnableSilenceDetection(AnalyzeAudioTracks(video, VideoPath));
        await _dialogService.ShowDialogAsync(this, dialog);
        if (dialog.DialogResult != true || !ReferenceEquals(video, _videoInstance))
            return;
        var before = CurrentExcludedPids ?? [];
        // 范围之外（其他节目）的选择保持不变。
        var scope = tracks.Select(track => track.Pid).ToHashSet();
        _excludedTsPids.RemoveWhere(scope.Contains);
        _excludedTsPids.UnionWith(dialog.Tracks.Where(track => !track.IsSelected).Select(track => track.Index));
        if (!before.SequenceEqual(CurrentExcludedPids ?? []))
            InvalidateClipExports();
        NotifyTsTracksChanged();
    }

    private string ProgramTitle(int? program) => program is { } id
        ? PreviewPrograms.FirstOrDefault(option => option.Id == id)?.DisplayText ??
          string.Format(LocalizationManager.Instance.String_PreviewTrack_ProgramFallback, id)
        : LocalizationManager.Instance.String_PreviewTrack_AllStreams;
}
