using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Classic.CommonControls.Dialogs;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.ViewModels;

/// <summary>
/// 自动保存与崩溃恢复：按设定间隔把未保存的剪辑状态写入本实例的恢复文件，
/// 正常关闭、保存工程或放弃修改时删除；启动时对已不在运行的实例留下的恢复文件提示恢复。
/// 光盘来源不能保存为工程，也不写恢复文件。
/// </summary>
public partial class MainWindowViewModel
{
    // 写入与删除都排在同一条后台任务链上依次执行，删除总在之前的写入完成之后进行。
    private Task _recoveryChain = Task.CompletedTask;
    // 每次删除递增；排队中、尚未落盘的写入发现代数变化后放弃，避免删除后又写回旧内容。
    private int _recoveryGeneration;
    private bool _recoveryWritePending;
    // 上次成功写入时的工程内容（不含缩略图与浏览位置）和工程路径；状态为 null 表示本实例当前没有恢复文件。
    private string? _lastRecoveryState;
    private string? _lastRecoveryPath;
    // 启动时提示恢复期间暂停自动保存，避免删掉刚认领的恢复文件。
    private bool _recoveryPaused;
    private bool _recoveryStarted;
    private DispatcherTimer? _recoveryTimer;
    // 从恢复文件载入、尚未保存：即使没有工程路径也视为有未保存的修改。
    private bool _recoveredUnsaved;
    // 缩略图按位图实例缓存编码结果，每次只编码新截取的缩略图；位图被替换释放后缓存项随之回收。
    private readonly ConditionalWeakTable<Bitmap, byte[]> _recoveryThumbnails = new();

    internal ProjectRecoveryStore RecoveryStore { get; set; } = ProjectRecoveryStore.Default;

    private void SetRecoveredUnsaved(bool value)
    {
        if (_recoveredUnsaved == value)
            return;
        _recoveredUnsaved = value;
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(HasUnsavedProjectChanges));
    }

    /// <summary>启动后开始定时自动保存；设置关闭时不启动。</summary>
    private void StartProjectRecovery()
    {
        _recoveryStarted = true;
        ApplyRecoverySettings();
    }

    private void ApplyRecoverySettings()
    {
        if (!_recoveryStarted)
            return;
        var config = _configService.CurrentConfig;
        if (!config.AutoSaveRecovery)
        {
            _recoveryTimer?.Stop();
            DiscardRecovery();
            return;
        }
        if (_recoveryTimer is null)
        {
            _recoveryTimer = new DispatcherTimer(DispatcherPriority.Background);
            _recoveryTimer.Tick += (_, _) => _ = SaveRecoveryAsync();
        }
        var interval = TimeSpan.FromSeconds(AppConfig.NormalizeAutoSaveInterval(config.AutoSaveIntervalSeconds));
        if (_recoveryTimer.Interval != interval || !_recoveryTimer.IsEnabled)
        {
            _recoveryTimer.Stop();
            _recoveryTimer.Interval = interval;
            _recoveryTimer.Start();
        }
    }

    /// <summary>
    /// 有变化时写入恢复文件，没有需要恢复的内容时删除。
    /// 已打开的工程只在有未保存修改时写入；视频或导出队列只要有片段或任务就写入。
    /// 界面线程只抓取状态并复制新截取的缩略图，编码、读取源文件指纹与写入在后台进行。
    /// </summary>
    internal async Task SaveRecoveryAsync()
    {
        if (_recoveryPaused || _recoveryWritePending || _isLoadingVideo)
            return;
        if (HasDiscSources || !IsVideoInitialized && ExportQueue.Count == 0 ||
            !_recoveredUnsaved && _projectPath is null && Clips.Count == 0 && ExportQueue.Count == 0)
        {
            if (_lastRecoveryState is not null)
                DiscardRecovery();
            return;
        }

        CutterProject project;
        string state;
        byte[]?[] thumbnails;
        var pending = new List<(int Index, Bitmap Original, Bitmap? Copy)>();
        try
        {
            project = CaptureProject(includeThumbnails: true);
            // 比较用的内容不含浏览位置，与已保存工程的比较口径一致；只拖动时间轴不会触发写入。
            var navigation = (project.CurrentTime, project.TimelineZoomLevel, project.TimelineViewStart);
            (project.CurrentTime, project.TimelineZoomLevel, project.TimelineViewStart) = (0, 0, 0);
            state = CutterProjectService.Serialize(project);
            (project.CurrentTime, project.TimelineZoomLevel, project.TimelineViewStart) = navigation;
            if (!_recoveredUnsaved && _projectPath is not null && state == _savedProjectState)
            {
                if (_lastRecoveryState is not null)
                    DiscardRecovery();
                return;
            }
            if (state == _lastRecoveryState && _projectPath == _lastRecoveryPath)
                return;

            thumbnails = new byte[]?[Clips.Count * 2];
            for (var index = 0; index < Clips.Count; index++)
            {
                Collect(index * 2, Clips[index].StartThumbnail);
                Collect(index * 2 + 1, Clips[index].EndThumbnail);
            }
        }
        catch (Exception exception)
        {
            foreach (var item in pending)
                item.Copy?.Dispose();
            Console.WriteLine($"Failed to capture recovery state: {exception.Message}");
            return;
        }

        var generation = _recoveryGeneration;
        var projectPath = _projectPath;
        var store = RecoveryStore;
        _recoveryWritePending = true;
        var write = _recoveryChain.ContinueWith(_ => WriteRecovery(store, generation, projectPath, project,
                thumbnails, pending), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        _recoveryChain = write;
        try
        {
            if (await write && generation == _recoveryGeneration)
                (_lastRecoveryState, _lastRecoveryPath) = (state, projectPath);
        }
        finally
        {
            _recoveryWritePending = false;
        }

        void Collect(int index, Bitmap? bitmap)
        {
            if (bitmap is null)
                return;
            if (_recoveryThumbnails.TryGetValue(bitmap, out var jpeg))
                thumbnails[index] = jpeg;
            else
                pending.Add((index, bitmap, CopyProjectThumbnail(bitmap)));
        }
    }

    private bool WriteRecovery(ProjectRecoveryStore store, int generation, string? projectPath, CutterProject project,
        byte[]?[] thumbnails, List<(int Index, Bitmap Original, Bitmap? Copy)> pending)
    {
        try
        {
            if (Volatile.Read(ref _recoveryGeneration) != generation)
                return false;
            foreach (var (index, original, copy) in pending)
            {
                var jpeg = EncodeProjectThumbnail(copy);
                if (jpeg is not null)
                    _recoveryThumbnails.AddOrUpdate(original, jpeg);
                thumbnails[index] = jpeg;
            }
            for (var index = 0; index < project.Clips.Count; index++)
            {
                project.Clips[index].StartThumbnailJpeg = thumbnails[index * 2];
                project.Clips[index].EndThumbnailJpeg = thumbnails[index * 2 + 1];
            }
            CutterProjectService.PopulateSources(project);
            var file = new ProjectRecoveryFile { SavedUtc = DateTime.UtcNow, ProjectPath = projectPath, Project = project };
            var content = ProjectRecoveryStore.Serialize(file);
            // 片段很多时去掉缩略图，保证恢复文件仍能按工程大小上限读回。
            if (content.Length > CutterProjectService.MaximumProjectBytes)
            {
                foreach (var clip in project.Clips)
                {
                    clip.StartThumbnailJpeg = null;
                    clip.EndThumbnailJpeg = null;
                }
                content = ProjectRecoveryStore.Serialize(file);
            }
            if (Volatile.Read(ref _recoveryGeneration) != generation)
                return false;
            store.Write(content);
            return true;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Failed to write recovery file: {exception.Message}");
            return false;
        }
        finally
        {
            foreach (var item in pending)
                item.Copy?.Dispose();
        }
    }

    /// <summary>删除本实例的恢复文件，并作废尚未落盘的写入。</summary>
    private void DiscardRecovery()
    {
        Interlocked.Increment(ref _recoveryGeneration);
        (_lastRecoveryState, _lastRecoveryPath) = (null, null);
        var store = RecoveryStore;
        _recoveryChain = _recoveryChain.ContinueWith(_ => store.DeleteOwn(), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>正常退出：停止自动保存并等待恢复文件删除完成。</summary>
    public void DiscardRecoveryOnExit()
    {
        _recoveryTimer?.Stop();
        _recoveryStarted = false;
        DiscardRecovery();
        try
        {
            _recoveryChain.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException exception)
        {
            Console.WriteLine($"Failed to remove recovery file: {exception.InnerException?.Message}");
        }
    }

    /// <summary>
    /// 启动时逐个提示恢复上次未正常退出时留下的工作；恢复其中一份后不再提示其余的，留到下次启动。
    /// 返回是否已恢复。
    /// </summary>
    private async Task<bool> OfferProjectRecoveryAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime
            { MainWindow: { } owner })
            return false;
        var store = RecoveryStore;
        _recoveryPaused = true;
        try
        {
            var entries = await Task.Run(() => store.FindOrphans()
                .Select(entry => (Entry: entry, SourceExists: entry.SourcePath is not { } path || File.Exists(path)))
                .ToList());
            for (var index = 0; index < entries.Count; index++)
            {
                var (entry, sourceExists) = entries[index];
                var message = DescribeRecovery(entry, sourceExists);
                var title = LocalizationManager.Instance.String_Recovery_Title +
                            (entries.Count > 1 ? $" ({index + 1}/{entries.Count})" : string.Empty);
                var result = await MessageBox.ShowDialog(owner, message, title,
                    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (result == MessageBoxResult.No)
                {
                    ProjectRecoveryStore.Discard(entry);
                    continue;
                }
                if (result != MessageBoxResult.Yes || !store.Claim(entry))
                    continue;
                var restored = false;
                try
                {
                    restored = await OpenProjectCoreAsync(entry.ProjectPath, store.OwnFilePath);
                }
                finally
                {
                    // 没有恢复成功（例如取消了重新定位源文件或载入出错）时放回原处，下次启动仍会提示。
                    if (!restored)
                        store.Release(entry);
                }
                return restored;
            }
            return false;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Failed to offer project recovery: {exception.Message}");
            return false;
        }
        finally
        {
            _recoveryPaused = false;
        }
    }

    // 恢复提示的正文：源文件、工程、片段与任务数量、保存时间。
    private static string DescribeRecovery(ProjectRecoveryEntry entry, bool sourceExists)
    {
        var strings = LocalizationManager.Instance;
        var sourceText = entry.SourcePath is not { } source ? strings.String_Recovery_NoSource
            : sourceExists ? source
            : string.Format(strings.String_Recovery_SourceMissing, source);
        var projectText = entry.ProjectPath ?? strings.String_Recovery_NotSaved;
        var time = entry.SavedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        return string.Format(strings.String_Recovery_Prompt, sourceText, projectText,
            entry.ClipCount, entry.QueueCount, time);
    }
}
