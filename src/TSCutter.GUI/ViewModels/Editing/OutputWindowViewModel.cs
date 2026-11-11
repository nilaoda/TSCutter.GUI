using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.TransportStream.Processing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.ViewModels;

namespace TSCutter.GUI.ViewModels.Editing;

public record BatchTask(string SourcePath, string DestinationPath, long StartPosition, long EndPosition, int? ProgramId = null,
    RemuxExportSettings? RemuxSettings = null, double StartSeconds = 0, double? EndSeconds = null,
    FrameAccurateExportSettings? FrameAccurate = null, long StartPts = 0, long? EndPts = null,
    string? StartSignature = null, string? EndSignature = null, int AudioDelayMs = 0, int? AudioDelayProgramId = null);

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
    public bool IsMergeMode => _mergeRequest is not null || _mediaRequest is not null || _frameAccurateRequest is not null;
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
    private FrameAccurateExportRequest? _frameAccurateRequest;
    public RemuxExportSettings? RemuxSettings { get; set; }
    public FrameAccurateExportSettings? FrameAccurate { get; set; }
    /// <summary>单个关键帧模式 TS 任务的音频延迟（毫秒）。</summary>
    public int AudioDelayMs { get; set; }
    /// <summary>音频延迟作用的节目号（所编辑的节目）；null 表示全部音轨。</summary>
    public int? AudioDelayProgramId { get; set; }
    public long StartPts { get; set; }
    public long? EndPts { get; set; }
    private bool UsesManagedOutput => ProgramId is not null || RemuxSettings is not null || FrameAccurate is not null;
    public double StartSeconds { get; set; }
    public double? EndSeconds { get; set; }

    // 导出开始时捕获一次设置，导出过程中修改设置不影响本次检测的完整性。
    private bool _checkOnExport = true;
    private int _exportClipCount;
    private string _exportClipRangeSummary = "";
    /// <summary>按路径关联任务的结果；执行前失败或取消的任务可能没有条目。</summary>
    public List<ExportTaskOutcome> Outcomes { get; } = [];

    private static bool ResolveCheckOnExport() =>
        App.ConfigService?.CurrentConfig.TsCheckOnExport ?? true;

    private ExportTsCheckSession? CreateCheckSession(string outputPath, bool checkApplicable)
    {
        if (IsRawByteRange || !_checkOnExport || !checkApplicable)
            return null;
        return new ExportTsCheckSession(outputPath);
    }

    private static (ExportKind Kind, bool CheckApplicable) ResolveTaskKind(BatchTask task)
    {
        if (task.FrameAccurate is { } frameAccurate)
            return (ExportKind.FrameAccurate, frameAccurate.Container is "ts" or "m2ts");
        if (task.RemuxSettings is { } settings)
            return (ContainerKind(settings.Container), settings.Container == "m2ts");
        return (ExportKind.TsCopy, true);
    }

    private ExportTaskOutcome RegisterOutcome(string outputPath, bool success, ExportTsCheckSession? session,
        ExportKind kind, bool checkApplicable, TimeSpan elapsed)
    {
        long? recordId = null;
        if (success && !IsRawByteRange && App.ExportRecordRegistry is { } registry)
        {
            var checkState = session?.State ?? TsExportCheckState.NotStarted;
            var result = session?.Result;
            long fileLength = 0;
            var fileModified = DateTime.MinValue;
            try
            {
                var info = new FileInfo(outputPath);
                if (info.Exists)
                {
                    fileLength = info.Length;
                    fileModified = info.LastWriteTimeUtc;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 文件元数据读取失败不阻断登记，长度按 0 处理。
            }
            var record = new ExportRecord
            {
                Timestamp = DateTime.Now,
                OutputPath = outputPath,
                Kind = kind,
                FileSize = fileLength,
                Elapsed = elapsed,
                ClipCount = _exportClipCount,
                ClipRangeSummary = _exportClipRangeSummary,
                CheckEnabled = _checkOnExport,
                CheckApplicable = checkApplicable,
                CheckState = checkState,
                CheckVerdict = result?.Verdict,
                ErrorCount = result?.TotalErrorCount ?? 0,
                WarningCount = result?.TotalWarningCount ?? 0,
                FailureKind = session?.FailureKind ?? TsExportCheckFailure.None,
                FailureDetail = session?.FailureDetail,
                FileLengthAtExport = fileLength,
                FileModifiedAtExport = fileModified,
                RetainedResult = result
            };
            recordId = registry.Register(record).Id;
        }
        var outcome = new ExportTaskOutcome(recordId, outputPath, success, elapsed, kind);
        Outcomes.Add(outcome);
        return outcome;
    }

    private void SetExportClipInfo(int clipCount, string clipRangeSummary)
    {
        _exportClipCount = clipCount;
        _exportClipRangeSummary = clipRangeSummary;
    }

    internal void SetRemuxTask(RemuxExportRequest request)
    {
        _mediaRequest = request;
        OnPropertyChanged(nameof(IsMergeMode));
    }

    internal void SetFrameAccurateTask(FrameAccurateExportRequest request, string rangeSummary)
    {
        _frameAccurateRequest = request;
        _frameAccurateRangeSummary = rangeSummary;
        OnPropertyChanged(nameof(IsMergeMode));
    }

    private string _frameAccurateRangeSummary = "";

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
        _checkOnExport = ResolveCheckOnExport();
        if (!await CheckProgramTablesAsync() || !await CheckTimestampNormalizationAsync())
            return;
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
        if (_frameAccurateRequest is { } frameAccurate)
        {
            var checkApplicable = frameAccurate.Segments[0].Settings.Container is "ts" or "m2ts";
            var session = CreateCheckSession(frameAccurate.OutputPath, checkApplicable);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                CurrentFileName = Path.GetFileName(frameAccurate.OutputPath);
                SetExportClipInfo(frameAccurate.Segments.Count, _frameAccurateRangeSummary);
                await ExportFrameAccurateAsync(frameAccurate, ++_copyGeneration, session);
                await CompleteCheckAsync(session);
                Percent = 100;
                RemainingSeconds = 0;
                DialogResult = true;
                RegisterOutcome(frameAccurate.OutputPath, true, session, ExportKind.FrameAccurate, checkApplicable, stopwatch.Elapsed);
            }
            catch (OperationCanceledException)
            {
                session?.Abort();
                RegisterOutcome(frameAccurate.OutputPath, false, session, ExportKind.FrameAccurate, checkApplicable, stopwatch.Elapsed);
                DialogResult = false;
            }
            catch (Exception exception)
            {
                session?.Abort();
                RegisterOutcome(frameAccurate.OutputPath, false, session, ExportKind.FrameAccurate, checkApplicable, stopwatch.Elapsed);
                Exception = exception;
                DialogResult = false;
            }
            RequestClose?.Invoke();
            return;
        }
        if (_mediaRequest is { } media)
        {
            var container = media.Segments[0].Settings.Container;
            var checkApplicable = container == "m2ts";
            var session = CreateCheckSession(media.OutputPath, checkApplicable);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                CurrentFileName = Path.GetFileName(media.OutputPath);
                SetExportClipInfo(media.Segments.Count, FormatRangeList(
                    media.Segments.Select(item => (item.StartSeconds, item.EndSeconds))));
                await ExportRemuxAsync(media, ++_copyGeneration, session);
                await CompleteCheckAsync(session);
                Percent = 100;
                RemainingSeconds = 0;
                DialogResult = true;
                RegisterOutcome(media.OutputPath, true, session, ContainerKind(container), checkApplicable, stopwatch.Elapsed);
            }
            catch (OperationCanceledException)
            {
                session?.Abort();
                RegisterOutcome(media.OutputPath, false, session, ContainerKind(container), checkApplicable, stopwatch.Elapsed);
                DialogResult = false;
            }
            catch (Exception exception)
            {
                session?.Abort();
                RegisterOutcome(media.OutputPath, false, session, ContainerKind(container), checkApplicable, stopwatch.Elapsed);
                Exception = exception;
                DialogResult = false;
            }
            RequestClose?.Invoke();
            return;
        }
        var request = _mergeRequest;
        if (request is null)
            return;
        var mergeSession = CreateCheckSession(request.OutputPath, checkApplicable: true);
        var mergeStopwatch = Stopwatch.StartNew();
        try
        {
            CurrentFileName = Path.GetFileName(request.OutputPath);
            SetExportClipInfo(request.Ranges.Count, FormatRangeList(
                request.Ranges.Select(item => (item.StartTimeSeconds, (double?)item.EndTimeSeconds))));
            await ExportTsAsync(request, ++_copyGeneration, supplementOutput, mergeSession).ConfigureAwait(true);
            await CompleteCheckAsync(mergeSession);
            Percent = 100;
            RemainingSeconds = 0;
            DialogResult = true;
            RegisterOutcome(request.OutputPath, true, mergeSession,
                request.ProgramId is null ? ExportKind.TsMerge : ExportKind.TsProgramOnly, true, mergeStopwatch.Elapsed);
            RequestClose?.Invoke();
        }
        catch (OperationCanceledException)
        {
            mergeSession?.Abort();
            RegisterOutcome(request.OutputPath, false, mergeSession,
                request.ProgramId is null ? ExportKind.TsMerge : ExportKind.TsProgramOnly, true, mergeStopwatch.Elapsed);
            Console.WriteLine("TS clip merge was canceled.");
            DialogResult = false;
            RequestClose?.Invoke();
        }
        catch (Exception exception)
        {
            mergeSession?.Abort();
            RegisterOutcome(request.OutputPath, false, mergeSession,
                request.ProgramId is null ? ExportKind.TsMerge : ExportKind.TsProgramOnly, true, mergeStopwatch.Elapsed);
            Exception = exception;
            DialogResult = false;
            RequestClose?.Invoke();
        }
    }

    private static ExportKind ContainerKind(string container) => container switch
    {
        "m2ts" => ExportKind.M2tsRemux,
        "mkv" => ExportKind.MkvRemux,
        "vob" => ExportKind.VobRemux,
        _ => ExportKind.Mp4Remux
    };

    private static string FormatRange(double startSeconds, double? endSeconds) =>
        $"{CommonUtil.FormatSeconds(startSeconds)} – {(endSeconds is { } end ? CommonUtil.FormatSeconds(end) : "Inf.")}";

    private static string FormatRangeList(IEnumerable<(double Start, double? End)> ranges)
    {
        var parts = new List<string>();
        foreach (var (start, end) in ranges)
        {
            parts.Add(FormatRange(start, end));
            if (parts.Count == 6)
            {
                parts[^1] = "…";
                break;
            }
        }
        return string.Join("; ", parts);
    }

    private Task ExportRemuxAsync(RemuxExportRequest request, int generation, ExportTsCheckSession? checkSession = null)
    {
        var progress = new Progress<RemuxExportProgress>(value =>
        {
            if (DialogResult is not null || generation != _copyGeneration)
                return;
            Percent = value.Percent;
            Speed = value.BytesPerSecond;
            RemainingSeconds = value.RemainingSeconds;
        });
        return new RemuxExportService().ExportAsync(request, progress, _cts.Token, checkSession);
    }

    private Task ExportFrameAccurateAsync(FrameAccurateExportRequest request, int generation,
        ExportTsCheckSession? checkSession = null)
    {
        var progress = new Progress<FrameAccurateExportProgress>(value =>
        {
            if (DialogResult is not null || generation != _copyGeneration)
                return;
            Percent = value.Percent;
            Speed = value.BytesPerSecond;
            RemainingSeconds = value.RemainingSeconds;
            ApplyFrameAccurateProgress(value);
        });
        var source = request.Segments[0].SourcePath;
        request = request with { NormalizeTimestamps = timestampNormalizationApprovedSources.Contains(source) };
        return new FrameAccurateExportService().ExportAsync(request, progress, _cts.Token, checkSession);
    }

    private Task ExportTsAsync(TsClipMergeRequest request, int generation, bool addTables,
        ExportTsCheckSession? checkSession = null)
    {
        var progress = new Progress<TsClipMergeProgress>(value =>
        {
            if (DialogResult is not null || generation != _copyGeneration)
                return;
            Percent = value.Percent;
            Speed = value.BytesPerSecond;
            RemainingSeconds = value.BytesPerSecond > 0
                ? Math.Max(0, value.TotalBytes - value.BytesProcessed) / value.BytesPerSecond : 0;
        });
        return Task.Run(() => new TsClipMergeService().MergeAsync(request, progress, _cts.Token, addTables, checkSession), _cts.Token);
    }

    private async Task RunSingleAsync()
    {
        var startedUtc = DateTime.UtcNow;
        try
        {
            CurrentFileName = Path.GetFileName(OutputPath!);
            _totalBytes = RemuxSettings is not null || FrameAccurate is not null ? 0 :
                EndPosition > 0 ? EndPosition - StartPosition : new FileInfo(SourceFilePath!).Length - StartPosition;
            _startTime = DateTime.Now;
            _lastUpdateTime = _startTime;
            await CopyClipAsync(new BatchTask(SourceFilePath!, OutputPath!, StartPosition, EndPosition, ProgramId,
                RemuxSettings, StartSeconds, EndSeconds, FrameAccurate, StartPts, EndPts, AudioDelayMs: AudioDelayMs,
                AudioDelayProgramId: AudioDelayProgramId));
            Percent = 100;
            RemainingSeconds = 0;
            DialogResult = true;
            RequestClose?.Invoke();
        } 
        catch (OperationCanceledException)
        {
            Console.WriteLine("File copy was canceled.");
            // 重新封装、按节目过滤和补充节目表的服务取消后自行删除半成品；按字节复制由这里清理。
            if (!UsesManagedOutput && !AddsProgramTablesTo(OutputPath!))
                DeletePartialOutput(OutputPath!, startedUtc);
            DialogResult = false;
            RequestClose?.Invoke();
        }
        catch (Exception e)
        {
            Exception = e;
            DialogResult = false;
            RequestClose?.Invoke();
        }
    }

    /// <summary>
    /// 删除取消后留下的按字节复制半成品：只删除本次导出开始后被写过的文件，开始前已存在且尚未改写的文件保留。
    /// 修改时间放宽 2 秒，兼容 FAT 类文件系统的时间精度。
    /// </summary>
    private static void DeletePartialOutput(string path, DateTime startedUtc)
    {
        try
        {
            if (File.Exists(path) && File.GetLastWriteTimeUtc(path) >= startedUtc.AddSeconds(-2))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Partial output was not removed: {exception.Message}");
        }
    }

    public bool IsRawByteRange { get; set; }

    private async Task CopyClipAsync(BatchTask task)
    {
        var (kind, checkApplicable) = ResolveTaskKind(task);
        if (AddsProgramTablesTo(task.DestinationPath))
            (kind, checkApplicable) = (ExportKind.TsProgramTablesSupplement, true);
        else if (task.ProgramId is not null)
            (kind, checkApplicable) = (ExportKind.TsProgramOnly, true);
        var session = CreateCheckSession(task.DestinationPath, checkApplicable);
        SetExportClipInfo(1, FormatRange(task.StartSeconds, task.EndSeconds));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await CopyClipCoreAsync(task, session);
            await CompleteCheckAsync(session);
            RegisterOutcome(task.DestinationPath, true, session, kind, checkApplicable, stopwatch.Elapsed);
        }
        catch
        {
            session?.Abort();
            RegisterOutcome(task.DestinationPath, false, session, kind, checkApplicable, stopwatch.Elapsed);
            throw;
        }
    }

    private async Task CompleteCheckAsync(ExportTsCheckSession? session)
    {
        _cts.Token.ThrowIfCancellationRequested();
        // 服务已关闭输出并完成临时文件替换，检测收尾也留在工作线程。
        if (session is not null)
            await Task.Run(session.Complete);
    }

    private Task CopyClipCoreAsync(BatchTask task, ExportTsCheckSession? checkSession)
    {
        var generation = ++_copyGeneration;
        if (task.FrameAccurate is { } frameAccurate)
            return ExportFrameAccurateAsync(new FrameAccurateExportRequest(task.DestinationPath,
                [new FrameAccurateExportSegment(task.SourcePath, task.StartPts, task.EndPts, frameAccurate)]), generation, checkSession);
        if (task.RemuxSettings is { } settings)
            return ExportRemuxAsync(new RemuxExportRequest(task.DestinationPath,
                [new RemuxExportSegment(task.SourcePath, task.StartSeconds, task.EndSeconds, settings,
                    StartSignature: task.StartSignature, EndSignature: task.EndSignature)]), generation, checkSession);
        // 补节目表或改写音频延迟时逐包处理：仍是原包复制，只改写节目表前缀与音轨时间戳（含只导出当前节目）。
        var addTables = AddsProgramTablesTo(task.DestinationPath);
        if (addTables || task.AudioDelayMs != 0 && !IsRawByteRange)
            return ExportTsAsync(new TsClipMergeRequest
            {
                SourcePath = task.SourcePath, OutputPath = task.DestinationPath,
                Ranges = [new(task.StartPosition, task.EndPosition, 0, 1)],
                ProgramId = task.ProgramId, AudioOffset90k = AudioDelay.To90k(task.AudioDelayMs),
                AudioDelayProgramId = task.AudioDelayProgramId
            }, generation, addTables, checkSession);
        if (task.ProgramId is { } programId)
        {
            var progress = new Progress<TsFilterProgress>(value =>
            {
                if (DialogResult is not null || generation != _copyGeneration)
                    return;
                Percent = value.Percent;
                Speed = value.BytesPerSecond;
                RemainingSeconds = value.BytesPerSecond > 0
                    ? Math.Max(0, value.FileSize - value.BytesProcessed) / value.BytesPerSecond : 0;
            });
            return Task.Run(() => new TsProgramClipExportService().ExportAsync(task.SourcePath, task.DestinationPath,
                task.StartPosition, task.EndPosition, programId, progress, _cts.Token, checkSession), _cts.Token);
        }
        // 复制回调来自后台；只将限频后的进度送回创建 Progress 的界面线程。
        IProgress<(double Percent, long Bytes)> progressUpdates = new Progress<(double Percent, long Bytes)>(value =>
        {
            if (DialogResult is not null || generation != _copyGeneration)
                return;
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
        }, _cts.Token, checkSession);
    }

    private Task CopySourceRangeAsync(BatchTask task, Action<double, long> progress, CancellationToken token,
        ExportTsCheckSession? checkSession = null)
    {
        return IsRawByteRange
            ? CommonUtil.CopyFileAsync(new FileInfo(task.SourcePath), task.DestinationPath,
                task.StartPosition, task.EndPosition, progress, token, checkSession)
            : TsClipCopyService.CopyAsync(task.SourcePath, task.DestinationPath,
                task.StartPosition, task.EndPosition, progress, token, checkSession);
    }

    private async Task RunBatchAsync()
    {
        _batchCancelled = false;
        var sources = _tasks.Select(task => task.SourcePath).Distinct(MediaFileUtil.PathComparer).ToArray();
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
                // 执行前再次检查全部源，避免某一项覆盖队列中另一张光盘的组成文件。
                if (sources.Any(source => MediaFileUtil.ConflictsWithSource(task.DestinationPath, source)))
                    throw new RemuxExportException(RemuxExportError.SameFile);
                _totalBytes = task.RemuxSettings is not null || task.FrameAccurate is not null ? 0 : task.EndPosition > 0
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
        if (elapsedTime < 1)
            return;
        Speed = _bytesCopied / elapsedTime;
        _lastUpdateTime = DateTime.Now;
        _bytesCopied = 0;
    }

    private void UpdateRemainingTime()
    {
        if (Speed <= 0 || Percent >= 100)
            return;
        var remainingBytes = _totalBytes * (1 - Percent / 100.0);
        RemainingSeconds = remainingBytes / Speed;
    }

    private static string FormatRemainingTime(double seconds)
    {
        if (seconds <= 0)
            return "--:--";
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    [RelayCommand]
    private void CancelOutput()
    {
        if (DialogResult is not null)
            return;

        if (checkingProgramTables)
        {
            _cts.Cancel();
        }
        else if (IsBatchMode)
        {
            _batchCancelled = true;
            _cts.Cancel();
        }
        else
        {
            // 等待导出停止并清理半成品后再关闭窗口，结果为未完成，不会把半截文件当作导出成功。
            _cts.Cancel();
        }
    }
    
    public void OnClosing(CancelEventArgs e)
    {
        // 单个导出总是等到停止并清理后才关闭；批量导出只为会自行清理的任务等待。
        if ((checkingProgramTables || !IsBatchMode ||
             _tasks.Exists(task => task.ProgramId is not null || task.RemuxSettings is not null || task.FrameAccurate is not null)) &&
            DialogResult is null)
            e.Cancel = true;
        CancelOutput();
    }

    public event Action? RequestClose;
}
