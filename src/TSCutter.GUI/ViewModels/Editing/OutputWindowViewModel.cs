using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.TransportStream.Processing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.ViewModels.Editing;

public record BatchTask(string SourcePath, string DestinationPath, long StartPosition, long EndPosition, int? ProgramId = null,
    RemuxExportSettings? RemuxSettings = null, double StartSeconds = 0, double? EndSeconds = null);

public partial class OutputWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    [ObservableProperty]
    public partial bool? DialogResult { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentStr))]
    public partial double Percent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedStr))]
    public partial double Speed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemainingTimeStr))]
    public partial double RemainingSeconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueProgressStr), nameof(QueueProgress))]
    public partial int CurrentTaskIndex { get; set; }

    [ObservableProperty]
    public partial string CurrentFileName { get; set; } = "";

    [ObservableProperty]
    public partial string CancelButtonText { get; set; } = "";

    public bool IsBatchMode => _tasks.Count > 0;
    public bool IsMergeMode => _mergeRequest is not null || _mediaRequest is not null;
    public int TotalTasks => _tasks.Count;
    public double QueueProgress => TotalTasks > 0 ? CurrentTaskIndex * 100.0 / TotalTasks : 0;
    public string QueueProgressStr => $"{CurrentTaskIndex} / {TotalTasks}";

    public string PercentStr => $"{Percent:0.00}%";
    public string SpeedStr => $"{CommonUtil.FormatFileSize(Speed)}/s";
    public string RemainingTimeStr => FormatRemainingTime(RemainingSeconds);

    public int? ProgramId { get; set; }
    public string? SourceFilePath { get; set; }
    public long StartPosition { get; set; }
    public long EndPosition { get; set; }
    public string? OutputPath { get; set; }
    public Exception? Exception { get; private set; }
    
    private readonly List<BatchTask> _tasks = [];
    private TsClipMergeRequest? _mergeRequest;
    private RemuxExportRequest? _mediaRequest;
    public RemuxExportSettings? RemuxSettings { get; set; }
    public double StartSeconds { get; set; }
    public double? EndSeconds { get; set; }

    internal void SetRemuxTask(RemuxExportRequest request)
    {
        _mediaRequest = request;
        OnPropertyChanged(nameof(IsMergeMode));
    }

    private Task ExportRemuxAsync(RemuxExportRequest request, int generation)
    {
        var progress = new Progress<RemuxExportProgress>(value =>
        {
            if (DialogResult is not null || generation != _copyGeneration) return;
            Percent = value.Percent;
            Speed = value.BytesPerSecond;
            RemainingSeconds = value.RemainingSeconds;
        });
        return new RemuxExportService().ExportAsync(request, progress, _cts.Token);
    }
    public List<int> CompletedTaskIndices { get; } = [];
    public List<int> FailedTaskIndices { get; } = [];
    private CancellationTokenSource _cts = new();
    private DateTime _lastUpdateTime;
    private DateTime _startTime;
    private long _bytesCopied = 0;
    private long _totalBytes;
    private bool _batchCancelled;
    private int _copyGeneration;

    public OutputWindowViewModel()
    {
        CancelButtonText = LocalizationManager.Instance.String_Cancel;
    }

    public void SetBatchTasks(List<BatchTask> tasks)
    {
        _tasks.AddRange(tasks);
        OnPropertyChanged(nameof(IsBatchMode));
        OnPropertyChanged(nameof(TotalTasks));
        CancelButtonText = LocalizationManager.Instance.String_Output_CancelRemaining;
    }

    internal void SetMergeTask(TsClipMergeRequest request)
    {
        _mergeRequest = request;
        OnPropertyChanged(nameof(IsMergeMode));
    }

    [RelayCommand]
    private async Task OutputAsync()
    {
        if (IsBatchMode)
        {
            await RunBatchAsync();
        }
        else if (IsMergeMode)
        {
            await RunMergeAsync();
        }
        else
        {
            await RunSingleAsync();
        }
    }

    private async Task RunMergeAsync()
    {
        if (_mediaRequest is { } media)
        {
            try
            {
                CurrentFileName = Path.GetFileName(media.OutputPath);
                await ExportRemuxAsync(media, ++_copyGeneration);
                Percent = 100;
                RemainingSeconds = 0;
                DialogResult = true;
            }
            catch (OperationCanceledException) { DialogResult = false; }
            catch (Exception exception) { Exception = exception; DialogResult = false; }
            RequestClose?.Invoke();
            return;
        }
        var request = _mergeRequest;
        if (request is null)
            return;

        try
        {
            CurrentFileName = Path.GetFileName(request.OutputPath);
            var progress = new Progress<TsClipMergeProgress>(value =>
            {
                Percent = value.Percent;
                Speed = value.BytesPerSecond;
                RemainingSeconds = value.BytesPerSecond > 0
                    ? Math.Max(0, value.TotalBytes - value.BytesProcessed) / value.BytesPerSecond
                    : 0;
            });
            await Task.Run(() => new TsClipMergeService().MergeAsync(
                request, progress, _cts.Token), _cts.Token).ConfigureAwait(true);
            Percent = 100;
            RemainingSeconds = 0;
            DialogResult = true;
            RequestClose?.Invoke();
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("TS clip merge was canceled.");
            DialogResult = false;
            RequestClose?.Invoke();
        }
        catch (Exception exception)
        {
            Exception = exception;
            DialogResult = false;
            RequestClose?.Invoke();
        }
    }

    private async Task RunSingleAsync()
    {
        try
        {
            CurrentFileName = Path.GetFileName(OutputPath!);
            _totalBytes = EndPosition > 0 ? EndPosition - StartPosition : new FileInfo(SourceFilePath!).Length - StartPosition;
            _startTime = DateTime.Now;
            _lastUpdateTime = _startTime;
            await CopyClipAsync(new BatchTask(SourceFilePath!, OutputPath!, StartPosition, EndPosition, ProgramId,
                RemuxSettings, StartSeconds, EndSeconds));
            Percent = 100;
            RemainingSeconds = 0;
            DialogResult = true;
            RequestClose?.Invoke();
        } 
        catch (OperationCanceledException)
        {
            Console.WriteLine("File copy was canceled.");
            if (ProgramId is not null || RemuxSettings is not null)
            {
                DialogResult = false;
                RequestClose?.Invoke();
            }
        }
        catch (Exception e)
        {
            Exception = e;
            DialogResult = false;
            RequestClose?.Invoke();
        }
    }

    public bool IsRawByteRange { get; set; }

    private Task CopyClipAsync(BatchTask task)
    {
        var generation = ++_copyGeneration;
        if (task.RemuxSettings is { } settings)
            return ExportRemuxAsync(new RemuxExportRequest(task.DestinationPath,
                [new RemuxExportSegment(task.SourcePath, task.StartSeconds, task.EndSeconds, settings)]), generation);
        if (task.ProgramId is { } programId)
        {
            var progress = new Progress<TsFilterProgress>(value =>
            {
                if (DialogResult is not null || generation != _copyGeneration) return;
                Percent = value.Percent;
                Speed = value.BytesPerSecond;
                RemainingSeconds = value.BytesPerSecond > 0
                    ? Math.Max(0, value.FileSize - value.BytesProcessed) / value.BytesPerSecond : 0;
            });
            return Task.Run(() => new TsProgramClipExportService().ExportAsync(task.SourcePath, task.DestinationPath,
                task.StartPosition, task.EndPosition, programId, progress, _cts.Token), _cts.Token);
        }
        // 复制回调来自后台；只将限频后的进度送回创建 Progress 的界面线程。
        IProgress<(double Percent, long Bytes)> progressUpdates = new Progress<(double Percent, long Bytes)>(value =>
        {
            if (DialogResult is not null || generation != _copyGeneration) return;
            Percent = value.Percent;
            _bytesCopied += value.Bytes;
            UpdateSpeed();
            UpdateRemainingTime();
        });
        var lastReport = Stopwatch.GetTimestamp();
        long pendingBytes = 0;
        return CopySourceRangeAsync(task, (percent, bytesCopied) =>
        {
            pendingBytes += bytesCopied;
            var now = Stopwatch.GetTimestamp();
            if (percent < 100 && Stopwatch.GetElapsedTime(lastReport, now) < TimeSpan.FromMilliseconds(100))
                return;
            progressUpdates.Report((percent, pendingBytes));
            pendingBytes = 0;
            lastReport = now;
        }, _cts.Token);
    }

    private Task CopySourceRangeAsync(BatchTask task, Action<double, long> progress, CancellationToken token)
    {
        return IsRawByteRange
            ? CommonUtil.CopyFileAsync(new FileInfo(task.SourcePath), task.DestinationPath,
                task.StartPosition, task.EndPosition, progress, token)
            : TsClipCopyService.CopyAsync(task.SourcePath, task.DestinationPath,
                task.StartPosition, task.EndPosition, progress, token);
    }

    private async Task RunBatchAsync()
    {
        _batchCancelled = false;
        for (int i = 0; i < _tasks.Count; i++)
        {
            if (_batchCancelled)
                break;

            var task = _tasks[i];
            CurrentTaskIndex = i;
            CurrentFileName = Path.GetFileName(task.DestinationPath);
            Percent = 0;
            Speed = 0;
            RemainingSeconds = 0;

            try
            {
                _totalBytes = task.EndPosition > 0
                    ? task.EndPosition - task.StartPosition
                    : new FileInfo(task.SourcePath).Length - task.StartPosition;
                _startTime = DateTime.Now;
                _lastUpdateTime = _startTime;
                _bytesCopied = 0;

                await CopyClipAsync(task);
                CompletedTaskIndices.Add(i);
            }
            catch (OperationCanceledException)
            {
                _batchCancelled = true;
            }
            catch (Exception e)
            {
                FailedTaskIndices.Add(i);
                Exception ??= e;
                Console.Error.WriteLine($"Task failed: {e.Message}");
            }
        }

        CurrentTaskIndex = _batchCancelled ? CurrentTaskIndex : _tasks.Count;
        RemainingSeconds = 0;
        DialogResult = true;
        RequestClose?.Invoke();
    }
    
    private void UpdateSpeed()
    {
        var elapsedTime = (DateTime.Now - _lastUpdateTime).TotalSeconds;
        if (elapsedTime < 1) return;
        Speed = _bytesCopied / elapsedTime;
        _lastUpdateTime = DateTime.Now;
        _bytesCopied = 0;
    }

    private void UpdateRemainingTime()
    {
        if (Speed <= 0 || Percent >= 100) return;
        var remainingBytes = _totalBytes * (1 - Percent / 100.0);
        RemainingSeconds = remainingBytes / Speed;
    }

    private static string FormatRemainingTime(double seconds)
    {
        if (seconds <= 0) return "--:--";
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    [RelayCommand]
    private void CancelOutput()
    {
        if (DialogResult is not null) return;

        if (IsBatchMode)
        {
            _batchCancelled = true;
            _cts.Cancel();
        }
        else if (IsMergeMode || ProgramId is not null || RemuxSettings is not null)
        {
            // 过滤和合并服务在取消后删除半成品；等待清理完成再关闭窗口，避免误报成功。
            _cts.Cancel();
        }
        else
        {
            _cts.Cancel();
            DialogResult = true;
            RequestClose?.Invoke();
        }
    }
    
    public void OnClosing(CancelEventArgs e)
    {
        if ((IsMergeMode || ProgramId is not null || RemuxSettings is not null || _tasks.Exists(task => task.ProgramId is not null || task.RemuxSettings is not null)) &&
            DialogResult is null)
            e.Cancel = true;
        CancelOutput();
    }

    public event Action? RequestClose;
}
