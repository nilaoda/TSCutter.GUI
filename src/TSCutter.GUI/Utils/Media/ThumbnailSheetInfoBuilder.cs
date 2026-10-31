using TSCutter.GUI.FFmpeg;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using TSCutter.GUI.Models.Discs;
using FFmpeg.AutoGen.Abstractions;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;
using TSCutter.GUI.Models.Preview;

namespace TSCutter.GUI.Utils.Media;

/// <summary>
/// 从容器中提取缩略图总览表头所需的结构化字段。
/// 与 <see cref="MediaInfoBuilder"/> 共享同样的取值口径（都来自 FormatContext 的
/// 已解析流信息），但输出的是可本地化、可绘制的数据，而不是预对齐的英文长文本。
/// </summary>
public static class ThumbnailSheetInfoBuilder
{
    public static ThumbnailSheetInfo Build(string filePath, PreviewStreamSelection? streamSelection = null,
        DiscInput? disc = null, CancellationToken cancellationToken = default)
    {
        var fi = new FileInfo(filePath);
        var deadline = new MediaReadDeadline(TimeSpan.FromSeconds(30), cancellationToken);
        using var fc = disc is not null
            ? InterruptibleInputFormatContext.Open(disc, () => deadline.ShouldInterrupt)
            : InterruptibleInputFormatContext.Open(filePath, () => deadline.ShouldInterrupt);
        try { fc.LoadStreamInfo(); }
        catch { deadline.ThrowIfInterrupted(); throw; }
        deadline.ThrowIfInterrupted();

        // MediaStream 是值类型，这里用索引标记是否找到，避免默认值带来的误判。
        var selectedVideo = streamSelection is null ? (MediaStream?)null : PreviewStreamUtil.ResolveVideo(fc, streamSelection);
        bool Included(MediaStream stream) => streamSelection?.ProgramId is not { } programId ||
            fc.Programs.Any(program => program.Id == programId && program.ContainsStream(stream.Index));
        var videoIndex = selectedVideo?.Index ?? -1;
        var audioIndex = -1;
        for (var i = 0; i < fc.Streams.Count; i++)
        {
            if (!Included(fc.Streams[i])) continue;
            var type = fc.Streams[i].Codecpar!.CodecType;
            if (type == AVMediaType.AVMEDIA_TYPE_VIDEO && videoIndex < 0)
                videoIndex = i;
            else if (type == AVMediaType.AVMEDIA_TYPE_AUDIO && audioIndex < 0)
                audioIndex = i;
        }

        var videoStream = videoIndex >= 0 ? fc.Streams[videoIndex] : default;
        var audioStream = audioIndex >= 0 ? fc.Streams[audioIndex] : default;
        var hasVideo = videoIndex >= 0;
        var hasAudio = audioIndex >= 0;
        var sampleAspectRatio = hasVideo
            ? videoStream.Codecpar!.SampleAspectRatio
            : default;
        if (hasVideo && (sampleAspectRatio.num <= 0 || sampleAspectRatio.den <= 0))
            sampleAspectRatio = videoStream.SampleAspectRatio;

        // 时长优先取视频流，回退到容器时长。部分异常 TS 缺少流时长，
        // 此时容器已估算的时长更可靠（与 VideoInstance.ResolveTimelineDuration 的策略一致）。
        var duration = disc?.Duration ?? ResolveDuration(hasVideo ? videoStream : default, hasVideo, fc.Duration);
        var videoBitRate = hasVideo ? videoStream.Codecpar!.BitRate : 0;
        var videoBitRateEstimated = false;
        // 多节目或多视频流无法可靠地将容器总码率归属于当前视频。
        if (disc is null && hasVideo && videoBitRate <= 0 && fc.BitRate > 0 && fc.Programs.Count <= 1 &&
            fc.Streams.Count(stream => stream.Codecpar?.CodecType == AVMediaType.AVMEDIA_TYPE_VIDEO &&
                                      !stream.IsAttachedPicture) == 1)
        {
            var remainingBitRate = fc.BitRate;
            for (var i = 0; i < fc.Streams.Count; i++)
            {
                if (i == videoIndex)
                    continue;
                var bitRate = fc.Streams[i].Codecpar?.BitRate ?? 0;
                if (bitRate > 0)
                {
                    remainingBitRate = Math.Max(0, remainingBitRate - bitRate);
                    if (remainingBitRate == 0)
                        break;
                }
            }
            if (remainingBitRate > 0)
            {
                // 容器码率包含少量封装开销，因此只作为近似视频码率显示。
                videoBitRate = remainingBitRate;
                videoBitRateEstimated = true;
            }
        }

        var additionalVideoCodecs = new List<string>();
        var additionalAudioTracks = new List<ThumbnailSheetTrackInfo>();
        var subtitleTracks = new List<ThumbnailSheetTrackInfo>();
        for (var i = 0; i < fc.Streams.Count; i++)
        {
            if (!Included(fc.Streams[i])) continue;
            var codecParameters = fc.Streams[i].Codecpar!;
            if (!IsDisplayableStreamType(codecParameters.CodecType))
                continue;

            var codec = codecParameters.CodecName;
            if (string.IsNullOrEmpty(codec))
                continue;

            switch (codecParameters.CodecType)
            {
                case AVMediaType.AVMEDIA_TYPE_VIDEO when i != videoIndex:
                    additionalVideoCodecs.Add(codec);
                    break;
                case AVMediaType.AVMEDIA_TYPE_AUDIO when i != audioIndex:
                    additionalAudioTracks.Add(new ThumbnailSheetTrackInfo(
                        codec,
                        GetLanguage(fc.Streams[i]),
                        codecParameters.ChLayout.nb_channels,
                        codecParameters.SampleRate,
                        codecParameters.BitRate));
                    break;
                case AVMediaType.AVMEDIA_TYPE_SUBTITLE:
                    subtitleTracks.Add(new ThumbnailSheetTrackInfo(
                        codec,
                        GetLanguage(fc.Streams[i])));
                    break;
            }
        }

        return new ThumbnailSheetInfo
        {
            FileName = fi.Name,
            Format = disc is not null ? disc.Kind == DiscKind.Dvd ? "DVD-Video" : "Blu-ray"
                : fc.InputFormat?.Name ?? string.Empty,
            // 镜像大小和底层容器码率不代表当前标题；目录也没有单一文件大小。
            FileSize = disc is null && fi.Exists ? fi.Length : 0,
            Duration = duration,
            OverallBitRate = disc is null && fc.BitRate > 0 ? fc.BitRate : 0,
            VideoCodec = hasVideo ? videoStream.Codecpar!.CodecName : null,
            VideoWidth = hasVideo ? videoStream.Codecpar!.Width : 0,
            VideoHeight = hasVideo ? videoStream.Codecpar!.Height : 0,
            VideoScanMode = hasVideo
                ? GetVideoScanMode(videoStream.Codecpar!.FieldOrder)
                : VideoScanMode.Unknown,
            VideoDynamicRange = hasVideo
                ? VideoDynamicRangeDetector.Detect(videoStream.Codecpar!)
                : VideoDynamicRange.Standard,
            VideoDisplayAspectRatio = hasVideo
                ? CalculateDisplayAspectRatio(
                    videoStream.Codecpar!.Width,
                    videoStream.Codecpar!.Height,
                    sampleAspectRatio.num,
                    sampleAspectRatio.den)
                : 0,
            VideoFrameRate = hasVideo && videoStream.AvgFrameRate.num > 0
                ? videoStream.AvgFrameRate.ToDouble()
                : 0,
            VideoBitRate = videoBitRate,
            VideoBitRateEstimated = videoBitRateEstimated,
            AudioCodec = hasAudio ? audioStream.Codecpar!.CodecName : null,
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
        mediaType is AVMediaType.AVMEDIA_TYPE_VIDEO or AVMediaType.AVMEDIA_TYPE_AUDIO or AVMediaType.AVMEDIA_TYPE_SUBTITLE;

    internal static VideoScanMode GetVideoScanMode(AVFieldOrder fieldOrder) => fieldOrder switch
    {
        AVFieldOrder.AV_FIELD_PROGRESSIVE => VideoScanMode.Progressive,
        AVFieldOrder.AV_FIELD_TT or AVFieldOrder.AV_FIELD_BB or AVFieldOrder.AV_FIELD_TB or AVFieldOrder.AV_FIELD_BT =>
            VideoScanMode.Interlaced,
        _ => VideoScanMode.Unknown
    };

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
            var seconds = videoStream.Duration * FF.av_q2d(videoStream.TimeBase);
            if (seconds > 0)
                return TimeSpan.FromSeconds(seconds);
        }

        return containerDuration > 0
            ? TimeSpan.FromSeconds(containerDuration / 1_000_000.0)
            : TimeSpan.Zero;
    }
}
