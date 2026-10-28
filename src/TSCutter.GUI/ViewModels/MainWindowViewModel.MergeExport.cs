using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Classic.CommonControls.Dialogs;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.FileSystem;
using HanumanInstitute.MvvmDialogs.FrameworkDialogs;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Models.TransportStream.Processing;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels.Editing;

namespace TSCutter.GUI.ViewModels;

public partial class MainWindowViewModel
{
    // 固定本次任务的边界和设置，后续检查、选择路径及导出均使用同一份快照。
    private sealed record MergeExportSnapshot(
        PickedClip[] Clips, VideoInstance Video, long SettingsRevision,
        string Extension, string FileFilterName, TsClipMergeRequest TsRequest,
        IReadOnlyList<RemuxExportSegment>? RemuxSegments);

    private MergeExportSnapshot? CaptureMergeExportSnapshot()
    {
        var selected = Clips.Where(clip => clip.IsSelected).OrderBy(clip => clip.StartPosition).ToArray();
        if (selected.Length < 2 || _videoInstance is not { } video) return null;

        var programId = SelectedOutputProgramId;
        var streamPids = video.GetMergeStreamPids(programId is not null);
        var request = new TsClipMergeRequest
        {
            SourcePath = selected[0].InFileInfo.FullName,
            OutputPath = string.Empty,
            ProgramId = programId,
            VideoPid = streamPids.VideoPid,
            AudioPids = streamPids.AudioPids,
            Ranges = selected.Select(clip => new TsClipMergeRange(
                clip.StartPosition, clip.EndPosition, clip.StartTime,
                clip.EndTime < 0 ? DurationMax : clip.EndTime)).ToArray()
        };
        return new(selected, video, _exportSettingsRevision, ClipExtension, ExportFileFilterName,
            request, IsRemuxExport ? CreateMergedRemuxSegments(selected) : null);
    }

    private bool IsMergeExportCurrent(MergeExportSnapshot snapshot)
    {
        if (_exportSettingsRevision != snapshot.SettingsRevision ||
            !ReferenceEquals(_videoInstance, snapshot.Video) || SelectedClipCount != snapshot.Clips.Length)
            return false;

        // 同时核对片段身份和边界，避免删除后重新添加、或改变切点时沿用旧任务。
        var currentClips = Clips.ToHashSet();
        for (var index = 0; index < snapshot.Clips.Length; index++)
        {
            var clip = snapshot.Clips[index];
            var range = snapshot.TsRequest.Ranges[index];
            if (!clip.IsSelected || !currentClips.Contains(clip) ||
                clip.StartPosition != range.StartPosition || clip.EndPosition != range.EndPosition ||
                clip.StartTime != range.StartTimeSeconds ||
                (clip.EndTime < 0 ? DurationMax : clip.EndTime) != range.EndTimeSeconds)
                return false;
        }
        return true;
    }

    private static void ConfigureMergeExportTask(OutputWindowViewModel output, MergeExportSnapshot snapshot, string path)
    {
        // 选好路径后只替换目标位置，保留准备阶段已确认的节目表处理方式。
        if (snapshot.RemuxSegments is { } segments)
            output.SetRemuxTask(new RemuxExportRequest(path, segments));
        else
        {
            var request = snapshot.TsRequest;
            output.SetMergeTask(new TsClipMergeRequest
            {
                SourcePath = request.SourcePath, OutputPath = path, ProgramId = request.ProgramId,
                VideoPid = request.VideoPid, AudioPids = request.AudioPids, Ranges = request.Ranges
            });
        }
    }

    private async Task<string?> ChooseMergedOutputPathAsync(MergeExportSnapshot snapshot)
    {
        var sourcePath = snapshot.TsRequest.SourcePath;
        var settings = new SaveFileDialogSettings
        {
            Title = LocalizationManager.Instance.String_MergeClips_Title,
            SuggestedStartLocation = new DesktopDialogStorageFolder(Path.GetDirectoryName(sourcePath)!),
            SuggestedFileName = Path.GetFileNameWithoutExtension(sourcePath) + "_merged." + snapshot.Extension,
            Filters =
            [
                new(snapshot.FileFilterName, [snapshot.Extension]),
                new(LocalizationManager.Instance.String_AllFiles, "*")
            ],
            DefaultExtension = snapshot.Extension
        };
        var result = await _dialogService.ShowSaveFileDialogAsync(this, settings);
        if (result?.Path is null || !IsMergeExportCurrent(snapshot)) return null;

        var path = result.Path.LocalPath;
        if (!PathsEqual(sourcePath, path)) return path;
        await ShowMessageAsync(LocalizationManager.Instance.String_MergeClips_OutputMatchesSource,
            LocalizationManager.Instance.String_Error, MessageBoxIcon.Error);
        return null;
    }

    private async Task CompleteMergedExportAsync(PickedClip[] clips, string outputPath, OutputWindowViewModel output)
    {
        if (output.Exception is not null)
        {
            await ShowMessageAsync(FormatMergeError(output.Exception),
                LocalizationManager.Instance.String_Error, MessageBoxIcon.Error);
            return;
        }
        if (output.DialogResult != true) return;

        if (File.Exists(outputPath))
        {
            // 所有片段指向同一合并结果，用于显示总大小和恢复项目状态。
            var group = new MergedClipOutput(Guid.NewGuid().ToString("N"), clips.Length);
            var check = BuildClipExportCheck(outputPath, output.Outcomes);
            foreach (var clip in clips)
            {
                clip.MergedOutput = group;
                clip.OutputFileInfo = new FileInfo(outputPath);
                clip.ExportStatus = ClipExportStatus.Done;
                clip.ExportCheck = check;
            }
            OnPropertyChanged(nameof(SelectedClipEstimatedSizeStr));
        }
        await ShowMessageAsync(string.Format(LocalizationManager.Instance.String_MergeClips_Completed, outputPath),
            LocalizationManager.Instance.String_MergeClips_Title, MessageBoxIcon.Information);
    }
}
