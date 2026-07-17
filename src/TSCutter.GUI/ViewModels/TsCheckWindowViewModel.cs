using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.FileSystem;
using HanumanInstitute.MvvmDialogs.FrameworkDialogs;
using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.ViewModels;

public enum TsCheckViewMode
{
    Summary,
    Details,
    Timeline
}

public partial class TsCheckWindowViewModel : ViewModelBase, IModalDialogViewModel
{
    private const int MaxUiEvents = 5_000;
    private readonly IDialogService _dialogService;
    private readonly TsCheckTextFormatter _text;
    private readonly Dictionary<int, TsCheckPidSummaryView> _pidSummaryRows = [];
    private readonly List<TsCheckEvent> _liveTimelineEvents = [];
    private CancellationTokenSource? _cancellationTokenSource;
    private TsCheckResult? _result;
    private bool _isClosing;
    private int _scanGeneration;

    public TsCheckWindowViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
        _text = new TsCheckTextFormatter();
        StatusText = _text.Strings.String_TsCheck_Status_Ready;
        App.LocalizationService.LanguageChanged += OnLanguageChanged;
        RebuildTimelineStreams();
    }

    public bool? DialogResult { get; }
    public string FilePath { get; set; } = string.Empty;
    public ObservableCollection<TsCheckEventView> Events { get; } = [];
    public ObservableCollection<TsCheckPidSummaryView> PidSummaries { get; } = [];
    public ObservableCollection<TsCheckTimelineStreamView> TimelineStreams { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSummaryView))]
    [NotifyPropertyChangedFor(nameof(IsDetailsView))]
    [NotifyPropertyChangedFor(nameof(IsTimelineView))]
    private TsCheckViewMode _viewMode = TsCheckViewMode.Summary;

    public bool IsSummaryView
    {
        get => ViewMode == TsCheckViewMode.Summary;
        set
        {
            if (value)
                ViewMode = TsCheckViewMode.Summary;
        }
    }

    public bool IsDetailsView
    {
        get => ViewMode == TsCheckViewMode.Details;
        set
        {
            if (value)
                ViewMode = TsCheckViewMode.Details;
        }
    }

    public bool IsTimelineView
    {
        get => ViewMode == TsCheckViewMode.Timeline;
        set
        {
            if (value)
                ViewMode = TsCheckViewMode.Timeline;
        }
    }

    [ObservableProperty]
    private IReadOnlyList<TsCheckTimelineBucket> _timelineBuckets = Array.Empty<TsCheckTimelineBucket>();

    [ObservableProperty]
    private IReadOnlyList<TsCheckEvent> _timelineEvents = Array.Empty<TsCheckEvent>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedTimelinePid))]
    [NotifyPropertyChangedFor(nameof(SelectedTimelineSeriesName))]
    private TsCheckTimelineStreamView? _selectedTimelineStream;

    public int SelectedTimelinePid => SelectedTimelineStream?.Pid ?? -1;
    public string SelectedTimelineSeriesName =>
        SelectedTimelineStream?.DisplayText ?? _text.Strings.String_TsCheck_Timeline_TotalTs;

    [ObservableProperty]
    private TsCheckEvent? _selectedTimelineEvent;

    [ObservableProperty]
    private TsCheckEventView? _selectedEventRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand), nameof(ExportCommand))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool _hasResult;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _speedText = $"{CommonUtil.FormatFileSize(0)}/s";

    [ObservableProperty]
    private string _progressText = "0 / 0";

    [ObservableProperty]
    private string _packetCountText = "0";

    [ObservableProperty]
    private int _errorCount;

    [ObservableProperty]
    private int _warningCount;

    [ObservableProperty]
    private string _verdictText = "-";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPassVerdict))]
    [NotifyPropertyChangedFor(nameof(IsWarningVerdict))]
    [NotifyPropertyChangedFor(nameof(IsErrorVerdict))]
    private TsCheckVerdict? _verdict;

    public bool IsPassVerdict => Verdict == TsCheckVerdict.Pass;
    public bool IsWarningVerdict => Verdict == TsCheckVerdict.Warning;
    public bool IsErrorVerdict => Verdict == TsCheckVerdict.Error;

    [ObservableProperty]
    private string _elapsedText = "00:00:00.000";

    public bool CanStart => !IsScanning;
    public bool CanCancel => IsScanning;
    public bool CanExport => HasResult && !IsScanning;
    public string FileName => Path.GetFileName(FilePath);
    public string FileSizeText => File.Exists(FilePath) ? CommonUtil.FormatFileSize(new FileInfo(FilePath).Length) : "-";

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (IsScanning || !File.Exists(FilePath))
            return;

        _result = null;
        var cancellationTokenSource = new CancellationTokenSource();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = cancellationTokenSource;
        var scanGeneration = ++_scanGeneration;
        IsScanning = true;
        HasResult = false;
        Events.Clear();
        PidSummaries.Clear();
        _pidSummaryRows.Clear();
        _liveTimelineEvents.Clear();
        TimelineBuckets = Array.Empty<TsCheckTimelineBucket>();
        TimelineEvents = Array.Empty<TsCheckEvent>();
        SelectedTimelineEvent = null;
        SelectedEventRow = null;
        RebuildTimelineStreams();
        Percent = 0;
        ErrorCount = 0;
        WarningCount = 0;
        VerdictText = "-";
        Verdict = null;
        StatusText = _text.Strings.String_TsCheck_Status_Scanning;

        try
        {
            // Progress 在 UI 线程创建，扫描器只按节流后的频率回传，避免逐包跨线程调度。
            var progress = new Progress<TsCheckProgress>(value =>
            {
                if (!_isClosing && scanGeneration == _scanGeneration)
                    UpdateProgress(value);
            });
            var analyzer = new TsStreamAnalyzer();
            // 即使操作系统缓存让异步读取同步完成，也强制让解析热路径留在线程池，避免阻塞 Avalonia UI。
            var result = await Task.Run(() => analyzer.AnalyzeAsync(FilePath, progress, cancellationTokenSource.Token));
            if (_isClosing || scanGeneration != _scanGeneration)
                return;

            // 扫描结束后立即使排队中的旧进度回调失效，最终 UI 统一从完整结果重建。
            _scanGeneration++;
            _result = result;
            Percent = result.FileSize > 0 ? result.BytesScanned * 100.0 / result.FileSize : 0;
            ProgressText = $"{CommonUtil.FormatFileSize(result.BytesScanned)} / {CommonUtil.FormatFileSize(result.FileSize)}";
            PacketCountText = result.PacketCount.ToString("N0");
            SpeedText = $"{CommonUtil.FormatFileSize(result.BytesScanned / Math.Max(0.001, result.Elapsed.TotalSeconds))}/s";
            ElapsedText = result.Elapsed.ToString(@"hh\:mm\:ss\.fff");
            RebuildEvents();
            RebuildPidSummaries();
            TimelineBuckets = result.Timeline.ToArray();
            TimelineEvents = result.Events.ToArray();
            RebuildTimelineStreams();
            ErrorCount = _result.ErrorCount;
            WarningCount = _result.WarningCount;
            Verdict = _result.Verdict;
            VerdictText = _text.FormatVerdict(_result);
            StatusText = _result.WasCancelled
                ? _text.Strings.String_TsCheck_Status_Cancelled
                : _text.Strings.String_TsCheck_Status_Completed;
            HasResult = true;
        }
        catch (Exception exception)
        {
            if (!_isClosing && scanGeneration == _scanGeneration)
                StatusText = string.Format(_text.Strings.String_TsCheck_Status_Failed, exception.Message);
        }
        finally
        {
            if (ReferenceEquals(_cancellationTokenSource, cancellationTokenSource))
                _cancellationTokenSource = null;
            cancellationTokenSource.Dispose();
            IsScanning = false;
            StartCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            ExportCommand.NotifyCanExecuteChanged();
        }
    }

    private void UpdateProgress(TsCheckProgress progress)
    {
        Percent = progress.Percent;
        SpeedText = $"{CommonUtil.FormatFileSize(progress.BytesPerSecond)}/s";
        ProgressText = $"{CommonUtil.FormatFileSize(progress.BytesScanned)} / {CommonUtil.FormatFileSize(progress.FileSize)}";
        PacketCountText = progress.PacketCount.ToString("N0");
        ElapsedText = progress.Elapsed.ToString(@"hh\:mm\:ss\.fff");
        ErrorCount = progress.ErrorCount;
        WarningCount = progress.WarningCount;
        TimelineBuckets = progress.Timeline;
        foreach (var pid in progress.Pids)
            UpdatePidSummary(pid, progress.PacketCount);
        if (progress.NewEvent is not null && Events.Count < MaxUiEvents)
        {
            Events.Add(new TsCheckEventView(progress.NewEvent, _text));
            _liveTimelineEvents.Add(progress.NewEvent);
            TimelineEvents = _liveTimelineEvents.ToArray();
        }
    }

    private void RebuildEvents()
    {
        Events.Clear();
        if (_result is null)
            return;
        for (var index = 0; index < Math.Min(MaxUiEvents, _result.Events.Count); index++)
            Events.Add(new TsCheckEventView(_result.Events[index], _text, _result));
    }

    private void RebuildPidSummaries()
    {
        PidSummaries.Clear();
        _pidSummaryRows.Clear();
        if (_result is null)
            return;

        if (_result.GlobalErrorCount + _result.GlobalWarningCount > 0)
        {
            UpdatePidSummary(new TsCheckPidProgress(
                -1, 0, _result.GlobalErrorCount, _result.GlobalWarningCount,
                null, null, null, null, null, false, false, true), _result.PacketCount);
        }

        var pids = new List<int>(_result.Pids.Keys);
        pids.Sort();
        foreach (var pidValue in pids)
        {
            var pid = _result.Pids[pidValue];
            UpdatePidSummary(new TsCheckPidProgress(
                pid.Pid, pid.PacketCount, pid.ErrorCount, pid.WarningCount,
                pid.ProgramNumber, pid.StreamType, pid.MpegAudioLayer,
                pid.SupplementaryStreamType, pid.Language,
                pid.IsPcrPid, pid.IsPmtPid, false), _result.PacketCount);
        }
    }

    private void RebuildTimelineStreams()
    {
        var selectedPid = SelectedTimelineStream?.Pid ?? -1;
        TimelineStreams.Clear();
        TimelineStreams.Add(new TsCheckTimelineStreamView(
            -1, _text.Strings.String_TsCheck_Timeline_TotalTs));

        if (_result is not null)
        {
            var pids = new List<int>(_result.Pids.Keys);
            pids.Sort();
            foreach (var pidValue in pids)
            {
                var pid = _result.Pids[pidValue];
                if (pid.PacketCount <= 0)
                    continue;
                var description = _text.FormatPidDescription(
                    pid.Pid, pid.ProgramNumber, pid.StreamType, pid.MpegAudioLayer,
                    pid.SupplementaryStreamType, pid.Language, pid.IsPcrPid, pid.IsPmtPid);
                TimelineStreams.Add(new TsCheckTimelineStreamView(
                    pid.Pid, $"{pid.PidText} · {description}"));
            }
        }

        SelectedTimelineStream = TimelineStreams.Count > 0
            ? FindTimelineStream(selectedPid) ?? TimelineStreams[0]
            : null;
    }

    private TsCheckTimelineStreamView? FindTimelineStream(int pid)
    {
        foreach (var item in TimelineStreams)
        {
            if (item.Pid == pid)
                return item;
        }
        return null;
    }

    partial void OnSelectedTimelineEventChanged(TsCheckEvent? value)
    {
        if (value is null)
            return;
        ViewMode = TsCheckViewMode.Details;
        SelectEventRow(value);
    }

    private void SelectEventRow(TsCheckEvent value)
    {
        SelectedEventRow = null;
        foreach (var item in Events)
        {
            if (ReferenceEquals(item.Item, value))
            {
                SelectedEventRow = item;
                break;
            }
        }
        if (SelectedEventRow is null && _result is not null)
        {
            // 时间轴可显示全部已保存异常；若目标超出详情表默认的 5,000 行上限，则临时置换末行以便定位。
            if (Events.Count >= MaxUiEvents)
                Events.RemoveAt(Events.Count - 1);
            SelectedEventRow = new TsCheckEventView(value, _text, _result);
            Events.Add(SelectedEventRow);
        }
    }

    private void OnLanguageChanged()
    {
        RebuildTimelineStreams();
        StatusText = IsScanning
            ? _text.Strings.String_TsCheck_Status_Scanning
            : _result is null
                ? _text.Strings.String_TsCheck_Status_Ready
                : _result.WasCancelled
                    ? _text.Strings.String_TsCheck_Status_Cancelled
                    : _text.Strings.String_TsCheck_Status_Completed;
        if (_result is null)
            return;
        RebuildEvents();
        RebuildPidSummaries();
        if (SelectedTimelineEvent is { } selectedEvent)
            SelectEventRow(selectedEvent);
        VerdictText = _text.FormatVerdict(_result);
    }

    private void UpdatePidSummary(TsCheckPidProgress progress, long totalPacketCount)
    {
        if (!_pidSummaryRows.TryGetValue(progress.Pid, out var row))
        {
            row = new TsCheckPidSummaryView(progress.Pid);
            _pidSummaryRows[progress.Pid] = row;
            PidSummaries.Add(row);
        }
        row.Update(progress, totalPacketCount, _text);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancellationTokenSource?.Cancel();

    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync()
    {
        // 保存对话框可能停留较久，固定本次导出使用的结果，避免后续状态变化影响报告内容。
        var result = _result;
        if (result is null)
            return;

        var settings = new SaveFileDialogSettings
        {
            Title = _text.Strings.String_TsCheck_Export_Title,
            SuggestedStartLocation = new DesktopDialogStorageFolder(Path.GetDirectoryName(FilePath)!),
            SuggestedFileName = Path.GetFileNameWithoutExtension(FilePath) + ".txt",
            Filters = [new(_text.Strings.String_TsCheck_Export_TextFiles, ["txt"])],
            DefaultExtension = "txt"
        };
        var selected = await _dialogService.ShowSaveFileDialogAsync(this, settings);
        if (selected?.Path is null)
            return;

        var reportBuilder = new TsCheckReportBuilder(_text);
        await reportBuilder.WriteAsync(selected.Path.LocalPath, result);
        StatusText = string.Format(_text.Strings.String_TsCheck_Status_Exported, selected.Path.LocalPath);
    }

    public void OnClosing(CancelEventArgs eventArgs)
    {
        ReleaseForClose();
    }

    public void OnClosed() => ReleaseForClose();

    private void ReleaseForClose()
    {
        if (_isClosing)
            return;

        _isClosing = true;
        App.LocalizationService.LanguageChanged -= OnLanguageChanged;
        _scanGeneration++;
        _cancellationTokenSource?.Cancel();
        _result = null;
        Events.Clear();
        PidSummaries.Clear();
        TimelineStreams.Clear();
        _liveTimelineEvents.Clear();
        TimelineBuckets = Array.Empty<TsCheckTimelineBucket>();
        TimelineEvents = Array.Empty<TsCheckEvent>();
        _pidSummaryRows.Clear();
    }

}
