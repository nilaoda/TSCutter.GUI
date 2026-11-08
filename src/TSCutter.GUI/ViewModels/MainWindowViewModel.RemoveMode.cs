using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Classic.CommonControls.Dialogs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.TransportStream.Processing;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels.Editing;

namespace TSCutter.GUI.ViewModels;

// 删除模式：片段表示要从视频中删除的范围 [起点, 终点)——终点是恢复播放的画面，本身保留。
// 导出时把选中的删除范围取反为保留段，交给现有的合并导出（TS 按字节、MP4/MKV 重新封装、智能剪辑）输出一个文件。
public partial class MainWindowViewModel
{
    [ObservableProperty]
    public partial ClipListMode ClipMode { get; set; }

    public bool IsRemoveMode
    {
        get => ClipMode == ClipListMode.Remove;
        set
        {
            var mode = value ? ClipListMode.Remove : ClipListMode.Keep;
            if (mode == ClipMode)
                return;
            ClipMode = mode;
            // 用户切换开关时记住选择，下次启动沿用；按工程恢复的模式不改动这一偏好。
            _configService.CurrentConfig.ClipListMode = mode;
            _configService.Save();
        }
    }

    /// <summary>工具栏删除模式开关的提示：写明开关状态与删除范围的含义。</summary>
    public string RemoveModeToolTip => IsRemoveMode
        ? LocalizationManager.Instance.String_RemoveMode_On + Environment.NewLine + LocalizationManager.Instance.String_RemoveMode_Tip
        : LocalizationManager.Instance.String_RemoveMode_Off;

    partial void OnClipModeChanged(ClipListMode value)
    {
        foreach (var clip in Clips)
            clip.IsRemoval = value == ClipListMode.Remove;
        OnPropertyChanged(nameof(IsRemoveMode));
        OnPropertyChanged(nameof(RemoveModeToolTip));
        OnPropertyChanged(nameof(ClipOutputCommand));
        OnPropertyChanged(nameof(ClipOutputText));
        OnPropertyChanged(nameof(ClipOutputToolTip));
        OnPropertyChanged(nameof(SelectedClipRanges));
        OnPropertyChanged(nameof(KeyFrameStripRanges));
        OnPropertyChanged(nameof(SelectedClipsSummaryStr));
        OnPropertyChanged(nameof(SelectedClipEstimatedSizeStr));
        OnPropertyChanged(nameof(HasUnsavedProjectChanges));
        NotifyRemoveModeCommands();
    }

    private void NotifyRemoveModeCommands()
    {
        ExportRemovingSelectedRangesCommand.NotifyCanExecuteChanged();
        SaveVideoClickCommand.NotifyCanExecuteChanged();
        AddToQueueCommand.NotifyCanExecuteChanged();
        ExportAllCommand.NotifyCanExecuteChanged();
        MergeSelectedClipsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>保留段的一端：时间轴秒数、画面 PTS、TS 字节位置与关键帧压缩包签名（DVD 按画面定位）。</summary>
    private readonly record struct KeptBoundary(double Time, long Pts, long Position, string? Signature = null);

    /// <summary>删除范围取反后的保留段；End 为 null 表示到文件结尾，否则止于该画面之前（不含）。</summary>
    private readonly record struct KeptRange(KeptBoundary Start, KeptBoundary? End);

    // 保留段起止的时间比较容差，与导出定位关键帧的容差一致。
    private const double BoundaryTolerance = 0.002;

    /// <summary>
    /// 把删除范围取反为按时间排列的保留段：重叠或相接的删除范围合并；删除范围从文件开头开始或到文件结尾时，
    /// 对应一侧没有保留段。终点（恢复播放的画面）作为下一段的起点。开头的保留段从首个关键帧开始
    /// （光盘标题的首帧可能略晚于时间轴原点）。
    /// </summary>
    private List<KeptRange> CreateKeptRanges(IEnumerable<PickedClip> removed)
    {
        var video = _videoInstance!;
        var result = new List<KeptRange>();
        KeptBoundary? cursor = new KeptBoundary(video.FirstKeyFrameSeconds, video.FirstFramePts, 0);
        foreach (var clip in removed.OrderBy(clip => clip.StartTime))
        {
            if (cursor is not { } start)
                break;
            // 删除起点晚于当前保留起点时，中间是一段保留内容。
            if (clip.StartTime - start.Time > BoundaryTolerance)
                result.Add(new KeptRange(start,
                    new KeptBoundary(clip.StartTime, clip.StartPts, clip.StartPosition, clip.StartSignature)));
            if (clip.EndPosition < 0)
            {
                cursor = null;
                continue;
            }
            // 重叠的删除范围取较晚的终点。
            if (clip.EndTime - start.Time > BoundaryTolerance)
                cursor = new KeptBoundary(clip.EndTime, clip.EndPts, clip.EndFramePosition, clip.EndSignature);
        }
        if (cursor is { } last && last.Time < DurationMax)
            result.Add(new KeptRange(last, null));
        return result;
    }

    /// <summary>删除模式的选中汇总：删除的范围数与导出后保留的总时长。</summary>
    private string RemoveModeSummaryStr
    {
        get
        {
            var removed = Clips.Where(clip => clip.IsSelected).ToArray();
            if (removed.Length == 0 || _videoInstance is null)
                return string.Empty;
            var kept = CreateKeptRanges(removed).Sum(range => (range.End?.Time ?? DurationMax) - range.Start.Time);
            return string.Format(LocalizationManager.Instance.String_RemoveMode_Summary, removed.Length,
                CommonUtil.FormatSeconds(Math.Max(0, kept)));
        }
    }

    /// <summary>
    /// 删除模式的预估大小：TS 关键帧剪辑按保留段的源字节范围合计；需要重新封装、按节目过滤或重新编码的输出导出后才能确定。
    /// </summary>
    private string RemoveModeSizeStr
    {
        get
        {
            var removed = Clips.Where(clip => clip.IsSelected).ToArray();
            if (removed.Length == 0)
                return string.Empty;
            // 已按当前选中的范围导出过：显示输出文件的实际大小。
            if (GetSelectedMergedOutputSize(removed: true) is { } actual)
                return actual;
            if (_videoInstance is null)
                return string.Empty;
            if (IsProgramExportFiltered || IsRemuxExport || UsesFrameAccurateCut && !CanEstimateFrameAccurateSize)
                return FilteredClipSizeStr;
            var kept = CreateKeptRanges(removed);
            if (kept.Any(range => range.Start.Position < 0))
                return FilteredClipSizeStr;
            var length = removed[0].InFileInfo.Length;
            var bytes = kept.Sum(range => Math.Max(0, (range.End?.Position ?? length) - range.Start.Position));
            return LocalizationManager.Instance.String_SizePrefix + string.Format(
                LocalizationManager.Instance.String_SizeApproximate, CommonUtil.FormatFileSize(bytes));
        }
    }

    private bool CanExportRemovingSelectedRanges => IsRemoveMode && CanEditVideo &&
        SelectedClipCount >= 1 && Clips.Where(clip => clip.IsSelected).All(clip => clip.EndTime > clip.StartTime);

    /// <summary>删除选中的范围，其余部分合并为一个文件。</summary>
    [RelayCommand(CanExecute = nameof(CanExportRemovingSelectedRanges))]
    private async Task ExportRemovingSelectedRangesAsync()
    {
        var removed = Clips.Where(clip => clip.IsSelected).OrderBy(clip => clip.StartTime).ToArray();
        if (removed.Length == 0 || _videoInstance is null)
            return;
        var kept = CreateKeptRanges(removed);
        if (kept.Count == 0)
        {
            await ShowMessageAsync(LocalizationManager.Instance.String_RemoveMode_NothingLeft,
                LocalizationManager.Instance.String_RemoveMode_Title, MessageBoxIcon.Information);
            return;
        }
        // TS 关键帧剪辑从删除终点画面所在的包开始保留；旧工程中未记录该位置时需要重新标记终点。
        var frameAccurate = UsesFrameAccurateCut;
        if (!frameAccurate && !IsRemuxExport && kept.Any(range => range.Start.Position < 0))
        {
            await ShowMessageAsync(LocalizationManager.Instance.String_RemoveMode_RemarkEnd,
                LocalizationManager.Instance.String_RemoveMode_Title, MessageBoxIcon.Warning);
            return;
        }

        var snapshot = CaptureRemoveExportSnapshot(removed, kept, frameAccurate);
        var boundaries = removed.Select(ClipStateSnapshot.Create).ToArray();
        bool IsCurrent() => IsRemoveMode && ReferenceEquals(_videoInstance, snapshot.Video) &&
                            _exportSettingsRevision == snapshot.SettingsRevision &&
                            Clips.Where(clip => clip.IsSelected).OrderBy(clip => clip.StartTime)
                                .Select(ClipStateSnapshot.Create).SequenceEqual(boundaries);

        var output = _dialogService.CreateViewModel<OutputWindowViewModel>();
        ConfigureMergeExportTask(output, snapshot, string.Empty);
        if (!await ConfirmGradualRefreshExportAsync()) return;
        if (!await PrepareProgramTableExportAsync(output, removed.Select(GetProgramTableClipLabel).ToArray(), IsCurrent))
            return;
        if (!await PrepareTimestampNormalizationAsync(output, IsCurrent))
            return;
        var outputPath = await ChooseMergedOutputPathAsync(snapshot, IsCurrent, "_cut");
        if (outputPath is null) return;
        ConfigureMergeExportTask(output, snapshot, outputPath);
        await _dialogService.ShowDialogAsync(this, output);
        await ForgetMissingClipOutputsAsync([outputPath]);
        if (output.Exception is not null)
            await ShowMessageAsync(FormatMergeError(output.Exception),
                LocalizationManager.Instance.String_Error, MessageBoxIcon.Error);
        else if (output.DialogResult == true)
        {
            // 删除的范围共享这次的输出文件：主界面汇总显示实际大小，修改范围后记录随之失效。
            if (File.Exists(outputPath))
            {
                var group = new MergedClipOutput(Guid.NewGuid().ToString("N"), removed.Length, Removed: true);
                var check = BuildClipExportCheck(outputPath, output.Outcomes);
                foreach (var clip in removed)
                {
                    clip.MergedOutput = group;
                    clip.OutputFileInfo = new FileInfo(outputPath);
                    clip.ExportStatus = ClipExportStatus.Done;
                    clip.ExportCheck = check;
                }
                OnPropertyChanged(nameof(SelectedClipEstimatedSizeStr));
            }
            await ShowMessageAsync(string.Format(LocalizationManager.Instance.String_RemoveMode_Completed, outputPath),
                LocalizationManager.Instance.String_RemoveMode_Title, MessageBoxIcon.Information);
        }
    }

    private MergeExportSnapshot CaptureRemoveExportSnapshot(PickedClip[] removed, List<KeptRange> kept, bool frameAccurate)
    {
        var video = _videoInstance!;
        var sourcePath = removed[0].InFileInfo.FullName;
        var programId = SelectedOutputProgramId;
        var streamPids = video.GetMergeStreamPids(programId is not null);
        var request = new TsClipMergeRequest
        {
            SourcePath = sourcePath,
            OutputPath = string.Empty,
            ProgramId = programId,
            VideoPid = streamPids.VideoPid,
            AudioPids = streamPids.AudioPids,
            // 保留段止于删除起点所在包之前：该关键帧及之后的数据属于删除范围。
            Ranges = kept.Select(range => new TsClipMergeRange(range.Start.Position, range.End?.Position ?? -1,
                range.Start.Time, range.End?.Time ?? DurationMax)).ToArray()
        };
        IReadOnlyList<FrameAccurateExportSegment>? frameAccurateSegments = null;
        IReadOnlyList<RemuxExportSegment>? remuxSegments = null;
        if (frameAccurate)
        {
            var settings = video.CreateFrameAccurateSettings(CurrentRemuxSettings);
            frameAccurateSegments = kept.Select(range => new FrameAccurateExportSegment(sourcePath, range.Start.Pts,
                range.End?.Pts, settings.Snapshot(), EndExclusive: range.End is not null)).ToArray();
        }
        else if (IsRemuxExport)
            remuxSegments = kept.Select(range => new RemuxExportSegment(sourcePath, range.Start.Time, range.End?.Time,
                CurrentRemuxSettings!.Snapshot(), EndExclusive: range.End is not null,
                StartSignature: range.Start.Signature, EndSignature: range.End?.Signature)).ToArray();
        return new MergeExportSnapshot(removed, video, _exportSettingsRevision, ClipExtension, ExportFileFilterName,
            request, remuxSegments, frameAccurateSegments);
    }
}
