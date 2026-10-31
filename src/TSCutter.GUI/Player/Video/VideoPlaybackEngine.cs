using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Extensions;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Models.Discs;
using System.Diagnostics;
using TSCutter.GUI.Utils.Media;
using TSCutter.GUI.Rendering.Gpu;
using FFmpeg.AutoGen.Abstractions;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Player.Video;

/// <summary>
/// 连续视频解码引擎：持有独立的输入上下文与解码器，把文件解复用、全帧解码，
/// 按显示顺序送入有界帧队列。与关键帧导航的 VideoInstance 完全隔离，
/// 两者只是查看同一文件，不共享原生状态。
/// 支持硬件解码与软件回退，软件连续播放使用自动线程数。
/// </summary>
internal sealed class VideoPlaybackEngine : IAsyncDisposable
{
    private const int MaxConsecutiveDecodeFailures = 100;

    private PlaybackOptions options;
    private VideoFrameQueue outputQueue;
    private CancellationTokenSource cancellation = new();

    private FormatContext? formatContext;
    // 预读压缩包，不增加未压缩视频帧队列；条目数和载荷字节数同时有界。
    private const int VideoPacketCapacity = 256;
    private const long VideoPacketByteLimit = 16L * 1024 * 1024;
    private Channel<Packet>? videoPackets;
    private CancellationTokenSource? packetReadCancellation;
    private Task? packetReadTask;
    private long queuedVideoBytes, inputBytesRead;
    private volatile bool audioTailRead;
    private TSCutter.GUI.Player.Audio.AudioPlaybackPipeline? audioPipeline;
    private Func<Task<TSCutter.GUI.Player.Audio.AudioPlaybackPipeline?>>? restartAudioPreroll;
    private CodecContext? videoDecoder;
    private Frame? reusableSoftwareFrame;
    private bool hardwareDecoderOpened;
    private int hardwareNoFrameCount;
    private bool waitingForKeyFrame = true;
    private MediaStream videoStream;
    private int videoStreamIndex = -1;
    private MediaStream? audioStream;
    private int audioStreamIndex = -1;
    private AVRational timeBase;
    private long firstPts;
    private long targetPts;
    private long keyFrameGapPts;
    private Frame? reusableDecodeFrame;
    private long lastEnqueuedPts = long.MinValue;
    private long decodedFrameCount, hardwareReadbackFrameCount;
    private readonly bool enableDeinterlacing;
    private VideoDeinterlacer? deinterlacer;
    private Frame? reusableFilteredFrame;
    private int outputRateMultiplier = 1;
    private long playbackFrameCount;
    private long readTicks;
    private long catchUpTargetPts = long.MinValue;
    private long catchUpCount;
    private long minimumOutputPts = long.MinValue;
    private MediaReadDeadline? readDeadline;
    private bool IsInterrupted => cancellation.IsCancellationRequested ||
        packetReadCancellation?.IsCancellationRequested == true || readDeadline?.ShouldInterrupt == true;

    private T ReadOperation<T>(Func<T> action)
    {
        var started = options.CollectReadTimings ? Stopwatch.GetTimestamp() : 0;
        var deadline = new MediaReadDeadline(TimeSpan.FromSeconds(30), cancellation.Token);
        readDeadline = deadline;
        try
        {
            deadline.ThrowIfInterrupted();
            var result = action();
            deadline.ThrowIfInterrupted();
            return result;
        }
        catch { deadline.ThrowIfInterrupted(); throw; }
        finally
        {
            readDeadline = null;
            if (options.CollectReadTimings)
                Interlocked.Add(ref readTicks, Stopwatch.GetTimestamp() - started);
        }
    }
    private bool reachedInputEof;
    private bool reachedEndBoundary;
    private long? endPts;
    private bool prerollCompleted;

    public VideoPlaybackEngine(PlaybackOptions options, VideoFrameQueue outputQueue, bool enableDeinterlacing = false)
    {
        this.options = options;
        this.outputQueue = outputQueue;
        this.enableDeinterlacing = enableDeinterlacing;
    }

    public CancellationToken CancellationToken => cancellation.Token;


    /// <summary>时间线秒数换算为原始 PTS（时间线原点由导航预览提供，保证两边一致）。</summary>
    public long TimelineSecondsToPts(double seconds)
        => firstPts + (long)Math.Round(seconds * timeBase.den / (double)timeBase.num);

    public double PtsToTimelineSeconds(long pts)
        => (pts - firstPts) * timeBase.num / (double)timeBase.den;

    /// <summary>当前节目内解析出的音频流；无声频文件为 null。</summary>
    public MediaStream? AudioStream => audioStream;

    /// <summary>当前是否使用硬件解码（运行期回退后为 false）。</summary>
    public bool IsHardwareDecoding { get; private set; }

    internal long PlaybackFrameCount => Interlocked.Read(ref playbackFrameCount);
    internal long HardwareReadbackFrameCount => Interlocked.Read(ref hardwareReadbackFrameCount);
    internal long ReadTicks => Interlocked.Read(ref readTicks);
    internal long CatchUpCount => Interlocked.Read(ref catchUpCount);

    internal void RequestCatchUp(double seconds)
    {
        if (options.EndSeconds is { } end && end - seconds < options.EstimatedKeyFrameIntervalSeconds)
            return;
        var target = TimelineSecondsToPts(seconds);
        // 合并同一轮请求，避免慢速输入一直追逐不断前移的目标而无法产出画面。
        Interlocked.CompareExchange(ref catchUpTargetPts, target, long.MinValue);
    }
    internal double SourceFrameRate => videoStream.AvgFrameRate.num > 0 && videoStream.AvgFrameRate.den > 0
        ? videoStream.AvgFrameRate.num / (double)videoStream.AvgFrameRate.den * Volatile.Read(ref outputRateMultiplier) : 0;

    /// <summary>播放区间终点（原始 PTS）；null 表示播到文件尾。</summary>
    public double? AudioEndSeconds => options.EndSeconds is { } end
        ? end + FrameDurationSeconds : options.Disc?.Duration.TotalSeconds;

    private double FrameDurationSeconds => videoStream.AvgFrameRate.num > 0 && videoStream.AvgFrameRate.den > 0
        ? videoStream.AvgFrameRate.den / (double)videoStream.AvgFrameRate.num : 1.0 / 25;

    /// <summary>由会话注入音频分发目标；null 时音频包照旧丢弃（无声模式）。</summary>
    public void SetAudioPipeline(TSCutter.GUI.Player.Audio.AudioPlaybackPipeline? pipeline,
        Func<Task<TSCutter.GUI.Player.Audio.AudioPlaybackPipeline?>>? restartPreroll = null)
    {
        if (prerollCompleted)
            throw new InvalidOperationException("Audio pipeline must be attached before preroll.");
        audioPipeline = pipeline;
        restartAudioPreroll = restartPreroll;
    }

    // 音轨选择：优先视频流所在节目内的第一条音轨，其次文件内第一条音轨。
    // MediaProgram/MediaStream 都是结构体，避免 FirstOrDefault 产生的默认值参与解引用。
    private MediaStream? ResolveAudioStream()
    {
        var context = formatContext!;
        if (options.AudioSelection is { } selection)
        {
            var candidates = context.Streams.Where(stream => stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_AUDIO &&
                stream.Id == selection.StreamId && (context.InputFormat?.Name == "mpegts" || stream.Index == selection.StreamIndex) &&
                (selection.ProgramId is not { } id || context.Programs.Any(program => program.Id == id && program.ContainsStream(stream.Index)))).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }
        foreach (var program in context.Programs)
        {
            if (!program.ContainsStream(videoStreamIndex))
                continue;
            var inProgram = context.Streams
                .Where(stream => stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_AUDIO)
                .FirstOrDefault(stream => program.ContainsStream(stream.Index));
            if (inProgram.IsValid)
                return inProgram;
            return null; // 不能把其他节目的声音配到当前视频上。
        }
        return context.Streams
            .Where(stream => stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_AUDIO)
            .Cast<MediaStream?>()
            .FirstOrDefault();
    }

    /// <summary>帧的方形显示像素尺寸与是否需要采样宽高比校正。</summary>
    public (Avalonia.PixelSize PixelSize, bool RequiresCorrection) GetDisplayGeometry(Frame frame)
    {
        var sampleAspectRatio = frame.SampleAspectRatio;
        if (sampleAspectRatio.num <= 0 || sampleAspectRatio.den <= 0)
            sampleAspectRatio = videoStream.Codecpar?.SampleAspectRatio ?? default;
        if (sampleAspectRatio.num <= 0 || sampleAspectRatio.den <= 0)
            sampleAspectRatio = videoStream.SampleAspectRatio;
        var requiresCorrection = VideoInstance.RequiresSampleAspectRatioCorrection(
            sampleAspectRatio.num, sampleAspectRatio.den);
        return (
            VideoInstance.CalculateDisplayPixelSize(frame.Width, frame.Height,
                sampleAspectRatio.num, sampleAspectRatio.den),
            requiresCorrection);
    }

    // 部分解码器只在流参数中报告色彩信息，补齐帧上缺失的字段再传递给映射器。
    private unsafe void CompleteFrameColorMetadata(Frame frame)
    {
        AVFrame* raw = frame;
        var parameters = videoStream.Codecpar!;
        if (raw->color_trc == AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED)
            raw->color_trc = parameters.ColorTrc;
        if (raw->color_primaries == AVColorPrimaries.AVCOL_PRI_UNSPECIFIED)
            raw->color_primaries = parameters.ColorPrimaries;
        if (raw->colorspace == AVColorSpace.AVCOL_SPC_UNSPECIFIED)
            raw->colorspace = parameters.ColorSpace;
        if (raw->color_range == AVColorRange.AVCOL_RANGE_UNSPECIFIED)
            raw->color_range = parameters.ColorRange;
    }

    /// <summary>
    /// 打开输入、视频解码器并解析音频流。会话需在预滚前完成此步骤，
    /// 才能在音频链路建立后开始分发音频包。
    /// </summary>
    public Task OpenAsync() => formatContext is null ? Task.Run(OpenCore, CancellationToken.None) : Task.CompletedTask;

    // 旧读包、解码任务退出后才能调用；保留输入和解码器，仅重置新位置的状态。
    internal void ResetForSeek(PlaybackOptions updatedOptions, VideoFrameQueue queue, CancellationToken token)
    {
        cancellation.Dispose();
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        options = updatedOptions;
        outputQueue = queue;
        videoDecoder!.FlushBuffers();
        ResetDeinterlacer();
        reusableDecodeFrame?.Unref();
        reusableSoftwareFrame?.Unref();
        reusableFilteredFrame?.Unref();
        endPts = options.EndSeconds is { } end ? TimelineSecondsToPts(end) : null;
        reachedInputEof = reachedEndBoundary = prerollCompleted = audioTailRead = false;
        waitingForKeyFrame = true;
        hardwareNoFrameCount = 0;
        lastEnqueuedPts = minimumOutputPts = catchUpTargetPts = long.MinValue;
        decodedFrameCount = playbackFrameCount = hardwareReadbackFrameCount = 0;
        readTicks = inputBytesRead = catchUpCount = 0;
        prerollSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void OpenCore()
    {
        OpenInput();
        OpenVideoDecoder();
    }

    /// <summary>预滚完成信号：会话据此建立主时钟并放行呈现线程。</summary>
    public Task PrerollCompletion => prerollSignal.Task;

    private TaskCompletionSource prerollSignal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// 解码主循环：先定位到开始锚点并预滚（目标之前的画面解码但不入队），
    /// 预滚完成后发出信号，再持续解码直到文件尾或取消。由会话在后台长期运行。
    /// </summary>
    public async Task RunAsync()
    {
        var token = cancellation.Token;
        var destination = reusableDecodeFrame ??= new Frame();
        var failures = 0;
        try
        {
            // 直接使用引擎（无会话）时在此自举打开；会话路径已在 OpenAsync 打开。
            if (formatContext is null)
                OpenCore();
            targetPts = TimelineSecondsToPts(options.StartSeconds);
            SeekToPrerollAnchor();
            await DecodeUntilTargetAsync(targetPts, token).ConfigureAwait(false);
            prerollCompleted = true;
            prerollSignal.TrySetResult();
        }
        catch (Exception exception)
        {
            // 预滚失败同样要放行等待方：异常经 PrerollCompletion 传播给会话。
            prerollSignal.TrySetException(exception is OperationCanceledException && token.IsCancellationRequested
                ? new OperationCanceledException(token)
                : exception);
            await StopPacketReaderAsync().ConfigureAwait(false);
            outputQueue.Complete();
            audioPipeline?.CompleteInput();
            throw;
        }
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (reachedEndBoundary)
                {
                    await ReadAudioTailAsync(token).ConfigureAwait(false);
                    break;
                }
                // 追赶期间先跳到新的关键帧；正常路径排空已有输出，避免 EAGAIN。
                if (Interlocked.Read(ref catchUpTargetPts) == long.MinValue &&
                    await ReceiveAndEnqueueAsync(destination, token).ConfigureAwait(false))
                    continue;

                if (reachedInputEof)
                {
                    // 文件尾：发送空包取出解码器内的延迟帧；全部取完后结束。
                    if (!await DecodeAndEnqueueAsync(null, destination, token).ConfigureAwait(false))
                    {
                        await DrainDeinterlacerAsync(token).ConfigureAwait(false);
                        break;
                    }
                    continue;
                }

                using var packet = await ReadNextPlaybackPacketAsync(token).ConfigureAwait(false);
                if (packet is null)
                {
                    reachedInputEof = true;
                    continue;
                }
                var previousCount = decodedFrameCount;
                bool produced;
                try { produced = await DecodeAndEnqueueAsync(packet, destination, token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is HardwareTransferException || hardwareDecoderOpened && exception is FFmpegException)
                {
                    await FallbackToSoftwareDecoderAsync(token).ConfigureAwait(false);
                    continue;
                }
                if (decodedFrameCount == previousCount && hardwareDecoderOpened && !reachedEndBoundary
                    && ++hardwareNoFrameCount >= HardwareNoFrameThreshold)
                {
                    Console.WriteLine("Hardware decoding produced no frames; falling back to software.");
                    if (await FallbackToSoftwareDecoderAsync(token).ConfigureAwait(false))
                        continue;
                }
                if (decodedFrameCount == previousCount && ++failures > MaxConsecutiveDecodeFailures)
                    throw new InvalidOperationException("连续过多视频包无法解码，播放已中止。");
                if (decodedFrameCount != previousCount)
                {
                    failures = 0;
                    hardwareNoFrameCount = 0;
                }
            }
        }
        catch (Exception exception) when (token.IsCancellationRequested || exception is OperationCanceledException)
        {
            // 会话停止引发的读取中断与取消不属于播放失败。
        }
        finally
        {
            await StopPacketReaderAsync().ConfigureAwait(false);
            outputQueue.Complete();
            audioPipeline?.CompleteInput();
        }
    }

    /// <summary>请求停止解码循环；由会话在关闭流程最开始调用。可重复调用。</summary>
    public void Cancel()
    {
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { /* 引擎已释放；无需再取消。 */ }
    }

    private void OpenInput()
    {
        var token = cancellation.Token;
        formatContext = ReadOperation(() => options.Disc is { } disc
            ? InterruptibleInputFormatContext.Open(disc, () => IsInterrupted)
            : InterruptibleInputFormatContext.Open(options.FilePath, () => IsInterrupted, options.TsSyncOffset));
        ReadOperation(() => { formatContext.LoadStreamInfo(); return true; });
        videoStream = PreviewStreamUtil.ResolveVideo(formatContext, options.StreamSelection);
        videoStreamIndex = videoStream.Index;
        timeBase = videoStream.TimeBase;
        if (timeBase.num <= 0 || timeBase.den <= 0)
            throw new InvalidOperationException("视频流缺少有效时间基，无法建立播放时间轴。");
        audioStream = ResolveAudioStream();
        audioStreamIndex = audioStream?.Index ?? -1;
        // 时间线原点来自导航预览（首关键帧包 PTS），播放与导航显示同一条时间轴。
        firstPts = (long)Math.Round(options.TimelineOriginSeconds * timeBase.den / (double)timeBase.num);
        keyFrameGapPts = Math.Max(1, (long)Math.Round(
            options.EstimatedKeyFrameIntervalSeconds * timeBase.den / (double)timeBase.num));
        if (options.EndSeconds is { } endSeconds)
            endPts = TimelineSecondsToPts(endSeconds);
    }

    private void OpenVideoDecoder()
    {
        if (options.PreferHardwareDecoding && TryOpenHardwareDecoder())
            return;
        OpenSoftwareDecoder();
    }

    private static readonly string[] HardwareDeviceTags =
        ["cuvid", "qsv", "vaapi", "dxva2", "d3d11va", "videotoolbox", "mediacodec", "nvdec", "amf"];

    // 与导航一致的平台硬件设备清单。
    private static readonly AVHWDeviceType[] HardwareDevices =
        OperatingSystem.IsMacOS()
            ? [AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX]
            : OperatingSystem.IsWindows()
                ? [AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2]
                : [];

    /// <summary>
    /// 优先硬件解码（VideoToolbox/D3D11VA）；打开成功不代表首帧一定输出，
    /// 运行期无帧或回读失败时回退软件解码（见 FallbackToSoftwareDecoder）。
    /// </summary>
    private bool TryOpenHardwareDecoder()
    {
        var codecId = videoStream.Codecpar?.CodecId;
        if (codecId is null || HardwareDevices.Length == 0)
            return false;
        // 与导航一致：给普通解码器附加硬件设备（videotoolbox/d3d11va 不是独立解码器名）。
        var decoders = Codec.FindDecoders(codecId.Value)
            .Where(decoder => HardwareDeviceTags.All(tag => !decoder.Name.Contains(tag, StringComparison.OrdinalIgnoreCase)))
            .Reverse()
            .ToList();
        foreach (var deviceType in HardwareDevices)
        {
            foreach (var decoder in decoders)
            {
                if (!decoder.SupportsHardwareDevice(deviceType))
                    continue;
                CodecContext? candidate = null;
                try
                {
                    candidate = new CodecContext(decoder);
                    candidate.FillParameters(videoStream.Codecpar!);
                    candidate.PacketTimeBase = timeBase;
                    // 播放需要全部画面：不设置 AVDISCARD_NONKEY。
                    candidate.AttachHardwareDevice(deviceType);
                    candidate.Open();
                    videoDecoder = candidate;
                    hardwareDecoderOpened = true;
                    hardwareNoFrameCount = 0;
                    return true;
                }
                catch (Exception)
                {
                    candidate?.Close();
                    candidate?.Dispose();
                }
            }
        }
        return false;
    }

    private void OpenSoftwareDecoder()
    {
        var codecId = videoStream.Codecpar?.CodecId
            ?? throw new InvalidOperationException("视频流缺少编码参数。");
        var candidates = Codec.FindDecoders(codecId)
            .Where(decoder => HardwareDeviceTags.All(tag => !decoder.Name.Contains(tag, StringComparison.OrdinalIgnoreCase)))
            .Reverse()
            .ToList();
        if (candidates.Count == 0)
            throw new VideoDecoderUnavailableException(codecId.ToString());

        foreach (var decoder in candidates)
        {
            CodecContext? candidate = null;
            try
            {
                candidate = new CodecContext(decoder);
                candidate.FillParameters(videoStream.Codecpar!);
                candidate.PacketTimeBase = timeBase;
                // 连续播放需要完整解码；FFmpeg 默认 thread_count=1 会让 AVS2 等退化为单线程。
                // 0 由解码器按机器能力选择线程数，不改变关键帧导航的低延迟设置。
                candidate.ThreadCount = 0;
                candidate.Open();
                videoDecoder = candidate;
                hardwareDecoderOpened = false;
                return;
            }
            catch (Exception)
            {
                candidate?.Close();
                candidate?.Dispose();
            }
        }
        throw new VideoDecoderUnavailableException(codecId.ToString());
    }

    /// <summary>
    /// 硬件解码运行期失效（回读失败或连续无输出——硬件设备打开成功不代表首帧
    /// 一定输出）：换软件解码器并从关键帧锚点重读。引用帧序列无法跨解码器
    /// 续接，必须重读 GOP；预滚期尚无入队帧时，从原始预滚目标重新预滚。
    /// </summary>
    private async Task<bool> FallbackToSoftwareDecoderAsync(CancellationToken token)
    {
        if (!hardwareDecoderOpened)
            return false;
        await StopPacketReaderAsync().ConfigureAwait(false);
        // 起播前重新 seek 时重建音频预滚，避免旧队列已满但设备尚未启动，
        // 让读包线程与视频首帧互相等待。播放中的回退仍保持音频和时钟连续。
        if (!prerollCompleted && lastEnqueuedPts == long.MinValue
            && audioPipeline is not null && restartAudioPreroll is { } restart)
        {
            audioPipeline = await restart().ConfigureAwait(false);
            audioTailRead = false;
        }
        hardwareDecoderOpened = false;
        hardwareNoFrameCount = 0;
        videoDecoder?.Close();
        videoDecoder?.Dispose();
        videoDecoder = null;
        OpenSoftwareDecoder();
        ResetDeinterlacer();
        // 预滚期没有任何入队帧（如硬件首帧初始化失败）时回到原始预滚目标；
        // 否则从最后一个入队帧恢复，目标帧之后的画面在预滚语义下照常入队。
        var resumeTarget = lastEnqueuedPts != long.MinValue ? lastEnqueuedPts + 1 : targetPts;
        targetPts = resumeTarget;
        ReadOperation(() =>
        {
            formatContext!.SeekFrame(PrerollAnchor(resumeTarget), videoStreamIndex, AVSEEK_FLAG.Backward);
            waitingForKeyFrame = true;
            return true;
        });
        reachedInputEof = false;
        await DecodeUntilTargetAsync(resumeTarget, token).ConfigureAwait(false);
        return true;
    }

    private void SeekToPrerollAnchor()
    {
        // 与导航不同的语义：总是从目标之前的关键帧锚点向后 seek，再顺序解码到目标，
        // 保证目标所在的 GOP 被完整解码（导航只需要关键帧，可以向前 seek）。
        ReadOperation(() =>
        {
            formatContext!.SeekFrame(PrerollAnchor(targetPts), videoStreamIndex, AVSEEK_FLAG.Backward);
            waitingForKeyFrame = true;
            return true;
        });
    }

    private long PrerollAnchor(long target)
    {
        // 有索引的容器已能定位到目标之前的关键帧，无需再回退两个 GOP 解码。
        if (options.Disc is null && formatContext!.InputFormat?.Name
            is "mov,mp4,m4a,3gp,3g2,mj2" or "matroska,webm" or "avi")
            return target;
        var minimumMargin = Math.Max(1, (long)(0.5 * timeBase.den / timeBase.num));
        var maximumMargin = Math.Max(minimumMargin, (long)(30.0 * timeBase.den / timeBase.num));
        var margin = Math.Clamp(keyFrameGapPts * 2, minimumMargin, maximumMargin);
        var anchor = target - margin;
        // MPEG-TS 在首 PTS 上定位可能落入下一个 GOP；仅片头预读才退到原点之前。
        var result = anchor <= firstPts ? firstPts - margin : anchor;
        return options.Disc is null ? result : Math.Max(0, result);
    }

    /// <summary>解码直到取出不早于 targetPts 的帧并入队；期间早于目标的帧直接丢弃。</summary>
    private async Task DecodeUntilTargetAsync(long target, CancellationToken token)
    {
        var destination = reusableDecodeFrame ??= new Frame();
        while (!token.IsCancellationRequested)
        {
            if (await ReceiveAndEnqueueAsync(destination, token, target).ConfigureAwait(false))
                return;
            using var packet = await ReadNextVideoPacketAsync(token).ConfigureAwait(false);
            if (packet is null)
            {
                // 输入结束仍可能缓存最后一帧；取不到目标帧时按无画面处理。
                var flushed = await DecodeAndEnqueueAsync(null, destination, token, target).ConfigureAwait(false);
                reachedInputEof = true;
                if (flushed)
                    return;
                prerollSignal.TrySetResult();
                return;
            }
            var previousCount = decodedFrameCount;
            bool produced;
            try { produced = await DecodeAndEnqueueAsync(packet, destination, token, target).ConfigureAwait(false); }
            catch (Exception exception) when (exception is HardwareTransferException || hardwareDecoderOpened && exception is FFmpegException)
            {
                await FallbackToSoftwareDecoderAsync(token).ConfigureAwait(false);
                return;
            }
            if (decodedFrameCount != previousCount)
                hardwareNoFrameCount = 0;
            if (produced || reachedEndBoundary)
                return;
            if (hardwareDecoderOpened && decodedFrameCount == previousCount && !reachedEndBoundary
                && ++hardwareNoFrameCount >= HardwareNoFrameThreshold)
            {
                if (await FallbackToSoftwareDecoderAsync(token).ConfigureAwait(false))
                    return;
            }
        }
        token.ThrowIfCancellationRequested();
    }

    private async Task<Packet?> ReadNextPlaybackPacketAsync(CancellationToken token)
    {
        while (true)
        {
            var packet = await ReadNextVideoPacketAsync(token).ConfigureAwait(false);
            if (packet is null)
            {
                Interlocked.Exchange(ref catchUpTargetPts, long.MinValue);
                return null;
            }
            var requested = Interlocked.Read(ref catchUpTargetPts);
            if (requested == long.MinValue)
                return packet;
            if (endPts is { } end && packet.Pts != FF.AV_NOPTS_VALUE && packet.Pts > end)
            {
                packet.Dispose();
                Interlocked.Exchange(ref catchUpTargetPts, long.MinValue);
                reachedEndBoundary = true;
                return null;
            }
            if ((packet.Flags & 1) == 0 || packet.Pts == FF.AV_NOPTS_VALUE || packet.Pts < requested)
            {
                packet.Dispose();
                continue;
            }
            // 只跳过视频解码；沿途音频仍照常分发，音频设备时钟保持连续。
            // 新关键帧重新建立参考链，旧队列与重排帧不再拖住后续播放。
            videoDecoder!.FlushBuffers();
            ResetDeinterlacer();
            minimumOutputPts = packet.Pts;
            Interlocked.Exchange(ref catchUpTargetPts, long.MinValue);
            Interlocked.Increment(ref catchUpCount);
            return packet;
        }
    }

    private async Task<Packet?> ReadNextVideoPacketAsync(CancellationToken token)
    {
        if (videoPackets is null)
        {
            videoPackets = Channel.CreateBounded<Packet>(new BoundedChannelOptions(VideoPacketCapacity)
            {
                SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
            });
            packetReadCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            packetReadTask = Task.Run(() => ReadPacketsAheadAsync(videoPackets.Writer, packetReadCancellation.Token));
        }
        if (!await videoPackets.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            return null;
        var packet = await videoPackets.Reader.ReadAsync(token).ConfigureAwait(false);
        Interlocked.Add(ref queuedVideoBytes, -PacketSize(packet));
        return packet;
    }

    private static unsafe int PacketSize(Packet packet) => Math.Max(0, ((AVPacket*)packet)->size);

    private static unsafe Packet ReferencePacket(Packet source)
    {
        var packet = new Packet();
        try
        {
            FFmpegException.Check(FF.av_packet_ref(packet, source));
            return packet;
        }
        catch
        {
            packet.Dispose();
            throw;
        }
    }

    private async Task ReadPacketsAheadAsync(ChannelWriter<Packet> writer, CancellationToken token)
    {
        Exception? failure = null;
        try
        {
            using var packets = formatContext!.ReadPackets().GetEnumerator();
            Func<bool> moveNext = packets.MoveNext;
            while (!token.IsCancellationRequested && ReadOperation(moveNext))
            {
                var packet = packets.Current;
                Interlocked.Add(ref inputBytesRead, Math.Max(1, PacketSize(packet)));
                if (packet.StreamIndex == videoStreamIndex)
                {
                    if (options.InputReadEndSeconds is { } limit && packet.Pts != FF.AV_NOPTS_VALUE &&
                        PtsToTimelineSeconds(packet.Pts) > limit)
                        break;
                    // TS seek 可能落在 GOP 中间，先等关键帧建立参考链；残缺 GOP
                    // 既无法正确显示，也不能作为硬解连续无输出的失败计数。
                    if (waitingForKeyFrame)
                    {
                        if ((packet.Flags & 1) == 0)
                            continue;
                        waitingForKeyFrame = false;
                    }
                    // 允许一个大包跨过字节上限，但后续读取必须等消费者释放空间。
                    while (Interlocked.Read(ref queuedVideoBytes) >= VideoPacketByteLimit)
                        await Task.Delay(2, token).ConfigureAwait(false);
                    var owned = ReferencePacket(packet);
                    var size = PacketSize(owned);
                    Interlocked.Add(ref queuedVideoBytes, size);
                    try { await writer.WriteAsync(owned, token).ConfigureAwait(false); }
                    catch
                    {
                        Interlocked.Add(ref queuedVideoBytes, -size);
                        owned.Dispose();
                        throw;
                    }
                }
                else if (!audioTailRead && audioPipeline is { } audio && packet.StreamIndex == audioStreamIndex)
                {
                    try
                    {
                        await audio.EnqueuePacketAsync(packet, token).ConfigureAwait(false);
                        if (AudioEndSeconds is { } end && packet.Pts != FF.AV_NOPTS_VALUE &&
                            packet.Pts * (double)audioStream!.Value.TimeBase.num / audioStream.Value.TimeBase.den
                                - options.TimelineOriginSeconds >= end)
                        {
                            audioTailRead = true;
                            audio.CompleteInput();
                        }
                    }
                    catch (ChannelClosedException)
                    {
                        // 音频到达终点或失败后仍允许视频继续读取。
                        audioTailRead = true;
                    }
                }
            }
        }
        catch (Exception) when (token.IsCancellationRequested) { }
        catch (Exception exception) { failure = exception; }
        finally
        {
            writer.TryComplete(failure);
            // 解码器回退会取消预读后重新 seek，不能提前关闭仍需使用的音频队列。
            if (!token.IsCancellationRequested)
                audioPipeline?.CompleteInput();
        }
    }

    private async Task StopPacketReaderAsync()
    {
        if (packetReadCancellation is not { } readerCancellation)
            return;
        readerCancellation.Cancel();
        if (packetReadTask is { } reader)
            await reader.ConfigureAwait(false);
        while (videoPackets!.Reader.TryRead(out var packet))
            packet.Dispose();
        videoPackets = null;
        packetReadTask = null;
        queuedVideoBytes = 0;
        packetReadCancellation = null;
        readerCancellation.Dispose();
    }

    private async Task ReadAudioTailAsync(CancellationToken token)
    {
        if (audioPipeline is null || AudioEndSeconds is null)
            return;
        // 释放尚未解码的视频包，让预读线程继续找到尾部音频；最多再读取 64 MiB。
        var limit = Interlocked.Read(ref inputBytesRead) + 64L * 1024 * 1024;
        while (!audioTailRead && Interlocked.Read(ref inputBytesRead) < limit)
        {
            using var packet = await ReadNextVideoPacketAsync(token).ConfigureAwait(false);
            if (packet is null)
                break;
        }
    }

    /// <summary>接收解码器已就绪的帧并入队；返回是否有帧产出。</summary>
    private async Task<bool> ReceiveAndEnqueueAsync(Frame destination, CancellationToken token, long? prerollTarget = null)
    {
        var produced = false;
        try
        {
            foreach (var frame in videoDecoder!.ReceiveFrames(destination))
            {
                token.ThrowIfCancellationRequested();
                if (await EnqueueDecodedFrameAsync(frame, token, prerollTarget).ConfigureAwait(false))
                    produced = true;
            }
        }
        catch (Exception exception) when (exception is HardwareTransferException || hardwareDecoderOpened && exception is FFmpegException)
        {
            await FallbackToSoftwareDecoderAsync(token).ConfigureAwait(false);
            return true;
        }
        return produced;
    }

    private async Task<bool> DecodeAndEnqueueAsync(Packet? packet, Frame destination, CancellationToken token,
        long? prerollTarget = null)
    {
        var produced = false;
        foreach (var frame in videoDecoder!.DecodePacket(packet, destination, preserveRemainingOutput: true))
        {
            token.ThrowIfCancellationRequested();
            if (await EnqueueDecodedFrameAsync(frame, token, prerollTarget).ConfigureAwait(false))
                produced = true;
        }
        return produced;
    }

    /// <summary>硬件解码连续无输出的包数上限；超过即回退软件解码。</summary>
    private const int HardwareNoFrameThreshold = 30;

    /// <summary>返回该帧是否真正入队（预滚参考帧与边界后的帧不计入）。</summary>
    private async Task<bool> EnqueueDecodedFrameAsync(Frame frame, CancellationToken token, long? prerollTarget)
    {
        decodedFrameCount++;
        if (!enableDeinterlacing || deinterlacer is null && (frame.Flags & FF.AV_FRAME_FLAG_INTERLACED) == 0)
            return await EnqueuePresentationFrameAsync(frame, token, prerollTarget).ConfigureAwait(false);

        if (frame.BestEffortTimestamp == FF.AV_NOPTS_VALUE && frame.Pts == FF.AV_NOPTS_VALUE)
            return false;
        CompleteFrameColorMetadata(frame);
        var software = GetSoftwareFrame(frame);
        IsHardwareDecoding = frame.HasHardwareFramesContext;
        if (deinterlacer is not null && !deinterlacer.Matches(software))
        {
            await DrainDeinterlacerAsync(token, prerollTarget).ConfigureAwait(false);
            ResetDeinterlacer();
        }
        deinterlacer ??= new VideoDeinterlacer(software, timeBase);
        Volatile.Write(ref outputRateMultiplier, (frame.Flags & FF.AV_FRAME_FLAG_INTERLACED) != 0 ? 2 : 1);
        deinterlacer.Push(software);
        return await ReceiveFilteredFramesAsync(token, prerollTarget).ConfigureAwait(false);
    }

    private async Task<bool> ReceiveFilteredFramesAsync(CancellationToken token, long? prerollTarget)
    {
        var destination = reusableFilteredFrame ??= new Frame();
        var produced = false;
        while (deinterlacer!.TryReceive(destination, out var presentationPts, out var sourcePts))
        {
            if (await EnqueuePresentationFrameAsync(destination, token, prerollTarget, presentationPts, sourcePts).ConfigureAwait(false))
                produced = true;
        }
        return produced;
    }

    private async Task DrainDeinterlacerAsync(CancellationToken token, long? prerollTarget = null)
    {
        if (deinterlacer is null)
            return;
        deinterlacer.Complete();
        await ReceiveFilteredFramesAsync(token, prerollTarget).ConfigureAwait(false);
    }

    private void ResetDeinterlacer()
    {
        deinterlacer?.Dispose();
        deinterlacer = null;
        Volatile.Write(ref outputRateMultiplier, 1);
    }

    private Frame GetSoftwareFrame(Frame frame)
    {
        if (!frame.HasHardwareFramesContext)
            return frame;
        try
        {
            reusableSoftwareFrame ??= new Frame();
            frame.TransferToSoftwareFrame(reusableSoftwareFrame);
            Interlocked.Increment(ref hardwareReadbackFrameCount);
            return reusableSoftwareFrame;
        }
        catch (Exception exception) when (hardwareDecoderOpened && exception is not OperationCanceledException)
        {
            Console.WriteLine($"Hardware frame transfer failed; falling back to software: {exception.Message}");
            // 枚举器完全退出后再释放旧解码器，避免原生 receive_frame 使用已释放的指针。
            throw new HardwareTransferException(exception);
        }
    }

    private async Task<bool> EnqueuePresentationFrameAsync(Frame frame, CancellationToken token, long? prerollTarget,
        long? presentationPts = null, long? sourcePts = null)
    {
        var pts = presentationPts ?? frame.BestEffortTimestamp;
        if (pts == FF.AV_NOPTS_VALUE)
            pts = frame.Pts;
        if (pts == FF.AV_NOPTS_VALUE)
            return false; // 无时间戳的帧无法参与调度，跳过。
        if (reachedEndBoundary)
            return false; // 边界触发后同批枚举可能还有输出，不再入队。
        var timelineSeconds = PtsToTimelineSeconds(pts);
        if (pts < minimumOutputPts || prerollTarget is { } target && pts < target)
            return false; // 预滚参考帧：解码但不呈现。
        if (options.Disc is { } disc && timelineSeconds >= disc.Duration.TotalSeconds)
            reachedEndBoundary = true;
        if (reachedEndBoundary || endPts is { } boundary && (sourcePts ?? pts) > boundary)
        {
            // 到达播放区间终点：不再入队后续帧，由队列完成信号触发结束状态。
            reachedEndBoundary = true;
            prerollSignal.TrySetResult();
            return false;
        }
        CompleteFrameColorMetadata(frame);
        // 兼容的逐行 VideoToolbox 帧直接排队，由渲染线程借用 IOSurface；其余情况保留回读路径。
        var hardwareSurface = options.EnableGpuPresentation && options.EnableHardwareSurfacePresentation
            && MacOSVideoTexture.CanImport(frame);
        var presentationSource = hardwareSurface ? frame : GetSoftwareFrame(frame);
        // 滤镜后的帧来自系统内存，仍保留本轮解码方式的诊断状态。
        if (presentationPts is null)
            IsHardwareDecoding = frame.HasHardwareFramesContext;
        var range = VideoInstance.GetVideoDynamicRange(frame);
        var mode = hardwareSurface ? VideoPresentationMode.HardwareGpu : IsHardwareDecoding
            ? VideoPresentationMode.HardwareCpuTransfer : VideoPresentationMode.SoftwareBitmap;
        var (pixelSize, requiresCorrection) = GetDisplayGeometry(frame);
        var item = QueuedVideoFrame.FromDecoded(presentationSource, timelineSeconds, sourcePts ?? pts,
            range, mode, pixelSize, requiresCorrection,
            (long)Math.Round((sourcePts ?? pts) * (double)timeBase.num * 90_000 / timeBase.den), frame.PacketPosition);
        try
        {
            await outputQueue.EnqueueAsync(item, token).ConfigureAwait(false);
            Interlocked.Increment(ref playbackFrameCount);
            lastEnqueuedPts = sourcePts ?? pts;
            prerollSignal.TrySetResult();
            return true;
        }
        catch
        {
            item.Dispose();
            throw;
        }
    }

    private sealed class HardwareTransferException(Exception inner) : Exception("Hardware frame transfer failed.", inner);

    public async ValueTask DisposeAsync()
    {
        cancellation.Cancel();
        outputQueue.Complete();
        await StopPacketReaderAsync().ConfigureAwait(false);
        videoDecoder?.Close();
        videoDecoder?.Dispose();
        videoDecoder = null;
        formatContext?.Close();
        formatContext?.Dispose();
        formatContext = null;
        reusableDecodeFrame?.Dispose();
        reusableDecodeFrame = null;
        reusableSoftwareFrame?.Dispose();
        reusableSoftwareFrame = null;
        ResetDeinterlacer();
        reusableFilteredFrame?.Dispose();
        reusableFilteredFrame = null;
        cancellation.Dispose();
    }
}
