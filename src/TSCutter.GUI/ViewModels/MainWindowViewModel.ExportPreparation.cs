using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.ViewModels.Editing;

namespace TSCutter.GUI.ViewModels;

public partial class MainWindowViewModel
{
    private async Task<T> RunExportPreparationAsync<T>(
        Func<IProgress<ExportPreparationProgress>, CancellationToken, Task<T>> operation,
        Func<bool>? isCurrent = null, string? caption = null, bool canCancel = true)
    {
        var progressViewModel = _dialogService.CreateViewModel<ExportPreparationWindowViewModel>();
        if (caption is not null) progressViewModel.Caption = caption;
        progressViewModel.CanCancel = canCancel;
        using var cancellation = new CancellationTokenSource();
        using var delayCancellation = new CancellationTokenSource();
        progressViewModel.CancelOperation = cancellation.Cancel;
        var work = operation(progressViewModel, cancellation.Token);
        progressViewModel.Operation = work;
        try
        {
            // 快速完成时不创建窗口；同一任务只判断一次，避免进度窗口反复闪现。
            await Task.WhenAny(work, Task.Delay(ExportPreparationWindowViewModel.DisplayDelay, delayCancellation.Token));
            if (!work.IsCompleted && (isCurrent?.Invoke() ?? true))
                await _dialogService.ShowDialogAsync(this, progressViewModel);
            return await work;
        }
        finally
        {
            delayCancellation.Cancel();
            if (!work.IsCompleted)
            {
                cancellation.Cancel();
                try { await work; } catch { }
            }
            progressViewModel.CancelOperation = null;
        }
    }

    private async Task ForgetMissingClipOutputsAsync(IEnumerable<string> outputPaths)
    {
        var paths = outputPaths.ToArray();
        var snapshots = Clips.Where(clip => clip.OutputFileInfo is not null)
            .Select(clip => (Clip: clip, File: clip.OutputFileInfo!)).ToArray();
        if (snapshots.Length == 0) return;
        var missing = await RunExportPreparationAsync((progress, token) => Task.Run(() =>
        {
            var cache = new Dictionary<string, string>(MediaFileUtil.PathComparer);
            string Canonical(string path)
            {
                if (!cache.TryGetValue(path, out var result)) cache[path] = result = MediaFileUtil.CanonicalPath(path);
                return result;
            }
            var missingPaths = new HashSet<string>(MediaFileUtil.PathComparer);
            for (var i = 0; i < paths.Length; i++)
            {
                if (!System.IO.File.Exists(paths[i])) missingPaths.Add(Canonical(paths[i]));
                if (i % 128 == 0 || i + 1 == paths.Length)
                    progress.Report(new(i + 1, paths.Length, System.IO.Path.GetFileName(paths[i])));
            }
            return missingPaths.Count == 0 ? [] : snapshots.Where(item => missingPaths.Contains(Canonical(item.File.FullName))).ToArray();
        }, token), caption: LocalizationManager.Instance.String_TsExport_UpdatingResults, canCancel: false);
        // 等待磁盘检查时用户可能已修改片段，只清除仍指向原记录的引用。
        foreach (var item in missing)
            if (ReferenceEquals(item.Clip.OutputFileInfo, item.File)) item.Clip.InvalidateExport();
    }
    private bool suppressQueueNotifications;

    private async Task ApplyQueueExportResultsAsync(ExportQueueItem[] submitted, OutputWindowViewModel output)
    {
        var completed = output.CompletedTaskIndices.ToHashSet();
        var failed = output.FailedTaskIndices.ToHashSet();
        var completedItems = new HashSet<ExportQueueItem>();
        for (var index = 0; index < submitted.Length; index++)
        {
            if (completed.Contains(index))
            {
                submitted[index].Status = ClipExportStatus.Done;
                completedItems.Add(submitted[index]);
            }
            else if (failed.Contains(index)) submitted[index].Status = ClipExportStatus.Failed;
            if (index % 128 == 127) await Task.Yield();
        }
        if (completedItems.Count == 0) return;
        var remaining = ExportQueue.Where(item => !completedItems.Contains(item)).ToArray();
        if (remaining.Length == ExportQueue.Count) return;
        suppressQueueNotifications = true;
        try
        {
            ExportQueue.Clear();
            foreach (var item in remaining) ExportQueue.Add(item);
        }
        finally
        {
            suppressQueueNotifications = false;
            NotifyQueueChanged();
        }
    }
}
