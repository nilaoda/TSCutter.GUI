using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Toolboxs.Extensions;
using Sdcb.FFmpeg.Utils;
using TSCutter.GUI.Extensions;
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
    private string hardwareDecoderName = string.Empty;
    private bool AudioMode { get; set; } = false;
    private bool hardwareDecoderOpened;
    private readonly bool preferHardwareDecoding = enableHardwareDecoding && AppConfig.IsHardwareDecodingSupported;
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

    public async Task InitVideoAsync()
    {
        await Task.Run(InitVideo);
    }

    public void InitVideo()
    {
        var options = new MediaDictionary();
        options.Set("scan_all_pmts", "1"); // Scan and combine all PMTs
        inFc = FormatContext.OpenInputUrl(videoPath, options: options);
        inFc.LoadStreamInfo();

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

        try
        {
            // calc KeyFrameGap from packet-level PTS (no frame decoding needed)
            var (firstPts, gap) = ReadKeyFramePacketPts();
            firstFrameTimestamp = firstPts;
            maxPts = timelineDurationPts + firstFrameTimestamp;
            keyFrameGap = gap;
            Console.WriteLine($"keyFrameGap: {keyFrameGap}");
            Seek(firstFrameTimestamp);
        }
        catch (TooManyDecodeFailuresException e)
        {
            Console.WriteLine(e);
            Console.WriteLine("Try Audio Mode...");
            InitAudio(inFc);
        }
        
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
        inFc.SeekFrame(pts - keyFrameGap * 4, videoStreamIndex);
        // flush
        videoDecoder.FlushBuffers();
    }

    public void Seek(long pts, AVSEEK_FLAG flag = 0)
    {
        // if (lastSeekPts == pts)
        //     return;
        Console.WriteLine($"lastSeekPts: {lastSeekPts}, targetPts: {pts}, flag: {flag}");
        lastSeekPts = pts;
        inFc.SeekFrame(pts, videoStreamIndex, flag);
        // flush
        videoDecoder.FlushBuffers();
    }

    public async Task<DecodeResult> DecodeNextFrameAsync(int count = 1)
    {
        return await Task.Run(() => DecodeNextFrame(count));
    }

    private DecodeResult DecodeNextFrame(int count = 1)
    {
        var anchorPts = currentKeyFramePts;
        try
        {
            return DecodeNextFrame(count, anchorPts, true, 0);
        }
        catch (HardwareDecodeException exception)
        {
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
                requireForwardAfterAnchor: count >= 0);
        }
    }

    private DecodeResult DecodeNextFrame(
        int count,
        long anchorPts,
        bool applyInitialSeek,
        int retryCount,
        bool requireForwardAfterAnchor = false)
    {
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

        foreach (var packet in inFc.ReadPackets(videoStreamIndex))
        {
            if (packet.StreamIndex != videoStreamIndex || packet.Pts < 0)
            {
                continue;
            }
            if ((packet.Flags & AV_PKT_FLAG_KEY_FRAME) == 0)
            {
                // Console.WriteLine($"Skip[NonKey] packet: {packet.Pts}");
                continue;
            }

            Console.WriteLine($"Current packet: {packet.Pts}");
            // PositionInFile = packet.Position;
            // Console.WriteLine($"Current packet positon: {packet.Position}");

            var result = DecodePacket(packet, packet.Position);
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
                result.Bitmap.Dispose();
                if (backward)
                    break;
                continue;
            }
            if (failureCount++ > MAX_FAILURE_COUT)
            {
                ThrowDecodeFailure();
            }
            Console.WriteLine("result is null");
        }

        // No suitable keyframe found, retry by seeking slightly earlier
        if (!AudioMode && lastSeekPts - 1000 < 0)
            throw new Exception("Decode Failed!");

        var retryStep = backward ? keyFrameGap : keyFrameGap / 2;
        Seek(lastSeekPts - retryStep, backward ? AVSEEK_FLAG.Backward : 0);
        return DecodeNextFrame(
            backward ? -1 : 1,
            anchorPts,
            applyInitialSeek: false,
            retryCount: retryCount + 1,
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
    private (long firstPts, long gap) ReadKeyFramePacketPts(int requiredKeyFrames = 3)
    {
        var keyFramePtsList = new List<long>();
        foreach (var packet in inFc.ReadPackets(videoStreamIndex))
        {
            if (packet.StreamIndex != videoStreamIndex || packet.Pts < 0)
                continue;
            if ((packet.Flags & AV_PKT_FLAG_KEY_FRAME) == 0)
                continue;

            keyFramePtsList.Add(packet.Pts);
            if (keyFramePtsList.Count >= requiredKeyFrames)
                break;
        }

        if (keyFramePtsList.Count < 2)
            return (keyFramePtsList.Count > 0 ? keyFramePtsList[0] : 0, 0);

        var gap = Math.Abs(keyFramePtsList[^1] - keyFramePtsList[^2]);
        Console.WriteLine($"KeyFramePacket PTS: {string.Join(", ", keyFramePtsList)}, gap: {gap}");
        return (keyFramePtsList[0], gap);
    }

    public void Close()
    {
        Inited = false;
        inFc?.Close();
        inFc?.Dispose();
        videoDecoder?.Close();
        videoDecoder?.Dispose();
    }

    public void Dispose()
    {
        Close();
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
    
    private DecodeResult? DecodePacket(Packet packet, long packetPosition)
    {
        try
        {
            using Frame destRef = new Frame();
            // 1 packet -> 0..N frame
            foreach (var frame in videoDecoder.DecodePacket(packet, destRef))
            {
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
                
                Console.WriteLine($"Current keyFrame: {pts}");
                currentKeyFramePts = pts;
                var pktPosition = frame.PktPosition;
                if (!AudioMode && pktPosition == -1)
                    pktPosition = packetPosition;
                
                PositionInFile = pktPosition;
                Console.WriteLine($"Current keyFrame PktPosition: {pktPosition}");
                if (AudioMode && pktPosition == -1)
                    continue;
#pragma warning restore CS0618 // Obsolete

                // 硬件帧位于 GPU 内存，先回读到系统内存，再复用原有的位图转换逻辑。
                Frame? softwareFrame = null;
                try
                {
                    if (!AudioMode && frame.HwFramesContext != null)
                        softwareFrame = frame.TransferToSoftwareFrame();
                }
                catch (Exception exception)
                {
                    // 硬件帧无法回读通常表示设备链路失效，此时才立即切换软件解码器。
                    throw new HardwareDecodeException(exception, pts);
                }
                using (softwareFrame)
                {
                    IsHardwareDecoding = softwareFrame != null;
                    Avalonia.Media.Imaging.Bitmap bitmap;
                    try
                    {
                        bitmap = AudioMode
                            ? ImageUtil.BlankImage
                            : ImageUtil.CreateBitmapFromFrame(softwareFrame ?? frame);
                    }
                    catch (Exception exception)
                    {
                        // 位图绘制属于界面链路，失败时不能据此判定硬件解码器不可用。
                        Console.WriteLine(exception);
                        return null;
                    }
                    return new DecodeResult()
                    {
                        Bitmap = bitmap,
                        FrameTimestamp = PtsToTimeSpan(pts),
                    };
                }
            }
        }
        catch (HardwareDecodeException)
        {
            throw;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
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
