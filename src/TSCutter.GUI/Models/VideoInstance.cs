using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Common;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using Sdcb.FFmpeg.Toolboxs.Extensions;
using Sdcb.FFmpeg.Utils;
using TSCutter.GUI.Extensions;
using TSCutter.GUI.Utils;
using static TSCutter.GUI.Utils.CommonUtil;

namespace TSCutter.GUI.Models;

public class VideoInstance(string filePath) : IDisposable
{
    private const int MAX_FAILURE_COUT = 100;
    private const int AV_PKT_FLAG_KEY_FRAME = 0x0001;
    public long PositionInFile { get; private set; } = 0;
    public long CurrentPts => currentKeyFramePts;
    public bool Inited { get; private set; } = false;
    private bool AudioMode { get; set; } = false;
    
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
    
    private string videoPath = filePath;

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

        var decoders = Codec.FindDecoders(inVideoStream.Codecpar!.CodecId).ToList();
        if (decoders.Count == 0)
        {
            throw new Exception("Cant find decoder!");
        }

        videoStreamIndex = inVideoStream.Index;
        timeBase = inVideoStream.TimeBase;

        var firstDecoder = decoders.First();
        videoDecoder = new(Codec.FindDecoderById(firstDecoder.Id));
        videoDecoder.SkipFrame = AVDiscard.Nonkey;
        videoDecoder.FillParameters(inVideoStream.Codecpar!);
        videoDecoder.Open();

        try
        {
            // calc KeyFrameGap
            DecodeNextFrame();
            DecodeNextFrame();
            var firstKeyFramePts = currentKeyFramePts;
            DecodeNextFrame();
            keyFrameGap = currentKeyFramePts - firstKeyFramePts;
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

        var firstDecoder = decoders.First();
        videoDecoder = new(Codec.FindDecoderById(firstDecoder.Id));
        videoDecoder.FillParameters(inVideoStream.Codecpar!);
        videoDecoder.Open();

        keyFrameGap = 90000;
        AudioMode = true;
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
        inFc.SeekFile(pts - keyFrameGap * 3 - 2, pts - keyFrameGap * 4, pts, videoStreamIndex);
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
        var failureCount = 0;
        
        if (count < 0)
        {
            // Seek backward by keyframe gap * abs(count)
            var targetPts = Math.Max(0, currentKeyFramePts - Math.Abs(keyFrameGap) * (Math.Abs(count) + 1)) - 2;
            Seek(targetPts, AVSEEK_FLAG.Backward);
        }

        if (count > 1)
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

            var result = DecodePacket(packet);
            if (result != null)
            {
                return result;
            }
            if (failureCount++ > MAX_FAILURE_COUT)
            {
                throw new TooManyDecodeFailuresException("Too many failed packets!");
            }
            Console.WriteLine("result is null");
        }

        // No keyframe found, retry by seeking slightly earlier
        if (!AudioMode && lastSeekPts - 1000 < 0)
            throw new Exception("Decode Failed!");

        Seek(lastSeekPts - keyFrameGap / 2);
        return DecodeNextFrame();
    }

    public double GetVideoDurationInSeconds()
    {
        return inVideoStream.GetDurationInSeconds();
    }

    public string GetVideoInfoText()
    {
        var width = inVideoStream.Codecpar!.Width;
        var height = inVideoStream.Codecpar.Height;
        var durationInSeconds = inVideoStream.GetDurationInSeconds();
        var fileSize = inFc.GetFileSize();
        return $"{inVideoStream.Codecpar.CodecId}, {width}x{height}, {FormatSeconds(durationInSeconds)}, {FormatFileSize(fileSize)}";
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
    
    private DecodeResult? DecodePacket(Packet packet)
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
                    maxPts = inVideoStream.Duration + firstFrameTimestamp;
                }

#pragma warning disable CS0618 // Obsolete
                if (frame.KeyFrame == 0)
                    continue;
                Console.WriteLine($"Current keyFrame: {frame.Pts}");
                currentKeyFramePts = frame.Pts;
                PositionInFile = frame.PktPosition;
                Console.WriteLine($"Current keyFrame PktPosition: {frame.PktPosition}");
                if (AudioMode && frame.PktPosition == -1)
                    continue;
#pragma warning restore CS0618 // Obsolete

                // 音频模式无法解码 返回固定图片
                var bitmap = AudioMode ? ImageUtil.BlankImage : ImageUtil.CreateBitmapFromFrame(frame);
                return new DecodeResult()
                {
                    Bitmap = bitmap,
                    FrameTimestamp = PtsToTimeSpan(frame.Pts),
                };
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            // Return null to indicate failure and continue with the next packet
            return null;
        }
        
        // If no frames were successfully processed
        Console.WriteLine("no frames were successfully processed");
        return null;
    }
}