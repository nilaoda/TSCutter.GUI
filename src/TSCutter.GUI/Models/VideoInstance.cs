using TSCutter.GUI.FFmpeg;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using FFmpeg.AutoGen.Abstractions;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;
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
    private const int FastPathNoFrameThreshold = 8;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeyFrameProbeTimeout = TimeSpan.FromSeconds(5);
    private MediaReadDeadline? activeReadDeadline;
    private const int HardwareNoFrameFailureThreshold = 3;
    private const int MaximumEstimatedKeyFrames = 100_000;
    private const int AV_PKT_FLAG_KEY_FRAME = 0x0001;
    private static readonly AVHWDeviceType[] MacHardwareDevices = [AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX];
    private static readonly AVHWDeviceType[] WindowsHardwareDevices =
        [AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2];
    private static readonly AVHWDeviceType[] NoHardwareDevices = [];

    public long PositionInFile { get; private set; } = -1;
    public long CurrentPts => currentKeyFramePts;
    public double EstimatedKeyFrameIntervalSeconds => keyFrameGap > 0 && timeBase.den != 0
        ? keyFrameGap * timeBase.num / (double)timeBase.den
        : 0;
    public bool Inited { get; private set; } = false;
    public bool CanBinaryClip { get; private set; }
    internal long TsSyncOffset { get; private set; } = -1;
    public bool IsHardwareDecoding { get; private set; }
    public bool IsGpuPresentation { get; private set; }
    public VideoPresentationMode PresentationMode { get; private set; } = VideoPresentationMode.SoftwareBitmap;
    private string hardwareDecoderName = string.Empty;
    private bool AudioMode { get; set; } = false;
    private bool hardwareDecoderOpened;
    private bool useFullPacketDecoding;
    private readonly bool preferHardwareDecoding = enableHardwareDecoding && AppConfig.IsHardwareDecodingSupported;
    private readonly IGpuFramePresenter gpuFramePresenter = GpuFramePresenterFactory.CreateDefault();
    private readonly HostFramePresenter hostFramePresenter = new();
    private Frame? reusableSoftwareFrame;
    private Frame? reusableDecodeFrame;
    private (long Position, long Pts, PacketEndSignature Signature)[] keyPacketBoundaries = [];
    private int keyPacketBoundaryCount;
    private int nextKeyPacketBoundary;
    private List<Codec> softwareDecoders = [];
    
    private FormatContext inFc;
    private CodecContext videoDecoder;
    private MediaStream inVideoStream;
    private int videoStreamIndex = 0;
    private AVRational timeBase;
    private long firstFrameTimestamp = -1;
    private long maxPts;
    private long currentKeyFramePts;
    private long keyFrameGap;
    private long lastSeekPts;
    private double timelineDurationSeconds;
    private long timelineDurationPts;
    private bool isSearchScan;
    
    private readonly string videoPath = filePath;

    public async Task InitVideoAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() => InitVideo(cancellationToken), cancellationToken);
    }

    public void InitVideo() => InitVideo(CancellationToken.None);

    public void InitVideo(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var probedBeforeOpening = TsScramblingProbe.ShouldProbeBeforeOpening(videoPath);
        var tsProbe = probedBeforeOpening ? TsScramblingProbe.Probe(videoPath, cancellationToken) : default;
        if (tsProbe.HasScrambledPayload)
            throw new ScrambledTsException();

        void OpenInput(long syncOffset = -1)
        {
            RunReadOperation(() =>
            {
                inFc = InterruptibleInputFormatContext.Open(videoPath,
                    () => activeReadDeadline?.ShouldInterrupt == true, syncOffset);
                inFc.LoadStreamInfo();
                activeReadDeadline!.ThrowIfInterrupted();
                return true;
            }, cancellationToken);

            if (!inFc.Streams.Any(stream => stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_VIDEO))
                throw new NoVideoStreamException();
        }

        try
        {
            OpenInput(tsProbe.Is188ByteTransportStream && tsProbe.SyncOffset > 0 ? tsProbe.SyncOffset : -1);
        }
        catch (Exception exception) when (!probedBeforeOpening &&
            exception is FFmpegException or NoVideoStreamException)
        {
            // 普通容器不额外扫描。自动探测失败时才检查其他后缀的损坏 TS。
            tsProbe = TsScramblingProbe.Probe(videoPath, cancellationToken);
            probedBeforeOpening = true;
            if (!tsProbe.Is188ByteTransportStream)
                throw;
            if (tsProbe.HasScrambledPayload)
                throw new ScrambledTsException();
            inFc?.Dispose();
            OpenInput(tsProbe.SyncOffset);
        }

        var isMpegTs = inFc.InputFormat?.Name == "mpegts";
        // 其他后缀在解复用器确认 TS 后再探测，避免误扫普通容器中的 TS 相似数据。
        if (isMpegTs && !probedBeforeOpening)
            tsProbe = TsScramblingProbe.Probe(videoPath, cancellationToken);
        if (isMpegTs && tsProbe.HasScrambledPayload)
            throw new ScrambledTsException();
        CanBinaryClip = isMpegTs && tsProbe.Is188ByteTransportStream;
        TsSyncOffset = CanBinaryClip ? tsProbe.SyncOffset : -1;
        // 仅可剪辑的 TS 预览需要压缩包摘要；其他格式不分配缓存、不计算摘要。
        if (CanBinaryClip)
            keyPacketBoundaries = new (long, long, PacketEndSignature)[16];
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

    private bool TryOpenHardwareDecoder(bool fullPacketDecoding = false)
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
                    candidate.PacketTimeBase = inVideoStream.TimeBase;
                    if (!fullPacketDecoding)
                        candidate.SkipFrame = AVDiscard.AVDISCARD_NONKEY;
                    candidate.AttachHardwareDevice(deviceType);
                    candidate.Open();
                    ReplaceVideoDecoder(candidate);
                    candidate = null;

                    hardwareDecoderOpened = true;
                    useFullPacketDecoding = fullPacketDecoding;
                    IsHardwareDecoding = true;
                    IsGpuPresentation = false;
                    PresentationMode = VideoPresentationMode.HardwareCpuTransfer;
                    hardwareDecoderName = GetHardwareDeviceDisplayName(deviceType);
                    Console.WriteLine($"Hardware decoder opened: {decoder.Name} ({hardwareDecoderName}, "
                        + (fullPacketDecoding ? "full packets" : "key packets") + ")");
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

    private bool TryOpenSoftwareDecoder(bool fullPacketDecoding = false)
    {
        foreach (var decoder in softwareDecoders.AsEnumerable().Reverse())
        {
            CodecContext? candidate = null;
            try
            {
                candidate = new CodecContext(decoder);
                candidate.FillParameters(inVideoStream.Codecpar!);
                candidate.PacketTimeBase = inVideoStream.TimeBase;
                if (!fullPacketDecoding)
                    candidate.SkipFrame = AVDiscard.AVDISCARD_NONKEY;
                candidate.Open();
                ReplaceVideoDecoder(candidate);
                candidate = null;

                hardwareDecoderOpened = false;
                useFullPacketDecoding = fullPacketDecoding;
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
        AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX => "VideoToolbox",
        AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA => "D3D11VA",
        AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 => "DXVA2",
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
        audioDecoder.PacketTimeBase = inVideoStream.TimeBase;
        audioDecoder.Open();
        ReplaceVideoDecoder(audioDecoder);

        keyFrameGap = 90000;
        AudioMode = true;
        hardwareDecoderOpened = false;
        IsHardwareDecoding = false;
        hardwareDecoderName = string.Empty;
    }
    
    public async Task SeekToTimeAsync(TimeSpan timeSpan, CancellationToken cancellationToken = default)
    {
        await Task.Run(() => RunReadOperation(() =>
        {
            SeekToTime(timeSpan);
            return true;
        }, cancellationToken), cancellationToken);
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
            return DecodeNextFrame(1, cancellationToken, maxWidth, maxHeight, isNavigation: false);
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
            () => DecodeNextFrame(count, cancellationToken, 0, 0, isNavigation: true), cancellationToken), cancellationToken);
    }

    /// <summary>
    /// 按文件中的包顺序扫描关键帧，供画面搜索使用。
    /// 扫描结束时返回空结果，不像交互式跳帧那样回退并重试。
    /// </summary>
    internal Task<DecodeResult?> DecodeNextSearchFrameAsync(
        int maxWidth, int maxHeight, CancellationToken cancellationToken)
    {
        return Task.Run(() => RunReadOperation(() =>
        {
            isSearchScan = true;
            try
            {
                try
                {
                    return DecodeNextSearchFrame(maxWidth, maxHeight, cancellationToken);
                }
                catch (FullDecodeRequiredException exception)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!TryOpenSoftwareDecoder(fullPacketDecoding: true))
                        ThrowDecodeFailure();

                    // 从首次失败的关键包前预读，避免重新扫描已搜索过的长片段。
                    Seek(Math.Max(0, (exception.ResumePts ?? lastSeekPts) - keyFrameGap * 2),
                        AVSEEK_FLAG.Backward);
                    return DecodeNextSearchFrame(maxWidth, maxHeight, cancellationToken);
                }
            }
            finally
            {
                isSearchScan = false;
            }
        }, cancellationToken), cancellationToken);
    }

    private DecodeResult? DecodeNextSearchFrame(
        int maxWidth, int maxHeight, CancellationToken cancellationToken)
    {
        // 上次可能在一个包或 EOF 中取到关键帧后提前返回；先接收剩余输出。
        var pending = DecodePacket(null, cancellationToken, maxWidth, maxHeight, receiveOnly: true);
        if (pending is not null) return pending;
        var failures = 0;
        long? firstFailedKeyPts = null;
        foreach (var packet in inFc.ReadPackets())
        {
            cancellationToken.ThrowIfCancellationRequested();
            activeReadDeadline!.ThrowIfInterrupted();
            if (packet.StreamIndex != videoStreamIndex
                || (!useFullPacketDecoding && packet.Pts < 0))
                continue;

            var isKeyPacket = (packet.Flags & AV_PKT_FLAG_KEY_FRAME) != 0;
            if (!useFullPacketDecoding && !isKeyPacket)
                continue;

            var result = DecodePacket(packet, cancellationToken, maxWidth, maxHeight, preserveRemainingOutput: true);
            if (result is not null)
                return result;
            if (!isKeyPacket)
                continue;

            firstFailedKeyPts ??= packet.Pts;
            if (++failures > MAX_FAILURE_COUT)
                ThrowDecodeFailure();
            if (!useFullPacketDecoding && failures >= FastPathNoFrameThreshold)
                throw new FullDecodeRequiredException(firstFailedKeyPts);
        }

        activeReadDeadline!.ThrowIfInterrupted();
        // 文件尾仍可能缓存最后一个关键帧，发送空包取出延迟输出。
        return DecodePacket(null, cancellationToken, maxWidth, maxHeight, preserveRemainingOutput: true);
    }

    private DecodeResult DecodeNextFrame(
        int count,
        CancellationToken cancellationToken,
        int maxWidth,
        int maxHeight,
        bool isNavigation)
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
            Console.WriteLine($"Hardware decoding failed: {exception.InnerException?.Message ?? exception.Message}");
            return DecodeWithFullPacketFallback(
                count, anchorPts, isNavigation, cancellationToken, maxWidth, maxHeight,
                tryHardwareFirst: !useFullPacketDecoding && exception.AllowFullPacketRetry,
                failurePts: exception.RetryPts);
        }
        catch (FullDecodeRequiredException)
        {
            return DecodeWithFullPacketFallback(
                count, anchorPts, isNavigation, cancellationToken, maxWidth, maxHeight);
        }
    }

    private DecodeResult DecodeWithFullPacketFallback(
        int count, long anchorPts, bool isNavigation,
        CancellationToken cancellationToken, int maxWidth, int maxHeight,
        bool tryHardwareFirst = false, long? failurePts = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var targetPts = isNavigation
            ? ResolveHardwareFallbackSeekPts(count, anchorPts, lastSeekPts, failurePts)
            : lastSeekPts;
        var selectionAnchorPts = count < 0 ? anchorPts : targetPts - 1;

        // 快速硬解没有画面时，先让同一设备接收完整码流；设备仍失败才用软解。
        if (tryHardwareFirst && TryOpenHardwareDecoder(fullPacketDecoding: true))
        {
            try
            {
                return DecodeFullPacketsFromTarget();
            }
            catch (HardwareDecodeException exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Console.WriteLine($"Full-packet hardware decoding failed, switching to software: "
                    + (exception.InnerException?.Message ?? exception.Message));
            }
        }

        if (!TryOpenSoftwareDecoder(fullPacketDecoding: true))
            ThrowDecodeFailure();
        return DecodeFullPacketsFromTarget();

        DecodeResult DecodeFullPacketsFromTarget()
        {
            var prerollPts = Math.Max(0, targetPts - keyFrameGap * 2);
            Seek(prerollPts, AVSEEK_FLAG.Backward);
            return DecodeNextFrame(
                count,
                selectionAnchorPts,
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
            cancellationToken.ThrowIfCancellationRequested();
            activeReadDeadline?.ThrowIfInterrupted();
            if (packet.StreamIndex != videoStreamIndex
                || (!useFullPacketDecoding && packet.Pts < 0))
                continue;
            var isKeyPacket = (packet.Flags & AV_PKT_FLAG_KEY_FRAME) != 0;
            if (!useFullPacketDecoding && !isKeyPacket)
            {
                // Console.WriteLine($"Skip[NonKey] packet: {packet.Pts}");
                continue;
            }

            if (!useFullPacketDecoding)
                Console.WriteLine($"Current packet: {packet.Pts}");
            // PositionInFile = packet.Position;
            // Console.WriteLine($"Current packet positon: {packet.Position}");

            var result = DecodePacket(packet, cancellationToken, maxWidth, maxHeight);
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
            if (!isKeyPacket)
                continue;
            failureCount++;
            if (!useFullPacketDecoding && !hardwareDecoderOpened
                && failureCount >= FastPathNoFrameThreshold)
                throw new FullDecodeRequiredException();
            // 硬解设备在首帧初始化失败时通常只返回空结果，不会抛出异常。
            // 连续几个关键包都没有帧即可判定硬解链路不可用，及时切换软件解码。
            if (failureCount > MAX_FAILURE_COUT
                || (hardwareDecoderOpened && failureCount >= HardwareNoFrameFailureThreshold))
                ThrowDecodeFailure();
            Console.WriteLine("result is null");
        }

        activeReadDeadline?.ThrowIfInterrupted();
        var delayedResult = DecodePacket(null, cancellationToken, maxWidth, maxHeight);
        if (delayedResult is not null)
        {
            if (IsDecodedFrameAtRequestedSide(backward, requireForwardAfterAnchor, currentKeyFramePts, anchorPts))
                return delayedResult;
            delayedResult.BitmapLease?.Dispose();
            if (delayedResult.BitmapLease is null) delayedResult.Bitmap?.Dispose();
            delayedResult.GpuFrame?.Dispose();
        }
        // 没找到合适的关键帧，稍微向前 seek 后重试。
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
        return $"{inVideoStream.Codecpar.CodecName}, {width}x{height}, {FormatSeconds(timelineDurationSeconds)}, {FormatFileSize(fileSize)}";
    }

    private void UpdateTimelineDuration()
    {
        (timelineDurationSeconds, timelineDurationPts) = ResolveTimelineDuration(
            inVideoStream.Duration,
            timeBase.num,
            timeBase.den,
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
        var fallbackSeconds = containerDuration / (double)FF.AV_TIME_BASE;
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
                    activeReadDeadline!.ThrowIfInterrupted();
                    if (packet.StreamIndex != videoStreamIndex || packet.Pts < 0 ||
                        (packet.Flags & AV_PKT_FLAG_KEY_FRAME) == 0)
                        continue;

                    keyFramePtsList.Add(packet.Pts);
                    if (keyFramePtsList.Count >= requiredKeyFrames)
                        break;
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

        return ResolveKeyFrameTiming(keyFramePtsList, inVideoStream.StartTime, timeBase.num, timeBase.den);
    }

    internal static (long firstPts, long gap) ResolveKeyFrameTiming(
        IReadOnlyList<long> keyFramePts, long streamStartTime, int timeBaseNumerator, int timeBaseDenominator)
    {
        var firstPts = keyFramePts.Count > 0 ? keyFramePts[0] :
            streamStartTime == FF.AV_NOPTS_VALUE ? 0 : streamStartTime;
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
                    if (context->pb->error == FF.AVERROR_EXIT)
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
        reusableDecodeFrame?.Dispose();
        reusableDecodeFrame = null;
        hostFramePresenter.Dispose();
    }

    public void Dispose()
    {
        Close();
        gpuFramePresenter.Dispose();
    }

    private long TimeSpanToPts(TimeSpan timeSpan)
    {
        var t = (double)timeBase.den / timeBase.num;
        return (long)(timeSpan.TotalSeconds * t) + firstFrameTimestamp;
    }

    private TimeSpan PtsToTimeSpan(long pts)
    {
        var t = (double)timeBase.den / timeBase.num;
        return TimeSpan.FromSeconds((pts - firstFrameTimestamp) / t);
    }
    
    internal (int VideoPid, int[] AudioPids) GetMergeStreamPids() =>
        (inVideoStream.Id, inFc.Streams
            .Where(stream => stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_AUDIO)
            .Select(stream => stream.Id).ToArray());

    private void RememberKeyPacketBoundary(Packet packet)
    {
        // 有界值类型缓存用于重排序/延迟输出；反复 seek 同一包时复用已有摘要。
        for (var index = 0; index < keyPacketBoundaryCount; index++)
            if (keyPacketBoundaries[index].Position == packet.Position && keyPacketBoundaries[index].Pts == packet.Pts)
                return;
        keyPacketBoundaries[nextKeyPacketBoundary] = (packet.Position, packet.Pts, packet.EndSignature);
        nextKeyPacketBoundary = (nextKeyPacketBoundary + 1) % keyPacketBoundaries.Length;
        keyPacketBoundaryCount = Math.Min(keyPacketBoundaryCount + 1, keyPacketBoundaries.Length);
    }

    private PacketEndSignature FindKeyPacketBoundary(long position, long pts)
    {
        var matches = 0;
        PacketEndSignature signature = default;
        for (var index = 0; index < keyPacketBoundaryCount; index++)
        {
            var boundary = keyPacketBoundaries[index];
            if (position < 0 || boundary.Position != position) continue;
            if (boundary.Pts == pts) return boundary.Signature;
            signature = boundary.Signature;
            matches++;
        }
        // CAVS 可能修正显示 PTS；只有该位置对应唯一关键包时才接受位置匹配。
        return matches == 1 ? signature : default;
    }

    private DecodeResult? DecodePacket(
        Packet? packet,
        CancellationToken cancellationToken,
        int maxWidth,
        int maxHeight,
        bool receiveOnly = false,
        bool preserveRemainingOutput = false)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (keyPacketBoundaries.Length > 0 && !isSearchScan && packet is { Position: >= 0 } && (packet.Flags & AV_PKT_FLAG_KEY_FRAME) != 0)
                RememberKeyPacketBoundary(packet);
            reusableDecodeFrame ??= new Frame();
            var destRef = reusableDecodeFrame;
            // 1 packet -> 0..N frame
            var frames = receiveOnly ? videoDecoder.ReceiveFrames(destRef)
                : videoDecoder.DecodePacket(packet, destRef, preserveRemainingOutput);
            foreach (var frame in frames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 回退路径仍只向关键帧导航返回关键帧，但必须先喂入完整码流。
                if (useFullPacketDecoding && !AudioMode
                    && (frame.Flags & FF.AV_FRAME_FLAG_KEY) == 0)
                    continue;
                if (firstFrameTimestamp == -1)
                {
                    firstFrameTimestamp = frame.BestEffortTimestamp;
                    maxPts = timelineDurationPts + firstFrameTimestamp;
                }

                var pts = frame.Pts;
                if (!AudioMode && pts < 0)
                    pts = frame.BestEffortTimestamp;
                
                currentKeyFramePts = pts;
                if (!isSearchScan)
                    Console.WriteLine($"Current keyFrame: {pts}");
                var pktPosition = frame.PacketPosition;
                // 延迟输出的帧不一定属于本次输入包；来源未知时保留 -1，避免剪错位置。
                
                PositionInFile = pktPosition;
                if (!isSearchScan)
                    Console.WriteLine($"Current keyFrame PktPosition: {pktPosition}");
                if (AudioMode && pktPosition == -1)
                    continue;

                // 成功打开硬件解码器并不代表当前编码器一定输出了硬件帧。
                // VideoToolbox/D3D11 的部分失败只会在首帧初始化时才报告，
                // 此时解码器其实已经完成打开。
                var isHardwareFrame = !AudioMode && frame.HasHardwareFramesContext;
                var displayGeometry = AudioMode
                    ? default
                    : GetDisplayGeometry(frame);
                var videoDynamicRange = AudioMode
                    ? VideoDynamicRange.Standard
                    : GetVideoDynamicRange(frame);

                // 优先让平台 presenter 直接使用原生 surface。只有 presenter
                // 明确无法处理时，才进入下面的 CPU 回读兜底路径。
                if (!AudioMode && frame.HasHardwareFramesContext)
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
                                SourcePixelSize = displayGeometry.PixelSize,
                                RequiresSampleAspectRatioCorrection = displayGeometry.RequiresCorrection,
                                VideoDynamicRange = videoDynamicRange,
                                FrameTimestamp = PtsToTimeSpan(pts),
                                FramePts = pts,
                                FramePts90k = pts == FF.AV_NOPTS_VALUE ? FF.AV_NOPTS_VALUE
                                    : (long)Math.Round(pts * (double)timeBase.num * 90_000 / timeBase.den),
                                FramePosition = pktPosition,
                                FrameEndSignature = FindKeyPacketBoundary(pktPosition, pts),
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
                    if (!AudioMode && frame.HasHardwareFramesContext)
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
                    throw new HardwareDecodeException(exception, pts, allowFullPacketRetry: false);
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
                            : displayGeometry.PixelSize,
                        RequiresSampleAspectRatioCorrection =
                            !AudioMode && displayGeometry.RequiresCorrection,
                        VideoDynamicRange = videoDynamicRange,
                        FrameTimestamp = PtsToTimeSpan(pts),
                        FramePts = pts,
                        FramePts90k = pts == FF.AV_NOPTS_VALUE ? FF.AV_NOPTS_VALUE
                            : (long)Math.Round(pts * (double)timeBase.num * 90_000 / timeBase.den),
                        FramePosition = pktPosition,
                        FrameEndSignature = FindKeyPacketBoundary(pktPosition, pts),
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
        if (!receiveOnly && !isSearchScan)
            Console.WriteLine("no frames were successfully processed");
        return null;
    }

    private static unsafe VideoDynamicRange GetVideoDynamicRange(Frame frame)
    {
        AVFrame* rawFrame = frame;
        var hasDolbyVisionMetadata =
            FF.av_frame_get_side_data(rawFrame, AVFrameSideDataType.AV_FRAME_DATA_DOVI_RPU_BUFFER) != null
            || FF.av_frame_get_side_data(rawFrame, AVFrameSideDataType.AV_FRAME_DATA_DOVI_METADATA) != null;
        var hasHdrMetadata =
            FF.av_frame_get_side_data(rawFrame, AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA) != null
            || FF.av_frame_get_side_data(rawFrame, AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL) != null
            || FF.av_frame_get_side_data(rawFrame, AVFrameSideDataType.AV_FRAME_DATA_DYNAMIC_HDR_PLUS) != null;
        return VideoDynamicRangeDetector.Detect(
            rawFrame->color_trc,
            hasDolbyVisionMetadata,
            hasHdrMetadata,
            codecTag: 0);
    }

    private (Avalonia.PixelSize PixelSize, bool RequiresCorrection) GetDisplayGeometry(Frame frame)
    {
        var sampleAspectRatio = frame.SampleAspectRatio;
        if (sampleAspectRatio.num <= 0 || sampleAspectRatio.den <= 0)
            sampleAspectRatio = inVideoStream.Codecpar!.SampleAspectRatio;
        if (sampleAspectRatio.num <= 0 || sampleAspectRatio.den <= 0)
            sampleAspectRatio = inVideoStream.SampleAspectRatio;

        var requiresCorrection = RequiresSampleAspectRatioCorrection(
            sampleAspectRatio.num,
            sampleAspectRatio.den);
        return (
            CalculateDisplayPixelSize(
                frame.Width,
                frame.Height,
                sampleAspectRatio.num,
                sampleAspectRatio.den),
            requiresCorrection);
    }

    internal static bool RequiresSampleAspectRatioCorrection(
        int sampleAspectRatioNumerator,
        int sampleAspectRatioDenominator) =>
        sampleAspectRatioNumerator > 0
        && sampleAspectRatioDenominator > 0
        && sampleAspectRatioNumerator != sampleAspectRatioDenominator;

    internal static Avalonia.PixelSize CalculateDisplayPixelSize(
        int codedWidth,
        int codedHeight,
        int sampleAspectRatioNumerator,
        int sampleAspectRatioDenominator)
    {
        if (codedWidth <= 0 || codedHeight <= 0)
            return default;
        if (!RequiresSampleAspectRatioCorrection(
                sampleAspectRatioNumerator,
                sampleAspectRatioDenominator))
            return new Avalonia.PixelSize(codedWidth, codedHeight);

        var displayWidth = codedWidth * (double)sampleAspectRatioNumerator /
                           sampleAspectRatioDenominator;
        if (!double.IsFinite(displayWidth) || displayWidth <= 0 || displayWidth > int.MaxValue)
            return new Avalonia.PixelSize(codedWidth, codedHeight);

        return new Avalonia.PixelSize(
            Math.Max(1, (int)Math.Round(displayWidth)),
            codedHeight);
    }

    private sealed class HardwareDecodeException(
        Exception innerException, long? retryPts = null, bool allowFullPacketRetry = true)
        : Exception("Hardware decoding failed.", innerException)
    {
        public long? RetryPts { get; } = retryPts;
        public bool AllowFullPacketRetry { get; } = allowFullPacketRetry;
    }

    private sealed class FullDecodeRequiredException(long? resumePts = null) : Exception
    {
        public long? ResumePts { get; } = resumePts;
    }
}
