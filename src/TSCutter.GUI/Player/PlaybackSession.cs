using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia.Threading;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Extensions;
using TSCutter.GUI.Player.Audio;
using TSCutter.GUI.Player.Video;
using TSCutter.GUI.Rendering;
using TSCutter.GUI.Rendering.Gpu;

namespace TSCutter.GUI.Player;

/// <summary>连续播放会话，拥有解复用、音频输出和呈现任务，取消后统一等待并释放。</summary>
public sealed class PlaybackSession : IAsyncDisposable
{
    private const double LateDropSeconds = 0.25;
    private const double CatchUpLagSeconds = 1;
    private PlaybackOptions options;
    private VideoFrameQueue frameQueue = new(6);
    private readonly VideoPlaybackEngine engine;
    private readonly PlaybackClock clock = new();
    private readonly HostFramePresenter hostPresenter = new();
    private readonly SkiaVideoFramePresenter gpuPresenter = new();
    private Action<bool, bool> gpuFrameRendered = null!;
    private Action gpuFrameDropped = null!;
    private long gpuPresentedFrameCount, hardwareSurfaceFrameCount;
    private TaskCompletionSource? firstGpuFrameReady;
    private Task pendingPresentation = Task.CompletedTask;
    private readonly CancellationTokenSource lifecycle;
    private readonly CancellationTokenRegistration cancelEngine;
    private readonly Action<Action> uiPost;
    private readonly object stopSync = new();
    private readonly object targetSizeSync = new();
    private readonly object publicationSync = new();
    private Avalonia.PixelSize targetSize;
    private AudioPlaybackPipeline? audioPipeline;
    private Task? initializationTask, decodeTask, presentTask, stopTask;
    private double currentVolume;
    private long presentedFrameCount;
    private long droppedFrameCount;
    private long lateDroppedFrameCount, busyDroppedFrameCount;
    private PlaybackPerformanceCounter performanceCounter = new();
    private readonly SemaphoreSlim operations = new(1, 1);
    private CancellationTokenSource runCancellation;
    private TaskCompletionSource? resumeSignal;
    private long generation;
    private double lastPresentedSeconds;
    private double lastPresentationAt;
    private double firstPresentationElapsed, lastPresentationElapsed;
    private Func<IAudioOutput?>? audioOutputFactory;
    private int stopped, disposed, presentationPending, terminalReported;
    private volatile PlaybackState state;

    private PlaybackSession(PlaybackOptions options, Action<Action>? uiPost, CancellationToken token)
    {
        this.options = options;
        lastPresentedSeconds = options.StartSeconds;
        currentVolume = Math.Clamp(options.Volume, 0, 1);
        targetSize = new(options.MaxPresentWidth, options.MaxPresentHeight);
        this.uiPost = uiPost ?? (action => Dispatcher.UIThread.Post(action));
        lifecycle = CancellationTokenSource.CreateLinkedTokenSource(token);
        runCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifecycle.Token);
        ConfigurePresentationCallbacks();
        engine = new VideoPlaybackEngine(options, frameQueue, enableDeinterlacing: true);
        cancelEngine = lifecycle.Token.Register(engine.Cancel);
    }

    private void ConfigurePresentationCallbacks()
    {
        var capturedGeneration = Interlocked.Read(ref generation);
        var token = runCancellation.Token;
        gpuFrameRendered = (gpu, hardwareSurface) =>
        {
            lock (publicationSync)
            {
                if (capturedGeneration != Interlocked.Read(ref generation) || token.IsCancellationRequested)
                    return;
                RecordPresentation();
                if (gpu)
                    Interlocked.Increment(ref gpuPresentedFrameCount);
                if (hardwareSurface)
                    Interlocked.Increment(ref hardwareSurfaceFrameCount);
                Volatile.Read(ref firstGpuFrameReady)?.TrySetResult();
            }
        };
        gpuFrameDropped = () =>
        {
            lock (publicationSync)
            {
                // 旧画面可能在跳转后才释放，不能唤醒新位置的首帧等待或修改其统计。
                if (capturedGeneration != Interlocked.Read(ref generation) || token.IsCancellationRequested)
                    return;
                Interlocked.Increment(ref droppedFrameCount);
                Interlocked.Increment(ref busyDroppedFrameCount);
                Volatile.Read(ref firstGpuFrameReady)?.TrySetResult();
            }
        };
    }

    public event Action<DecodeResult>? FramePresented;
    internal event Action<TSCutter.GUI.FFmpeg.Frame, DecodeResult>? CapturePresentedFrame;
    public event Action? Ended;
    public event Action<Exception>? Failed;
    public event Action<PlaybackAudioStatus>? AudioStatusChanged;
    public PlaybackState State => state;
    public PlaybackClock Clock => clock;
    internal double? EndSeconds => options.EndSeconds;
    internal double LastPresentedSeconds => Interlocked.CompareExchange(ref lastPresentedSeconds, 0, 0);
    public PlaybackAudioStatus AudioStatus { get; private set; } = PlaybackAudioStatus.Normal;
    public bool IsHardwareDecodingAware => engine.IsHardwareDecoding;
    internal double OutputFrameRate => engine.SourceFrameRate;
    internal long HardwareReadbackFrameCount => engine.HardwareReadbackFrameCount;
    internal long CatchUpCount => engine.CatchUpCount;
    internal long LateDroppedFrameCount => Interlocked.Read(ref lateDroppedFrameCount);
    internal long BusyDroppedFrameCount => Interlocked.Read(ref busyDroppedFrameCount);
    internal long PresentedFrameCount => Interlocked.Read(ref presentedFrameCount);
    internal bool UsesHardwareSurfacePresentation => Interlocked.Read(ref hardwareSurfaceFrameCount) > 0
        && Interlocked.Read(ref hardwareSurfaceFrameCount) == Interlocked.Read(ref presentedFrameCount);
    internal bool UsesGpuPresentation => Interlocked.Read(ref gpuPresentedFrameCount) > 0;

    public static Task<PlaybackSession> StartAsync(PlaybackOptions options)
        => StartAsync(options, uiPost: null);

    internal static async Task<PlaybackSession> StartAsync(PlaybackOptions options,
        Action<Action>? uiPost, CancellationToken cancellationToken = default,
        Action<PlaybackSession>? initialize = null, Func<IAudioOutput?>? audioOutputFactory = null)
    {
        if (!double.IsFinite(options.StartSeconds) || options.StartSeconds < 0 ||
            !double.IsFinite(options.TimelineOriginSeconds) ||
            options.EndSeconds is { } end && (!double.IsFinite(end) || end < options.StartSeconds))
            throw new ArgumentOutOfRangeException(nameof(options));
        var session = new PlaybackSession(options, uiPost, cancellationToken);
        try
        {
            // 先建立所有权和事件订阅，极短文件的首帧、失败或 EOF 也不会丢失。
            initialize?.Invoke(session);
            session.state = PlaybackState.Starting;
            session.audioOutputFactory = audioOutputFactory;
            session.initializationTask = session.InitializeAsync(initializeAudio: true);
            await session.initializationTask.ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task InitializeAsync(bool initializeAudio)
    {
        var token = runCancellation.Token;
        var capturedGeneration = Interlocked.Read(ref generation);
        token.ThrowIfCancellationRequested();
        await engine.OpenAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (initializeAudio)
            await InitializeAudioAsync(audioOutputFactory).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        engine.SetAudioPipeline(audioPipeline, async () =>
        {
            await ResetAudioForSeekAsync(token).ConfigureAwait(false);
            return audioPipeline;
        });
        decodeTask = Task.Run(engine.RunAsync);
        await engine.PrerollCompletion.WaitAsync(token).ConfigureAwait(false);
        if (audioPipeline is { } pipeline)
            await pipeline.PrerollAsync(token).ConfigureAwait(false);
        await PrepareFirstGpuFrameAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        clock.Start(options.StartSeconds);
        if (audioPipeline is { Failure: null } output && !output.Completion.IsCompleted)
        {
            if (output.Start())
                clock.StartAudio(options.StartSeconds, () => output.ConsumedFrames, output.SampleRate, 0);
            else
            {
                output.Cancel();
                SetAudioStatus(PlaybackAudioStatus.DeviceUnavailable);
            }
        }
        if (audioPipeline is { } active)
            _ = ObserveAudioCompletionAsync(active, token);
        token.ThrowIfCancellationRequested();
        // 预滚首帧在时钟启动前已经显示；不能把等待设备启动的时间计入呈现速率。
        lock (publicationSync)
            lastPresentationAt = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        state = PlaybackState.Playing;
        presentTask = Task.Run(() => PresentLoopAsync(token));
        _ = decodeTask.ContinueWith(task =>
        {
            if (!token.IsCancellationRequested)
                ReportFailure(task.Exception!.GetBaseException(), capturedGeneration);
        }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    /// <summary>保留设备、解码器和有界队列，只冻结音视频输出。</summary>
    public async Task PauseAsync()
    {
        await operations.WaitAsync(lifecycle.Token).ConfigureAwait(false);
        try
        {
            if (state != PlaybackState.Playing || lifecycle.IsCancellationRequested)
                return;
            lock (publicationSync)
            {
                Volatile.Write(ref resumeSignal, new(TaskCreationOptions.RunContinuationsAsynchronously));
                state = PlaybackState.Paused;
            }
            if (audioPipeline is { Failure: null, IsPlaying: true } audio
                && !await Task.Run(audio.Pause).ConfigureAwait(false))
                await DisableAudioAsync().ConfigureAwait(false);
            clock.Pause();
        }
        finally { operations.Release(); }
    }

    public async Task ResumeAsync()
    {
        await operations.WaitAsync(lifecycle.Token).ConfigureAwait(false);
        try
        {
            if (state != PlaybackState.Paused || lifecycle.IsCancellationRequested)
                return;
            var audioAnchor = audioPipeline?.ConsumedFrames;
            if (audioPipeline is { Failure: null } audio && !audio.Completion.IsCompleted
                && !await Task.Run(audio.Start).ConfigureAwait(false))
                await DisableAudioAsync().ConfigureAwait(false);
            clock.Resume(audioAnchor);
            lock (publicationSync)
            {
                lastPresentationAt = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                Volatile.Write(ref performanceCounter, new());
            }
            state = PlaybackState.Playing;
            Interlocked.Exchange(ref resumeSignal, null)?.TrySetResult();
        }
        finally { operations.Release(); }
    }

    /// <summary>同一来源中重新定位，清除旧队列并复用输入、解码器、音频设备和呈现器。</summary>
    public async Task SeekAsync(double startSeconds, double? endSeconds = null)
    {
        if (!double.IsFinite(startSeconds) || startSeconds < 0 ||
            endSeconds is { } end && (!double.IsFinite(end) || end < startSeconds))
            throw new ArgumentOutOfRangeException(nameof(startSeconds));
        await operations.WaitAsync(lifecycle.Token).ConfigureAwait(false);
        try
        {
            lifecycle.Token.ThrowIfCancellationRequested();
            state = PlaybackState.Starting;
            await StopRunAsync().ConfigureAwait(false);
            lifecycle.Token.ThrowIfCancellationRequested();
            clock.Pause();
            options = options with { StartSeconds = startSeconds, EndSeconds = endSeconds };
            runCancellation.Dispose();
            runCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifecycle.Token);
            frameQueue.Dispose();
            frameQueue = new(6);
            engine.ResetForSeek(options, frameQueue, runCancellation.Token);
            await ResetAudioForSeekAsync(runCancellation.Token).ConfigureAwait(false);
            presentationPending = terminalReported = 0;
            pendingPresentation = Task.CompletedTask;
            presentedFrameCount = gpuPresentedFrameCount = hardwareSurfaceFrameCount = 0;
            firstPresentationElapsed = lastPresentationElapsed = 0;
            droppedFrameCount = lateDroppedFrameCount = busyDroppedFrameCount = 0;
            Volatile.Write(ref performanceCounter, new());
            lastPresentedSeconds = startSeconds;
            Interlocked.Exchange(ref resumeSignal, null)?.TrySetResult();
            ConfigurePresentationCallbacks();
            initializationTask = InitializeAsync(initializeAudio: false);
            await initializationTask.ConfigureAwait(false);
        }
        finally { operations.Release(); }
    }

    private async Task ResetAudioForSeekAsync(CancellationToken token)
    {
        if (audioPipeline is { } audio)
        {
            await audio.StopForSeekAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (audio.ResetForSeek(options.StartSeconds, engine.AudioEndSeconds, token))
            {
                SetAudioStatus(PlaybackAudioStatus.Normal);
                audio.SetVolume(currentVolume);
                _ = audio.RunAsync();
                return;
            }
            audioPipeline = null;
            await audio.DisposeAsync().ConfigureAwait(false);
        }
        await InitializeAudioAsync(audioOutputFactory).ConfigureAwait(false);
    }

    private async Task ObserveAudioCompletionAsync(AudioPlaybackPipeline audio, CancellationToken token)
    {
        try
        {
            await audio.Completion.WaitAsync(token).ConfigureAwait(false);
            await operations.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (token.IsCancellationRequested || !ReferenceEquals(audioPipeline, audio))
                    return;
                clock.FallbackToMonotonic();
                if (audio.Failure is not null)
                    SetAudioStatus(PlaybackAudioStatus.DeviceUnavailable);
            }
            finally { operations.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task DisableAudioAsync()
    {
        clock.FallbackToMonotonic();
        if (audioPipeline is { } audio)
        {
            audioPipeline = null;
            await audio.DisposeAsync().ConfigureAwait(false);
        }
        SetAudioStatus(PlaybackAudioStatus.DeviceUnavailable);
    }

    private async Task WaitUntilResumedAsync(CancellationToken token)
    {
        while (state == PlaybackState.Paused)
        {
            token.ThrowIfCancellationRequested();
            if (Volatile.Read(ref resumeSignal) is { } signal)
                await signal.Task.WaitAsync(token).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
    }

    private async Task StopRunAsync()
    {
        // 先递增代次，尚未执行的界面回调和 GPU 回调不能再发布旧位置的数据。
        lock (publicationSync)
            Interlocked.Increment(ref generation);
        runCancellation.Cancel();
        engine.Cancel();
        var audioStopped = audioPipeline?.StopForSeekAsync() ?? Task.CompletedTask;
        if (decodeTask is { } decode)
        {
            try { await decode.ConfigureAwait(false); }
            catch { /* 原始失败由启动流程或 Failed 事件报告。 */ }
        }
        if (presentTask is { } present)
        {
            try { await present.ConfigureAwait(false); }
            catch { /* 旧呈现任务取消后由新位置接管。 */ }
        }
        await audioStopped.ConfigureAwait(false);
        decodeTask = presentTask = null;
    }

    private async Task InitializeAudioAsync(Func<IAudioOutput?>? audioOutputFactory)
    {
        if (engine.AudioStream is { } stream)
        {
            try
            {
                audioPipeline = await AudioPlaybackPipeline.TryCreateAsync(stream, options.StartSeconds,
                    engine.AudioEndSeconds, options.TimelineOriginSeconds, audioOutputFactory).ConfigureAwait(false);
                if (audioPipeline is null)
                    SetAudioStatus(PlaybackAudioStatus.DeviceUnavailable);
            }
            catch (VideoDecoderUnavailableException)
            {
                SetAudioStatus(PlaybackAudioStatus.TrackUnsupported);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Audio setup failed: {exception.Message}");
                SetAudioStatus(PlaybackAudioStatus.DeviceUnavailable);
            }
        }
        else
        {
            SetAudioStatus(PlaybackAudioStatus.NoAudioTrack);
        }
        if (audioPipeline is { } audio)
        {
            SetAudioStatus(PlaybackAudioStatus.Normal);
            audio.SetVolume(currentVolume);
            _ = audio.RunAsync();
        }
    }

    private async Task PrepareFirstGpuFrameAsync(CancellationToken token)
    {
        if (!options.EnableGpuPresentation || !SkiaVideoFramePresenter.IsRendererAvailable || FramePresented is null)
            return;
        QueuedVideoFrame first;
        try { first = await frameQueue.DequeueAsync(token).ConfigureAwait(false); }
        catch (ChannelClosedException) { return; }
        using (first)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref firstGpuFrameReady, ready);
            try
            {
                // 首次驱动编译和纹理上传在走表前完成，避免编译期间音频已前进、后续视频追赶丢帧。
                Present(first);
                try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(2), token).ConfigureAwait(false); }
                catch (TimeoutException) { /* 窗口不可见时可能不绘制，不能无限阻塞起播。 */ }
            }
            finally { Volatile.Write(ref firstGpuFrameReady, null); }
        }
    }

    private void SetAudioStatus(PlaybackAudioStatus status)
    {
        AudioStatus = status;
        var capturedGeneration = Interlocked.Read(ref generation);
        uiPost(() =>
        {
            if (!lifecycle.IsCancellationRequested && capturedGeneration == Interlocked.Read(ref generation))
                AudioStatusChanged?.Invoke(status);
        });
    }

    public void SetVolume(double volume)
    {
        Interlocked.Exchange(ref currentVolume, Math.Clamp(volume, 0, 1));
        audioPipeline?.SetVolume(currentVolume);
    }

    private async Task PresentLoopAsync(CancellationToken token)
    {
        var capturedGeneration = Interlocked.Read(ref generation);
        try
        {
            while (true)
            {
                await WaitUntilResumedAsync(token).ConfigureAwait(false);
                // 等上一帧进入绘制队列后再交付下一帧，避免 hover 时覆盖尚未提交的画面。
                // 不等待 GPU 绘制结束，让 UI 与 GPU 并行，避免接近屏幕刷新率时吞吐不足。
                // 窗口不绘制时最多等待 50ms。
                var waitingForPresentation = !pendingPresentation.IsCompleted;
                if (waitingForPresentation)
                {
                    try { await pendingPresentation.WaitAsync(TimeSpan.FromMilliseconds(50), token).ConfigureAwait(false); }
                    catch (TimeoutException) { }
                }
                QueuedVideoFrame item;
                try { item = await frameQueue.DequeueAsync(token).ConfigureAwait(false); }
                catch (ChannelClosedException)
                {
                    if (decodeTask is { IsFaulted: true })
                        await decodeTask.ConfigureAwait(false);
                    if (audioPipeline is { } audio)
                    {
                        var deadline = clock.ActiveElapsedSeconds + 5;
                        while (!audio.Completion.IsCompleted)
                        {
                            await WaitUntilResumedAsync(token).ConfigureAwait(false);
                            if (clock.ActiveElapsedSeconds > deadline)
                                throw new TimeoutException("Audio output did not drain.");
                            await Task.WhenAny(audio.Completion, Task.Delay(20, token)).ConfigureAwait(false);
                        }
                    }
                    await WaitUntilResumedAsync(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (Interlocked.Exchange(ref terminalReported, 1) == 0)
                    {
                        if (audioPipeline is { IsPlaying: true } finishedAudio)
                            await Task.Run(finishedAudio.Pause).ConfigureAwait(false);
                        clock.Pause();
                        state = PlaybackState.Ended;
                        uiPost(() =>
                        {
                            if (Volatile.Read(ref stopped) == 0 && !token.IsCancellationRequested)
                                Ended?.Invoke();
                        });
                    }
                    return;
                }
                using (item)
                {
                    await WaitUntilResumedAsync(token).ConfigureAwait(false);
                    var lag = clock.CurrentSeconds - item.TimelineSeconds;
                    // 持续绘制不足（包括片源帧率高于屏幕刷新率）时跳过积压画面，
                    // 等待呈现不能让视频逐渐落后于音频；短暂输入延迟保留两个帧间隔的余量。
                    var maximumLag = waitingForPresentation && engine.SourceFrameRate > 0
                        ? Math.Min(LateDropSeconds, 2 / engine.SourceFrameRate) : LateDropSeconds;
                    if (lag > maximumLag)
                    {
                        if (lag > CatchUpLagSeconds)
                            engine.RequestCatchUp(clock.CurrentSeconds);
                        // 队列中有更新画面才丢弃旧帧；解码暂时跟不上时仍呈现最新可用帧，
                        // 避免恢复到接近实时速度后，所有画面仍因历史积压被永久丢弃。
                        if (frameQueue.HasReadyFrames)
                        {
                            Interlocked.Increment(ref droppedFrameCount);
                            Interlocked.Increment(ref lateDroppedFrameCount);
                            continue;
                        }
                    }
                    await clock.WaitUntilAsync(item.TimelineSeconds, token).ConfigureAwait(false);
                    await WaitUntilResumedAsync(token).ConfigureAwait(false);
                    Present(item);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { ReportFailure(exception, capturedGeneration); }
    }

    // 计数和呈现时刻必须一起取样，避免渲染线程恰好跨过采样边界。
    internal PlaybackPerformance? MeasurePerformance()
    {
        lock (publicationSync)
            return Volatile.Read(ref performanceCounter).Measure(
                Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency,
                engine.PlaybackFrameCount, presentedFrameCount, engine.SourceFrameRate,
                droppedFrameCount, lastPresentationAt);
    }

    // 与计数一起在 publicationSync 内更新，CPU 发布和 GPU 实际绘制使用同一个口径。
    private void RecordPresentation()
    {
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        var elapsed = state == PlaybackState.Starting ? 0 : clock.ActiveElapsedSeconds;
        if (presentedFrameCount == 0)
            firstPresentationElapsed = elapsed;
        lastPresentationElapsed = elapsed;
        lastPresentationAt = now;
        Interlocked.Increment(ref presentedFrameCount);
    }

    internal (long FrameCount, double ElapsedSeconds) MeasurePresentationTiming()
    {
        lock (publicationSync)
        {
            if (presentedFrameCount < 2)
                return (presentedFrameCount, 0);
            // 活跃时钟在暂停和结束时冻结，既不计自检轮询尾部，也不丢失末尾的真实停顿。
            // 一帧以内的采样相位差沿用实际绘制时刻，超过一帧无新画面时继续走表。
            var frameSeconds = engine.SourceFrameRate > 0 ? 1 / engine.SourceFrameRate : 0;
            var end = Math.Max(lastPresentationElapsed, clock.ActiveElapsedSeconds - frameSeconds);
            return (presentedFrameCount, Math.Max(0, end - firstPresentationElapsed));
        }
    }

    public void SetPresentationSize(Avalonia.PixelSize size)
    {
        lock (targetSizeSync)
            targetSize = size;
    }

    private void Present(QueuedVideoFrame item)
    {
        var capturedGeneration = Interlocked.Read(ref generation);
        var token = runCancellation.Token;
        var firstReady = Volatile.Read(ref firstGpuFrameReady);
        // UI 最多保留一个待发布画面；界面忙时不无限排队，也不逐帧分配兜底位图。
        if (Interlocked.CompareExchange(ref presentationPending, 1, 0) != 0)
        {
            Interlocked.Increment(ref droppedFrameCount);
            Interlocked.Increment(ref busyDroppedFrameCount);
            return;
        }
        var presented = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rendered = gpuFrameRendered;
        var dropped = gpuFrameDropped;
        pendingPresentation = presented.Task;
        IBitmapFrameLease? lease = null;
        SkiaVideoFrame? gpuFrame = null;
        var published = false;
        TSCutter.GUI.FFmpeg.Frame? capturedFrame = null;
        try
        {
            Avalonia.PixelSize size;
            lock (targetSizeSync)
                size = targetSize;
            // 直传失败后已经排队的硬件帧也先回读，再尝试 GPU 上传，避免整批落到 CPU 转色。
            using var softwareFrame = item.Frame.HasHardwareFramesContext && !MacOSVideoTexture.CanImport(item.Frame)
                ? item.Frame.TransferToSoftwareFrame() : null;
            var displaySource = softwareFrame ?? item.Frame;
            if ((!options.EnableGpuPresentation || !gpuPresenter.TryPresent(displaySource, options.EnableSdrToneMapping, size,
                    options.RenderAtRequestedSize,
                    (gpu, hardware) => { rendered(gpu, hardware); presented.TrySetResult(); },
                    () => { dropped(); presented.TrySetResult(); }, out gpuFrame,
                    submitted: () => presented.TrySetResult()))
                && !hostPresenter.TryConvert(displaySource, size.Width, size.Height,
                    out lease, options.EnableSdrToneMapping))
            {
                Interlocked.Increment(ref droppedFrameCount);
                Interlocked.Increment(ref busyDroppedFrameCount);
                return;
            }
            var presentationMode = item.Mode;
            if (gpuFrame is not null)
            {
                presentationMode = displaySource.HasHardwareFramesContext ? VideoPresentationMode.HardwareGpu
                    : item.Mode is VideoPresentationMode.HardwareGpu or VideoPresentationMode.HardwareCpuTransfer
                        ? VideoPresentationMode.HardwareGpuUpload : VideoPresentationMode.SoftwareGpu;
            }
            else if (presentationMode == VideoPresentationMode.HardwareGpu)
                presentationMode = VideoPresentationMode.HardwareCpuTransfer;
            var result = new DecodeResult
            {
                Bitmap = lease?.Bitmap, BitmapLease = lease, GpuFrame = gpuFrame,
                SourcePixelSize = item.PixelSize, RequiresSampleAspectRatioCorrection = item.RequiresCorrection,
                VideoDynamicRange = item.Range, FrameTimestamp = TimeSpan.FromSeconds(item.TimelineSeconds),
                FramePts = item.Pts, FramePts90k = item.Pts90k, FramePosition = item.Position,
                PresentationMode = presentationMode
            };
            if (CapturePresentedFrame is not null)
                capturedFrame = item.Frame.CloneReference();
            var sourceFrame = capturedFrame;
            void ReleaseResult()
            {
                result.BitmapLease?.Dispose();
                result.GpuFrame?.Dispose();
                sourceFrame?.Dispose();
                firstReady?.TrySetResult();
                if (capturedGeneration == Interlocked.Read(ref generation))
                    Volatile.Write(ref presentationPending, 0);
                presented.TrySetResult();
            }
            async Task PublishAfterResumeAsync()
            {
                try
                {
                    await WaitUntilResumedAsync(token).ConfigureAwait(false);
                    uiPost(PublishOnUi);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { ReleaseResult(); }
                catch (Exception exception) { ReleaseResult(); ReportFailure(exception, capturedGeneration); }
            }
            void PublishOnUi()
            {
                lock (publicationSync)
                {
                    if (!token.IsCancellationRequested && state == PlaybackState.Paused)
                    {
                        _ = PublishAfterResumeAsync();
                        return;
                    }
                    try
                    {
                        if (Volatile.Read(ref stopped) == 0 && !token.IsCancellationRequested
                            && capturedGeneration == Interlocked.Read(ref generation) && FramePresented is { } handler)
                        {
                            if (sourceFrame is not null)
                                CapturePresentedFrame?.Invoke(sourceFrame, result);
                            Interlocked.Exchange(ref lastPresentedSeconds, result.FrameTimestamp.TotalSeconds);
                            handler(result);
                            if (result.GpuFrame is null)
                            {
                                RecordPresentation();
                                firstReady?.TrySetResult();
                            }
                        }
                        else
                        {
                            result.BitmapLease?.Dispose();
                            result.GpuFrame?.Dispose();
                            firstReady?.TrySetResult();
                        }
                    }
                    catch (Exception exception)
                    {
                        result.BitmapLease?.Dispose();
                        result.GpuFrame?.Dispose();
                        ReportFailure(exception, capturedGeneration);
                    }
                    finally
                    {
                        sourceFrame?.Dispose();
                        if (capturedGeneration == Interlocked.Read(ref generation))
                            Volatile.Write(ref presentationPending, 0);
                        if (result.GpuFrame is null)
                            presented.TrySetResult();
                    }
                }
            }
            uiPost(PublishOnUi);
            published = true;
            capturedFrame = null;
            lease = null;
            gpuFrame = null;
        }
        finally
        {
            lease?.Dispose();
            gpuFrame?.Dispose();
            // 发布成功时由 UI 回调解除；未取得缓冲时直接丢弃当前帧。
            capturedFrame?.Dispose();
            if (!published)
            {
                Volatile.Write(ref presentationPending, 0);
                presented.TrySetResult();
                firstReady?.TrySetResult();
            }
        }
    }

    private void ReportFailure(Exception exception, long? expectedGeneration = null)
    {
        var capturedGeneration = expectedGeneration ?? Interlocked.Read(ref generation);
        lock (publicationSync)
        {
            if (capturedGeneration != Interlocked.Read(ref generation) || lifecycle.IsCancellationRequested
                || Interlocked.Exchange(ref terminalReported, 1) != 0)
                return;
            state = PlaybackState.Idle;
        }
        uiPost(() =>
        {
            if (Volatile.Read(ref stopped) == 0 && capturedGeneration == Interlocked.Read(ref generation))
                Failed?.Invoke(exception);
        });
    }

    public void Cancel()
    {
        if (Volatile.Read(ref stopped) != 0)
            return;
        try { lifecycle.Cancel(); }
        catch (ObjectDisposedException) { return; }
        engine.Cancel();
        audioPipeline?.Cancel();
    }

    public Task StopAsync()
    {
        lock (stopSync)
            return stopTask ??= StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        Cancel();
        Volatile.Write(ref stopped, 1);
        if (initializationTask is { } initialization)
        {
            try { await initialization.ConfigureAwait(false); }
            catch { /* 启动失败由 StartAsync 传播。 */ }
        }
        await operations.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopRunAsync().ConfigureAwait(false);
            clock.Stop();
            if (audioPipeline is { } pipeline)
            {
                await pipeline.DisposeAsync().ConfigureAwait(false);
                audioPipeline = null;
            }
        }
        finally { operations.Release(); }
        await engine.DisposeAsync().ConfigureAwait(false);
        frameQueue.Dispose();
        state = PlaybackState.Idle;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        hostPresenter.Dispose();
        gpuPresenter.Dispose();
        cancelEngine.Dispose();
        runCancellation.Dispose();
        lifecycle.Dispose();
        operations.Dispose();
    }
}

public enum PlaybackAudioStatus
{
    Normal,
    NoAudioTrack,
    DeviceUnavailable,
    TrackUnsupported
}
