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
    private const int AV_PKT_FLAG_KEY_FRAME = 0x0001;
    public long PositionInFile { get; private set; } = 0;
    public bool Inited { get; private set; } = false;
    
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
        inFc = FormatContext.OpenInputUrl(videoPath);
        inFc.LoadStreamInfo();

        inVideoStream = inFc.GetVideoStream();
        if (inVideoStream.Codecpar?.CodecId is null)
        {
            throw new Exception("Read Failed!");
        }

        var decoders = Codec.FindDecoders(inVideoStream.Codecpar!.CodecId);
        if (!decoders.Any())
        {
            throw new Exception("Cant find decoder!");
        }

        videoStreamIndex = inVideoStream.Index;
        timeBase = inVideoStream.TimeBase;

        var firstDecoder = decoders.First();
        videoDecoder = new(Codec.FindDecoderById(firstDecoder.Id));
        videoDecoder.FillParameters(inVideoStream.Codecpar!);
        videoDecoder.Open();

        // calc KeyFrameGap
        DecodeNextFrame();
        DecodeNextFrame();
        var firstKeyFramePts = currentKeyFramePts;
        DecodeNextFrame();
        keyFrameGap = currentKeyFramePts - firstKeyFramePts;
        Console.WriteLine($"keyFrameGap: {keyFrameGap}");
        SeekToTime(TimeSpan.Zero);

        Inited = true;
    }
    
    public async Task SeekToTimeAsync(TimeSpan timeSpan)
    {
        await Task.Run(() => SeekToTime(timeSpan));
    }

    public void SeekToTime(TimeSpan timeSpan)
    {
        var targetTimestamp = TimeSpanToPts(timeSpan);
        targetTimestamp = Math.Min(maxPts, targetTimestamp);
        Seek(targetTimestamp);
    }

    public void Seek(long pts, AVSEEK_FLAG flag = 0)
    {
        lastSeekPts = pts;
        inFc.SeekFrame(pts, videoStreamIndex, flag);
    }

    public async Task<DecodeResult> DecodeNextFrameAsync(int count = 1)
    {
        return await Task.Run(() => DecodeNextFrame(count));
    }

    private DecodeResult DecodeNextFrame(int count = 1)
    {
        if (count < 0)
        {
            // Seek backward by keyframe gap * abs(count)
            var targetPts = Math.Max(0, currentKeyFramePts - Math.Abs(keyFrameGap) * Math.Abs(count)) - 2;
            Seek(targetPts, AVSEEK_FLAG.Backward);
            return DecodeNextFrame();
        }

        if (count > 1)
        {
            // Seek forward by keyframe gap * abs(count)
            var targetPts = Math.Min(maxPts, currentKeyFramePts + Math.Abs(keyFrameGap) * Math.Abs(count)) + 2;
            Seek(targetPts);
            return DecodeNextFrame();
        }
        
        foreach (var packet in inFc.ReadPackets(videoStreamIndex))
        {
            if ((packet.Flags & AV_PKT_FLAG_KEY_FRAME) == 0)
            {
                Console.WriteLine($"Skip[NonKey] packet: {packet.Pts}");
                continue;
            }

            Console.WriteLine($"Current packet: {packet.Pts}");
            PositionInFile = packet.Position;

            var result = DecodePacket(packet);
            if (result != null)
            {
                return result;
            }
        }

        // No keyframe found, retry by seeking slightly earlier
        if (lastSeekPts - 1000 < 0)
            throw new Exception("Decode Failed!");

        Seek(lastSeekPts - 1000);
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
        using Frame destRef = new Frame();
        // 1 packet -> 0..N frame
        foreach (var frame in videoDecoder.DecodePacket(packet, destRef))
        {
            if (firstFrameTimestamp == -1)
            {
                firstFrameTimestamp = frame.BestEffortTimestamp;
                maxPts = inVideoStream.Duration + firstFrameTimestamp;
            }

            Console.WriteLine($"Current keyFrame: {frame.Pts}");
            currentKeyFramePts = frame.Pts;

            Bitmap decodedBitmap;

            try
            {
                decodedBitmap = ImageUtil.CreateBitmapFromFrame(frame);
                videoDecoder.FlushBuffers();
            }
            catch (FFmpegException e)
            {
                Console.WriteLine(e);
                // Return null to indicate failure and continue with the next packet
                return null;
            }

            return new DecodeResult()
            {
                Bitmap = decodedBitmap,
                FrameTimestamp = PtsToTimeSpan(frame.Pts),
            };
        }

        // If no frames were successfully processed
        return null;
    }
}