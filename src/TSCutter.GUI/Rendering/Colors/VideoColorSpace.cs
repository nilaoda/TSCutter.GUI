using FFmpeg.AutoGen.Abstractions;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Rendering.Colors;

internal static unsafe class VideoColorSpace
{
    internal static AVColorSpace ResolveMatrix(AVFrame* frame)
        => frame->colorspace == AVColorSpace.AVCOL_SPC_UNSPECIFIED
            ? frame->width >= 1280 || frame->height > 576
                ? AVColorSpace.AVCOL_SPC_BT709 : AVColorSpace.AVCOL_SPC_SMPTE170M
            : frame->colorspace;

    internal static bool IsFullRange(AVFrame* frame)
    {
        var descriptor = FF.av_pix_fmt_desc_get((AVPixelFormat)frame->format);
        return frame->color_range == AVColorRange.AVCOL_RANGE_JPEG
            || descriptor != null && (descriptor->flags & ffmpeg.AV_PIX_FMT_FLAG_RGB) != 0
            || (AVPixelFormat)frame->format is AVPixelFormat.AV_PIX_FMT_YUVJ420P or AVPixelFormat.AV_PIX_FMT_YUVJ422P
                or AVPixelFormat.AV_PIX_FMT_YUVJ444P or AVPixelFormat.AV_PIX_FMT_YUVJ440P or AVPixelFormat.AV_PIX_FMT_YUVJ411P;
    }
}
