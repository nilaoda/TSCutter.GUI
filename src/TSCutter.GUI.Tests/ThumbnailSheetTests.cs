using Avalonia;
using Sdcb.FFmpeg.Raw;
using TSCutter.GUI.Models;
using TSCutter.GUI.Rendering;
using TSCutter.GUI.Utils;
using TSCutter.GUI.ViewModels;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class ThumbnailSheetTests
{
    [Theory]
    [InlineData(AVMediaType.Video, true)]
    [InlineData(AVMediaType.Audio, true)]
    [InlineData(AVMediaType.Subtitle, true)]
    [InlineData(AVMediaType.Data, false)]
    [InlineData(AVMediaType.Attachment, false)]
    [InlineData(AVMediaType.Unknown, false)]
    public void HeaderOnlyIncludesAudioVideoAndSubtitleStreams(
        AVMediaType mediaType,
        bool expected)
    {
        Assert.Equal(expected, ThumbnailSheetInfoBuilder.IsDisplayableStreamType(mediaType));
    }

    [Theory]
    [InlineData(AVFieldOrder.Progressive, VideoScanMode.Progressive)]
    [InlineData(AVFieldOrder.Tt, VideoScanMode.Interlaced)]
    [InlineData(AVFieldOrder.Bb, VideoScanMode.Interlaced)]
    [InlineData(AVFieldOrder.Tb, VideoScanMode.Interlaced)]
    [InlineData(AVFieldOrder.Bt, VideoScanMode.Interlaced)]
    [InlineData(AVFieldOrder.Unknown, VideoScanMode.Unknown)]
    public void FieldOrderMapsToScanMode(AVFieldOrder fieldOrder, VideoScanMode expected)
    {
        Assert.Equal(expected, ThumbnailSheetInfoBuilder.GetVideoScanMode(fieldOrder));
    }

    [Theory]
    [InlineData(1920, 1080, 1, 1, 16d / 9d)]
    [InlineData(1440, 1080, 1, 1, 4d / 3d)]
    [InlineData(720, 576, 16, 15, 4d / 3d)]
    [InlineData(720, 576, 64, 45, 16d / 9d)]
    [InlineData(1080, 1920, 0, 0, 9d / 16d)]
    public void DisplayAspectRatioIncludesSampleAspectRatio(
        int width,
        int height,
        int sarNumerator,
        int sarDenominator,
        double expected)
    {
        var result = ThumbnailSheetInfoBuilder.CalculateDisplayAspectRatio(
            width,
            height,
            sarNumerator,
            sarDenominator);

        Assert.Equal(expected, result, 6);
    }

    [Fact]
    public void HeaderLabelsAndGroupsTracksByType()
    {
        var info = new ThumbnailSheetInfo
        {
            FileName = "sample.ts",
            Duration = TimeSpan.FromSeconds(65),
            VideoCodec = "Hevc",
            VideoWidth = 3840,
            VideoHeight = 2160,
            VideoFrameRate = 50,
            AudioCodec = "Ac3",
            AudioChannels = 6,
            AudioSampleRate = 48000,
            AdditionalVideoCodecs = ["Mjpeg"],
            AdditionalAudioCodecs = ["Aac"],
            SubtitleCodecs = ["SubRip", "HdmvPgsSubtitle"]
        };

        var lines = ThumbnailSheetWindowViewModel.BuildHeaderLines(
            info,
            "fallback.ts",
            "文件名：",
            "文件信息：",
            "视频轨道：",
            "音频轨道：",
            "字幕轨道：");

        Assert.Equal(5, lines.Count);
        Assert.Equal("文件名：sample.ts", lines[0]);
        Assert.StartsWith("文件信息：", lines[1]);
        Assert.Equal("视频轨道：Hevc 3840x2160 50fps  |  Mjpeg", lines[2]);
        Assert.Equal("音频轨道：Ac3 6ch 48kHz  |  Aac", lines[3]);
        Assert.Equal("字幕轨道：SubRip  |  HdmvPgsSubtitle", lines[4]);
    }

    [Fact]
    public void HeaderOmitsMissingTrackCategories()
    {
        var info = new ThumbnailSheetInfo
        {
            FileName = "audio-only.ts",
            AudioCodec = "Aac"
        };

        var lines = ThumbnailSheetWindowViewModel.BuildHeaderLines(
            info,
            "fallback.ts",
            "File: ",
            "Info: ",
            "Video: ",
            "Audio: ",
            "Subtitle: ");

        Assert.Equal(["File: audio-only.ts", "Audio: Aac"], lines);
    }

    [Theory]
    [InlineData(VideoScanMode.Progressive, "Video: H264 1920x1080p")]
    [InlineData(VideoScanMode.Interlaced, "Video: H264 1920x1080i")]
    [InlineData(VideoScanMode.Unknown, "Video: H264 1920x1080")]
    public void HeaderAppendsScanSuffixOnlyWhenKnown(VideoScanMode scanMode, string expected)
    {
        var info = new ThumbnailSheetInfo
        {
            VideoCodec = "H264",
            VideoWidth = 1920,
            VideoHeight = 1080,
            VideoScanMode = scanMode
        };

        var lines = ThumbnailSheetWindowViewModel.BuildHeaderLines(
            info,
            string.Empty,
            "File: ",
            "Info: ",
            "Video: ",
            "Audio: ",
            "Subtitle: ");

        Assert.Equal([expected], lines);
    }

    [Fact]
    public void MultiLineHeaderReservesMoreSpaceBeforeGrid()
    {
        var twoLines = ThumbnailSheetComposer.CalculateLayout(
            4,
            3,
            new PixelSize(320, 180),
            showHeader: true,
            showCaption: true,
            headerLineCount: 2);
        var fourLines = ThumbnailSheetComposer.CalculateLayout(
            4,
            3,
            new PixelSize(320, 180),
            showHeader: true,
            showCaption: true,
            headerLineCount: 4);

        Assert.Equal(twoLines.Width, fourLines.Width);
        Assert.True(fourLines.HeaderHeight > twoLines.HeaderHeight);
        Assert.Equal(
            fourLines.HeaderHeight - twoLines.HeaderHeight,
            fourLines.Height - twoLines.Height);
    }

    [Fact]
    public void FiveThousandPixelOutputProducesLargePortraitSheetWithEightRows()
    {
        var cellWidth = ThumbnailSheetComposer.CalculateCellWidthForOutputWidth(4, 5000);
        var layout = ThumbnailSheetComposer.CalculateLayout(
            4,
            8,
            new PixelSize(cellWidth, (int)Math.Round(cellWidth * 9d / 16d)),
            showHeader: true,
            showCaption: true,
            headerLineCount: 4);

        Assert.Equal(1236, cellWidth);
        Assert.Equal(5000, layout.Width);
        Assert.InRange(layout.Height, 6400, 6470);
        Assert.True((long)layout.Width * layout.Height < ThumbnailSheetComposer.MaximumPixelCount);
    }

    [Fact]
    public void CaptionMetricsScaleWithCellWidthAndLayoutReservesTheScaledHeight()
    {
        var smallCaptionHeight = ThumbnailSheetComposer.CalculateCaptionHeight(320);
        var largeCaptionHeight = ThumbnailSheetComposer.CalculateCaptionHeight(1280);
        var withoutCaption = ThumbnailSheetComposer.CalculateLayout(
            1,
            1,
            new PixelSize(1280, 720),
            showHeader: false,
            showCaption: false);
        var withCaption = ThumbnailSheetComposer.CalculateLayout(
            1,
            1,
            new PixelSize(1280, 720),
            showHeader: false,
            showCaption: true);

        Assert.Equal(18, smallCaptionHeight);
        Assert.Equal(72, largeCaptionHeight);
        Assert.Equal(46, ThumbnailSheetComposer.CalculateCaptionFontSize(1280), 3);
        Assert.Equal(largeCaptionHeight, withCaption.Height - withoutCaption.Height);
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, false, false, false)]
    public void PreviewCanOnlyBeExportedWhenItMatchesCurrentParameters(
        bool hasPreview,
        bool isDirty,
        bool isBusy,
        bool expected)
    {
        Assert.Equal(
            expected,
            ThumbnailSheetWindowViewModel.IsPreviewReadyForExport(
                hasPreview,
                isDirty,
                isBusy));
    }

    [Fact]
    public void OutputWidthUsesFixedPresetsWithFourThousandAsDefault()
    {
        Assert.Equal(4000, ThumbnailSheetWindowViewModel.DefaultOutputWidth);
        Assert.Equal(
            [1280d, 1920d, 2560d, 3840d, 4000d, 5000d, 6000d, 7680d, 10000d, 12000d],
            ThumbnailSheetWindowViewModel.SupportedOutputWidths);
        Assert.True(ThumbnailSheetWindowViewModel.IsSupportedOutputWidth(4000));
        Assert.False(ThumbnailSheetWindowViewModel.IsSupportedOutputWidth(4096));
    }

    [Theory]
    [InlineData(12000, 12000)]
    [InlineData(5500, 5000)]
    [InlineData(3999, 3840)]
    [InlineData(1200, 0)]
    public void OversizeHintSelectsLargestAvailableSafePreset(int maximum, int expected)
    {
        Assert.Equal(expected, ThumbnailSheetWindowViewModel.FindLargestSupportedOutputWidth(maximum));
    }

    [Fact]
    public void HiddenHeaderDoesNotReserveSpaceForHeaderLines()
    {
        var oneLine = ThumbnailSheetComposer.CalculateLayout(
            4,
            3,
            new PixelSize(320, 180),
            showHeader: false,
            showCaption: true,
            headerLineCount: 1);
        var manyLines = ThumbnailSheetComposer.CalculateLayout(
            4,
            3,
            new PixelSize(320, 180),
            showHeader: false,
            showCaption: true,
            headerLineCount: 8);

        Assert.Equal(oneLine, manyLines);
        Assert.Equal(0, manyLines.HeaderHeight);
    }
}
