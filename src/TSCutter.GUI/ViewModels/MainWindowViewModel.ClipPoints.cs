using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Classic.CommonControls.Dialogs;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs.FrameworkDialogs;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.ViewModels.Editing;

namespace TSCutter.GUI.ViewModels;

public partial class MainWindowViewModel
{
    private bool clipPointTransferActive;
    private CancellationTokenSource? clipPointCancellation;
    private bool CanImportClipPoints => CanEditVideo && _discInput is null && !IsDecoding && !_isResolvingClipEnd && !clipPointTransferActive;
    private bool CanExportClipPoints => CanImportClipPoints && Clips.Count > 0;

    private void SetClipPointTransferActive(bool active)
    {
        clipPointTransferActive = active;
        NotifyVideoCapabilityChanged();
        NotifyHistoryChanged();
        OnPropertyChanged(nameof(CanToggleFrameAccurate));
    }

    [RelayCommand(CanExecute = nameof(CanExportClipPoints))]
    private async Task ExportClipPointsAsync()
    {
        if (!CanExportClipPoints)
            return;
        await PausePlaybackForNavigationAsync();
        var video = _videoInstance!;
        var strings = LocalizationManager.Instance;
        var selected = await _dialogService.ShowSaveFileAtExportFolderAsync(this, new SaveFileDialogSettings
        {
            Title = strings.String_ClipPoints_ExportTitle,
            SuggestedFileName = Path.GetFileNameWithoutExtension(VideoPath) + "." + ClipPointService.Extension,
            DefaultExtension = ClipPointService.Extension,
            Filters = [new(strings.String_ClipPoints_Files, [ClipPointService.Extension])]
        }, Path.GetDirectoryName(VideoPath));
        if (selected?.Path is null || !CanExportClipPoints || !ReferenceEquals(video, _videoInstance))
            return;
        var file = new ClipPointFile
        {
            Source = new ProjectSource { Path = VideoPath }, PreviewSelection = video.StreamSelection,
            Duration = DurationMax, FirstFramePts = video.FirstFramePts,
            TimeBaseNumerator = video.TimeBaseNumerator, TimeBaseDenominator = video.TimeBaseDenominator,
            TimeBasedSource = video.CanRemuxClip || UsesFrameAccurateCut,
            CutMode = UsesFrameAccurateCut ? ClipCutMode.FrameAccurate : ClipCutMode.KeyFrame,
            ClipMode = ClipMode, Clips = CaptureProject(includeThumbnails: false).Clips.Select(ClipPointService.Copy).ToList()
        };
        // 缩略图先在界面线程复制，后台序列化期间编辑器可释放原位图。
        var thumbnails = Clips.SelectMany(clip => new[] { CopyProjectThumbnail(clip.StartThumbnail), CopyProjectThumbnail(clip.EndThumbnail) }).ToArray();
        using var cancellation = new CancellationTokenSource();
        clipPointCancellation = cancellation;
        SetClipPointTransferActive(true);
        try
        {
            await RunExportPreparationAsync(async (_, token) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellation.Token);
                await Task.Run(() =>
                {
                    for (var index = 0; index < file.Clips.Count; index++)
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        file.Clips[index].StartThumbnailJpeg = EncodeProjectThumbnail(thumbnails[index * 2]);
                        file.Clips[index].EndThumbnailJpeg = EncodeProjectThumbnail(thumbnails[index * 2 + 1]);
                    }
                    var source = new CutterProject { Source = file.Source, Clips = file.Clips };
                    CutterProjectService.PopulateSources(source);
                    file.Source = source.Source!;
                }, linked.Token);
                await ClipPointService.SaveAsync(selected.Path.LocalPath, file, linked.Token);
                return true;
            }, () => ReferenceEquals(video, _videoInstance), strings.String_ClipPoints_Export);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Console.WriteLine($"Clip point export failed: {error}");
            var message = error is IOException && MediaFileAccess.Current.IsOutputConflict(selected.Path.LocalPath)
                ? error.Message : strings.String_ClipPoints_ExportFailed;
            if (ReferenceEquals(video, _videoInstance))
                await ShowMessageAsync(message, strings.String_Error, MessageBoxIcon.Error);
        }
        finally
        {
            foreach (var image in thumbnails)
                image?.Dispose();
            clipPointCancellation = null;
            SetClipPointTransferActive(false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanImportClipPoints))]
    private async Task ImportClipPointsAsync()
    {
        if (!CanImportClipPoints)
            return;
        await PausePlaybackForNavigationAsync();
        var video = _videoInstance!;
        var strings = LocalizationManager.Instance;
        var paths = await _dialogService.ShowOpenFilesAtImportFolderAsync(this, new OpenFileDialogSettings
        {
            Title = strings.String_ClipPoints_Title,
            Filters = [new(strings.String_ClipPoints_Files, [ClipPointService.Extension])]
        });
        if (paths.Count == 0 || !CanImportClipPoints || !ReferenceEquals(video, _videoInstance))
            return;
        try
        {
            var file = await ClipPointService.LoadAsync(paths[0].LocalPath);
            var targetPath = VideoPath;
            var selection = video.StreamSelection;
            var matches = await Task.Run(() => ClipPointService.Matches(file, targetPath, selection,
                video.FirstFramePts, video.TimeBaseNumerator, video.TimeBaseDenominator));
            if (!CanImportClipPoints || !ReferenceEquals(video, _videoInstance))
                return;
            // 保存的智能剪辑模式当前不可用时不能精确恢复，仍可按当前模式按时间转移。
            var exactUnavailable = matches && file.CutMode == ClipCutMode.FrameAccurate && !CanSelectFrameAccurate;
            using var dialog = _dialogService.CreateViewModel<ClipPointImportViewModel>();
            dialog.Initialize(file, DurationMax, _displayedFrameTime, matches && !exactUnavailable,
                UsesFrameAccurateCut ? ClipCutMode.FrameAccurate : ClipCutMode.KeyFrame, ClipMode, Clips.Count, exactUnavailable);
            await _dialogService.ShowDialogAsync(this, dialog);
            if (dialog.DialogResult != true || !CanImportClipPoints || !ReferenceEquals(video, _videoInstance))
                return;
            using var cancellation = new CancellationTokenSource();
            clipPointCancellation = cancellation;
            SetClipPointTransferActive(true);
            try
            {
                var before = Clips.Select(ClipStateSnapshot.Create).ToArray();
                var revision = _exportSettingsRevision;
                var ranges = dialog.Ranges.Where(range => range.InRange).ToArray();
                var mapped = await RunExportPreparationAsync(async (progress, token) =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellation.Token);
                    if (dialog.Exact)
                    {
                        var unchanged = await Task.Run(() => ClipPointService.Matches(file, targetPath, selection,
                            video.FirstFramePts, video.TimeBaseNumerator, video.TimeBaseDenominator), linked.Token);
                        if (!unchanged)
                            throw new InvalidDataException("The source changed during import.");
                        return (IReadOnlyList<ProjectClip>)ranges.Select(range => ClipPointService.Copy(range.Clip)).ToArray();
                    }
                    return await ClipPointService.RelocateAsync(targetPath, selection, ranges, dialog.ImportCutMode,
                        new Progress<int>(completed => progress.Report(new ExportPreparationProgress(completed, ranges.Length, Path.GetFileName(targetPath)))),
                        linked.Token);
                }, () => ReferenceEquals(video, _videoInstance), strings.String_ClipPoints_Locating);
                cancellation.Token.ThrowIfCancellationRequested();
                // 缩略图在后台解码，大量片段时界面线程只需加入列表。
                var thumbnails = await Task.Run(() => DecodeImportedThumbnails(mapped), cancellation.Token);
                try
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(video, _videoInstance) || revision != _exportSettingsRevision ||
                        !before.SequenceEqual(Clips.Select(ClipStateSnapshot.Create)))
                        throw new InvalidOperationException("The workspace changed during import.");
                    ApplyImportedClipPoints(mapped, dialog.Replace, dialog.ImportCutMode, file.ClipMode, thumbnails);
                    thumbnails = null;
                }
                finally
                {
                    DisposeThumbnails(thumbnails);
                }
            }
            finally
            {
                clipPointCancellation = null;
                SetClipPointTransferActive(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Console.WriteLine($"Clip point import failed: {error}");
            if (ReferenceEquals(video, _videoInstance))
                await ShowMessageAsync(strings.String_ClipPoints_ImportFailed, strings.String_Error, MessageBoxIcon.Error);
        }
    }

    private static (Bitmap? Start, Bitmap? End)[] DecodeImportedThumbnails(IReadOnlyList<ProjectClip> points)
    {
        var thumbnails = new (Bitmap? Start, Bitmap? End)[points.Count];
        for (var index = 0; index < points.Count; index++)
            thumbnails[index] = (DecodeProjectThumbnail(points[index].StartThumbnailJpeg), DecodeProjectThumbnail(points[index].EndThumbnailJpeg));
        return thumbnails;
    }

    /// <summary>释放尚未交给片段的缩略图并清空对应项，重复调用不会重复释放。</summary>
    private static void DisposeThumbnails((Bitmap? Start, Bitmap? End)[]? thumbnails)
    {
        for (var index = 0; index < (thumbnails?.Length ?? 0); index++)
        {
            thumbnails![index].Start?.Dispose();
            thumbnails[index].End?.Dispose();
            thumbnails[index] = default;
        }
    }

    /// <param name="thumbnails">与 points 一一对应、已解码的起止缩略图，由本方法接管；为 null 时在此解码。</param>
    internal void ApplyImportedClipPoints(IReadOnlyList<ProjectClip> points, bool replace, ClipCutMode mode, ClipListMode listMode,
        (Bitmap? Start, Bitmap? End)[]? thumbnails = null)
    {
        thumbnails ??= DecodeImportedThumbnails(points);
        var imported = new List<PickedClip>();
        try
        {
            for (var index = 0; index < points.Count; index++)
            {
                var point = points[index];
                var clip = new PickedClip
                {
                    InFileInfo = new FileInfo(VideoPath), RequiresRemux = IsRemuxExport,
                    RequiresFrameAccurate = point.FrameAccurate, UsesFrameAccurateExport = mode == ClipCutMode.FrameAccurate,
                    CanEstimateFrameAccurateSize = CanEstimateFrameAccurateSize,
                    Name = point.Name, Notes = point.Notes, Color = point.Color,
                    StartTime = point.StartTime, EndTime = point.EndTime, StartPts = point.StartPts, EndPts = point.EndPts,
                    StartPosition = point.StartPosition, EndPosition = point.EndPosition, EndFramePosition = point.EndFramePosition,
                    StartSignature = point.StartSignature, EndSignature = point.EndSignature,
                    NeedsBoundaryReview = point.NeedsBoundaryReview, IsSelected = true
                };
                imported.Add(clip);
                clip.ReplaceStartThumbnail(thumbnails[index].Start);
                thumbnails[index].Start = null;
                clip.ReplaceEndThumbnail(thumbnails[index].End);
                thumbnails[index].End = null;
            }
        }
        catch
        {
            foreach (var clip in imported)
                clip.Dispose();
            DisposeThumbnails(thumbnails);
            throw;
        }
        // 整次导入作为一条带模式的撤销记录；替换下来的片段不释放，撤销时连同缩略图恢复。
        var entry = RecordBulkHistory();
        if (replace)
        {
            entry?.DetachedClips?.AddRange(Clips);
            if (entry is null)
                DisposeClipThumbnails();
            Clips.Clear();
        }
        else
            foreach (var clip in Clips)
                clip.IsSelected = false;
        if (CutMode != mode)
            _exportSettingsRevision++;
        ApplyCutMode(mode);
        ClipMode = listMode;
        foreach (var clip in imported)
            Clips.Add(clip);
        SelectedClip = imported.FirstOrDefault();
        _clipSelectionAnchor = SelectedClip;
        NotifyClipSelectionChanged();
    }
}
