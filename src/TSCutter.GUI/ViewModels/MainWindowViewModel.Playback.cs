using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Threading;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Classic.CommonControls.Dialogs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Player;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Media;

namespace TSCutter.GUI.ViewModels;

// 主窗口的播放协调：会话的建立/停止、呈现回调到界面状态的转换。
// 播放引擎细节（线程、队列、时钟）都在 Player 目录，这里只做窄接口调用。
public partial class MainWindowViewModel
{
    private PlaybackSession? playbackSession;
    private bool updatingPreviewAudio;
    private IDisposable? playbackPerformanceTimer;
    private PlaybackPerformance? playbackPerformance;

    public string PlaybackFpsText => playbackPerformance is { } value ? $"{value.PlaybackFps:0.0##} FPS" : "— FPS";
    public bool IsPlaybackSmooth => playbackPerformance?.Fluency == PlaybackFluency.Smooth;
    public bool IsPlaybackLimited => playbackPerformance?.Fluency == PlaybackFluency.Limited;
    public bool IsPlaybackSlow => playbackPerformance?.Fluency == PlaybackFluency.Slow;

    private void SetPlaybackPerformance(PlaybackPerformance? value)
    {
        if (playbackPerformance == value)
            return;
        playbackPerformance = value;
        OnPropertyChanged(nameof(PlaybackFpsText));
        OnPropertyChanged(nameof(IsPlaybackSmooth));
        OnPropertyChanged(nameof(IsPlaybackLimited));
        OnPropertyChanged(nameof(IsPlaybackSlow));
    }

    [ObservableProperty]
    public partial IReadOnlyList<PreviewAudioTrackOption> PreviewAudioTracks { get; set; } = [];
    [ObservableProperty]
    public partial PreviewAudioTrackOption? SelectedPreviewAudioTrack { get; set; }
    public bool HasPreviewAudioTracks => PreviewAudioTracks.Count > 0;
    // 音轨只影响独立播放会话，不随视频逐帧解码反复禁用。
    public bool CanSelectPreviewAudio => PreviewAudioTracks.Count > 0 && IsVideoInitialized && !_isLoadingVideo;

    private void SetPlaybackTrackChoices(IReadOnlyList<PreviewAudioTrackOption> tracks)
    {
        updatingPreviewAudio = true;
        try
        {
            PreviewAudioTracks = tracks;
            SelectedPreviewAudioTrack = tracks.FirstOrDefault(track => track.CanDecode) ?? tracks.FirstOrDefault();
            playbackAudioStatus = PlaybackAudioStatus.Normal;
            OnPropertyChanged(nameof(PlaybackAudioStatusText));
            OnPropertyChanged(nameof(HasPlaybackAudioProblem));
            OnPropertyChanged(nameof(HasPreviewAudioTracks));
            OnPropertyChanged(nameof(CanSelectPreviewAudio));
            OnPropertyChanged(nameof(ShowPreviewTrackSelector));
        }
        finally { updatingPreviewAudio = false; }
    }

    partial void OnSelectedPreviewAudioTrackChanged(PreviewAudioTrackOption? oldValue, PreviewAudioTrackOption? newValue)
    {
        if (updatingPreviewAudio)
            return;
        if (newValue is { CanDecode: false } || newValue is null && HasPreviewAudioTracks)
        {
            updatingPreviewAudio = true;
            try
            {
                SelectedPreviewAudioTrack = oldValue is { CanDecode: true }
                    ? oldValue : PreviewAudioTracks.FirstOrDefault(track => track.CanDecode) ?? PreviewAudioTracks.FirstOrDefault();
            }
            finally { updatingPreviewAudio = false; }
            return;
        }
        var resume = IsPlaybackActive;
        var end = playbackSession?.EndSeconds;
        StopPlaybackSession();
        if (resume)
            _ = StartPlaybackWithOptions(CurrentTime, end);
    }

    private bool playbackStarting;
    private IDisposable? playbackLoadingDelay;
    [ObservableProperty]
    private bool _showPlaybackLoading;
    /// <summary>启动请求代次：StopPlaybackSession 递增，晚到的会话据此自我丢弃。</summary>
    private long playbackRequestGeneration;
    private readonly Stopwatch playbackStripThrottle = Stopwatch.StartNew();
    /// <summary>当前显示帧来自播放（而非关键帧导航）；标记前需吸附合法关键帧。</summary>
    private bool lastFrameFromPlayback;
    private bool timelineScrubActive;
    private bool scrubResumePlayback;

    private bool playbackSuspending, playbackNavigationPending;
    private Task playbackPauseTask = Task.CompletedTask;

    /// <summary>正在起播或播放；暂停保留的会话不占用导航和标记操作。</summary>
    public bool IsPlaybackActive => playbackStarting || !playbackSuspending && !playbackNavigationPending
        && playbackSession is { State: PlaybackState.Playing or PlaybackState.Starting };

    public bool IsPlaybackPlaying => !playbackSuspending && !playbackNavigationPending
        && playbackSession is { State: PlaybackState.Playing };

    public bool IsPlaybackStarting => playbackStarting || !playbackSuspending && !playbackNavigationPending
        && playbackSession is { State: PlaybackState.Starting };

    public bool CanTogglePlayback => IsVideoInitialized && !_isLoadingVideo
        && (IsPlaybackActive || !IsDecoding && !timelineScrubActive);

    [RelayCommand(CanExecute = nameof(CanTogglePlayback), AllowConcurrentExecutions = true)]
    private async Task TogglePlaybackAsync()
    {
        if (IsPlaybackActive)
        {
            if (playbackStarting || playbackSession?.State == PlaybackState.Starting)
                StopPlaybackSession();
            else
            {
                PausePlaybackForNavigation(navigation: false);
                await playbackPauseTask;
            }
            return;
        }
        var end = playbackSession is { State: PlaybackState.Paused, EndSeconds: { } boundary }
            && CurrentTime <= boundary ? (double?)boundary : null;
        await StartPlaybackWithOptions(CurrentTime, end);
    }

    private void StopPlaybackPerformance()
    {
        playbackPerformanceTimer?.Dispose();
        playbackPerformanceTimer = null;
        SetPlaybackPerformance(null);
    }

    private void PausePlaybackForNavigation(bool navigation = true)
    {
        if (playbackStarting || playbackSession?.State == PlaybackState.Starting)
        {
            StopPlaybackSession();
            return;
        }
        if (playbackSession is not { } session)
            return;
        playbackNavigationPending |= navigation;
        if (!playbackSuspending && session.State == PlaybackState.Playing)
        {
            playbackSuspending = true;
            playbackPauseTask = PausePlaybackCoreAsync(session);
        }
        StopPlaybackPerformance();
        NotifyPlaybackStateChanged();
    }

    private async Task PausePlaybackCoreAsync(PlaybackSession owner)
    {
        try { await owner.PauseAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (ReferenceEquals(playbackSession, owner))
                OnPlaybackFailed(owner, exception);
        }
        finally
        {
            if (ReferenceEquals(playbackSession, owner))
            {
                playbackSuspending = false;
                NotifyPlaybackStateChanged();
            }
        }
    }

    private async Task PausePlaybackForNavigationAsync()
    {
        PausePlaybackForNavigation();
        await playbackPauseTask;
    }

    public bool CanPlaySelectedClip => CanTogglePlayback
        && SelectedClip is { } clip && clip.EndTime > clip.StartTime;

    /// <summary>播放当前选中片段：使用片段实际保存的边界，到终点自动暂停（结束状态保留画面）。</summary>
    [RelayCommand(CanExecute = nameof(CanPlaySelectedClip))]
    private async Task PlaySelectedClipAsync()
    {
        if (SelectedClip is not { } clip || !CanTogglePlayback)
            return;
        await PausePlaybackForNavigationAsync();
        // 现有语义：EndTime 即用户所选截止关键帧的显示时间，播放包含该帧。
        await StartPlaybackWithOptions(clip.StartTime, clip.EndTime);
    }

    /// <summary>时间轴拖动开始：记录播放状态并暂停输出，保留会话供跳转后复用。</summary>
    public void BeginTimelineScrub()
    {
        if (!timelineScrubActive)
        {
            timelineScrubActive = true;
            scrubResumePlayback = IsPlaybackActive;
            NotifyPreviewTrackChoicesChanged();
        }
        PausePlaybackForNavigation();
    }

    /// <summary>时间轴拖动结束：只有原先正在播放才从新位置恢复。</summary>
    public void EndTimelineScrub()
    {
        if (!timelineScrubActive)
            return;
        timelineScrubActive = false;
        NotifyPreviewTrackChoicesChanged();
        NotifyPlaybackStateChanged();
        if (!scrubResumePlayback || !CanTogglePlayback)
        {
            scrubResumePlayback = false;
            return;
        }
        scrubResumePlayback = false;
        _ = StartPlaybackWithOptions(CurrentTime, endSeconds: null);
    }

    public bool IsTimelineScrubActive => timelineScrubActive;

    private void NotifyPlaybackStateChanged()
    {
        if (!IsPlaybackStarting)
        {
            playbackLoadingDelay?.Dispose();
            playbackLoadingDelay = null;
            ShowPlaybackLoading = false;
        }
        var wasSuspended = KeyFrameStrip.IsSuspended;
        KeyFrameStrip.SetSuspended(IsPlaybackActive);
        if (wasSuspended && !IsPlaybackActive)
            RequestKeyFrameStrip(CurrentTime, currentPts: _displayedFramePts);
        OnPropertyChanged(nameof(CanTogglePlayback));
        OnPropertyChanged(nameof(CanPlaySelectedClip));
        PlaySelectedClipCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsPlaybackActive));
        OnPropertyChanged(nameof(IsPlaybackPlaying));
        OnPropertyChanged(nameof(IsPlaybackStarting));
        OnPropertyChanged(nameof(PlaybackButtonToolTip));
        TogglePlaybackCommand.NotifyCanExecuteChanged();
    }

    /// <summary>预览音量（0..1）；只作用于预览输出，不影响源数据与导出。</summary>
    [ObservableProperty]
    public partial double PreviewVolume { get; set; } = 1.0;

    /// <summary>预览静音开关；与“音轨不可播放”是不同的状态。</summary>
    [ObservableProperty]
    public partial bool PreviewMuted { get; set; }

    /// <summary>当前会话音频状态（无声/设备不可用/音轨不支持时用于提示）。</summary>
    private PlaybackAudioStatus playbackAudioStatus = PlaybackAudioStatus.Normal;

    /// <summary>由主窗口注入的预览呈现目标尺寸（按预览区域与 DPI 换算）。</summary>
    public Func<PixelSize>? PlaybackTargetSizeProvider { get; set; }

    private PixelSize PlaybackTargetSize => PlaybackTargetSizeProvider?.Invoke() ?? default;

    public void UpdatePlaybackPresentationSize() => playbackSession?.SetPresentationSize(PlaybackTargetSize);

    public double EffectivePreviewVolume => PreviewMuted ? 0.0 : PreviewVolume;

    partial void OnPreviewVolumeChanged(double value) => ApplyPreviewVolume();
    partial void OnPreviewMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(MuteTextDecorations));
        OnPropertyChanged(nameof(MuteButtonToolTip));
        ApplyPreviewVolume();
    }

    private void ApplyPreviewVolume()
    {
        if (playbackSession is { } session)
            session.SetVolume(EffectivePreviewVolume);
    }

    [RelayCommand]
    private void TogglePreviewMute() => PreviewMuted = !PreviewMuted;

    public Avalonia.Media.TextDecorationCollection? MuteTextDecorations => PreviewMuted
        ? Avalonia.Media.TextDecorations.Strikethrough
        : null;

    public string MuteButtonToolTip => PreviewMuted
        ? LocalizationManager.Instance.String_Playback_Unmute
        : LocalizationManager.Instance.String_Playback_Mute;

    /// <summary>音频链路存在问题时的人工可读提示；正常时为空。</summary>
    public string PlaybackAudioStatusText => playbackAudioStatus switch
    {
        PlaybackAudioStatus.NoAudioTrack => LocalizationManager.Instance.String_Playback_NoAudio,
        PlaybackAudioStatus.DeviceUnavailable => LocalizationManager.Instance.String_Playback_AudioDeviceUnavailable,
        PlaybackAudioStatus.TrackUnsupported => LocalizationManager.Instance.String_Playback_AudioTrackUnsupported,
        _ => string.Empty,
    };

    public bool HasPlaybackAudioProblem => playbackAudioStatus != PlaybackAudioStatus.Normal;

    private void OnPlaybackAudioStatusChanged(PlaybackSession owner, PlaybackAudioStatus status)
    {
        if (!ReferenceEquals(playbackSession, owner))
            return;
        playbackAudioStatus = status;
        OnPropertyChanged(nameof(PlaybackAudioStatusText));
        OnPropertyChanged(nameof(HasPlaybackAudioProblem));
    }

    public string PlaybackButtonToolTip => IsPlaybackStarting
        ? LocalizationManager.Instance.String_Playback_CancelStart
        : IsPlaybackActive ? LocalizationManager.Instance.String_Playback_Pause
        : LocalizationManager.Instance.String_Playback_Play;

    private CancellationTokenSource? playbackStartCancellation;
    private Task playbackStopTask = Task.CompletedTask;

    private async Task StartPlaybackWithOptions(double startSeconds, double? endSeconds)
    {
        var instance = _videoInstance;
        if (instance is null || !CanTogglePlayback || IsPlaybackActive)
            return;
        using var cancellation = new CancellationTokenSource();
        playbackStartCancellation = cancellation;
        playbackStarting = true;
        // 短暂起播只更新按钮；持续等待半秒后再显示提示，避免快速加载时闪烁。
        playbackLoadingDelay = DispatcherTimer.RunOnce(() =>
        {
            if (ReferenceEquals(playbackStartCancellation, cancellation) && IsPlaybackStarting)
                ShowPlaybackLoading = true;
        }, TimeSpan.FromMilliseconds(500));
        var requestGeneration = Interlocked.Read(ref playbackRequestGeneration);
        NotifyPlaybackStateChanged();
        PlaybackSession? session = null;
        try
        {
            await playbackStopTask;
            cancellation.Token.ThrowIfCancellationRequested();
            var targetSize = PlaybackTargetSize;
            await playbackPauseTask;
            cancellation.Token.ThrowIfCancellationRequested();
            playbackNavigationPending = false;
            if (playbackSession is { State: PlaybackState.Paused or PlaybackState.Ended } retained)
            {
                session = retained;
                if (retained.State == PlaybackState.Paused && retained.EndSeconds == endSeconds
                    && Math.Abs(retained.LastPresentedSeconds - startSeconds) < 0.001)
                    await retained.ResumeAsync();
                else
                    await retained.SeekAsync(startSeconds, endSeconds);
            }
            else
            {
                session = await PlaybackSession.StartAsync(new PlaybackOptions
                {
                    FilePath = VideoPath, Disc = instance.Disc,
                    StartSeconds = startSeconds, EndSeconds = endSeconds,
                    TimelineOriginSeconds = instance.TimelineOriginSeconds,
                    EstimatedKeyFrameIntervalSeconds = instance.EstimatedKeyFrameIntervalSeconds,
                    StreamSelection = instance.StreamSelection, AudioSelection = SelectedPreviewAudioTrack?.Selection,
                    TsSyncOffset = instance.TsSyncOffset,
                    EnableSdrToneMapping = EnableSdrToneMapping, Volume = EffectivePreviewVolume,
                    PreferHardwareDecoding = preferHardwareDecodingForPreview,
                    MaxPresentWidth = targetSize.Width, MaxPresentHeight = targetSize.Height,
                }, uiPost: null, cancellationToken: cancellation.Token, initialize: owner =>
                {
                    playbackSession = owner;
                    owner.CapturePresentedFrame += instance.RetainPlaybackFrame;
                    owner.FramePresented += result => OnPlaybackFramePresented(owner, result);
                    owner.Ended += () => OnPlaybackEnded(owner);
                    owner.Failed += exception => OnPlaybackFailed(owner, exception);
                    owner.AudioStatusChanged += status => OnPlaybackAudioStatusChanged(owner, status);
                });
            }
            if (!ReferenceEquals(_videoInstance, instance) || cancellation.IsCancellationRequested ||
                Interlocked.Read(ref playbackRequestGeneration) != requestGeneration)
            {
                await session.DisposeAsync();
                return;
            }
            OnPlaybackAudioStatusChanged(session, session.AudioStatus);
            if (ReferenceEquals(playbackSession, session) && session.State == PlaybackState.Playing)
            {
                var owner = session;
                playbackPerformanceTimer = DispatcherTimer.Run(() =>
                {
                    if (!ReferenceEquals(playbackSession, owner))
                        return false;
                    SetPlaybackPerformance(owner.MeasurePerformance());
                    return true;
                }, TimeSpan.FromMilliseconds(500));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Console.WriteLine($"Failed to start playback: {exception}");
            if (Interlocked.Read(ref playbackRequestGeneration) == requestGeneration && ReferenceEquals(instance, _videoInstance))
            {
                StopPlaybackSession();
                await ShowMessageAsync(exception is MediaReadTimeoutException
                        ? LocalizationManager.Instance.String_MediaReadTimeout : exception.Message,
                    LocalizationManager.Instance.String_FailedToDecode, MessageBoxIcon.Error);
            }
        }
        finally
        {
            if (ReferenceEquals(playbackStartCancellation, cancellation))
            {
                playbackStartCancellation = null;
                playbackStarting = false;
                if (playbackSession?.State == PlaybackState.Idle)
                    StopPlaybackSession();
                NotifyPlaybackStateChanged();
            }
        }
    }

    /// <summary>取消正在启动或播放的会话，后台释放；下一次启动会等待上次设备退出。</summary>
    private void StopPlaybackSession()
    {
        StopPlaybackPerformance();
        playbackSuspending = playbackNavigationPending = false;
        Interlocked.Increment(ref playbackRequestGeneration);
        playbackStartCancellation?.Cancel();
        playbackStartCancellation = null;
        playbackStarting = false;
        var session = Interlocked.Exchange(ref playbackSession, null);
        if (session is not null)
        {
            session.Cancel();
            playbackStopTask = Task.WhenAll(playbackStopTask, DisposePlaybackAsync(session));
        }
        NotifyPlaybackStateChanged();
    }

    private static async Task DisposePlaybackAsync(PlaybackSession session)
    {
        try { await session.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { Console.WriteLine($"Playback dispose failed: {exception}"); }
    }

    private void OnPlaybackFramePresented(PlaybackSession owner, DecodeResult result)
    {
        if (!ReferenceEquals(playbackSession, owner) || playbackNavigationPending)
        {
            DisposePreviewResult(result);
            return;
        }
        var firstFrame = !lastFrameFromPlayback;
        lastFrameFromPlayback = true;
        // 与 ApplyDecodeResult 相同的快照字段，但跳过每帧的命令通知与字幕刷新；
        // 标记入口会先停止播放并按需刷新。
        _displayedFramePosition = result.FramePosition;
        _displayedFramePts = result.FramePts;
        _displayedFramePts90k = result.FramePts90k;
        _displayedFrameTime = result.FrameTimestamp.TotalSeconds;
        _displayedFrameEndSignature = result.FrameEndSignature;
        IsHardwareDecoding = result.PresentationMode
            is VideoPresentationMode.HardwareCpuTransfer or VideoPresentationMode.HardwareGpu or VideoPresentationMode.HardwareGpuUpload;
        SetDisplayedVideoDynamicRange(result.VideoDynamicRange);
        DecodedFrameSourceSize = result.SourcePixelSize;
        decodedFrameRequiresSampleAspectRatioCorrection = result.RequiresSampleAspectRatioCorrection;
        DecodedGpuFrame?.Dispose();
        DecodedGpuFrame = result.GpuFrame;
        SetDecodedBitmap(result.Bitmap, result.BitmapLease);
        CurrentTime = result.FrameTimestamp.TotalSeconds;
        if (firstFrame)
            NotifyVideoCapabilityChanged();
        // 播放期间不推进缩略图条；字幕合并刷新请求，复用未变化的渲染结果，
        // 避免逐帧重建解码/渲染任务；暂停后由导航路径恢复缩略图条刷新。
        if (playbackStripThrottle.ElapsedMilliseconds >= 33)
        {
            playbackStripThrottle.Restart();
            _ = RefreshSubtitleOverlayAsync();
        }
    }

    /// <summary>
    /// 播放画面可能是 P/B 帧，不能作为无损剪辑边界。
    /// 来自播放的显示帧在标记前吸附到不晚于其的关键帧，并把吸附结果作为画面显示，
    /// 让用户确认最终剪切位置；导航画面保持原有零解码路径。
    /// </summary>
    private async Task<bool> SnapDisplayedFrameToKeyFrameAsync()
    {
        if (!lastFrameFromPlayback)
            return true;
        var targetPts = _displayedFramePts;
        if (_videoInstance is not { Inited: true } video || targetPts == long.MinValue)
            return false;
        DecodeResult? snapped = null;
        try
        {
            await RunDecodeOperationAsync(async () => snapped = await video.DecodeKeyFrameAtOrBeforePtsAsync(targetPts));
            if (!ReferenceEquals(video, _videoInstance))
                return false;
            if (snapped is null)
                throw new InvalidOperationException(LocalizationManager.Instance.String_Playback_BoundarySnapFailed);
            ApplyDecodeResult(snapped);
            snapped = null;
            return true;
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(video, _videoInstance))
                await ShowMessageAsync(exception.Message, LocalizationManager.Instance.String_Playback_PlayClip, MessageBoxIcon.Error);
            return false;
        }
        finally
        {
            if (snapped is not null)
                DisposePreviewResult(snapped);
        }
    }

    private void OnPlaybackEnded(PlaybackSession owner)
    {
        if (!ReferenceEquals(playbackSession, owner))
            return;
        // 保留已打开的资源，结束后再次定位仍可复用。
        StopPlaybackPerformance();
        NotifyPlaybackStateChanged();
    }

    private async void OnPlaybackFailed(PlaybackSession owner, Exception exception)
    {
        if (!ReferenceEquals(playbackSession, owner))
            return;
        StopPlaybackSession();
        await ShowMessageAsync(
            exception is MediaReadTimeoutException
                ? LocalizationManager.Instance.String_MediaReadTimeout
                : exception.Message,
            LocalizationManager.Instance.String_FailedToDecode, MessageBoxIcon.Error);
    }
}
