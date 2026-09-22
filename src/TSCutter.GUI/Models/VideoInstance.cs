using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Toolboxs.Extensions;
using Sdcb.FFmpeg.Utils;
using TSCutter.GUI.Extensions;
using TSCutter.GUI.Rendering;
using TSCutter.GUI.Services;
using TSCutter.GUI.Utils;
using static TSCutter.GUI.Utils.CommonUtil;

namespace TSCutter.GUI.Models;

public class VideoInstance(string filePath, bool enableHardwareDecoding = false) : IDisposable
{
    private static readonly HashSet<string> HardwareTags = new() 
    { 
        "cuvid", "qsv", "vaapi", "dxva2", "d3d11va", "videotoolbox", "mediacodec", "nvdec", "amf" 
    };

    private const int MAX_FAILURE_COUT = 100;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeyFrameProbeTimeout = TimeSpan.FromSeconds(5);
    private MediaReadDeadline? activeReadDeadline;
    private const int HardwareNoFrameFailureThreshold = 3;
    private const int MaximumEstimatedKeyFrames = 100_000;
    private const int AV_PKT_FLAG_KEY_FRAME = 0x0001;
    private static readonly AVHWDeviceType[] MacHardwareDevices = [AVHWDeviceType.Videotoolbox];
    private static readonly AVHWDeviceType[] WindowsHardwareDevices =
        [AVHWDeviceType.D3d11va, AVHWDeviceType.Dxva2];
    private static readonly AVHWDeviceType[] NoHardwareDevices = [];

    public long PositionInFile { get; private set; } = 0;
    public long CurrentPts => currentKeyFramePts;
    public double EstimatedKeyFrameIntervalSeconds => keyFrameGap > 0 && timeBase.Den != 0
        ? keyFrameGap * timeBase.Num / (double)timeBase.Den
        : 0;
    public bool Inited { get; private set; } = false;
    public bool IsHardwareDecoding { get; private set; }
    public bool IsGpuPresentation { get; private set; }
    public VideoPresentationMode PresentationMode { get; private set; } = VideoPresentationMode.SoftwareBitmap;
    private string hardwareDecoderName = string.Empty;
    private bool AudioMode { get; set; } = false;
    private bool hardwareDecoderOpened;
    private readonly bool preferHardwareDecoding = enableHardwareDecoding && AppConfig.IsHardwareDecodingSupported;
    private readonly IGpuFramePresenter gpuFramePresenter = GpuFramePresenterFactory.CreateDefault();
    private readonly HostFramePresenter hostFramePresenter = new();
    private Frame? reusableSoftwareFrame;
    private List<Codec> softwareDecoders = [];
    
    private FormatContext inFc;
    private CodecContext videoDecoder;
    private MediaStream inVideoStream;
    private int videoStreamIndex = 0;
    private AVRational timeBase;
    private long firstFrameTimestamp = -1;
    private long maxPts;
    private long currentKeyFramePts;
    private long currentKeyFramePositionInFile;
    private long keyFrameGap;
    private long lastSeekPts;
    private double timelineDurationSeconds;
    private long timelineDurationPts;
    
    private readonly string videoPath = filePath;

    public async Task InitVideoAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() => InitVideo(cancellationToken), cancellationToken);
    }

    public void InitVideo() => InitVideo(CancellationToken.None);

    public void InitVideo(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TsScramblingProbe.HasScrambledPayload(videoPath))
            throw new ScrambledTsException();

        RunReadOperation(() =>
        {
            inFc = InterruptibleInputFormatContext.Open(videoPath,
                () => activeReadDeadline?.ShouldInterrupt == true);
            inFc.LoadStreamInfo();
            activeReadDeadline!.ThrowIfInterrupted();
            return true;
        }, cancellationToken);

        if (!inFc.Streams.Any(stream => stream.Codecpar?.CodecType == AVMediaType.Video))
            throw new NoVideoStreamException();

        inVideoStream = inFc.GetVideoStream();
        if (inVideoStream.Codecpar?.CodecId is null)
        {
            throw new Exception("Read Failed!");
        }

        softwareDecoders = Codec.FindDecoders(inVideoStream.Codecpar!.CodecId)
            .Where(x =>
            {
                var name = x.Name;
                // 排除名称中有硬件标识符的解码器
                return HardwareTags.All(tag => !name.Contains(tag, StringComparison.OrdinalIgnoreCase));
            })
            .ToList();
        if (softwareDecoders.Count == 0)
        {
            throw new Exception("Cant find decoder!");
        }

        foreach (var decoder in softwareDecoders)
        {
            Console.WriteLine($"Found decoder: {decoder.Name}");
        }
        
        videoStreamIndex = inVideoStream.Index;
        timeBase = inVideoStream.TimeBase;
        UpdateTimelineDuration();

        var decoderOpened = preferHardwareDecoding && TryOpenHardwareDecoder();
        if (!decoderOpened)
            decoderOpened = TryOpenSoftwareDecoder();

        if (!decoderOpened)
            throw new Exception("Cant open decoder!");

        Console.WriteLine($"GPU presentation: {(gpuFramePresenter.Capabilities.IsAvailable ? "available" : "fallback to bitmap")} - "
            + gpuFramePresenter.Capabilities.UnavailableReason);

        var (firstPts, gap) = ReadKeyFramePacketPts(cancellationToken);
        firstFrameTimestamp = firstPts;
        maxPts = timelineDurationPts + firstFrameTimestamp;
        keyFrameGap = gap;
        Console.WriteLine($"keyFrameGap: {keyFrameGap}");
        RunReadOperation(() => { Seek(firstFrameTimestamp); return true; }, cancellationToken);
        
        Inited = true;
    }

    private bool TryOpenHardwareDecoder()
    {
        foreach (var deviceType in GetHardwareDeviceCandidates())
        {
            foreach (var decoder in softwareDecoders.AsEnumerable().Reverse())
            {
                if (!decoder.SupportsHardwareDevice(deviceType))
                    continue;

                CodecContext? candidate = null;
                try
                {
                    candidate = new CodecContext(decoder);
                    candidate.FillParameters(inVideoStream.Codecpar!);
                    candidate.SkipFrame = AVDiscard.Nonkey;
                    candidate.AttachHardwareDevice(deviceType);
                    candidate.Open();
                    ReplaceVideoDecoder(candidate);
                    candidate = null;

                    hardwareDecoderOpened = true;
                    IsHardwareDecoding = true;
                    IsGpuPresentation = false;
                    PresentationMode = VideoPresentationMode.HardwareCpuTransfer;
                    hardwareDecoderName = GetHardwareDeviceDisplayName(deviceType);
                    Console.WriteLine($"Hardware decoder opened: {decoder.Name} ({hardwareDecoderName})");
                    return true;
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"Failed to open {decoder.Name} with {deviceType}: {exception.Message}");
                }
                finally
                {
                    candidate?.Close();
                    candidate?.Dispose();
                }
            }
        }

        Console.WriteLine("No supported hardware decoder was available; falling back to software decoding.");
        return false;
    }

    private bool TryOpenSoftwareDecoder()
    {
        foreach (var decoder in softwareDecoders.AsEnumerable().Reverse())
        {
            CodecContext? candidate = null;
            try
            {
                candidate = new CodecContext(decoder);
                candidate.FillParameters(inVideoStream.Codecpar!);
                candidate.SkipFrame = AVDiscard.Nonkey;
                candidate.Open();
                ReplaceVideoDecoder(candidate);
                candidate = null;

                hardwareDecoderOpened = false;
                IsHardwareDecoding = false;
                IsGpuPresentation = false;
                PresentationMode = VideoPresentationMode.SoftwareBitmap;
                hardwareDecoderName = string.Empty;
                Console.WriteLine($"Software decoder opened: {decoder.Name}");
                return true;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Failed to open software decoder {decoder.Name}: {exception.Message}");
            }
            finally
            {
                candidate?.Close();
                candidate?.Dispose();
            }
        }

        return false;
    }

    private void ReplaceVideoDecoder(CodecContext decoder)
    {
        videoDecoder?.Close();
        videoDecoder?.Dispose();
        videoDecoder = decoder;
    }

    private static IReadOnlyList<AVHWDeviceType> GetHardwareDeviceCandidates()
    {
        if (OperatingSystem.IsMacOS())
            return MacHardwareDevices;
        if (OperatingSystem.IsWindows())
            return WindowsHardwareDevices;
        return NoHardwareDevices;
    }

    private static string GetHardwareDeviceDisplayName(AVHWDeviceType deviceType) => deviceType switch
    {
        AVHWDeviceType.Videotoolbox => "VideoToolbox",
        AVHWDeviceType.D3d11va => "D3D11VA",
        AVHWDeviceType.Dxva2 => "DXVA2",
        _ => deviceType.ToString()
    };

    private void InitAudio(FormatContext inFc)
    {
        inVideoStream = inFc.GetAudioStream();
        if (inVideoStream.Codecpar?.CodecId is null)
        {
            throw new Exception("Read Failed!");
        }

        var decoders = Codec.FindDecoders(inVideoStream.Codecpar!.CodecId).ToList();
        if (decoders.Count == 0)
        {
            throw new Exception("Cant find decoder!");
        }

        videoStreamIndex = inVideoStream.Index;
        timeBase = inVideoStream.TimeBase;
        UpdateTimelineDuration();

        var firstDecoder = decoders.First();
        var audioDecoder = new CodecContext(Codec.FindDecoderById(firstDecoder.Id));
        audioDecoder.FillParameters(inVideoStream.Codecpar!);
        audioDecoder.Open();
        ReplaceVideoDecoder(audioDecoder);

        keyFrameGap = 90000;
        AudioMode = true;
        hardwareDecoderOpened = false;
        IsHardwareDecoding = false;
        hardwareDecoderName = string.Empty;
    }
    
    public async Task SeekToTimeAsync(TimeSpan timeSpan)
    {
        await Task.Run(() => SeekToTime(timeSpan));
    }
    
    public async Task SeekFileAsync(long pts)
    {
        await Task.Run(() => SeekFile(pts));
    }

    public Task<DecodeResult> DecodeAtTimeAsync(
        TimeSpan timeSpan,
        int maxWidth = 0,
        int maxHeight = 0,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => RunReadOperation(() =>
        {
            SeekToTime(timeSpan);
            return DecodeNextFrame(1, cancellationToken, maxWidth, maxHeight);
        }, cancellationToken), cancellationToken);
    }

    public void SeekToTime(TimeSpan timeSpan)
    {
        var targetTimestamp = TimeSpanToPts(timeSpan);
        targetTimestamp = Math.Min(maxPts, targetTimestamp);
        Seek(targetTimestamp);
    }

    public void SeekFile(long pts)
    {
        // if (lastSeekPts == pts)
        //     return;
        Console.WriteLine($"SeekFile lastSeekPts: {lastSeekPts}, targetPts: {pts}");
        lastSeekPts = pts;
        RunReadOperation(() =>
        {
            inFc.SeekFrame(pts - keyFrameGap * 4, videoStreamIndex);
            activeReadDeadline!.ThrowIfInterrupted();
            return true;
        }, CancellationToken.None);
        // flush
        videoDecoder.FlushBuffers();
    }

    public void Seek(long pts, AVSEEK_FLAG flag = 0)
    {
        // if (lastSeekPts == pts)
        //     return;
        Console.WriteLine($"lastSeekPts: {lastSeekPts}, targetPts: {pts}, flag: {flag}");
        lastSeekPts = pts;
        RunReadOperation(() =>
        {
            inFc.SeekFrame(pts, videoStreamIndex, flag);
            activeReadDeadline!.ThrowIfInterrupted();
            return true;
        }, CancellationToken.None);
        // flush
        videoDecoder.FlushBuffers();
    }

    public async Task<DecodeResult> DecodeNextFrameAsync(int count = 1, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() => RunReadOperation(
            () => DecodeNextFrame(count, cancellationToken, 0, 0), cancellationToken), cancellationToken);
    }

    private DecodeResult DecodeNextFrame(
        int count,
        CancellationToken cancellationToken,
        int maxWidth,
        int maxHeight)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var anchorPts = currentKeyFramePts;
        try
        {
            return DecodeNextFrame(
                count,
                anchorPts,
                true,
                0,
                cancellationToken,
                maxWidth,
                maxHeight);
        }
        catch (HardwareDecodeException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Console.WriteLine($"Hardware decoding failed, switching to software: {exception.InnerException?.Message ?? exception.Message}");
            if (!TryOpenSoftwareDecoder())
                throw;

            // 普通“下一帧”不会更新 lastSeekPts，回退时必须结合失败帧和当前锚点计算恢复位置。
            var fallbackSeekPts = ResolveHardwareFallbackSeekPts(
                count,
                anchorPts,
                lastSeekPts,
                exception.RetryPts);
            Seek(fallbackSeekPts, count < 0 ? AVSEEK_FLAG.Backward : 0);
            return DecodeNextFrame(
                count,
                anchorPts,
                applyInitialSeek: false,
                retryCount: 0,
                cancellationToken: cancellationToken,
                maxWidth: maxWidth,
                maxHeight: maxHeight,
                requireForwardAfterAnchor: count >= 0);
        }
    }

    private DecodeResult DecodeNextFrame(
        int count,
        long anchorPts,
        bool applyInitialSeek,
        int retryCount,
        CancellationToken cancellationToken,
        int maxWidth,
        int maxHeight,
        bool requireForwardAfterAnchor = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var failureCount = 0;
        var backward = count < 0;

        if (retryCount > MAX_FAILURE_COUT)
            ThrowDecodeFailure();
        
        if (applyInitialSeek && count < 0)
        {
            // Seek backward by keyframe gap * abs(count)
            var targetPts = Math.Max(0, currentKeyFramePts - Math.Abs(keyFrameGap) * (Math.Abs(count) + 1)) - 2;
            Seek(targetPts, AVSEEK_FLAG.Backward);
        }

        if (applyInitialSeek && count > 1)
        {
            // Seek forward by keyframe gap * abs(count)
            var targetPts = Math.Min(maxPts, currentKeyFramePts + Math.Abs(keyFrameGap) * Math.Abs(count)) + 2;
            Seek(targetPts);
        }

        foreach (var packet in inFc.ReadPackets())
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                activeReadDeadline?.ThrowIfInterrupted();
                if (packet.StreamIndex != videoStreamIndex || packet.Pts < 0)
                    continue;
                if ((packet.Flags & AV_PKT_FLAG_KEY_FRAME) == 0)
                {
                    // Console.WriteLine($"Skip[NonKey] packet: {packet.Pts}");
                    continue;
                }

                Console.WriteLine($"Current packet: {packet.Pts}");
                // PositionInFile = packet.Position;
                // Console.WriteLine($"Current packet positon: {packet.Position}");

                var result = DecodePacket(packet, packet.Position, cancellationToken, maxWidth, maxHeight);
                if (result != null)
                {
                    if (IsDecodedFrameAtRequestedSide(
                            backward,
                            requireForwardAfterAnchor,
                            currentKeyFramePts,
                            anchorPts))
                    {
                        return result;
                    }

                    Console.WriteLine($"Skip[SameOrLaterFrame] keyFrame: {currentKeyFramePts}, anchorPts: {anchorPts}");
                    result.BitmapLease?.Dispose();
                    if (result.BitmapLease is null)
                        result.Bitmap?.Dispose();
                    result.GpuFrame?.Dispose();
                    if (backward)
                        break;
                    continue;
                }
                failureCount++;
                // 硬解设备在首帧初始化失败时通常只返回空结果，不会抛出异常。
                // 连续几个关键包都没有帧即可判定硬解链路不可用，及时切换软件解码。
                if (failureCount > MAX_FAILURE_COUT
                    || (hardwareDecoderOpened && failureCount >= HardwareNoFrameFailureThreshold))
                    ThrowDecodeFailure();
                Console.WriteLine("result is null");
            }
            finally
            {
                // ReadPackets 会复用同一个原生 AVPacket。所有路径都必须
                // 在读取下一个包前释放当前负载，避免 native 内存持续增长。
                packet.Unref();
            }
        }

        // No suitable keyframe found, retry by seeking slightly earlier
        activeReadDeadline?.ThrowIfInterrupted();
        if (!AudioMode && lastSeekPts - 1000 < 0)
            throw new Exception("Decode Failed!");

        var retryStep = backward ? keyFrameGap : keyFrameGap / 2;
        Seek(lastSeekPts - retryStep, backward ? AVSEEK_FLAG.Backward : 0);
        return DecodeNextFrame(
            backward ? -1 : 1,
            anchorPts,
            applyInitialSeek: false,
            retryCount: retryCount + 1,
            cancellationToken: cancellationToken,
            maxWidth: maxWidth,
            maxHeight: maxHeight,
            requireForwardAfterAnchor: requireForwardAfterAnchor);
    }

    internal static long ResolveHardwareFallbackSeekPts(
        int count,
        long anchorPts,
        long lastSeekPts,
        long? failurePts)
    {
        if (count < 0)
            return failurePts ?? lastSeekPts;

        var firstPtsAfterAnchor = anchorPts == long.MaxValue ? long.MaxValue : anchorPts + 1;
        return Math.Max(firstPtsAfterAnchor, failurePts ?? lastSeekPts);
    }

    internal static bool IsDecodedFrameAtRequestedSide(
        bool backward,
        bool requireForwardAfterAnchor,
        long currentPts,
        long anchorPts)
    {
        return backward
            ? currentPts < anchorPts
            : !requireForwardAfterAnchor || currentPts > anchorPts;
    }

    private void ThrowDecodeFailure()
    {
        var exception = new TooManyDecodeFailuresException("Too many failed packets!");
        if (hardwareDecoderOpened)
            throw new HardwareDecodeException(exception);
        throw exception;
    }

    public double GetVideoDurationInSeconds()
    {
        return timelineDurationSeconds;
    }

    /// <summary>
    /// 创建轻量的、基于时间的总览索引。不扫描整个输入文件，只有 tile
    /// 进入可视范围时，才将对应时间点解析为实际关键帧。
    /// </summary>
    internal IReadOnlyList<KeyFrameIndexEntry> CreateEstimatedKeyFrameIndex()
    {
        if (AudioMode || timelineDurationSeconds <= 0)
            return [];

        var interval = EstimatedKeyFrameIntervalSeconds;
        if (!double.IsFinite(interval) || interval <= 0)
            interval = 1;

        var estimatedCount = Math.Ceiling(timelineDurationSeconds / interval);
        var count = estimatedCount >= MaximumEstimatedKeyFrames
            ? MaximumEstimatedKeyFrames
            : Math.Max(1, (int)estimatedCount);
        if (estimatedCount >= MaximumEstimatedKeyFrames)
        {
            count = MaximumEstimatedKeyFrames;
            interval = timelineDurationSeconds / count;
        }

        var entries = new List<KeyFrameIndexEntry>(count);
        for (var index = 0; index < count; index++)
        {
            var timestamp = Math.Min(timelineDurationSeconds, index * interval);
            var time = TimeSpan.FromSeconds(timestamp);
            entries.Add(new KeyFrameIndexEntry(
                index,
                TimeSpanToPts(time),
                time,
                FilePosition: -1));
        }

        return entries;
    }

    public string GetVideoInfoText()
    {
        var width = inVideoStream.Codecpar!.Width;
        var height = inVideoStream.Codecpar.Height;
        var fileSize = inFc.GetFileSize();
        return $"{inVideoStream.Codecpar.CodecId}, {width}x{height}, {FormatSeconds(timelineDurationSeconds)}, {FormatFileSize(fileSize)}";
    }

    private void UpdateTimelineDuration()
    {
        (timelineDurationSeconds, timelineDurationPts) = ResolveTimelineDuration(
            inVideoStream.Duration,
            timeBase.Num,
            timeBase.Den,
            inFc.Duration);
    }

    internal static (double Seconds, long StreamPts) ResolveTimelineDuration(
        long streamDuration,
        int timeBaseNumerator,
        int timeBaseDenominator,
        long containerDuration)
    {
        if (timeBaseNumerator <= 0 || timeBaseDenominator <= 0)
            return default;

        if (streamDuration > 0)
        {
            var seconds = streamDuration * timeBaseNumerator / (double)timeBaseDenominator;
            return (seconds, streamDuration);
        }

        if (containerDuration <= 0)
            return default;

        // 部分异常 TS 缺少视频流时长，此时使用 FFmpeg 已估算出的容器时长作为回退。
        var fallbackSeconds = containerDuration / (double)ffmpeg.AV_TIME_BASE;
        var fallbackPts = (long)Math.Round(
            fallbackSeconds * timeBaseDenominator / timeBaseNumerator,
            MidpointRounding.AwayFromZero);
        return (fallbackSeconds, fallbackPts);
    }

    /// <summary>
    /// 仅读取 packet 级别的 PTS 来计算关键帧间隔，不执行真正的帧解码。
    /// 用于 InitVideo 阶段快速估算 keyFrameGap。
    /// </summary>
    private (long firstPts, long gap) ReadKeyFramePacketPts(CancellationToken cancellationToken, int requiredKeyFrames = 3)
    {
        var keyFramePtsList = new List<long>();
        try
        {
            RunReadOperation(() =>
            {
                foreach (var packet in inFc.ReadPackets())
                {
                    try
                    {
                        activeReadDeadline!.ThrowIfInterrupted();
                        if (packet.StreamIndex != videoStreamIndex || packet.Pts < 0 ||
                            (packet.Flags & AV_PKT_FLAG_KEY_FRAME) == 0)
                            continue;

                        keyFramePtsList.Add(packet.Pts);
                        if (keyFramePtsList.Count >= requiredKeyFrames)
                            break;
                    }
                    finally
                    {
                        packet.Unref();
                    }
                }
                activeReadDeadline!.ThrowIfInterrupted();
                return true;
            }, cancellationToken, KeyFrameProbeTimeout);
        }
        catch (MediaReadTimeoutException)
        {
            // Sampling is optional; a long GOP must not switch the selected video to audio.
            Console.WriteLine("Keyframe sampling timed out; using the available timestamps.");
        }

        return ResolveKeyFrameTiming(keyFramePtsList, inVideoStream.StartTime, timeBase.Num, timeBase.Den);
    }

    internal static (long firstPts, long gap) ResolveKeyFrameTiming(
        IReadOnlyList<long> keyFramePts, long streamStartTime, int timeBaseNumerator, int timeBaseDenominator)
    {
        var firstPts = keyFramePts.Count > 0 ? keyFramePts[0] :
            streamStartTime == ffmpeg.AV_NOPTS_VALUE ? 0 : streamStartTime;
        var gap = keyFramePts.Count >= 2 ? Math.Abs(keyFramePts[^1] - keyFramePts[^2]) : 0;
        if (gap == 0)
            gap = timeBaseNumerator > 0 && timeBaseDenominator > 0
                ? Math.Max(1, (long)Math.Round(timeBaseDenominator / (double)timeBaseNumerator))
                : 1;
        return (firstPts, gap);
    }

    private unsafe T RunReadOperation<T>(Func<T> operation, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        if (activeReadDeadline is not null)
        {
            activeReadDeadline.ThrowIfInterrupted();
            return operation();
        }

        var deadline = new MediaReadDeadline(timeout ?? ReadTimeout, cancellationToken);
        activeReadDeadline = deadline;
        try
        {
            deadline.ThrowIfInterrupted();
            return operation();
        }
        catch
        {
            // Translate native AVERROR_EXIT (or EOF after interruption) to cancellation/timeout.
            deadline.ThrowIfInterrupted();
            throw;
        }
        finally
        {
            activeReadDeadline = null;
            if (deadline.ShouldInterrupt)
            {
                AVFormatContext* context = inFc;
                if (context != null && context->pb != null)
                {
                    // Interrupted I/O can retain EOF/error state; allow the next seek to recover.
                    context->pb->eof_reached = 0;
                    if (context->pb->error == ffmpeg.AVERROR_EXIT)
                        context->pb->error = 0;
                }
            }
        }
    }

    public void Close()
    {
        Inited = false;
        inFc?.Close();
        inFc?.Dispose();
        videoDecoder?.Close();
        videoDecoder?.Dispose();
        reusableSoftwareFrame?.Dispose();
        reusableSoftwareFrame = null;
        hostFramePresenter.Dispose();
    }

    public void Dispose()
    {
        Close();
        gpuFramePresenter.Dispose();
    }

    private long TimeSpanToPts(TimeSpan timeSpan)
    {
        var t = (double)timeBase.Den / timeBase.Num;
        return (long)(timeSpan.TotalSeconds * t) + firstFrameTimestamp;
    }

    private TimeSpan PtsToTimeSpan(long pts)
    {
        var t = (double)timeBase.Den / timeBase.Num;
        return TimeSpan.FromSeconds((pts - firstFrameTimestamp) / t);
    }
    
    private DecodeResult? DecodePacket(
        Packet packet,
        long packetPosition,
        CancellationToken cancellationToken,
        int maxWidth,
        int maxHeight)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using Frame destRef = new Frame();
            // 1 packet -> 0..N frame
            foreach (var frame in videoDecoder.DecodePacket(packet, destRef, unref: false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (firstFrameTimestamp == -1)
                {
                    firstFrameTimestamp = frame.BestEffortTimestamp;
                    maxPts = timelineDurationPts + firstFrameTimestamp;
                }

#pragma warning disable CS0618 // Obsolete
                // if (frame.KeyFrame == 0)
                //     continue;
                var pts = frame.Pts;
                if (!AudioMode && pts < 0)
                    pts = frame.BestEffortTimestamp;
                
                currentKeyFramePts = pts;
                Console.WriteLine($"Current keyFrame: {pts}");
                var pktPosition = frame.PktPosition;
                if (!AudioMode && pktPosition == -1)
                    pktPosition = packetPosition;
                
                PositionInFile = pktPosition;
                Console.WriteLine($"Current keyFrame PktPosition: {pktPosition}");
                if (AudioMode && pktPosition == -1)
                    continue;

                // 成功打开硬件解码器并不代表当前编码器一定输出了硬件帧。
                // VideoToolbox/D3D11 的部分失败只会在首帧初始化时才报告，
                // 此时解码器其实已经完成打开。
                var isHardwareFrame = !AudioMode && frame.HwFramesContext != null;
#pragma warning restore CS0618 // Obsolete

                // 优先让平台 presenter 直接使用原生 surface。只有 presenter
                // 明确无法处理时，才进入下面的 CPU 回读兜底路径。
                if (!AudioMode && frame.HwFramesContext != null)
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (gpuFramePresenter.TryPresent(frame, out var gpuFrame) && gpuFrame != null)
                        {
                            IsHardwareDecoding = true;
                            IsGpuPresentation = true;
                            PresentationMode = VideoPresentationMode.HardwareGpu;
                            return new DecodeResult
                            {
                                Bitmap = null,
                                GpuFrame = gpuFrame,
                                SourcePixelSize = new Avalonia.PixelSize(frame.Width, frame.Height),
                                FrameTimestamp = PtsToTimeSpan(pts),
                                PresentationMode = PresentationMode,
                            };
                        }
                    }
                    catch (Exception exception)
                    {
                        if (exception is OperationCanceledException)
                            throw;
                        Console.WriteLine($"GPU frame presentation unavailable; using CPU fallback: {exception.Message}");
                    }
                }

                // 硬件帧兜底：仅当原生 presenter 拒绝处理或不可用时，
                // 才将帧复制到系统内存。
                Frame? softwareFrame = null;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!AudioMode && frame.HwFramesContext != null)
                    {
                        reusableSoftwareFrame ??= new Frame();
                        frame.TransferToSoftwareFrame(reusableSoftwareFrame);
                        softwareFrame = reusableSoftwareFrame;
                    }
                }
                catch (Exception exception)
                {
                    if (exception is OperationCanceledException)
                        throw;
                    // 硬件帧无法回读通常表示设备链路失效，此时才立即切换软件解码器。
                    throw new HardwareDecodeException(exception, pts);
                }
                {
                    IBitmapFrameLease? bitmapLease = null;
                    IsHardwareDecoding = isHardwareFrame;
                    IsGpuPresentation = false;
                    PresentationMode = isHardwareFrame
                        ? VideoPresentationMode.HardwareCpuTransfer
                        : VideoPresentationMode.SoftwareBitmap;
                    Avalonia.Media.Imaging.Bitmap bitmap;
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (AudioMode)
                        {
                            bitmap = ImageUtil.BlankImage;
                        }
                        else
                        {
                            try
                            {
                                hostFramePresenter.TryConvert(
                                    softwareFrame ?? frame,
                                    maxWidth,
                                    maxHeight,
                                    out bitmapLease);
                            }
                            catch (Exception hostException)
                            {
                                Console.WriteLine($"Host bitmap reuse unavailable; using allocation fallback: {hostException.Message}");
                            }

                            if (bitmapLease is not null)
                            {
                                bitmap = bitmapLease.Bitmap;
                            }
                            else
                            {
                                bitmap = maxWidth > 0 && maxHeight > 0
                                    ? ImageUtil.CreateScaledBitmapFromFrame(
                                        softwareFrame ?? frame,
                                        maxWidth,
                                        maxHeight)
                                    : ImageUtil.CreateBitmapFromFrame(softwareFrame ?? frame);
                            }
                        }
                    }
                    catch (Exception exception)
                    {
                        bitmapLease?.Dispose();
                        if (exception is OperationCanceledException)
                            throw;
                        // 位图绘制属于界面链路，失败时不能据此判定硬件解码器不可用。
                        Console.WriteLine(exception);
                        return null;
                    }
                    return new DecodeResult()
                    {
                        Bitmap = bitmap,
                        BitmapLease = bitmapLease,
                        SourcePixelSize = AudioMode
                            ? bitmap.PixelSize
                            : new Avalonia.PixelSize(frame.Width, frame.Height),
                        FrameTimestamp = PtsToTimeSpan(pts),
                        PresentationMode = PresentationMode,
                    };
                }
            }
        }
        catch (HardwareDecodeException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            if (hardwareDecoderOpened)
                throw new HardwareDecodeException(e, currentKeyFramePts);
            // 局部码流错误在硬解和软解下都可能出现，继续尝试后续关键帧。
            return null;
        }
        
        // If no frames were successfully processed
        Console.WriteLine("no frames were successfully processed");
        return null;
    }

    private sealed class HardwareDecodeException(Exception innerException, long? retryPts = null)
        : Exception("Hardware decoding failed.", innerException)
    {
        public long? RetryPts { get; } = retryPts;
    }
}
