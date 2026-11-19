using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Player;
using TSCutter.GUI.Rendering.Gpu;
using TSCutter.GUI.Tests.FFmpeg;
using Xunit;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Tests.Player;

/// <summary>播放用的定时器精度申请与超大软件帧缩小。</summary>
public sealed class PlaybackRenderingHelpersTests
{
    [Fact]
    public void TimerResolutionLeasesNestAndReleaseOnce()
    {
        // 多个会话交错申请和归还；重复释放不应多归还一次，也不抛出异常。
        var first = PlaybackTimerResolution.Acquire();
        var second = PlaybackTimerResolution.Acquire();
        first.Dispose();
        first.Dispose();
        second.Dispose();
        using var third = PlaybackTimerResolution.Acquire();
    }

    [NativeMediaFact]
    public unsafe void LargeSoftwareFramesAreScaledToFourKWithColorDescription()
    {
        using var source = Frame.CreateVideo(7680, 4320, AVPixelFormat.AV_PIX_FMT_YUV420P10LE);
        AVFrame* raw = source;
        raw->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67;
        raw->color_primaries = AVColorPrimaries.AVCOL_PRI_BT2020;
        raw->colorspace = AVColorSpace.AVCOL_SPC_BT2020_NCL;
        raw->sample_aspect_ratio = new AVRational { num = 1, den = 1 };
        // 10 位渐变图案，检验缩放结果而不只是尺寸。
        for (uint plane = 0; plane < 3; plane++)
        {
            var height = plane == 0 ? raw->height : raw->height / 2;
            var width = plane == 0 ? raw->width : raw->width / 2;
            for (var y = 0; y < height; y++)
            {
                var row = (ushort*)(raw->data[plane] + (long)y * raw->linesize[plane]);
                for (var x = 0; x < width; x++)
                    row[x] = (ushort)((x * 7 + y * 3 + (int)plane * 101) % 1024);
            }
        }
        using var downscaler = new UploadDownscaler();
        Assert.True(UploadDownscaler.Applies(source));
        using var scaled = downscaler.TryScale(source);
        Assert.NotNull(scaled);
        AVFrame* output = scaled;
        Assert.Equal(3840, output->width);
        Assert.Equal(2160, output->height);
        Assert.Equal(raw->format, output->format);
        Assert.Equal(AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67, output->color_trc);
        Assert.Equal(AVColorPrimaries.AVCOL_PRI_BT2020, output->color_primaries);
        Assert.Equal(AVColorSpace.AVCOL_SPC_BT2020_NCL, output->colorspace);
        Assert.Equal(1, output->sample_aspect_ratio.num);

        // 多线程缩放与单线程逐像素一致。
        using var reference = Frame.CreateVideo(3840, 2160, AVPixelFormat.AV_PIX_FMT_YUV420P10LE);
        AVFrame* expected = reference;
        var context = FF.sws_getCachedContext(null, raw->width, raw->height, (AVPixelFormat)raw->format,
            3840, 2160, (AVPixelFormat)raw->format, 2, null, null, null);
        try
        {
            Assert.True(FF.sws_scale(context, (byte**)&raw->data, (int*)&raw->linesize, 0, raw->height,
                (byte**)&expected->data, (int*)&expected->linesize) > 0);
        }
        finally { FF.sws_freeContext(context); }
        for (uint plane = 0; plane < 3; plane++)
        {
            var height = plane == 0 ? 2160 : 1080;
            var bytes = (plane == 0 ? 3840 : 1920) * 2;
            for (var y = 0; y < height; y++)
                Assert.True(new ReadOnlySpan<byte>(expected->data[plane] + (long)y * expected->linesize[plane], bytes)
                    .SequenceEqual(new ReadOnlySpan<byte>(output->data[plane] + (long)y * output->linesize[plane], bytes)),
                    $"plane {plane} row {y} differs");
        }

        // 4K 及以下不缩放，原样上传。
        using var regular = Frame.CreateVideo(3840, 2160, AVPixelFormat.AV_PIX_FMT_YUV420P10LE);
        Assert.False(UploadDownscaler.Applies(regular));
        Assert.Null(downscaler.TryScale(regular));
    }
}
