using System;
using System.Collections.Generic;
using System.IO;
using Sdcb.FFmpeg.Codecs;
using Sdcb.FFmpeg.Formats;
using Sdcb.FFmpeg.Raw;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.Utils;

/// <summary>
/// 从容器中提取缩略图总览表头所需的结构化字段。
/// 与 <see cref="MediaInfoBuilder"/> 共享同样的取值口径（都来自 FormatContext 的
/// 已解析流信息），但输出的是可本地化、可绘制的数据，而不是预对齐的英文长文本。
/// </summary>
public static class ThumbnailSheetInfoBuilder
{
    public static ThumbnailSheetInfo Build(string filePath)
    {
        var fi = new FileInfo(filePath);
        using var fc = FormatContext.OpenInputUrl(filePath);
        fc.LoadStreamInfo();

        // MediaStream 是值类型，这里用索引标记是否找到，避免默认值带来的误判。
        var videoIndex = -1;
        var audioIndex = -1;
        for (var i = 0; i < fc.Streams.Count; i++)
        {
            var type = fc.Streams[i].Codecpar!.CodecType;
            if (type == AVMediaType.Video && videoIndex < 0)
                videoIndex = i;
            else if (type == AVMediaType.Audio && audioIndex < 0)
                audioIndex = i;
        }

        var videoStream = videoIndex >= 0 ? fc.Streams[videoIndex] : default;
        var audioStream = audioIndex >= 0 ? fc.Streams[audioIndex] : default;
        var hasVideo = videoIndex >= 0;
        var hasAudio = audioIndex >= 0;
        var sampleAspectRatio = hasVideo
            ? videoStream.Codecpar!.SampleAspectRatio
            : default;
        if (hasVideo && (sampleAspectRatio.Num <= 0 || sampleAspectRatio.Den <= 0))
            sampleAspectRatio = videoStream.SampleAspectRatio;

        // 时长优先取视频流，回退到容器时长。部分异常 TS 缺少流时长，
        // 此时容器已估算的时长更可靠（与 VideoInstance.ResolveTimelineDuration 的策略一致）。
        var duration = ResolveDuration(hasVideo ? videoStream : default, hasVideo, fc.Duration);

        var additionalVideoCodecs = new List<string>();
        var additionalAudioTracks = new List<ThumbnailSheetTrackInfo>();
        var subtitleTracks = new List<ThumbnailSheetTrackInfo>();
        for (var i = 0; i < fc.Streams.Count; i++)
        {
            var codecParameters = fc.Streams[i].Codecpar!;
            if (!IsDisplayableStreamType(codecParameters.CodecType))
                continue;

            var codec = codecParameters.CodecId.ToString();
            if (string.IsNullOrEmpty(codec))
                continue;

            switch (codecParameters.CodecType)
            {
                case AVMediaType.Video when i != videoIndex:
                    additionalVideoCodecs.Add(codec);
                    break;
                case AVMediaType.Audio when i != audioIndex:
                    additionalAudioTracks.Add(new ThumbnailSheetTrackInfo(
                        codec,
                        GetLanguage(fc.Streams[i])));
                    break;
                case AVMediaType.Subtitle:
                    subtitleTracks.Add(new ThumbnailSheetTrackInfo(
                        codec,
                        GetLanguage(fc.Streams[i])));
                    break;
            }
        }

        return new ThumbnailSheetInfo
        {
            FileName = fi.Name,
            Format = fc.InputFormat?.Name ?? string.Empty,
            FileSize = fi.Exists ? fi.Length : 0,
            Duration = duration,
            OverallBitRate = fc.BitRate > 0 ? fc.BitRate : 0,
            VideoCodec = hasVideo ? videoStream.Codecpar!.CodecId.ToString() : null,
            VideoWidth = hasVideo ? videoStream.Codecpar!.Width : 0,
            VideoHeight = hasVideo ? videoStream.Codecpar!.Height : 0,
            VideoScanMode = hasVideo
                ? GetVideoScanMode(videoStream.Codecpar!.FieldOrder)
                : VideoScanMode.Unknown,
            VideoDynamicRange = hasVideo
                ? GetVideoDynamicRange(videoStream)
                : VideoDynamicRange.Standard,
            VideoDisplayAspectRatio = hasVideo
                ? CalculateDisplayAspectRatio(
                    videoStream.Codecpar!.Width,
                    videoStream.Codecpar!.Height,
                    sampleAspectRatio.Num,
                    sampleAspectRatio.Den)
                : 0,
            VideoFrameRate = hasVideo && videoStream.AvgFrameRate.Num > 0
                ? videoStream.AvgFrameRate.ToDouble()
                : 0,
            VideoBitRate = hasVideo ? videoStream.Codecpar!.BitRate : 0,
            AudioCodec = hasAudio ? audioStream.Codecpar!.CodecId.ToString() : null,
            AudioLanguage = hasAudio ? GetLanguage(audioStream) : null,
            AudioChannels = hasAudio ? audioStream.Codecpar!.ChLayout.nb_channels : 0,
            AudioSampleRate = hasAudio ? audioStream.Codecpar!.SampleRate : 0,
            AudioBitRate = hasAudio ? audioStream.Codecpar!.BitRate : 0,
            AdditionalVideoCodecs = additionalVideoCodecs,
            AdditionalAudioTracks = additionalAudioTracks,
            SubtitleTracks = subtitleTracks
        };
    }

    private static string? GetLanguage(MediaStream stream)
    {
        if (!stream.Metadata.TryGetValue("language", out var language))
            return null;

        language = language.Trim();
        return language.Length > 0 ? language : null;
    }

    internal static bool IsDisplayableStreamType(AVMediaType mediaType) =>
        mediaType is AVMediaType.Video or AVMediaType.Audio or AVMediaType.Subtitle;

    internal static VideoScanMode GetVideoScanMode(AVFieldOrder fieldOrder) => fieldOrder switch
    {
        AVFieldOrder.Progressive => VideoScanMode.Progressive,
        AVFieldOrder.Tt or AVFieldOrder.Bb or AVFieldOrder.Tb or AVFieldOrder.Bt =>
            VideoScanMode.Interlaced,
        _ => VideoScanMode.Unknown
    };

    private static VideoDynamicRange GetVideoDynamicRange(MediaStream stream)
    {
        var codecParameters = stream.Codecpar!;
        var hasDolbyVisionConfig = HasCodecSideData(codecParameters, AVPacketSideDataType.DoviConf);
        var hasHdrMetadata =
            HasCodecSideData(codecParameters, AVPacketSideDataType.MasteringDisplayMetadata)
            || HasCodecSideData(codecParameters, AVPacketSideDataType.ContentLightLevel)
            || HasCodecSideData(codecParameters, AVPacketSideDataType.DynamicHdr10Plus);
        return VideoDynamicRangeDetector.Detect(
            codecParameters.ColorTrc,
            hasDolbyVisionConfig,
            hasHdrMetadata,
            codecParameters.CodecTag);
    }

    private static unsafe bool HasCodecSideData(CodecParameters codecParameters, AVPacketSideDataType type)
    {
        AVCodecParameters* rawParameters = codecParameters;
        var sideData = rawParameters->coded_side_data;
        for (var index = 0; sideData != null && index < rawParameters->nb_coded_side_data; index++)
        {
            if (sideData[index].type == type)
                return true;
        }

        return false;
    }

    internal static double CalculateDisplayAspectRatio(
        int width,
        int height,
        int sampleAspectRatioNumerator,
        int sampleAspectRatioDenominator)
    {
        if (width <= 0 || height <= 0)
            return 0;

        var pixelAspectRatio = sampleAspectRatioNumerator > 0 && sampleAspectRatioDenominator > 0
            ? sampleAspectRatioNumerator / (double)sampleAspectRatioDenominator
            : 1d;
        return width * pixelAspectRatio / height;
    }

    private static unsafe TimeSpan ResolveDuration(MediaStream videoStream, bool hasVideo, long containerDuration)
    {
        if (hasVideo && videoStream.Duration > 0)
        {
            // 与 MediaInfoBuilder.FormatDuration(AVStream*) 保持同一口径：
            // 流时长 × time_base 得到秒数。
            var seconds = videoStream.Duration * ffmpeg.av_q2d(videoStream.TimeBase);
            if (seconds > 0)
                return TimeSpan.FromSeconds(seconds);
        }

        return containerDuration > 0
            ? TimeSpan.FromSeconds(containerDuration / 1_000_000.0)
            : TimeSpan.Zero;
    }
}
