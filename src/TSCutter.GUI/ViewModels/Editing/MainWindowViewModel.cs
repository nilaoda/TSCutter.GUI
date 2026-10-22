using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Classic.CommonControls.Dialogs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.FileSystem;
using HanumanInstitute.MvvmDialogs.FrameworkDialogs;
using TSCutter.GUI.Rendering;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Models.TransportStream.Processing;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Editing;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.Utils.Media;
using TSCutter.GUI.Utils.TransportStream.Packets;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.ViewModels.Application;
using TSCutter.GUI.ViewModels.FrameSearch;
using TSCutter.GUI.ViewModels.Preview;
using TSCutter.GUI.ViewModels.TransportStream.Analysis;
using TSCutter.GUI.ViewModels.TransportStream.Processing;
using TSCutter.GUI.ViewModels.TransportStream.Repair;
using TSCutter.GUI.Views.Editing;

namespace TSCutter.GUI.ViewModels.Editing;

public partial class MainWindowViewModel : ViewModelBase
{
    private const int DefaultScrubPreviewWidth = 1280;
    private const int DefaultScrubPreviewHeight = 720;
    private static readonly string[] PreviewFileExtensions =
        ["ts", "m2ts", "mts", "mp4", "mkv", "mov", "avi", "webm", "m4v", "mpg", "mpeg", "wmv", "flv", "3gp"];
    private const int MaximumHistoryEntries = 100;
    private const int MaximumHistoryThumbnailEntries = 4;
    private static string TitleInfo => ReleaseInfo.WindowTitle;

    private static string PleaseLoadTip => LocalizationManager.Instance.String_PleaseLoadVideo;
    public string WindowTitle
    {
        get
        {
            var projectName = _projectPath is null ? string.Empty :
                $" [{Path.GetFileName(_projectPath)}]";
            if (!string.IsNullOrEmpty(VideoPath))
                return $"{TitleInfo} - {Path.GetFileName(VideoPath)}{projectName}";
            return TitleInfo + projectName;
        }
    }
    private VideoInstance? _videoInstance;
    private bool _isLoadingVideo;
    private bool _currentFileSupportsTsTools;
    private Exception? _previewFailure;
    private KeyFrameOverviewWindowViewModel? keyFrameOverviewWindow;
    private readonly IDialogService _dialogService;
    private readonly IConfigurationService _configService;
    private readonly object _scrubPreviewSync = new();
    private readonly Stack<EditorStateSnapshot> _undoHistory = new();
    private readonly Stack<EditorStateSnapshot> _redoHistory = new();
    private readonly Dictionary<HistoryThumbnailKey, Bitmap> _historyThumbnails = new();
    private readonly LinkedList<HistoryThumbnailKey> _historyThumbnailOrder = new();
    private ScrubPreviewRequest? _pendingScrubPreview;
    private CancellationTokenSource? _activeScrubPreviewCancellation;
    private Task _scrubPreviewWorker = Task.CompletedTask;
    private int activeDecodeOperations;
    private TaskCompletionSource? decodeIdleCompletion;
    private Task previewDisposalTask = Task.CompletedTask;
    private long _scrubPreviewGeneration;
    private bool _acceptScrubPreviews;
    private bool _scrubPreviewWorkerRunning;
    private bool _restoringHistory;
    
    public MainWindowViewModel(IDialogService dialogService, IConfigurationService configService)
    {
        _dialogService = dialogService;
        _configService = configService;
        enableSdrToneMapping = configService.CurrentConfig.EnableSdrToneMapping;
        preferHardwareDecodingForPreview = configService.CurrentConfig.PreferHardwareDecoding;
        _exportCurrentProgramOnly = configService.CurrentConfig.ExportCurrentProgramOnly;
        ExportQueue.CollectionChanged += (_, _) => NotifyQueueChanged();
        // 构造菜单项
        BuildThemeMenuItems();
        // 计算型本地化文本不会随资源字典自动刷新，语言切换后需主动通知界面重新取值。
        App.LocalizationService.LanguageChanged += OnLanguageChanged;
        Avalonia.Application.Current!.ActualThemeVariantChanged += (_, _) => UpdateThemeMenuChecks();
    }

    private void OnLanguageChanged()
    {
        // 集合项的计算属性不会随资源字典自动刷新，需要逐项通知界面重新取值。
        foreach (var program in PreviewPrograms)
            program.RefreshLocalizedText();
        foreach (var subtitle in PreviewSubtitleTracks)
            subtitle.RefreshLocalizedText();
        foreach (var clip in Clips)
            clip.RefreshLocalizedText();
        foreach (var item in ExportQueue)
            item.RefreshLocalizedText();

        OnPropertyChanged(nameof(ZoomFactorStr));
        OnPropertyChanged(nameof(DecodeModeText));
        OnPropertyChanged(nameof(ToneMappingStatusText));
        OnPropertyChanged(nameof(StatusInfoText));
        OnPropertyChanged(nameof(ToolsOnlyMessage));
        OnPropertyChanged(nameof(FilteredClipSizeStr));
        OnPropertyChanged(nameof(ClipOutputText));
        OnPropertyChanged(nameof(ClipOutputToolTip));
        OnPropertyChanged(nameof(SelectedClipEstimatedSizeStr));
        OnPropertyChanged(nameof(SelectedClipsSummaryStr));
    }

    private long _displayedFramePosition = -1;
    private long _displayedFramePts = long.MinValue;
    private long _displayedFramePts90k = long.MinValue;
    private double _displayedFrameTime;
    private PacketEndSignature _displayedFrameEndSignature;
    private bool _isResolvingClipEnd;
    private CancellationTokenSource? _clipEndCancellation;
    private long PositionInFile => _displayedFramePosition;
    private long CurrentPts => _displayedFramePts;

    [ObservableProperty]
    private ObservableCollection<MenuItem> _themeMenuItems = new();

    public TimelineViewportState TimelineViewport { get; } = new();

    [ObservableProperty]
    public partial ObservableCollection<PickedClip> Clips { get; set; } = new();

    // SelectedClip 表示用于编辑起止点的活动项；IsSelected 表示参与多选合并的集合。
    private PickedClip? _clipSelectionAnchor;
    public int SelectedClipCount => Clips.Count(clip => clip.IsSelected);

    // 按选中数量复用现有输出命令，让按钮的文案、可用状态和执行行为保持一致。
    public IAsyncRelayCommand ClipOutputCommand => SelectedClipCount > 1
        ? MergeSelectedClipsCommand
        : SaveVideoClickCommand;
    public string ClipOutputText => SelectedClipCount > 1
        ? LocalizationManager.Instance.String_MergeClips_Button
        : LocalizationManager.Instance.String_SaveClip;
    public string ClipOutputToolTip => SelectedClipCount > 1
        ? string.Format(LocalizationManager.Instance.String_MergeClips_ButtonTip, SelectedClipCount)
        : LocalizationManager.Instance.String_SaveClipTip;
    public IReadOnlyList<ClipTimelineRange> SelectedClipRanges => Clips
        .Where(clip => clip.IsSelected)
        .Select(clip => new ClipTimelineRange(
            clip.StartTime,
            clip.EndTime < 0 ? DurationMax : clip.EndTime,
            ReferenceEquals(clip, SelectedClip), clip.DisplayName, clip.Notes, clip.Color))
        .ToArray();

    public string SelectedClipEstimatedSizeStr => GetSelectedMergedOutputSize() is { } mergedSize ? mergedSize
        : SelectedClipCount > 1
        ? IsProgramExportFiltered || IsRemuxExport ? FilteredClipSizeStr
            : LocalizationManager.Instance.String_SizePrefix + CommonUtil.FormatFileSize(GetSelectedClipAggregate().Bytes)
        : SelectedClip is { } clip
            ? IsProgramExportFiltered || IsRemuxExport ? clip.ExportSizeStr : clip.EstimatedSizeStr ?? string.Empty
            : string.Empty;

    private string? GetSelectedMergedOutputSize()
    {
        var selected = Clips.Where(clip => clip.IsSelected).ToArray();
        if (selected.Length < 2 || selected[0].MergedOutput is not { } group || group.ClipCount != selected.Length ||
            selected[0].OutputFileInfo is not { Exists: true } file ||
            selected.Any(clip => clip.MergedOutput != group || clip.OutputFileInfo is not { Exists: true } output ||
                                 !PathsEqual(file.FullName, output.FullName))) return null;
        return LocalizationManager.Instance.String_SizePrefix + CommonUtil.FormatFileSize(file.Length);
    }

    public string SelectedClipsSummaryStr
    {
        get
        {
            var aggregate = GetSelectedClipAggregate();
            if (aggregate.Count == 0)
                return string.Empty;
            var duration = CommonUtil.FormatSeconds(aggregate.DurationSeconds);
            return aggregate.Count == 1
                ? string.Format(
                    LocalizationManager.Instance.String_Clips_SelectionSummarySingle,
                    aggregate.Count,
                    duration)
                : string.Format(
                    LocalizationManager.Instance.String_Clips_SelectionSummary,
                    aggregate.Count,
                    duration);
        }
    }

    private long _queueIdCounter;
    private string? _lastQueueOutputDirectory;
    private string? _projectPath;
    private string? _savedProjectState;
    private void SetProjectPath(string? path)
    {
        _projectPath = path;
        OnPropertyChanged(nameof(WindowTitle));
    }
    public ObservableCollection<ExportQueueItem> ExportQueue { get; } = new();

    public bool HasExportQueue => ExportQueue.Count > 0;
    public int ExportQueueCount => ExportQueue.Count;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(
        nameof(AddClipCommand), nameof(RemoveClipCommand), nameof(MarkClipStartCommand),
        nameof(MarkClipEndCommand), nameof(SaveVideoClickCommand), nameof(CloseVideoClickCommand),
        nameof(SaveFrameClickCommand), nameof(ShowMediaInfoClickCommand), nameof(ExportAllCommand),
        nameof(AddToQueueCommand)
    )]
    public partial PickedClip? SelectedClip { get; set; }

    partial void OnSelectedClipChanged(PickedClip? value)
    {
        foreach (var clip in Clips)
            clip.IsActive = ReferenceEquals(clip, value);
        OnPropertyChanged(nameof(SelectedClipEstimatedSizeStr));
    }

    partial void OnSelectedClipChanged(PickedClip? oldValue, PickedClip? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnSelectedClipSizeChanged;
        if (newValue is not null) newValue.PropertyChanged += OnSelectedClipSizeChanged;
    }

    private void OnSelectedClipSizeChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(PickedClip.EstimatedSizeStr))
            OnPropertyChanged(nameof(SelectedClipEstimatedSizeStr));
    }

    public void SelectClip(PickedClip clip, KeyModifiers modifiers = KeyModifiers.None)
    {
        var toggle = modifiers.HasFlag(KeyModifiers.Control) ||
                     modifiers.HasFlag(KeyModifiers.Meta);
        if (modifiers.HasFlag(KeyModifiers.Shift) && _clipSelectionAnchor is not null)
        {
            var anchorIndex = Clips.IndexOf(_clipSelectionAnchor);
            var targetIndex = Clips.IndexOf(clip);
            if (anchorIndex >= 0 && targetIndex >= 0)
            {
                foreach (var item in Clips)
                    item.IsSelected = false;
                var start = Math.Min(anchorIndex, targetIndex);
                var end = Math.Max(anchorIndex, targetIndex);
                for (var index = start; index <= end; index++)
                    Clips[index].IsSelected = true;
                SelectedClip = clip;
            }
        }
        else if (toggle)
        {
            clip.IsSelected = !clip.IsSelected;
            if (clip.IsSelected)
            {
                SelectedClip = clip;
                _clipSelectionAnchor = clip;
            }
            else if (ReferenceEquals(SelectedClip, clip))
            {
                SelectedClip = Clips.LastOrDefault(item => item.IsSelected);
            }
        }
        else
        {
            foreach (var item in Clips)
                item.IsSelected = ReferenceEquals(item, clip);
            SelectedClip = clip;
            _clipSelectionAnchor = clip;
        }

        NotifyClipSelectionChanged();
    }

    private void NotifyClipSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedClipCount));
        OnPropertyChanged(nameof(ClipOutputCommand));
        OnPropertyChanged(nameof(ClipOutputText));
        OnPropertyChanged(nameof(ClipOutputToolTip));
        OnPropertyChanged(nameof(SelectedClipRanges));
        OnPropertyChanged(nameof(SelectedClipEstimatedSizeStr));
        OnPropertyChanged(nameof(SelectedClipsSummaryStr));
        SelectAllClipsCommand.NotifyCanExecuteChanged();
        MergeSelectedClipsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSelectAllClips))]
    private void SelectAllClips()
    {
        foreach (var clip in Clips)
            clip.IsSelected = true;

        SelectedClip ??= Clips.FirstOrDefault();
        _clipSelectionAnchor = SelectedClip;
        NotifyClipSelectionChanged();
    }

    private bool CanSelectAllClips => Clips.Any(clip => !clip.IsSelected);

    private (int Count, long Bytes, double DurationSeconds) GetSelectedClipAggregate()
    {
        if (IsRemuxExport)
        {
            var selected = Clips.Where(clip => clip.IsSelected && clip.EndTime > clip.StartTime)
                .OrderBy(clip => clip.StartTime).ToArray();
            if (selected.Length == 0) return default;
            var start = selected[0].StartTime;
            var end = selected[0].EndTime;
            double duration = 0;
            foreach (var clip in selected.Skip(1))
            {
                if (clip.StartTime > end) { duration += end - start; start = clip.StartTime; }
                end = Math.Max(end, clip.EndTime);
            }
            return (selected.Length, 0, duration + end - start);
        }
        var ranges = Clips
            .Where(clip => clip.IsSelected)
            .Select(clip => new ClipAggregateRange(
                Math.Max(0, clip.StartPosition),
                clip.EndPosition > 0
                    ? Math.Min(clip.EndPosition, clip.InFileInfo.Length)
                    : clip.InFileInfo.Length,
                Math.Max(0, clip.StartTime),
                clip.EndTime < 0 ? DurationMax : clip.EndTime))
            .Where(range => range.EndPosition > range.StartPosition &&
                            range.EndTime > range.StartTime)
            .OrderBy(range => range.StartPosition)
            .ToArray();
        if (ranges.Length == 0)
            return default;

        long totalBytes = 0;
        double totalDuration = 0;
        var current = ranges[0];
        for (var index = 1; index < ranges.Length; index++)
        {
            var next = ranges[index];
            if (next.StartPosition > current.EndPosition)
            {
                totalBytes += current.EndPosition - current.StartPosition;
                totalDuration += current.EndTime - current.StartTime;
                current = next;
                continue;
            }

            // 与实际合并服务保持一致：重叠剪辑只输出一次，大小和时长按并集统计。
            current = current with
            {
                EndPosition = Math.Max(current.EndPosition, next.EndPosition),
                EndTime = Math.Max(current.EndTime, next.EndTime)
            };
        }

        totalBytes += current.EndPosition - current.StartPosition;
        totalDuration += current.EndTime - current.StartTime;
        return (ranges.Length, totalBytes, totalDuration);
    }
    
    [RelayCommand]
    private async Task JumpToStartTimeAsync(object? time)
    {
        if (time is PickedClip { StartTime: > -1 } pickedClip)
        {
            SelectClip(pickedClip);
            await JumpToTimeAsync(pickedClip.StartPts);
        }
    }
    
    [RelayCommand]
    private async Task JumpToEndTimeAsync(object? time)
    {
        if (time is PickedClip { EndTime: > -1, EndPosition: >= 0 } pickedClip)
        {
            SelectClip(pickedClip);
            await JumpToTimeAsync(pickedClip.EndPts);
        }
    }

    private async Task JumpToTimeAsync(long time)
    {
        if (!IsVideoInitialized || IsDecoding) return;
        await RunDecodeOperationAsync(async () =>
        {
            await SeekFileAsync(time, true);
            // 解码至目标关键帧
            await DrawNextFrameCoreAsync(1);
            while (CurrentPts < time)
            {
                await DrawNextFrameCoreAsync(1);
            }
        });
    }
    
    [RelayCommand]
    private void FindFile(object? fileInfo)
    {
        if (fileInfo is FileInfo file)
        {
            CommonUtil.OpenFileLocation(file.FullName);
        }
    }

    [RelayCommand]
    private async Task JumpToClickAsync()
    {
        if (!IsVideoInitialized)
        {
            await ShowMessageAsync(LocalizationManager.Instance.String_PleaseLoadAVideo,
                LocalizationManager.Instance.String_VideoNotInit, MessageBoxIcon.Information);
            return;
        }
        
        var vm = _dialogService.CreateViewModel<JumpTimeViewModel>();
        var currentTimeStr = CommonUtil.FormatSeconds(CurrentTime);
        vm.InputText = currentTimeStr;
        var result = await _dialogService.ShowDialogAsync(this, vm);
        
        if (result is false || result is null || currentTimeStr == vm.InputText) return;
        
        if (CommonUtil.TryParseFormattedTime(vm.InputText, out var newTime) && newTime < DurationMax)
        {
            await SeekToTimeAsync(TimeSpan.FromSeconds(Math.Max(0, newTime - 1)));
            await DrawNextFrameAsync(1);
            return;
        }

        await ShowMessageAsync(LocalizationManager.Instance.String_PleaseInputValidTime,
            LocalizationManager.Instance.String_WrongFormat, MessageBoxIcon.Error);
    }

    [RelayCommand]
    private async Task ProcessCommandLineAsync()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
        {
            if (IsProjectFile(args[1]))
            {
                await OpenProjectPathAsync(args[1]);
                return;
            }
            await OpenVideoPathAsync(args[1]);
        }
    }

    [RelayCommand(CanExecute = nameof(CanMarkVideo))]
    private void AddClip()
    {
        RecordHistory();
        AddClipCore();
    }

    private PickedClip AddClipCore()
    {
        var newClip = new PickedClip()
        {
            InFileInfo = new FileInfo(VideoPath),
            RequiresRemux = IsRemuxExport,
            StartTime = _displayedFrameTime,
            StartPosition = Math.Max(0, PositionInFile),
            StartPts = CurrentPts,
            EndTime = DurationMax,
        };
        newClip.ReplaceStartThumbnail(CreateCurrentFrameThumbnail());
        Clips.Add(newClip);
        SelectClip(newClip);
        return newClip;
    }

    [RelayCommand(CanExecute = nameof(CanEditSelectedClip))]
    private void RemoveClip()
    {
        var index = Clips.ToList().FindIndex(x => x.ClipID == SelectedClip?.ClipID);
        if (index <= -1) return;

        RecordHistory();

        var removed = Clips[index];
        Clips.RemoveAt(index);
        removed.Dispose();
        if (ReferenceEquals(_clipSelectionAnchor, removed))
            _clipSelectionAnchor = null;
        SelectedClip = Clips.LastOrDefault(item => item.IsSelected);
        NotifyClipSelectionChanged();
    }

    [RelayCommand(CanExecute = nameof(CanMarkSelectedClip))]
    private void MarkClipStart()
    {
        RecordHistory();
        MarkClipStartCore();
    }

    private void MarkClipStartCore()
    {
        SelectedClip!.StartTime = _displayedFrameTime;
        SelectedClip!.StartPts = CurrentPts;
        SelectedClip!.StartPosition = Math.Max(0, PositionInFile);
        SelectedClip!.ReplaceStartThumbnail(CreateCurrentFrameThumbnail());
        if (SelectedClip!.EndTime <= _displayedFrameTime)
        {
            SelectedClip!.EndTime = DurationMax;
            SelectedClip!.EndPosition = -1;
            SelectedClip!.ReplaceEndThumbnail(null);
        }
        NotifyClipSelectionChanged();
    }

    // 并发由当前视频的扫描状态控制，更换源后不等待旧任务退出。
    [RelayCommand(CanExecute = nameof(CanMarkSelectedClip), AllowConcurrentExecutions = true)]
    private Task MarkClipEndAsync() => MarkClipEndCoreAsync(ensureSelectedClip: false);

    private async Task MarkClipEndCoreAsync(bool ensureSelectedClip)
    {
        if (!CanMarkVideo || (!ensureSelectedClip && SelectedClip is null))
            return;
        // 先冻结当前显示帧。扫描期间允许导航，结果仍属于点击时的画面和片段。
        var video = _videoInstance;
        var path = VideoPath;
        var clip = SelectedClip;
        var time = _displayedFrameTime;
        var pts = CurrentPts;
        var position = PositionInFile;
        var pts90k = _displayedFramePts90k;
        var signature = _displayedFrameEndSignature;
        var thumbnail = CreateCurrentFrameThumbnail();
        using var cancellation = new CancellationTokenSource();
        _clipEndCancellation = cancellation;
        _isResolvingClipEnd = true;
        NotifyVideoCapabilityChanged();
        try
        {
            var endPosition = video is { CanRemuxClip: true } ? Math.Max(1, position) :
                await TsClipBoundaryResolver.ResolveEndAsync(path, position, pts90k,
                    signature, cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_clipEndCancellation, cancellation)
                || !ReferenceEquals(video, _videoInstance) || clip is not null && !Clips.Contains(clip))
                return;
            // 成功定位后才记录历史和修改片段；扫描失败不会留下半个标记。
            RecordHistory();
            if (clip is null)
            {
                clip = new PickedClip { InFileInfo = new FileInfo(path), RequiresRemux = IsRemuxExport };
                Clips.Add(clip);
                SelectClip(clip);
            }
            clip.EndTime = time;
            clip.EndPts = pts;
            clip.EndPosition = endPosition;
            clip.ReplaceEndThumbnail(thumbnail);
            thumbnail = null;
            if (clip.StartTime >= time)
            {
                clip.StartTime = 0;
                clip.StartPts = 0;
                clip.StartPosition = 0;
                clip.ReplaceStartThumbnail(null);
            }
            NotifyClipSelectionChanged();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 关闭或更换视频时取消尚未完成的边界扫描。
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Unable to mark clip end: {exception}");
            if (!cancellation.IsCancellationRequested && ReferenceEquals(_clipEndCancellation, cancellation)
                && ReferenceEquals(video, _videoInstance))
                await ShowMessageAsync(LocalizationManager.Instance.String_ClipEndBoundaryFailed,
                    LocalizationManager.Instance.String_Error, MessageBoxIcon.Error);
        }
        finally
        {
            thumbnail?.Dispose();
            // 旧扫描的结束回调不能解锁或清除新视频的扫描。
            if (ReferenceEquals(_clipEndCancellation, cancellation))
            {
                _clipEndCancellation = null;
                _isResolvingClipEnd = false;
                NotifyVideoCapabilityChanged();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanMarkVideo))]
    private void MarkClipStartShortcut()
    {
        RecordHistory();
        EnsureSelectedClipCore();
        MarkClipStartCore();
    }

    [RelayCommand(CanExecute = nameof(CanMarkVideo), AllowConcurrentExecutions = true)]
    private Task MarkClipEndShortcutAsync() => MarkClipEndCoreAsync(ensureSelectedClip: true);

    private void EnsureSelectedClipCore()
    {
        if (SelectedClip is null)
            AddClipCore();
    }

    private bool CanUndo => _undoHistory.Count > 0;
    private bool CanRedo => _redoHistory.Count > 0;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (!_undoHistory.TryPop(out var previousState))
            return;

        _redoHistory.Push(CaptureEditorState());
        RestoreEditorState(previousState);
        NotifyHistoryChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (!_redoHistory.TryPop(out var nextState))
            return;

        _undoHistory.Push(CaptureEditorState());
        RestoreEditorState(nextState);
        NotifyHistoryChanged();
    }

    private void RecordHistory()
    {
        if (_restoringHistory)
            return;

        _undoHistory.Push(CaptureEditorState());
        _redoHistory.Clear();
        if (_undoHistory.Count > MaximumHistoryEntries)
        {
            var retained = _undoHistory.Take(MaximumHistoryEntries).ToArray();
            _undoHistory.Clear();
            for (var index = retained.Length - 1; index >= 0; index--)
                _undoHistory.Push(retained[index]);
        }

        NotifyHistoryChanged();
    }

    private void ClearHistory()
    {
        _undoHistory.Clear();
        _redoHistory.Clear();
        foreach (var thumbnail in _historyThumbnails.Values)
            thumbnail.Dispose();
        _historyThumbnails.Clear();
        _historyThumbnailOrder.Clear();
        NotifyHistoryChanged();
    }

    private void NotifyHistoryChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private EditorStateSnapshot CaptureEditorState()
    {
        var snapshot = new EditorStateSnapshot(
            Clips.Select(ClipStateSnapshot.Create).ToArray(),
            SelectedClip?.ClipID,
            _clipSelectionAnchor?.ClipID,
            _exportSettingsRevision);
        CacheSelectedClipThumbnails(snapshot);
        return snapshot;
    }

    private void CacheSelectedClipThumbnails(EditorStateSnapshot snapshot)
    {
        if (snapshot.SelectedClipId is not { } selectedId)
            return;

        var state = snapshot.Clips.FirstOrDefault(clip => clip.ClipId == selectedId);
        var clip = Clips.FirstOrDefault(item => item.ClipID == selectedId);
        if (state is null || clip is null)
            return;

        if (state.HasStartThumbnail && clip.StartThumbnail is not null)
            CacheHistoryThumbnail(CreateThumbnailKey(state, isStart: true), clip.StartThumbnail);
        if (state.HasEndThumbnail && clip.EndThumbnail is not null)
            CacheHistoryThumbnail(CreateThumbnailKey(state, isStart: false), clip.EndThumbnail);
    }

    private void CacheHistoryThumbnail(HistoryThumbnailKey key, Bitmap source)
    {
        if (_historyThumbnails.ContainsKey(key))
        {
            TouchHistoryThumbnail(key);
            return;
        }

        Bitmap copy;
        try
        {
            copy = ImageUtil.CreateThumbnail(source, source.PixelSize.Width, source.PixelSize.Height);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Unable to cache history thumbnail: {exception.Message}");
            return;
        }

        _historyThumbnails.Add(key, copy);
        _historyThumbnailOrder.AddFirst(key);
        while (_historyThumbnailOrder.Count > MaximumHistoryThumbnailEntries)
        {
            var evictedKey = _historyThumbnailOrder.Last!.Value;
            _historyThumbnailOrder.RemoveLast();
            if (_historyThumbnails.Remove(evictedKey, out var evictedThumbnail))
                evictedThumbnail.Dispose();
        }
    }

    private void TouchHistoryThumbnail(HistoryThumbnailKey key)
    {
        _historyThumbnailOrder.Remove(key);
        _historyThumbnailOrder.AddFirst(key);
    }

    private static HistoryThumbnailKey CreateThumbnailKey(ClipStateSnapshot state, bool isStart) =>
        isStart
            ? new HistoryThumbnailKey(state.ClipId, true, state.StartTime, state.StartPts, state.StartPosition)
            : new HistoryThumbnailKey(state.ClipId, false, state.EndTime, state.EndPts, state.EndPosition);

    private Bitmap? GetHistoryThumbnail(ClipStateSnapshot state, bool isStart)
    {
        var key = CreateThumbnailKey(state, isStart);
        if (!_historyThumbnails.TryGetValue(key, out var cached))
            return null;

        TouchHistoryThumbnail(key);
        try
        {
            return ImageUtil.CreateThumbnail(cached, cached.PixelSize.Width, cached.PixelSize.Height);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Unable to restore history thumbnail: {exception.Message}");
            return null;
        }
    }

    private void RestoreEditorState(EditorStateSnapshot snapshot)
    {
        // 撤销/重做已取代当前编辑，旧结尾扫描不能再写回复用的片段对象。
        CancelClipEndScan();
        _restoringHistory = true;
        try
        {
            var existing = Clips.ToDictionary(clip => clip.ClipID);
            var targetIds = snapshot.Clips.Select(state => state.ClipId).ToHashSet();
            foreach (var clip in Clips.Where(clip => !targetIds.Contains(clip.ClipID)))
                clip.Dispose();

            var restored = new List<PickedClip>(snapshot.Clips.Count);
            foreach (var state in snapshot.Clips)
            {
                if (!existing.TryGetValue(state.ClipId, out var clip))
                {
                    clip = new PickedClip(state.ClipId)
                    {
                        InFileInfo = new FileInfo(state.FilePath), RequiresRemux = IsRemuxExport
                    };
                }

                ApplyClipState(clip, state);
                if (snapshot.ExportSettingsRevision != _exportSettingsRevision) clip.InvalidateExport();
                restored.Add(clip);
            }

            Clips.Clear();
            foreach (var clip in restored)
                Clips.Add(clip);

            foreach (var clip in Clips)
                clip.IsActive = false;
            SelectedClip = snapshot.SelectedClipId is { } selectedId
                ? Clips.FirstOrDefault(clip => clip.ClipID == selectedId)
                : null;
            _clipSelectionAnchor = snapshot.SelectionAnchorId is { } anchorId
                ? Clips.FirstOrDefault(clip => clip.ClipID == anchorId)
                : null;
            NotifyClipSelectionChanged();
        }
        finally
        {
            _restoringHistory = false;
            NotifyVideoCapabilityChanged();
        }
    }

    private void ApplyClipState(PickedClip clip, ClipStateSnapshot state)
    {
        var startChanged = clip.StartTime != state.StartTime
                           || clip.StartPts != state.StartPts
                           || clip.StartPosition != state.StartPosition;
        var endChanged = clip.EndTime != state.EndTime
                         || clip.EndPts != state.EndPts
                         || clip.EndPosition != state.EndPosition;

        clip.Name = state.Name;
        clip.Notes = state.Notes;
        clip.Color = state.Color;
        clip.StartTime = state.StartTime;
        clip.StartPts = state.StartPts;
        clip.StartPosition = state.StartPosition;
        clip.EndTime = state.EndTime;
        clip.EndPts = state.EndPts;
        clip.EndPosition = state.EndPosition;
        clip.IsSelected = state.IsSelected;
        clip.MergedOutput = state.MergedOutput;
        clip.OutputFileInfo = state.OutputFilePath is null ? null : new FileInfo(state.OutputFilePath);
        clip.ExportStatus = state.ExportStatus;
        clip.ExportPercent = state.ExportPercent;

        if (startChanged || clip.HasStartThumbnail != state.HasStartThumbnail)
        {
            clip.ReplaceStartThumbnail(null);
            if (state.HasStartThumbnail)
                clip.ReplaceStartThumbnail(GetHistoryThumbnail(state, isStart: true));
        }
        if (endChanged || clip.HasEndThumbnail != state.HasEndThumbnail)
        {
            clip.ReplaceEndThumbnail(null);
            if (state.HasEndThumbnail)
                clip.ReplaceEndThumbnail(GetHistoryThumbnail(state, isStart: false));
        }
    }

    private Bitmap? CreateCurrentFrameThumbnail() => DecodedBitmap is { } bitmap
        ? ImageUtil.CreateThumbnail(bitmap)
        : null;

    // 顶部菜单不传路径，继续选择文件；工具栏传入当前路径，直接创建独立工具窗口。
    private async Task<string?> ResolveTsToolFileAsync(string? filePath, string title, bool standardOnly = false)
    {
        if (filePath is not null)
        {
            if (!CanUseCurrentFileTools || !File.Exists(filePath))
                return null;
        }
        else
        {
            var settings = new OpenFileDialogSettings
            {
                Title = title,
                Filters = [new(LocalizationManager.Instance.String_TsFiles, ["ts", "m2ts", "mts", "m2t", "mpegts"])]
            };
            var result = await _dialogService.ShowOpenFilesDialogAsync(this, settings);
            filePath = result.FirstOrDefault()?.LocalPath;
        }
        if (standardOnly && filePath is not null)
        {
            try
            {
                var layout = await Task.Run(() => TsPacketLayout.Detect(filePath));
                if (layout.PacketSize != 0) layout.RequireSupported(standardOnly: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                await ShowMessageAsync(exception.Message, LocalizationManager.Instance.String_Error, MessageBoxIcon.Error);
                return null;
            }
        }
        return filePath;
    }

    [RelayCommand]
    private async Task RawCutterClickAsync(string? filePath)
    {
        filePath = await ResolveTsToolFileAsync(filePath, LocalizationManager.Instance.String_OpenTsFile);
        if (filePath is null) return;

        var dialogViewModel = _dialogService.CreateViewModel<RawCutterWindowViewModel>();
        try
        {
            await dialogViewModel.InitializeAsync(filePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            dialogViewModel.OnClosed();
            await ShowMessageAsync(exception.Message, "TSCutter", MessageBoxIcon.Error);
            return;
        }

        // 工具窗口需要与主窗口自由切换前后层级，因此不设置 Owner；文件选择框等
        // 真正的对话框仍继续以主窗口为 Owner。
        _dialogService.Show(null, dialogViewModel);
    }

    [RelayCommand]
    private async Task TsCheckClickAsync(string? filePath)
    {
        filePath = await ResolveTsToolFileAsync(filePath, LocalizationManager.Instance.String_TsCheck_OpenFile);
        if (filePath is null) return;

        var dialogViewModel = _dialogService.CreateViewModel<TsCheckWindowViewModel>();
        dialogViewModel.FilePath = filePath;
        _dialogService.Show(null, dialogViewModel);
    }

    [RelayCommand]
    private void TsBatchCheckClick()
    {
        var dialogViewModel = _dialogService.CreateViewModel<TsBatchCheckWindowViewModel>();
        _dialogService.Show(null, dialogViewModel);
    }

    [RelayCommand]
    private async Task TsTimelineRepairClickAsync(string? filePath)
    {
        filePath = await ResolveTsToolFileAsync(filePath, LocalizationManager.Instance.String_TsTimelineRepair_OpenFile, standardOnly: true);
        if (filePath is null) return;

        var dialogViewModel = _dialogService.CreateViewModel<TsTimelineRepairWindowViewModel>();
        dialogViewModel.Initialize(filePath);
        _dialogService.Show(null, dialogViewModel);
    }

    [RelayCommand]
    private async Task TsFilterClickAsync(string? filePath)
    {
        filePath = await ResolveTsToolFileAsync(filePath, LocalizationManager.Instance.String_TsFilter_OpenFile, standardOnly: true);
        if (filePath is null) return;

        var dialogViewModel = _dialogService.CreateViewModel<TsFilterWindowViewModel>();
        dialogViewModel.FilePath = filePath;
        _dialogService.Show(null, dialogViewModel);
    }

    [RelayCommand]
    private async Task TsServiceFilterClickAsync(string? filePath)
    {
        filePath = await ResolveTsToolFileAsync(filePath, LocalizationManager.Instance.String_TsServiceFilter_OpenFile);
        if (filePath is null) return;

        var dialogViewModel = _dialogService.CreateViewModel<TsServiceFilterWindowViewModel>();
        dialogViewModel.FilePath = filePath;
        _dialogService.Show(null, dialogViewModel);
    }

    [RelayCommand]
    private async Task TsEsExtractorClickAsync(string? filePath)
    {
        filePath = await ResolveTsToolFileAsync(filePath, LocalizationManager.Instance.String_TsEsExtractor_OpenFile);
        if (filePath is null) return;

        var dialogViewModel = _dialogService.CreateViewModel<TsEsExtractorWindowViewModel>();
        dialogViewModel.FilePath = filePath;
        _dialogService.Show(null, dialogViewModel);
    }

    [RelayCommand]
    private void TsMultiSourceRepairClick()
    {
        var dialogViewModel = _dialogService.CreateViewModel<TsMultiSourceRepairWindowViewModel>();
        _dialogService.Show(null, dialogViewModel);
    }

    [RelayCommand]
    private void TsBinaryMergeClick()
    {
        var dialogViewModel = _dialogService.CreateViewModel<TsBinaryMergeWindowViewModel>();
        _dialogService.Show(null, dialogViewModel);
    }

    [RelayCommand]
    private async Task TsPacketViewerClickAsync(string? filePath)
    {
        filePath = await ResolveTsToolFileAsync(filePath, LocalizationManager.Instance.String_TsPacketViewer_OpenFile);
        if (filePath is null) return;

        var dialogViewModel = _dialogService.CreateViewModel<TsPacketViewerWindowViewModel>();
        dialogViewModel.FilePath = filePath;
        _dialogService.Show(null, dialogViewModel);
    }

    [RelayCommand(CanExecute = nameof(CanUsePreview))]
    private async Task KeyFrameOverviewClickAsync()
    {
        keyFrameOverviewWindow?.CloseWindow();
        var dialogViewModel = _dialogService.CreateViewModel<KeyFrameOverviewWindowViewModel>();
        dialogViewModel.FilePath = VideoPath;
        dialogViewModel.EnableSdrToneMapping = EnableSdrToneMapping;
        dialogViewModel.StreamSelection = _activePreviewSelection;
        keyFrameOverviewWindow = dialogViewModel;
        try
        {
            await _dialogService.ShowDialogAsync(this, dialogViewModel);
            await dialogViewModel.ClosedTask;
            if (dialogViewModel.SelectedTime is { } time)
                await OnOverviewTimeSelected(time);
        }
        finally
        {
            if (ReferenceEquals(keyFrameOverviewWindow, dialogViewModel))
                keyFrameOverviewWindow = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUsePreview))]
    private async Task ThumbnailSheetClickAsync()
    {
        var dialogViewModel = _dialogService.CreateViewModel<ThumbnailSheetWindowViewModel>();
        dialogViewModel.FilePath = VideoPath;
        dialogViewModel.EnableSdrToneMapping = EnableSdrToneMapping;
        dialogViewModel.StreamSelection = _activePreviewSelection;
        await _dialogService.ShowDialogAsync(this, dialogViewModel);
    }

    [RelayCommand(CanExecute = nameof(CanUsePreview))]
    private async Task FrameSearchClickAsync()
    {
        var manager = _dialogService.CreateViewModel<FrameTemplateManagerWindowViewModel>();
        manager.FilePath = VideoPath;
        manager.EnableSdrToneMapping = EnableSdrToneMapping;
        manager.CurrentTimeSeconds = CurrentTime;
        manager.SearchFromCurrent = CurrentTime > 0;
        await _dialogService.ShowDialogAsync(this, manager);
        if (manager.ChosenTemplate is not { } template) return;

        var search = _dialogService.CreateViewModel<FrameSearchWindowViewModel>();
        search.FilePath = VideoPath;
        search.EnableSdrToneMapping = EnableSdrToneMapping;
        search.StreamSelection = _activePreviewSelection;
        search.Template = template;
        search.StartTime = manager.SearchFromCurrent
            ? TimeSpan.FromSeconds(manager.CurrentTimeSeconds) : TimeSpan.Zero;
        await _dialogService.ShowDialogAsync(this, search);
        await search.ClosedTask;
        if (search.SelectedTime is { } time)
            await OnOverviewTimeSelected(time);
    }

    private async Task OnOverviewTimeSelected(TimeSpan time)
    {
        if (!IsVideoInitialized)
            return;
        try
        {
            await CompleteScrubPreviewAsync();
            await SeekToTimeAndDrawFrameAsync(time);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Overview seek failed: {exception.Message}");
        }
    }

    [RelayCommand]
    private async Task TsRemuxClickAsync(string? filePath)
    {
        filePath = await ResolveTsToolFileAsync(filePath, LocalizationManager.Instance.String_TsRemux_OpenFile, standardOnly: true);
        if (filePath is null) return;

        var dialogViewModel = _dialogService.CreateViewModel<TsRemuxWindowViewModel>();
        dialogViewModel.FilePath = filePath;
        _dialogService.Show(null, dialogViewModel);
    }

    [RelayCommand(CanExecute = nameof(CanEditSelectedClip))]
    private async Task SaveVideoClickAsync() => await SaveVideoAsync();

    [RelayCommand(CanExecute = nameof(CanMergeSelectedClips))]
    private async Task MergeSelectedClipsAsync()
    {
        var selected = Clips
            .Where(clip => clip.IsSelected)
            .OrderBy(clip => clip.StartPosition)
            .ToArray();
        if (selected.Length < 2)
            return;

        var sourcePath = selected[0].InFileInfo.FullName;
        var programId = SelectedOutputProgramId;
        var streamPids = _videoInstance!.GetMergeStreamPids(programId is not null);
        var settings = new SaveFileDialogSettings
        {
            Title = LocalizationManager.Instance.String_MergeClips_Title,
            SuggestedStartLocation = new DesktopDialogStorageFolder(
                Path.GetDirectoryName(sourcePath)!),
            SuggestedFileName = Path.GetFileNameWithoutExtension(sourcePath) + "_merged." + ClipExtension,
            Filters =
            [
                new(ExportFileFilterName, [ClipExtension]),
                new(LocalizationManager.Instance.String_AllFiles, "*")
            ],
            DefaultExtension = ClipExtension
        };
        var result = await _dialogService.ShowSaveFileDialogAsync(this, settings);
        if (result?.Path is null)
            return;

        var outputPath = result.Path.LocalPath;
        if (PathsEqual(sourcePath, outputPath))
        {
            await ShowMessageAsync(
                LocalizationManager.Instance.String_MergeClips_OutputMatchesSource,
                LocalizationManager.Instance.String_Error,
                MessageBoxIcon.Error);
            return;
        }

        var request = new TsClipMergeRequest
        {
            SourcePath = sourcePath,
            OutputPath = outputPath,
            ProgramId = programId,
            VideoPid = streamPids.VideoPid,
            AudioPids = streamPids.AudioPids,
            Ranges = selected.Select(clip => new TsClipMergeRange(
                clip.StartPosition,
                clip.EndPosition,
                clip.StartTime,
                clip.EndTime < 0 ? DurationMax : clip.EndTime)).ToArray()
        };
        var outputViewModel = _dialogService.CreateViewModel<OutputWindowViewModel>();
        if (IsRemuxExport)
            outputViewModel.SetRemuxTask(new RemuxExportRequest(outputPath, CreateMergedRemuxSegments(selected)));
        else
            outputViewModel.SetMergeTask(request);
        await _dialogService.ShowDialogAsync(this, outputViewModel).ConfigureAwait(true);
        if (outputViewModel.Exception is not null)
        {
            await ShowMessageAsync(
                FormatMergeError(outputViewModel.Exception),
                LocalizationManager.Instance.String_Error,
                MessageBoxIcon.Error);
            return;
        }

        if (outputViewModel.DialogResult == true)
        {
            if (File.Exists(outputPath))
            {
                var group = new MergedClipOutput(Guid.NewGuid().ToString("N"), selected.Length);
                foreach (var clip in selected)
                {
                    clip.MergedOutput = group;
                    clip.OutputFileInfo = new FileInfo(outputPath);
                    clip.ExportStatus = ClipExportStatus.Done;
                }
                OnPropertyChanged(nameof(SelectedClipEstimatedSizeStr));
            }
            await ShowMessageAsync(
                string.Format(
                    LocalizationManager.Instance.String_MergeClips_Completed,
                    outputPath),
                LocalizationManager.Instance.String_MergeClips_Title,
                MessageBoxIcon.Information);
        }
    }

    private bool CanMergeSelectedClips => CanEditVideo && SelectedClipCount >= 2 &&
                                          Clips.Where(clip => clip.IsSelected).All(clip =>
                                              clip.EndTime > clip.StartTime &&
                                              (IsRemuxExport || clip.StartPosition >= 0 &&
                                              (clip.EndPosition < 0 ||
                                               clip.EndPosition > clip.StartPosition)));

    private static string FormatMergeError(Exception exception)
    {
        if (exception is RemuxExportException remuxException)
            return FormatRemuxExportError(remuxException);
        if (exception is TsFilterException filterException)
        {
            var strings = LocalizationManager.Instance;
            var format = filterException.Code switch
            {
                TsFilterErrorCode.SameFile => strings.String_TsFilter_Error_SameFile,
                TsFilterErrorCode.SyncLost => strings.String_TsFilter_Error_SyncLost,
                TsFilterErrorCode.PatTooLarge => strings.String_TsFilter_Error_PatTooLarge,
                TsFilterErrorCode.PmtTooLarge => strings.String_TsFilter_Error_PmtTooLarge,
                TsFilterErrorCode.SdtTooLarge => strings.String_TsServiceFilter_Error_SdtTooLarge,
                TsFilterErrorCode.MissingProgram => strings.String_TsServiceFilter_Error_MissingProgram,
                _ => null
            };
            return format is null ? filterException.Message : string.Format(format, filterException.Arguments);
        }
        if (exception is not TsClipMergeException mergeException)
            return exception.Message;
        return mergeException.Code switch
        {
            TsClipMergeErrorCode.OutputMatchesSource =>
                LocalizationManager.Instance.String_MergeClips_OutputMatchesSource,
            TsClipMergeErrorCode.NoSync =>
                LocalizationManager.Instance.String_MergeClips_NoSync,
            TsClipMergeErrorCode.InvalidRange =>
                LocalizationManager.Instance.String_MergeClips_InvalidRange,
            _ => LocalizationManager.Instance.String_MergeClips_SourceChanged
        };
    }

    [RelayCommand(CanExecute = nameof(CanUseCurrentFile))]
    private async Task CloseVideoClickAsync()
    {
        if (!await ConfirmProjectReplacementAsync()) return;
        Close();
        ClearVars();
        SetProjectPath(null);
        if (ExportQueue.Count == 0) MarkProjectCheckpoint();
    }

    [RelayCommand(CanExecute = nameof(CanUseCurrentFile))]
    private async Task ShowMediaInfoClickAsync()
    {
        var dialogViewModel = _dialogService.CreateViewModel<MediainfoWindowViewModel>();
        dialogViewModel.FilePath = VideoPath;
        await _dialogService.ShowDialogAsync(this, dialogViewModel);
    }
    
    [RelayCommand(CanExecute = nameof(CanUsePreview))]
    private async Task SaveFrameClickAsync()
    {
        if (DecodedBitmap is null) return;

        var captureVm = _dialogService.CreateViewModel<CaptureFrameViewModel>();
        var dialogResult = await _dialogService.ShowDialogAsync(this, captureVm);
        if (dialogResult != true) return;

        if (captureVm.IsSaveToFile)
        {
            var defaultName = Path.GetFileNameWithoutExtension(VideoPath) + $"_{CommonUtil.FormatSeconds(CurrentTime, true)}";
            var isPng = captureVm.IsPngFormat;
            var ext = isPng ? "png" : "jpg";
            var filterName = isPng
                ? LocalizationManager.Instance.String_PngImages
                : LocalizationManager.Instance.String_JpgImages;
            var settings = new SaveFileDialogSettings
            {
                Title = LocalizationManager.Instance.String_SaveFrame,
                SuggestedStartLocation = new DesktopDialogStorageFolder(Path.GetDirectoryName(VideoPath)!),
                SuggestedFileName = defaultName + "." + ext,
                Filters = [new(filterName, [ext])],
                DefaultExtension = ext
            };
            var result = await _dialogService.ShowSaveFileDialogAsync(this, settings);
            if (result is null) return;

            var source = DecodedBitmap;
            if (source is null) return;
            // 保存期间预览可能切换或归还帧租约，先复制像素以固定本次截图。
            using var snapshot = ImageUtil.CreateThumbnail(source, source.PixelSize.Width, source.PixelSize.Height);
            var sourceSize = DecodedFrameSourceSize;
            var correctAspectRatio = decodedFrameRequiresSampleAspectRatioCorrection;
            var outputPath = result!.Path!.LocalPath;
            await Task.Run(() =>
            {
                using var correctedBitmap = ImageUtil.CreateSampleAspectRatioCorrectedBitmap(
                    snapshot, sourceSize, correctAspectRatio);
                var bitmapToSave = correctedBitmap ?? snapshot;
                using var stream = File.Create(outputPath);
                if (isPng)
                    bitmapToSave.Save(stream);
                else
                    ImageUtil.SaveAsJpeg(bitmapToSave, stream);
            });
        }
        else
        {
            var source = DecodedBitmap;
            if (source is null) return;
            using var snapshot = ImageUtil.CreateThumbnail(source, source.PixelSize.Width, source.PixelSize.Height);
            var sourceSize = DecodedFrameSourceSize;
            var correctAspectRatio = decodedFrameRequiresSampleAspectRatioCorrection;
            using var correctedBitmap = await Task.Run(() => ImageUtil.CreateSampleAspectRatioCorrectedBitmap(
                snapshot, sourceSize, correctAspectRatio));
            await ImageUtil.CopyBitmapToClipboardAsync(
                correctedBitmap ?? snapshot,
                captureVm.IsPngFormat);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle), nameof(IsPreviewOnly),
        nameof(HasCurrentFile), nameof(CanUseCurrentFile), nameof(CanUseCurrentFileTools),
        nameof(IsToolsOnly), nameof(ToolsOnlyMessage), nameof(StatusInfoText))]
    public partial string VideoPath { get; set; }

    private Bitmap? decodedBitmap;
    private IBitmapFrameLease? decodedBitmapLease;

    public Bitmap? DecodedBitmap
    {
        get => decodedBitmap;
        set => SetDecodedBitmap(value);
    }

    private void SetDecodedBitmap(Bitmap? value, IBitmapFrameLease? bitmapLease = null)
    {
        var previousBitmap = decodedBitmap;
        var previousLease = decodedBitmapLease;

        decodedBitmap = value;
        decodedBitmapLease = bitmapLease;
        OnPropertyChanged(nameof(DecodedBitmap));
        SaveFrameClickCommand.NotifyCanExecuteChanged();
        ShowMediaInfoClickCommand.NotifyCanExecuteChanged();

        previousLease?.Dispose();
        if (previousLease is null && !ReferenceEquals(previousBitmap, value))
            previousBitmap?.Dispose();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomFactorStr))]
    public partial double ZoomFactor { get; set; } = 1.0;
    [ObservableProperty]
    public partial double OffsetX { get; set; } = 0;
    [ObservableProperty]
    public partial double OffsetY { get; set; } = 0;

    public AppConfig ViewerPreferences => _configService.CurrentConfig;

    public double MaxZoomFactor => 3.0;
    public double MinZoomFactor => 0.1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusInfoText))]
    public partial string VideoInfoText { get; set; } = PleaseLoadTip;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddClipCommand))]
    public partial double DurationMax { get; set; } = 0.0;

    [ObservableProperty]
    public partial double CurrentTime { get; set; } = 0.0;

    [ObservableProperty]
    public partial PixelSize DecodedFrameSourceSize { get; set; }

    private bool decodedFrameRequiresSampleAspectRatioCorrection;

    partial void OnCurrentTimeChanged(double value) => TimelineViewport.SetPlayhead(value);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusInfoText))]
    public partial long DecodeCost { get; set; } = 0L;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusInfoText), nameof(DecodeModeText))]
    public partial bool IsHardwareDecoding { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDecoding))]
    public partial int DecodingOpCount { get; set; } = 0;

    public string DecodeModeText => IsHardwareDecoding
        ? LocalizationManager.Instance.String_Status_HardwareDecoding
        : LocalizationManager.Instance.String_Status_SoftwareDecoding;

    public string StatusInfoText => IsToolsOnly
        ? LocalizationManager.Instance.String_Status_ToolsOnly
        : IsVideoInitialized
        ? $"{VideoInfoText}{(IsPreviewOnly ? $" | {LocalizationManager.Instance.String_PreviewOnly}" : string.Empty)} | {DecodeCost,3}ms"
        : PleaseLoadTip;
    public bool IsDecoding => DecodingOpCount > 0;

    partial void OnDecodingOpCountChanged(int value) => NotifyVideoCapabilityChanged();

    public string ZoomFactorStr => string.Format(LocalizationManager.Instance.String_ZoomFactor, $"{ZoomFactor * 100.0:0}");

    [RelayCommand]
    private void OpenFileInExplorer()
    {
        CommonUtil.OpenFileLocation(VideoPath);
    }

    [RelayCommand]
    private void ExitApplication()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopApp)
        {
            desktopApp.MainWindow?.Close();
        }
    }
    
    [RelayCommand]
    private void CheckUpdate()
    {
        Process.Start(new ProcessStartInfo("https://github.com/nilaoda/TSCutter.GUI/releases") { UseShellExecute = true });
    }
    
    [RelayCommand]
    private async Task ShowAboutDialogAsync()
    {
        var dialogViewModel = _dialogService.CreateViewModel<AboutWindowViewModel>();
        await _dialogService.ShowDialogAsync(this, dialogViewModel);
    }
    
    [RelayCommand(CanExecute = nameof(CanLoadVideo))]
    private async Task LoadVideoClickAsync()
    {
        var settings = new OpenFileDialogSettings()
        {
            Title = LocalizationManager.Instance.String_OpenVideoFile,
            Filters =
            [
                new(LocalizationManager.Instance.String_VideoFiles, PreviewFileExtensions),
                new(LocalizationManager.Instance.String_AllFiles, "*")
            ]
        };
        var result = await _dialogService.ShowOpenFilesDialogAsync(this, settings);
        if (!result.Any()) return;

        await OpenVideoPathAsync(result[0].LocalPath);
    }

    [RelayCommand(CanExecute = nameof(CanLoadVideo))]
    private async Task OpenProjectAsync()
    {
        var settings = new OpenFileDialogSettings
        {
            Title = LocalizationManager.Instance.String_Project_Open,
            Filters = [new(LocalizationManager.Instance.String_Project_Files, ["tscut"])]
        };
        var selected = await _dialogService.ShowOpenFilesDialogAsync(this, settings);
        if (!selected.Any())
            return;

        await OpenProjectPathAsync(selected[0].LocalPath);
    }

    private async Task OpenProjectPathAsync(string projectPath)
    {
        if (_isLoadingVideo) return;
        CutterProject project;
        try
        {
            project = await CutterProjectService.LoadAsync(projectPath);
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(string.Format(
                    LocalizationManager.Instance.String_Project_OpenFailed,
                    FormatProjectError(exception)),
                LocalizationManager.Instance.String_Error, MessageBoxIcon.Error);
            return;
        }

        if (_isLoadingVideo) return;
        if (HasUnsavedProjectChanges)
        {
            if (!await ConfirmProjectReplacementAsync()) return;
        }
        else if ((IsVideoInitialized || Clips.Count > 0 || ExportQueue.Count > 0) &&
                 !await ShowConfirmAsync(LocalizationManager.Instance.String_Project_ReplaceConfirm))
            return;

        if (_isLoadingVideo) return;
        if (project.Source is not null)
        {
            VideoInstance? prepared = null;
            _isLoadingVideo = true;
            NotifyVideoCapabilityChanged();
            try
            {
                prepared = new VideoInstance(project.Source.Path,
                    _configService.CurrentConfig.PreferHardwareDecoding, project.PreviewSelection,
                    () => EnableSdrToneMapping, retainPreviewFrame: true, probeHardwareDecoding: true);
                await prepared.InitVideoAsync();
                if (project.Clips.Count > 0 && !prepared.CanClip)
                    throw new InvalidDataException("Clips require a supported TS, M2TS, MP4 or MKV source file.");
            }
            catch (Exception exception)
            {
                prepared?.Dispose();
                await ShowMessageAsync(string.Format(
                        LocalizationManager.Instance.String_Project_OpenFailed,
                        FormatProjectError(exception)),
                    LocalizationManager.Instance.String_Error, MessageBoxIcon.Error);
                return;
            }
            finally
            {
                _isLoadingVideo = false;
                NotifyVideoCapabilityChanged();
            }
            VideoPath = project.Source.Path;
            if (!await LoadVideoAsync(prepared))
                return;
        }
        else
        {
            await CompleteScrubPreviewAsync();
            Close();
            ClearVars();
        }

        await RestoreProjectAsync(project);
        SetProjectPath(projectPath);
        MarkProjectCheckpoint();
    }

    [RelayCommand(CanExecute = nameof(CanSaveProject))]
    private async Task SaveProjectAsync() => await SaveProjectCoreAsync(saveAs: false);

    [RelayCommand(CanExecute = nameof(CanSaveProject))]
    private async Task SaveProjectAsAsync() => await SaveProjectCoreAsync(saveAs: true);

    private bool CanSaveProject => !_isLoadingVideo && (IsVideoInitialized || ExportQueue.Count > 0);

    private async Task<bool> SaveProjectCoreAsync(bool saveAs)
    {
        var path = saveAs ? null : _projectPath;
        if (path is null)
        {
            var settings = new SaveFileDialogSettings
            {
                Title = LocalizationManager.Instance.String_Project_Save,
                SuggestedStartLocation = new DesktopDialogStorageFolder(
                    Path.GetDirectoryName(_projectPath ?? VideoPath) ??
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                SuggestedFileName = GetSuggestedProjectName(),
                Filters = [new(LocalizationManager.Instance.String_Project_Files, ["tscut"])],
                DefaultExtension = "tscut"
            };
            var result = await _dialogService.ShowSaveFileDialogAsync(this, settings);
            if (result?.Path is null)
                return false;
            path = result.Path.LocalPath;
        }

        try
        {
            var project = CaptureProject(includeThumbnails: true);
            var savedState = CutterProjectService.Serialize(CaptureProject(includeThumbnails: false));
            var thumbnails = new Bitmap?[project.Clips.Count * 2];
            try
            {
                for (var index = 0; index < Clips.Count; index++)
                {
                    var clip = Clips[index];
                    thumbnails[index * 2] = CopyProjectThumbnail(clip.StartThumbnail);
                    thumbnails[index * 2 + 1] = CopyProjectThumbnail(clip.EndThumbnail);
                }
                await Task.Run(() =>
                {
                    for (var index = 0; index < project.Clips.Count; index++)
                    {
                        project.Clips[index].StartThumbnailJpeg = EncodeProjectThumbnail(thumbnails[index * 2]);
                        project.Clips[index].EndThumbnailJpeg = EncodeProjectThumbnail(thumbnails[index * 2 + 1]);
                    }
                    CutterProjectService.PopulateSources(project);
                });
                await CutterProjectService.SaveAsync(path, project);
            }
            finally
            {
                foreach (var thumbnail in thumbnails) thumbnail?.Dispose();
            }
            SetProjectPath(path);
            _savedProjectState = savedState;
            return true;
        }
        catch (Exception exception)
        {
            await ShowMessageAsync(string.Format(
                    LocalizationManager.Instance.String_Project_SaveFailed,
                    FormatProjectError(exception)),
                LocalizationManager.Instance.String_Error, MessageBoxIcon.Error);
            return false;
        }
    }

    private string GetSuggestedProjectName()
    {
        var name = _projectPath is not null
            ? Path.GetFileNameWithoutExtension(_projectPath)
            : string.IsNullOrEmpty(VideoPath) ? "TSCutter" : Path.GetFileNameWithoutExtension(VideoPath);
        while (name.EndsWith(".tscut", StringComparison.OrdinalIgnoreCase))
            name = name[..^".tscut".Length];
        return string.IsNullOrWhiteSpace(name) ? "TSCutter" : name;
    }

    private CutterProject CaptureProject(bool includeThumbnails) => new()
    {
        Source = IsVideoInitialized ? new ProjectSource { Path = VideoPath } : null,
        PreviewSelection = IsVideoInitialized ? _activePreviewSelection : null,
        ExportCurrentProgramOnly = ExportCurrentProgramOnly,
        RemuxSettings = CurrentRemuxSettings?.Snapshot(),
        Clips = Clips.Select(clip => new ProjectClip
        {
            Name = clip.Name, Notes = clip.Notes, Color = clip.Color,
            StartTime = clip.StartTime,
            EndTime = clip.EndTime,
            StartPts = clip.StartPts,
            EndPts = clip.EndPts,
            StartPosition = clip.StartPosition,
            EndPosition = clip.EndPosition,
            IsSelected = clip.IsSelected,
            OutputFilePath = clip.OutputFileInfo?.FullName,
            MergedOutput = clip.MergedOutput,
            WasExported = clip.ExportStatus == ClipExportStatus.Done &&
                          clip.OutputFileInfo?.Exists == true
        }).ToList(),
        Queue = ExportQueue.Select(item => new ProjectQueueItem
        {
            Name = item.Name, Notes = item.Notes, Color = item.Color, ClipIndex = item.ClipIndex,
            GeneratedFileName = item.GeneratedFileName,
            Source = new ProjectSource { Path = item.SourceFilePath },
            ProgramId = item.ProgramId,
            RemuxSettings = item.RemuxSettings?.Snapshot(),
            OutputFilePath = item.OutputFilePath,
            StartTime = item.StartTimeSeconds,
            EndTime = item.EndTimeSeconds,
            StartPosition = item.StartPosition,
            EndPosition = item.EndPosition
        }).ToList(),
        SelectedClipIndex = SelectedClip is null ? -1 : Clips.IndexOf(SelectedClip),
        CurrentTime = includeThumbnails ? CurrentTime : 0,
        TimelineZoomLevel = includeThumbnails ? TimelineViewport.ZoomLevel : 0,
        TimelineViewStart = includeThumbnails ? TimelineViewport.ViewStart : 0
    };

    public bool HasUnsavedProjectChanges => _projectPath is not null &&
        (_savedProjectState is null ||
         _savedProjectState != CutterProjectService.Serialize(CaptureProject(includeThumbnails: false)));

    private void MarkProjectCheckpoint() =>
        _savedProjectState = CutterProjectService.Serialize(CaptureProject(includeThumbnails: false));

    public async Task<bool> ConfirmProjectReplacementAsync()
    {
        if (!HasUnsavedProjectChanges) return true;
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktopApp)
            return false;
        var result = await MessageBox.ShowDialog(desktopApp.MainWindow!,
            LocalizationManager.Instance.String_Project_UnsavedChanges,
            LocalizationManager.Instance.String_Project_Save,
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return result switch
        {
            MessageBoxResult.Yes => await SaveProjectCoreAsync(saveAs: false),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private async Task OpenVideoPathAsync(string path)
    {
        if (_isLoadingVideo) return;
        if (!await ConfirmProjectReplacementAsync() || _isLoadingVideo) return;
        VideoPath = path;
        if (await LoadVideoAsync() && ExportQueue.Count == 0)
            MarkProjectCheckpoint();
    }

    private async Task RestoreProjectAsync(CutterProject project)
    {
        ExportCurrentProgramOnly = project.ExportCurrentProgramOnly;
        var currentSourceLength = project.Source is null ? 0 : new FileInfo(project.Source.Path).Length;
        foreach (var saved in project.Clips)
        {
            var outputIsCurrent = CutterProjectService.IsSavedExportCurrent(saved, project.Source!.Length, currentSourceLength)
                && saved.OutputFilePath is not null && File.Exists(saved.OutputFilePath);
            var clip = new PickedClip
            {
                InFileInfo = new FileInfo(project.Source!.Path),
                RequiresRemux = IsRemuxExport,
                Name = saved.Name, Notes = saved.Notes, Color = saved.Color,
                StartTime = saved.StartTime,
                EndTime = saved.EndPosition < 0 ? DurationMax : saved.EndTime,
                StartPts = saved.StartPts,
                EndPts = saved.EndPts,
                StartPosition = saved.StartPosition,
                EndPosition = saved.EndPosition,
                IsSelected = saved.IsSelected,
                MergedOutput = outputIsCurrent ? saved.MergedOutput : null,
                OutputFileInfo = outputIsCurrent ? new FileInfo(saved.OutputFilePath!) : null,
                ExportStatus = outputIsCurrent ? ClipExportStatus.Done : ClipExportStatus.Pending
            };
            clip.ReplaceStartThumbnail(DecodeProjectThumbnail(saved.StartThumbnailJpeg));
            clip.ReplaceEndThumbnail(DecodeProjectThumbnail(saved.EndThumbnailJpeg));
            Clips.Add(clip);
        }

        SelectedClip = project.SelectedClipIndex >= 0 ? Clips[project.SelectedClipIndex] : null;
        _clipSelectionAnchor = SelectedClip;
        NotifyClipSelectionChanged();

        _remuxExportSettings = project.RemuxSettings?.Snapshot();
        ExportQueue.Clear();
        foreach (var saved in project.Queue)
            ExportQueue.Add(new ExportQueueItem
            {
                Name = saved.Name, Notes = saved.Notes, Color = saved.Color, ClipIndex = saved.ClipIndex,
                GeneratedFileName = saved.GeneratedFileName,
                QueueItemId = Interlocked.Increment(ref _queueIdCounter),
                SourceFilePath = saved.Source.Path,
                ProgramId = saved.ProgramId,
                RemuxSettings = saved.RemuxSettings?.Snapshot(),
                SourceFileName = Path.GetFileName(saved.Source.Path),
                OutputFilePath = saved.OutputFilePath,
                StartTimeSeconds = saved.StartTime,
                EndTimeSeconds = saved.EndPosition < 0 ? -1 : saved.EndTime,
                StartPosition = saved.StartPosition,
                EndPosition = saved.EndPosition,
                EstimatedBytes = Math.Max(0,
                    (saved.EndPosition > 0 ? saved.EndPosition :
                        new FileInfo(saved.Source.Path).Length) - saved.StartPosition)
            });
        _lastQueueOutputDirectory = ExportQueue.LastOrDefault() is { } last
            ? Path.GetDirectoryName(last.OutputFilePath) : null;
        NotifyQueueChanged();

        if (IsVideoInitialized)
        {
            // The saved time is a navigation hint. Decode to a real keyframe before
            // the user edits a boundary, so byte offsets and PTS stay in sync.
            await SeekToTimeAndDrawFrameAsync(TimeSpan.FromSeconds(
                Math.Clamp(project.CurrentTime, 0, DurationMax)));
            TimelineViewport.SetZoomLevel(project.TimelineZoomLevel, CurrentTime);
            TimelineViewport.ViewStart = project.TimelineViewStart;
        }
    }

    private void NotifyQueueChanged()
    {
        OnPropertyChanged(nameof(HasExportQueue));
        OnPropertyChanged(nameof(HasRemuxQueue));
        OnPropertyChanged(nameof(ExportQueueCount));
        AddToQueueCommand.NotifyCanExecuteChanged();
        ClearQueueCommand.NotifyCanExecuteChanged();
        BatchExportQueueCommand.NotifyCanExecuteChanged();
        SaveProjectCommand.NotifyCanExecuteChanged();
        SaveProjectAsCommand.NotifyCanExecuteChanged();
    }

    private static string FormatProjectError(Exception exception) => exception switch
    {
        ProjectSourceChangedException changed => string.Format(
            LocalizationManager.Instance.String_Project_SourceChanged, changed.SourcePath),
        FileNotFoundException missing when missing.FileName is not null => string.Format(
            LocalizationManager.Instance.String_Project_SourceMissing, missing.FileName),
        InvalidDataException or System.Text.Json.JsonException =>
            LocalizationManager.Instance.String_Project_Invalid,
        _ => exception.Message
    };

    private static Bitmap? CopyProjectThumbnail(Bitmap? bitmap) => bitmap is null ? null :
        ImageUtil.CreateThumbnail(bitmap, bitmap.PixelSize.Width, bitmap.PixelSize.Height);

    private static byte[]? EncodeProjectThumbnail(Bitmap? bitmap)
    {
        if (bitmap is null)
            return null;
        using var stream = new MemoryStream();
        ImageUtil.SaveAsJpeg(bitmap, stream, quality: 80);
        return stream.ToArray();
    }

    private static Bitmap? DecodeProjectThumbnail(byte[]? jpeg)
    {
        if (jpeg is null)
            return null;
        try
        {
            using var stream = new MemoryStream(jpeg, writable: false);
            return new Bitmap(stream);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Unable to restore project thumbnail: {exception.Message}");
            return null;
        }
    }

    [RelayCommand]
    private async Task PrevGopClickAsync() => await DrawNextFrameAsync(-1);

    [RelayCommand]
    private async Task NextGopClickAsync() => await DrawNextFrameAsync(1);

    [RelayCommand]
    private async Task Prev10GopClickAsync() => await DrawNextFrameAsync(-10);

    [RelayCommand]
    private async Task Next10GopClickAsync() => await DrawNextFrameAsync(10);

    [RelayCommand]
    private async Task Prev20GopClickAsync() => await DrawNextFrameAsync(-20);

    [RelayCommand]
    private async Task Next20GopClickAsync() => await DrawNextFrameAsync(20);

    [RelayCommand]
    private void ZoomIn() => ZoomFactor = Math.Min(MaxZoomFactor, ZoomFactor + 0.1);
    
    [RelayCommand]
    private void ZoomOut() => ZoomFactor = Math.Max(MinZoomFactor, ZoomFactor - 0.1);

    [RelayCommand]
    private void ZoomNone()
    {
        ZoomFactor = 1.0;
        OffsetX = 0;
        OffsetY = 0;
    }

    [RelayCommand]
    private async Task SettingsClickAsync()
    {
        var dialogViewModel = _dialogService.CreateViewModel<SettingsWindowViewModel>();
        await _dialogService.ShowDialogAsync(this, dialogViewModel);
    }

    public async Task SeekToTimeAsync(TimeSpan timeSpan)
    {
        if (!IsVideoInitialized || IsDecoding) return;
        await RunDecodeOperationAsync(() => _videoInstance!.SeekToTimeAsync(timeSpan));
    }

    [ObservableProperty]
    public partial IGpuFrameLease? DecodedGpuFrame { get; set; }

    public void BeginScrubPreview()
    {
        lock (_scrubPreviewSync)
        {
            if (_isLoadingVideo)
                return;
            _scrubPreviewGeneration++;
            _acceptScrubPreviews = true;
            _pendingScrubPreview = null;
            _activeScrubPreviewCancellation?.Cancel();
        }
    }

    public void RequestScrubPreview(double timeSeconds, PixelSize targetSize = default)
    {
        var startWorker = false;
        lock (_scrubPreviewSync)
        {
            if (_isLoadingVideo || !_acceptScrubPreviews || !IsVideoInitialized)
                return;

            _pendingScrubPreview = new ScrubPreviewRequest(
                _scrubPreviewGeneration,
                TimeSpan.FromSeconds(Math.Clamp(timeSeconds, 0, DurationMax)),
                NormalizeScrubPreviewTarget(targetSize));
            if (!_scrubPreviewWorkerRunning)
            {
                _scrubPreviewWorkerRunning = true;
                startWorker = true;
            }
        }

        if (!startWorker)
            return;

        var worker = ProcessScrubPreviewQueueAsync();
        lock (_scrubPreviewSync)
            _scrubPreviewWorker = worker;
    }

    public void CancelScrubPreview() => StopAcceptingScrubPreviews();

    public async Task CompleteScrubPreviewAsync()
    {
        Task worker;
        lock (_scrubPreviewSync)
        {
            StopAcceptingScrubPreviewsCore();
            worker = _scrubPreviewWorker;
        }
        await worker;
    }

    public async Task SeekToTimeAndDrawFrameAsync(TimeSpan timeSpan)
    {
        if (!IsVideoInitialized || (IsDecoding && !toneMappingRefreshRunning))
            return;
        var instance = _videoInstance!;

        try
        {
            // 拖动结束的最终定位须等待映射刷新，不能因短暂忙碌而丢掉请求。
            await toneMappingRefreshTask;
            if (!ReferenceEquals(instance, _videoInstance) || IsDecoding) return;
            await RunDecodeOperationAsync(async () =>
            {
                var stopwatch = Stopwatch.StartNew();
                var decodeResult = await instance.DecodeAtTimeAsync(timeSpan);
                if (!ReferenceEquals(instance, _videoInstance))
                {
                    DisposePreviewResult(decodeResult);
                    return;
                }
                stopwatch.Stop();
                DecodeCost = stopwatch.ElapsedMilliseconds;
                ApplyDecodeResult(decodeResult);
            });
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception);
            if (!ReferenceEquals(instance, _videoInstance)) return;
            await ShowMessageAsync(
                exception is MediaReadTimeoutException
                    ? LocalizationManager.Instance.String_MediaReadTimeout
                    : exception.Message,
                LocalizationManager.Instance.String_FailedToDecode,
                MessageBoxIcon.Error);
        }
        finally
        {
            UpdateDecodeMode();
        }
    }

    private async Task ProcessScrubPreviewQueueAsync()
    {
        while (true)
        {
            ScrubPreviewRequest request;
            CancellationTokenSource cancellation;
            VideoInstance? videoInstance;
            lock (_scrubPreviewSync)
            {
                if (_pendingScrubPreview is not { } pending)
                {
                    _scrubPreviewWorkerRunning = false;
                    return;
                }

                request = pending;
                _pendingScrubPreview = null;
                cancellation = new CancellationTokenSource();
                _activeScrubPreviewCancellation = cancellation;
                videoInstance = _videoInstance;
            }

            if (videoInstance is null)
            {
                lock (_scrubPreviewSync)
                {
                    if (ReferenceEquals(_activeScrubPreviewCancellation, cancellation))
                        _activeScrubPreviewCancellation = null;
                }
                cancellation.Dispose();
                continue;
            }

            DecodingOpCount++;
            try
            {
                var stopwatch = Stopwatch.StartNew();
                var decodeResult = await videoInstance.DecodeAtTimeAsync(
                    request.Time,
                    request.TargetSize.Width,
                    request.TargetSize.Height,
                    cancellation.Token);
                stopwatch.Stop();

                bool shouldPublish;
                lock (_scrubPreviewSync)
                {
                    shouldPublish = _acceptScrubPreviews &&
                                    request.Generation == _scrubPreviewGeneration;
                }

                if (shouldPublish)
                {
                    DecodeCost = stopwatch.ElapsedMilliseconds;
                    ApplyDecodeResult(decodeResult);
                    UpdateDecodeMode();
                }
                else
                {
                    decodeResult.BitmapLease?.Dispose();
                    if (decodeResult.BitmapLease is null)
                        decodeResult.Bitmap?.Dispose();
                    decodeResult.GpuFrame?.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                // 新拖动会话或最终 seek 已取代当前预览。
            }
            catch (Exception exception)
            {
                // 拖动预览失败时保留上一帧，最终 seek 仍会报告真正的解码错误。
                Console.WriteLine($"Scrub preview failed: {exception}");
            }
            finally
            {
                DecodingOpCount = Math.Max(0, DecodingOpCount - 1);
                lock (_scrubPreviewSync)
                {
                    if (ReferenceEquals(_activeScrubPreviewCancellation, cancellation))
                        _activeScrubPreviewCancellation = null;
                }
                cancellation.Dispose();
            }
        }
    }

    private void StopAcceptingScrubPreviews()
    {
        lock (_scrubPreviewSync)
            StopAcceptingScrubPreviewsCore();
    }

    private void StopAcceptingScrubPreviewsCore()
    {
        _scrubPreviewGeneration++;
        _acceptScrubPreviews = false;
        _pendingScrubPreview = null;
        _activeScrubPreviewCancellation?.Cancel();
    }

    private void ApplyDecodeResult(DecodeResult decodeResult)
    {
        SetDisplayedVideoDynamicRange(decodeResult.VideoDynamicRange);
        _displayedFramePosition = decodeResult.FramePosition;
        _displayedFramePts = decodeResult.FramePts;
        _displayedFramePts90k = decodeResult.FramePts90k;
        _displayedFrameTime = decodeResult.FrameTimestamp.TotalSeconds;
        _displayedFrameEndSignature = decodeResult.FrameEndSignature;
        AddClipCommand.NotifyCanExecuteChanged();
        MarkClipStartCommand.NotifyCanExecuteChanged();
        MarkClipEndCommand.NotifyCanExecuteChanged();
        MarkClipStartShortcutCommand.NotifyCanExecuteChanged();
        MarkClipEndShortcutCommand.NotifyCanExecuteChanged();
        DecodedFrameSourceSize = decodeResult.SourcePixelSize;
        decodedFrameRequiresSampleAspectRatioCorrection =
            decodeResult.RequiresSampleAspectRatioCorrection;
        DecodedGpuFrame?.Dispose();
        DecodedGpuFrame = decodeResult.GpuFrame;
        SetDecodedBitmap(decodeResult.Bitmap, decodeResult.BitmapLease);
        CurrentTime = decodeResult.FrameTimestamp.TotalSeconds;
        _ = RefreshSubtitleOverlayAsync();
    }

    private static PixelSize NormalizeScrubPreviewTarget(PixelSize targetSize) =>
        targetSize.Width > 0 && targetSize.Height > 0
            ? targetSize
            : new PixelSize(DefaultScrubPreviewWidth, DefaultScrubPreviewHeight);
    
    public async Task SeekFileAsync(long pts)
    {
        await SeekFileAsync(pts, false);
    }

    private async Task SeekFileAsync(long pts, bool bypassBusyCheck)
    {
        if (!IsVideoInitialized || (!bypassBusyCheck && IsDecoding)) return;
        if (bypassBusyCheck)
        {
            await SeekFileCoreAsync(pts);
            return;
        }
        await RunDecodeOperationAsync(() => SeekFileCoreAsync(pts));
    }
    
    public async Task DrawNextFrameAsync(int count = 1)
    {
        if (!IsVideoInitialized || IsDecoding) return;
        await RunDecodeOperationAsync(() => DrawNextFrameCoreAsync(count));
    }

    private async Task DrawNextFrameCoreAsync(int count = 1, bool propagateFailure = false)
    {
        try
        {
            DecodeCost = 0;
            var stopwatch = Stopwatch.StartNew();
            var instance = _videoInstance!;
            var decodeResult = await instance.DecodeNextFrameAsync(count);
            if (!ReferenceEquals(instance, _videoInstance))
            {
                DisposePreviewResult(decodeResult);
                return;
            }
            DecodeCost = stopwatch.ElapsedMilliseconds;
            Console.WriteLine($"PositionInFile: {instance.PositionInFile}");
            stopwatch.Stop();
            ApplyDecodeResult(decodeResult);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            if (propagateFailure) throw;
            await ShowMessageAsync(
                e is MediaReadTimeoutException ? LocalizationManager.Instance.String_MediaReadTimeout : e.Message,
                LocalizationManager.Instance.String_FailedToDecode, MessageBoxIcon.Error);
        }
        finally
        {
            // 即使软件回退后的重试仍失败，状态栏也必须反映解码器的实际模式。
            UpdateDecodeMode();
        }
    }

    private void CancelClipEndScan()
    {
        _clipEndCancellation?.Cancel();
        _clipEndCancellation = null;
        _isResolvingClipEnd = false;
    }

    private void ClearVars()
    {
        SetDisplayedVideoDynamicRange(VideoDynamicRange.Standard);
        SetPreviewTrackChoices([], null);
        SetSubtitleTrackChoices([]);
        _currentFileSupportsTsTools = false;
        _previewFailure = null;
        CancelClipEndScan();
        keyFrameOverviewWindow?.CloseWindow();
        keyFrameOverviewWindow = null;
        StopAcceptingScrubPreviews();
        VideoInfoText = PleaseLoadTip;
        DecodedGpuFrame?.Dispose();
        DecodedGpuFrame = null;
        SetDecodedBitmap(null);
        DecodedFrameSourceSize = default;
        decodedFrameRequiresSampleAspectRatioCorrection = false;
        DisposeClipThumbnails();
        Clips.Clear();
        SelectedClip = null;
        _clipSelectionAnchor = null;
        NotifyClipSelectionChanged();
        DurationMax = 0.0;
        CurrentTime = 0.0;
        _displayedFramePosition = -1;
        _displayedFramePts = long.MinValue;
        _displayedFramePts90k = long.MinValue;
        _displayedFrameTime = 0;
        _displayedFrameEndSignature = default;
        TimelineViewport.Reset(0, 0);
        DecodeCost = 0L;
        IsHardwareDecoding = false;
        ClearHistory();
        NotifyVideoCapabilityChanged();
    }

    private async Task<bool> LoadVideoAsync(VideoInstance? prepared = null)
    {
        _isLoadingVideo = true;
        // 换源从等待预览任务时就禁用标记，避免用旧画面的位置扫描新路径。
        DecodingOpCount++;
        try
        {
            CancelClipEndScan();
            await CompleteScrubPreviewAsync();
            await toneMappingRefreshTask;
            await (decodeIdleCompletion?.Task ?? Task.CompletedTask);
            ClearVars();
            ResetPreviewDefaults();
            SetProjectPath(null);
            _videoInstance?.Close();
            await RunDecodeOperationAsync(async () =>
            {
                _videoInstance = prepared ?? new VideoInstance(
                    VideoPath,
                    preferHardwareDecodingForPreview,
                    enableSdrToneMapping: () => EnableSdrToneMapping, retainPreviewFrame: true, probeHardwareDecoding: true);
                if (prepared is null)
                    await _videoInstance.InitVideoAsync();
                _remuxExportSettings = null;
                SetPreviewTrackChoices(_videoInstance.PreviewPrograms, _videoInstance.StreamSelection);
                SetSubtitleTrackChoices(_videoInstance.PreviewSubtitleTracks);
                UpdateDecodeMode();
                VideoInfoText = _videoInstance.GetVideoInfoText();
                DurationMax = _videoInstance.GetVideoDurationInSeconds();
                TimelineViewport.Reset(
                    DurationMax,
                    _videoInstance.EstimatedKeyFrameIntervalSeconds);
        
                CloseVideoClickCommand.NotifyCanExecuteChanged();
                SaveFrameClickCommand.NotifyCanExecuteChanged();
                ShowMediaInfoClickCommand.NotifyCanExecuteChanged();
                KeyFrameOverviewClickCommand.NotifyCanExecuteChanged();
                ThumbnailSheetClickCommand.NotifyCanExecuteChanged();
                FrameSearchClickCommand.NotifyCanExecuteChanged();
                NotifyVideoCapabilityChanged();

                // decode
                await DrawNextFrameCoreAsync(1, propagateFailure: true);
                // 发送消息通知 View 执行 FitCommand
                WeakReferenceMessenger.Default.Send(new FitMessage());
            });
            return true;
        }
        catch (Exception e)
        {
            var failedPrograms = _videoInstance?.PreviewPrograms ?? [];
            var failedSelection = _videoInstance?.StreamSelection;
            ClearVars();
            _videoInstance?.Dispose();
            _videoInstance = null;
            Console.WriteLine($"Failed to load video: {e}");
            // 项目导入仍要求可靠的预览与剪辑状态；普通文件打开可降级为工具模式。
            if (prepared is null && await TryRetainFileForToolsAsync(e))
            {
                SetPreviewTrackChoices(failedPrograms, failedSelection);
                return true;
            }

            VideoPath = string.Empty;
            await ShowMessageAsync(FormatPreviewFailure(e),
                LocalizationManager.Instance.String_FailedToLoadVideo,
                MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            _isLoadingVideo = false;
            DecodingOpCount = Math.Max(0, DecodingOpCount - 1);
            NotifyVideoCapabilityChanged();
        }
    }

    private async Task<bool> TryRetainFileForToolsAsync(Exception failure)
    {
        try
        {
            if (failure is NotSupportedException)
                return false;
            // 无视频轨道的文件已完成媒体探测，仅保留媒体信息入口。
            // 其他预览失败仍做有界探测，确认 TS 工具支持的包结构。
            var probe = failure is NoVideoStreamException ? default :
                await Task.Run(() => TsScramblingProbe.Probe(VideoPath));
            if (!probe.IsSupportedTransportStream &&
                failure is not (VideoDecoderUnavailableException or NoVideoStreamException))
                return false;

            _currentFileSupportsTsTools = probe.IsSupportedTransportStream;
            _previewFailure = failure;
            NotifyVideoCapabilityChanged();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.WriteLine($"Cannot retain file for tools: {exception.Message}");
            return false;
        }
    }

    private static string FormatPreviewFailure(Exception exception) => exception switch
    {
        NotSupportedException => exception.Message,
        MediaReadTimeoutException => LocalizationManager.Instance.String_MediaReadTimeout,
        ScrambledTsException => LocalizationManager.Instance.String_ScrambledTs,
        NoVideoStreamException => LocalizationManager.Instance.String_NoVideoStream,
        VideoDecoderUnavailableException decoder => string.Format(
            LocalizationManager.Instance.String_VideoDecoderUnavailable, decoder.CodecName),
        _ => FFmpegNativeBootstrapper.BuildLoadFailureMessage(exception)
    };

    private async Task RunDecodeOperationAsync(Func<Task> action)
    {
        if (activeDecodeOperations++ == 0)
            decodeIdleCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DecodingOpCount++;
        try
        {
            await action();
        }
        finally
        {
            if (--activeDecodeOperations == 0)
            {
                var completion = decodeIdleCompletion;
                decodeIdleCompletion = null;
                completion?.TrySetResult();
            }
            DecodingOpCount = Math.Max(0, DecodingOpCount - 1);
        }
    }
    
    private async Task SeekFileCoreAsync(long pts)
    {
        await _videoInstance!.SeekFileAsync(pts);
    }

    private async Task ShowMessageAsync(string message, string title = "TSCutter", MessageBoxIcon icon = MessageBoxIcon.None)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopApp)
        {
            await MessageBox.ShowDialog(desktopApp.MainWindow!, message, title, MessageBoxButtons.Ok, icon);
        }
    }

    private async Task<bool> ShowConfirmAsync(string message, string title = "TSCutter")
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopApp)
        {
            var result = await MessageBox.ShowDialog(desktopApp.MainWindow!, message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            return result == MessageBoxResult.Yes;
        }
        return false;
    }
    
    [RelayCommand]
    private void DragOver(DragEventArgs? e)
    {
        var filePath = CheckDropFilePath(e, requireExistingFile: false);
        e!.DragEffects = filePath is not null ? DragDropEffects.Copy : DragDropEffects.None;
    }

    [RelayCommand]
    private async Task Drop(DragEventArgs? e)
    {
        var filePath = CheckDropFilePath(e, requireExistingFile: true);
        if (filePath is not null)
        {
            if (IsProjectFile(filePath))
            {
                await OpenProjectPathAsync(filePath);
                return;
            }
            await OpenVideoPathAsync(filePath);
        }
    }

    private string? CheckDropFilePath(DragEventArgs? e, bool requireExistingFile)
    {
        if (e is null || _isLoadingVideo) return null;
        if (!e.Data.Contains(DataFormats.Files)) return null;

        var fileNames = e.Data.GetFiles()?.ToArray();
        if (fileNames is not { Length: > 0 }) return null;
        var filePath = fileNames[0].Path.LocalPath;
        if ((!requireExistingFile || File.Exists(filePath))
            && (string.IsNullOrWhiteSpace(VideoPath) || !PathsEqual(VideoPath, filePath)))
        {
            return filePath;
        }
        return null;
    }

    private static bool IsProjectFile(string? path) =>
        string.Equals(Path.GetExtension(path), ".tscut", StringComparison.OrdinalIgnoreCase);

    public void Close()
    {
        _previewSelectionCancellation?.Cancel();
        SetPreviewTrackChoices([], null);
        SetSubtitleTrackChoices([]);
        CancelClipEndScan();
        keyFrameOverviewWindow?.CloseWindow();
        keyFrameOverviewWindow = null;
        toneMappingRefreshPending = false;
        SetDisplayedVideoDynamicRange(VideoDynamicRange.Standard);
        Task scrubWorker;
        VideoInstance? videoInstance;
        lock (_scrubPreviewSync)
        {
            StopAcceptingScrubPreviewsCore();
            scrubWorker = _scrubPreviewWorker;
            videoInstance = _videoInstance;
            _videoInstance = null;
        }

        // 先释放界面持有的租约，原生解码器等待所有在途操作结束后再销毁。
        SetDecodedBitmap(null);
        DecodedGpuFrame?.Dispose();
        DecodedGpuFrame = null;
        DisposeClipThumbnails();
        if (videoInstance is not null)
            previewDisposalTask = DisposeVideoAfterScrubStopsAsync(Task.WhenAll(scrubWorker, toneMappingRefreshTask,
                decodeIdleCompletion?.Task ?? Task.CompletedTask), videoInstance);
        VideoPath = string.Empty;
    }

    private static async Task DisposeVideoAfterScrubStopsAsync(Task scrubWorker, VideoInstance videoInstance)
    {
        try
        {
            await scrubWorker.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // 操作自身负责错误提示；关闭时只记录失败，仍须完成原生资源释放。
            Console.WriteLine($"Preview operation ended while closing: {exception}");
        }
        finally
        {
            videoInstance.Dispose();
        }
    }

    private void UpdateDecodeMode()
    {
        if (_videoInstance is null)
            return;

        IsHardwareDecoding = _videoInstance.IsHardwareDecoding;
        OnPropertyChanged(nameof(CanToggleHardwareDecoding));
        ToggleHardwareDecodingCommand.NotifyCanExecuteChanged();
    }

    // 文件工具独立读取源文件，拖动预览时仍可使用；换源加载期间才需要禁用。
    public bool HasCurrentFile => !string.IsNullOrEmpty(VideoPath);
    public bool CanUseCurrentFile => HasCurrentFile && !_isLoadingVideo;
    public bool CanUseCurrentFileTools => CanUseCurrentFile && (_videoInstance is { CanBinaryClip: true } || _currentFileSupportsTsTools);
    public bool IsToolsOnly => HasCurrentFile && _previewFailure is not null;
    public string ToolsOnlyMessage => _previewFailure is null ? string.Empty :
        $"{LocalizationManager.Instance.String_ToolsOnly_Title}\n\n{FormatPreviewFailure(_previewFailure)}\n\n" +
        (_currentFileSupportsTsTools
            ? LocalizationManager.Instance.String_ToolsOnly_TsHint
            : LocalizationManager.Instance.String_ToolsOnly_MediaHint);

    public bool IsPreviewOnly => IsVideoInitialized && !CanEditVideo;

    private bool CanEditVideo => !_isLoadingVideo && _videoInstance is { Inited: true, CanClip: true };
    private bool CanEditSelectedClip => CanEditVideo && HasSelectedClip;
    // 标记取已显示帧的快照，后台 seek 不影响可用状态；加载、边界扫描和无效帧仍需禁用。
    private bool CanMarkVideo => CanEditVideo && !_isResolvingClipEnd
        && (PositionInFile >= 0 || IsRemuxExport) && CurrentPts != long.MinValue;
    private bool CanMarkSelectedClip => CanMarkVideo && HasSelectedClip;

    private void NotifyVideoCapabilityChanged()
    {
        NotifyPreviewTrackChoicesChanged();
        OnPropertyChanged(nameof(IsVideoInitialized));
        OnPropertyChanged(nameof(CanTogglePreviewOptions));
        OnPropertyChanged(nameof(CanToggleHardwareDecoding));
        OnPropertyChanged(nameof(CanToggleSdrToneMapping));
        ToggleHardwareDecodingCommand.NotifyCanExecuteChanged();
        ToggleSdrToneMappingCommand.NotifyCanExecuteChanged();
        StartToneMappingRefresh();
        SaveFrameClickCommand.NotifyCanExecuteChanged();
        KeyFrameOverviewClickCommand.NotifyCanExecuteChanged();
        ThumbnailSheetClickCommand.NotifyCanExecuteChanged();
        FrameSearchClickCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanUseCurrentFile));
        OnPropertyChanged(nameof(CanUseCurrentFileTools));
        OnPropertyChanged(nameof(IsToolsOnly));
        OnPropertyChanged(nameof(ToolsOnlyMessage));
        CloseVideoClickCommand.NotifyCanExecuteChanged();
        ShowMediaInfoClickCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsPreviewOnly));
        EditClipDetailsCommand.NotifyCanExecuteChanged();
        RemuxExportOptionsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsRemuxExport));
        OnPropertyChanged(nameof(StatusInfoText));
        AddClipCommand.NotifyCanExecuteChanged();
        RemoveClipCommand.NotifyCanExecuteChanged();
        MarkClipStartCommand.NotifyCanExecuteChanged();
        MarkClipEndCommand.NotifyCanExecuteChanged();
        MarkClipStartShortcutCommand.NotifyCanExecuteChanged();
        MarkClipEndShortcutCommand.NotifyCanExecuteChanged();
        SaveVideoClickCommand.NotifyCanExecuteChanged();
        AddToQueueCommand.NotifyCanExecuteChanged();
        MergeSelectedClipsCommand.NotifyCanExecuteChanged();
        ExportAllCommand.NotifyCanExecuteChanged();
        SaveProjectCommand.NotifyCanExecuteChanged();
        SaveProjectAsCommand.NotifyCanExecuteChanged();
    }

    private void DisposeClipThumbnails()
    {
        foreach (var clip in Clips)
            clip.Dispose();
    }

    private bool HasSelectedClip => SelectedClip is not null;
    private bool CanExportAll => CanEditVideo && Clips.Count > 0;
    public bool IsVideoInitialized => _videoInstance is { Inited: true };
    
    private async Task SaveVideoAsync()
    {
        var defaultName = CreateClipOutputName(SelectedClip!);
        var settings = new SaveFileDialogSettings
        {
            Title = LocalizationManager.Instance.String_SaveYourClip,
            SuggestedStartLocation = new DesktopDialogStorageFolder(Path.GetDirectoryName(SelectedClip!.InFileInfo.FullName)!),
            SuggestedFileName = defaultName,
            Filters = new List<FileFilter>()
            {
                new(ExportFileFilterName, new[] { ClipExtension }),
                new(LocalizationManager.Instance.String_AllFiles, "*")
            },
            DefaultExtension = ClipExtension
        };
        var result = await _dialogService.ShowSaveFileDialogAsync(this, settings);
        if (result is null) return;
        
        var dialogViewModel = _dialogService.CreateViewModel<OutputWindowViewModel>();
        dialogViewModel.SourceFilePath = SelectedClip!.InFileInfo.FullName;
        dialogViewModel.StartPosition = SelectedClip!.StartPosition;
        dialogViewModel.EndPosition = SelectedClip!.EndPosition;
        dialogViewModel.OutputPath = result!.Path!.LocalPath;
        dialogViewModel.ProgramId = SelectedOutputProgramId;
        dialogViewModel.RemuxSettings = CurrentRemuxSettings?.Snapshot();
        dialogViewModel.StartSeconds = SelectedClip.StartTime;
        dialogViewModel.EndSeconds = SelectedClip.EndPosition < 0 ? null : SelectedClip.EndTime;
        await _dialogService.ShowDialogAsync(this, dialogViewModel).ConfigureAwait(true);
        if (dialogViewModel.ProgramId is not null || dialogViewModel.RemuxSettings is not null)
            ForgetMissingClipOutput(SelectedClip!, dialogViewModel.OutputPath);
        if (dialogViewModel.Exception is not null)
        {
            await ShowMessageAsync(FormatMergeError(dialogViewModel.Exception), LocalizationManager.Instance.String_Error, MessageBoxIcon.Error);
            Console.WriteLine(dialogViewModel.Exception);
            return;
        }
        if (dialogViewModel.DialogResult == true && File.Exists(dialogViewModel.OutputPath))
        {
            SelectedClip!.MergedOutput = null;
            SelectedClip.OutputFileInfo = new FileInfo(dialogViewModel.OutputPath);
            SelectedClip.ExportStatus = ClipExportStatus.Done;
        }
    }

    [RelayCommand(CanExecute = nameof(CanExportAll))]
    private async Task ExportAllAsync()
    {
        var sourceDir = Path.GetDirectoryName(Clips[0].InFileInfo.FullName)!;
        var confirmMsg = string.Format(LocalizationManager.Instance.String_ExportAllConfirm, Clips.Count, sourceDir);
        if (!await ShowConfirmAsync(confirmMsg)) return;

        var tasks = new List<BatchTask>();
        var reservedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(VideoPath) };
        foreach (var clip in Clips)
        {
            var fileName = GenerateClipFileName(clip);
            var destPath = ClipOutputNaming.UniquePath(sourceDir, fileName, reservedPaths);
            tasks.Add(new BatchTask(clip.InFileInfo.FullName, destPath, clip.StartPosition, clip.EndPosition, SelectedOutputProgramId,
                CurrentRemuxSettings?.Snapshot(), clip.StartTime, clip.EndPosition < 0 ? null : clip.EndTime));
            clip.ExportStatus = ClipExportStatus.Pending;
        }

        var dialogViewModel = _dialogService.CreateViewModel<OutputWindowViewModel>();
        dialogViewModel.SetBatchTasks(tasks);
        await _dialogService.ShowDialogAsync(this, dialogViewModel).ConfigureAwait(true);

        var completedSet = dialogViewModel.CompletedTaskIndices.ToHashSet();
        var failedSet = dialogViewModel.FailedTaskIndices.ToHashSet();
        for (int i = 0; i < Clips.Count; i++)
        {
            if (tasks[i].ProgramId is not null || tasks[i].RemuxSettings is not null)
                ForgetMissingClipOutput(Clips[i], tasks[i].DestinationPath);
            if (completedSet.Contains(i))
            {
                Clips[i].ExportStatus = ClipExportStatus.Done;
                Clips[i].MergedOutput = null;
                Clips[i].OutputFileInfo = new FileInfo(tasks[i].DestinationPath);
            }
            else if (failedSet.Contains(i))
            {
                Clips[i].ExportStatus = ClipExportStatus.Failed;
            }
            else if (Clips[i].ExportStatus == ClipExportStatus.Exporting)
            {
                Clips[i].ExportStatus = ClipExportStatus.Cancelled;
            }
        }
        if (dialogViewModel.Exception is { } exception)
            await ShowMessageAsync(FormatMergeError(exception), LocalizationManager.Instance.String_Error, MessageBoxIcon.Error);
    }

    private static void ForgetMissingClipOutput(PickedClip clip, string outputPath)
    {
        if (clip.OutputFileInfo is { } previous && PathsEqual(previous.FullName, outputPath) &&
            !File.Exists(outputPath))
        {
            clip.OutputFileInfo = null;
            clip.ExportStatus = ClipExportStatus.Pending;
        }
    }

    private string ClipExtension => CurrentRemuxSettings?.Container ?? _videoInstance?.ClipExtension ?? "ts";

    private async Task<string> GetClipExtensionAsync(string sourcePath)
    {
        if (PathsEqual(sourcePath, VideoPath))
            return ClipExtension;
        try
        {
            var queued = ExportQueue.LastOrDefault(item => PathsEqual(item.SourceFilePath, sourcePath));
            if (queued?.RemuxSettings is { } media) return media.Container;
            return await Task.Run(() => TsPacketLayout.Detect(sourcePath).Extension);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 队列源文件可能暂时不可读；命名提示不应阻止当前文件加入队列。
            return Path.GetExtension(sourcePath).Equals(".m2ts", StringComparison.OrdinalIgnoreCase) ||
                   Path.GetExtension(sourcePath).Equals(".mts", StringComparison.OrdinalIgnoreCase) ? "m2ts" : "ts";
        }
    }

    private string GenerateClipFileName(PickedClip clip)
    {
        return CreateClipOutputName(clip);
    }

    // ---- 导出队列 ----

    [RelayCommand(CanExecute = nameof(CanEditSelectedClip))]
    private async Task AddToQueueAsync()
    {
        var clip = SelectedClip!;
        var extension = ClipExtension;
        var programId = SelectedOutputProgramId;
        var sourceDirectory = Path.GetDirectoryName(clip.InFileInfo.FullName)!;
        var generatedName = await GenerateQueueFileNameAsync(
            clip.InFileInfo.FullName, clip.StartTime, clip.EndTime,
            clip.Name, Math.Max(1, Clips.IndexOf(clip) + 1));
        var previousItem = ExportQueue.LastOrDefault();
        // 只有上一项确实偏离系统默认名称时，才把它当作用户的手工命名参照；如果用户
        // 始终沿用默认名称，则每个源文件继续独立生成文件名，仅记住输出目录。
        var suggestedName = OutputNameTemplate == ClipOutputNaming.DefaultTemplate && previousItem is not null &&
            previousItem.EndTimeSeconds >= 0 && !FileNamesEqual(
            previousItem.OutputFileName,
            string.IsNullOrEmpty(previousItem.GeneratedFileName)
                ? await GenerateQueueFileNameAsync(previousItem.SourceFilePath, previousItem.StartTimeSeconds,
                    previousItem.EndTimeSeconds,
                    previousItem.Name, previousItem.ClipIndex)
                : previousItem.GeneratedFileName)
            ? previousItem.OutputFileName
            : generatedName;
        if (!ReferenceEquals(SelectedClip, clip) || !CanEditSelectedClip) return;
        var previousExtension = Path.GetExtension(suggestedName);
        if (previousExtension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
            previousExtension.Equals(".m2ts", StringComparison.OrdinalIgnoreCase) ||
            previousExtension.Equals(".mts", StringComparison.OrdinalIgnoreCase) ||
            previousExtension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
            previousExtension.Equals(".mkv", StringComparison.OrdinalIgnoreCase))
            suggestedName = Path.ChangeExtension(suggestedName, extension);
        var suggestedDirectory = !string.IsNullOrWhiteSpace(_lastQueueOutputDirectory) &&
                                 Directory.Exists(_lastQueueOutputDirectory)
            ? _lastQueueOutputDirectory
            : sourceDirectory;
        var settings = new SaveFileDialogSettings
        {
            Title = LocalizationManager.Instance.String_AddToQueue,
            SuggestedStartLocation = new DesktopDialogStorageFolder(suggestedDirectory),
            SuggestedFileName = suggestedName,
            Filters = new List<FileFilter>()
            {
                new(ExportFileFilterName, new[] { extension }),
                new(LocalizationManager.Instance.String_AllFiles, "*")
            },
            DefaultExtension = extension
        };
        var result = await _dialogService.ShowSaveFileDialogAsync(this, settings);
        if (result?.Path is null || !ReferenceEquals(SelectedClip, clip) || !CanEditSelectedClip) return;

        var outputPath = result.Path.LocalPath;
        if (PathsEqual(outputPath, clip.InFileInfo.FullName) || ExportQueue.Any(item =>
                PathsEqual(item.SourceFilePath, outputPath) || PathsEqual(item.OutputFilePath, clip.InFileInfo.FullName)))
        {
            await ShowMessageAsync(LocalizationManager.Instance.String_MergeClips_OutputMatchesSource,
                LocalizationManager.Instance.String_Error, MessageBoxIcon.Error);
            return;
        }
        if (ExportQueue.Any(item => PathsEqual(item.OutputFilePath, outputPath)))
        {
            await ShowMessageAsync(
                string.Format(
                    LocalizationManager.Instance.String_ExportQueue_DuplicateOutputPath,
                    outputPath),
                LocalizationManager.Instance.String_Error,
                MessageBoxIcon.Error);
            return;
        }

        var item = new ExportQueueItem
        {
            Name = clip.Name, Notes = clip.Notes, Color = clip.Color,
            ClipIndex = Math.Max(1, Clips.IndexOf(clip) + 1),
            GeneratedFileName = generatedName,
            QueueItemId = Interlocked.Increment(ref _queueIdCounter),
            SourceFilePath = clip.InFileInfo.FullName,
            ProgramId = programId,
            RemuxSettings = CurrentRemuxSettings?.Snapshot(),
            OutputFilePath = outputPath,
            StartPosition = clip.StartPosition,
            EndPosition = clip.EndPosition,
            SourceFileName = Path.GetFileName(clip.InFileInfo.FullName),
            StartTimeSeconds = clip.StartTime,
            EndTimeSeconds = clip.EndTime,
            EstimatedBytes = Math.Max(0,
                (clip.EndPosition > 0 ? clip.EndPosition : clip.InFileInfo.Length)
                - clip.StartPosition),
        };
        ExportQueue.Add(item);
        _lastQueueOutputDirectory = Path.GetDirectoryName(outputPath);
        OnPropertyChanged(nameof(HasExportQueue));
        OnPropertyChanged(nameof(HasRemuxQueue));
        OnPropertyChanged(nameof(ExportQueueCount));
        AddToQueueCommand.NotifyCanExecuteChanged();
        ClearQueueCommand.NotifyCanExecuteChanged();
        BatchExportQueueCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void RemoveFromQueue(object? item)
    {
        if (item is ExportQueueItem queueItem)
        {
            ExportQueue.Remove(queueItem);
            OnPropertyChanged(nameof(HasExportQueue));
            OnPropertyChanged(nameof(ExportQueueCount));
            ClearQueueCommand.NotifyCanExecuteChanged();
            BatchExportQueueCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(HasExportQueueItems))]
    private void ClearQueue()
    {
        ExportQueue.Clear();
        OnPropertyChanged(nameof(HasExportQueue));
        OnPropertyChanged(nameof(HasRemuxQueue));
        OnPropertyChanged(nameof(ExportQueueCount));
        ClearQueueCommand.NotifyCanExecuteChanged();
        BatchExportQueueCommand.NotifyCanExecuteChanged();
    }

    private bool HasExportQueueItems => ExportQueue.Count > 0;

    private async Task<string> GenerateQueueFileNameAsync(
        string sourceFilePath,
        double startTimeSeconds,
        double endTimeSeconds,
        string name,
        int index) => ClipOutputNaming.Generate(OutputNameTemplate, sourceFilePath, name, index,
            startTimeSeconds, endTimeSeconds, await GetClipExtensionAsync(sourceFilePath));

    private static bool FileNamesEqual(string left, string right) => string.Equals(
        left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool PathsEqual(string left, string right) => MediaFileUtil.PathsEqual(left, right);

    [RelayCommand(CanExecute = nameof(HasExportQueueItems))]
    private async Task BatchExportQueueAsync()
    {
        if (ExportQueue.Count == 0) return;

        var confirmMsg = string.Format(
            LocalizationManager.Instance.String_BatchExportConfirm,
            ExportQueue.Count);
        if (!await ShowConfirmAsync(confirmMsg)) return;

        var tasks = ExportQueue.Select(item =>
            new BatchTask(item.SourceFilePath, item.OutputFilePath, item.StartPosition, item.EndPosition, item.ProgramId,
                item.RemuxSettings?.Snapshot(), item.StartTimeSeconds, item.EndPosition < 0 ? null : item.EndTimeSeconds)
        ).ToList();

        var dialogViewModel = _dialogService.CreateViewModel<OutputWindowViewModel>();
        dialogViewModel.SetBatchTasks(tasks);
        await _dialogService.ShowDialogAsync(this, dialogViewModel).ConfigureAwait(true);

        // 更新导出队列中的状态为已完成
        var completedSet = dialogViewModel.CompletedTaskIndices.ToHashSet();
        var failedSet = dialogViewModel.FailedTaskIndices.ToHashSet();
        for (int i = 0; i < ExportQueue.Count; i++)
        {
            var item = ExportQueue[i];
            if (completedSet.Contains(i))
            {
                item.Status = ClipExportStatus.Done;
            }
            else if (failedSet.Contains(i))
            {
                item.Status = ClipExportStatus.Failed;
            }
        }
        // 从导出队列中移除已完成项
        for (int i = ExportQueue.Count - 1; i >= 0; i--)
        {
            if (ExportQueue[i].Status == ClipExportStatus.Done)
                ExportQueue.RemoveAt(i);
        }
        OnPropertyChanged(nameof(HasExportQueue));
        OnPropertyChanged(nameof(HasRemuxQueue));
        OnPropertyChanged(nameof(ExportQueueCount));
        ClearQueueCommand.NotifyCanExecuteChanged();
        BatchExportQueueCommand.NotifyCanExecuteChanged();
        if (dialogViewModel.Exception is { } exception)
            await ShowMessageAsync(FormatMergeError(exception), LocalizationManager.Instance.String_Error, MessageBoxIcon.Error);
    }
    
    private void BuildThemeMenuItems()
    {
        ThemeMenuItems.Clear();
        foreach (var themeName in new[] { "Classic", "Luna Blue", "Windows 7 Aero", "Windows 8", "Windows 10", "Windows 11" })
        {
            var lightTheme = ThemeModel.AllThemes.Single(theme => theme.Name == themeName);
            var darkTheme = ThemeModel.AllDarkThemes.Single(theme => theme.Family == lightTheme.Family);
            var menuItem = new MenuItem
            {
                Header = lightTheme.Family,
                ToggleType = MenuItemToggleType.Radio
            };
            menuItem.Click += (_, _) => ApplyThemePreset(lightTheme, darkTheme);
            ThemeMenuItems.Add(menuItem);
        }
        UpdateThemeMenuChecks();
    }

    private void ApplyThemePreset(ThemeModel lightTheme, ThemeModel darkTheme)
    {
        // 同时保存该系列的明暗配色，保留用户当前的明暗模式。
        var config = _configService.CurrentConfig;
        config.ThemeName = lightTheme.Name;
        config.DarkThemeName = darkTheme.Name;
        var theme = config.ThemeVariantMode switch
        {
            ThemeVariantMode.Automatic when AppConfig.IsSystemDarkMode => darkTheme,
            ThemeVariantMode.Dark => darkTheme,
            _ => lightTheme
        };
        _configService.ApplyTheme(theme.Name);
        _configService.Save();
        UpdateThemeMenuChecks();
    }

    private void UpdateThemeMenuChecks()
    {
        // 设置中选用同系列的其他配色时，菜单仍勾选对应的系列。
        var activeFamily = ThemeModel.AllThemes.Concat(ThemeModel.AllDarkThemes)
            .FirstOrDefault(theme => theme.Variant == Avalonia.Application.Current!.ActualThemeVariant)?.Family;
        foreach (var menuItem in ThemeMenuItems)
            menuItem.IsChecked = Equals(menuItem.Header, activeFamily);
    }

    private sealed record EditorStateSnapshot(
        IReadOnlyList<ClipStateSnapshot> Clips,
        long? SelectedClipId,
        long? SelectionAnchorId,
        long ExportSettingsRevision);

    private sealed record ClipStateSnapshot(
        long ClipId,
        string FilePath,
        double StartTime,
        long StartPts,
        long StartPosition,
        double EndTime,
        long EndPts,
        long EndPosition,
        bool IsSelected,
        bool HasStartThumbnail,
        bool HasEndThumbnail,
        string? OutputFilePath,
        MergedClipOutput? MergedOutput,
        ClipExportStatus ExportStatus,
        double ExportPercent,
        string Name,
        string Notes,
        string Color)
    {
        public static ClipStateSnapshot Create(PickedClip clip) => new(
            clip.ClipID,
            clip.InFileInfo.FullName,
            clip.StartTime,
            clip.StartPts,
            clip.StartPosition,
            clip.EndTime,
            clip.EndPts,
            clip.EndPosition,
            clip.IsSelected,
            clip.HasStartThumbnail,
            clip.HasEndThumbnail,
            clip.OutputFileInfo?.FullName,
            clip.MergedOutput,
            clip.ExportStatus,
            clip.ExportPercent,
            clip.Name,
            clip.Notes,
            clip.Color);
    }

    private readonly record struct HistoryThumbnailKey(
        long ClipId,
        bool IsStart,
        double Time,
        long Pts,
        long Position);

    private readonly record struct ClipAggregateRange(
        long StartPosition,
        long EndPosition,
        double StartTime,
        double EndTime);

    private readonly record struct ScrubPreviewRequest(
        long Generation,
        TimeSpan Time,
        PixelSize TargetSize);
}
