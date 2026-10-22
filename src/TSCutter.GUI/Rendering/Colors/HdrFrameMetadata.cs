using System;
using FFmpeg.AutoGen.Abstractions;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Rendering.Colors;

internal enum HdrTransfer { Pq, Hlg }

internal sealed unsafe class HdrFrameMetadata
{
    internal ReshapeCurve[]? Curves { get; init; }
    internal required ColorMatrix Nonlinear { get; init; }
    internal required ColorMatrix Linear { get; init; }
    internal required float[] Offset { get; init; }
    internal int BitDepth { get; init; }
    internal float PeakNits { get; init; }
    internal HdrTransfer Transfer { get; init; }

    private static readonly ColorMatrix Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);
    private static readonly ColorMatrix LmsTo2020 = new(
        3.06441879f, -2.16597676f, 0.10155818f,
        -0.65612108f, 1.78554118f, -0.12943749f,
        0.01736321f, -0.04725154f, 1.03004253f);
    internal static readonly ColorMatrix From2020To709 = new(
        1.660491f, -0.587641f, -0.072850f,
        -0.124550f, 1.132900f, -0.008349f,
        -0.018151f, -0.100579f, 1.118730f);

    internal static HdrFrameMetadata FromDolby(DolbyVisionMetadata metadata) => new()
    {
        Curves = metadata.Curves, Nonlinear = metadata.Nonlinear,
        Linear = From2020To709 * LmsTo2020 * metadata.Linear,
        Offset = metadata.Offset, BitDepth = metadata.BitDepth,
        PeakNits = metadata.PeakNits, Transfer = HdrTransfer.Pq
    };

    internal static HdrFrameMetadata? Read(AVFrame* frame)
    {
        if (DolbyVisionMetadata.Read(frame) is { } dolby) return FromDolby(dolby);
        if (frame->color_trc is not (AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084
            or AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67)) return null;
        var descriptor = FF.av_pix_fmt_desc_get((AVPixelFormat)frame->format);
        if (descriptor == null) throw new NotSupportedException("Unsupported HDR pixel format.");
        return Create(frame->color_trc, frame->color_primaries, frame->colorspace, frame->color_range,
            descriptor->comp[0].depth, ReadPeakNits(frame));
    }

    internal static HdrFrameMetadata Create(AVColorTransferCharacteristic transfer, AVColorPrimaries primaries,
        AVColorSpace matrix, AVColorRange range, int depth, float peakNits = 1000)
    {
        if (depth is < 8 or > 16) throw new NotSupportedException("Unsupported HDR bit depth.");
        var hlg = transfer == AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67;
        if (!hlg && transfer != AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084)
            throw new NotSupportedException("Unsupported HDR transfer function.");
        var (kr, kb) = matrix switch
        {
            AVColorSpace.AVCOL_SPC_UNSPECIFIED or AVColorSpace.AVCOL_SPC_BT2020_NCL => (0.2627f, 0.0593f),
            AVColorSpace.AVCOL_SPC_BT709 => (0.2126f, 0.0722f),
            AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M => (0.299f, 0.114f),
            _ => throw new NotSupportedException($"Unsupported HDR color matrix: {matrix}.")
        };
        var maximum = (1 << depth) - 1f;
        var codeScale = (1 << (depth - 8)) / maximum;
        var full = range == AVColorRange.AVCOL_RANGE_JPEG;
        var yScale = full ? 1 : 1 / (219 * codeScale);
        var cScale = full ? 1 : 1 / (224 * codeScale);
        var kg = 1 - kr - kb;
        var nonlinear = new ColorMatrix(
            yScale, 0, 2 * (1 - kr) * cScale,
            yScale, -2 * kb * (1 - kb) / kg * cScale, -2 * kr * (1 - kr) / kg * cScale,
            yScale, 2 * (1 - kb) * cScale, 0);
        var linear = primaries switch
        {
            AVColorPrimaries.AVCOL_PRI_UNSPECIFIED or AVColorPrimaries.AVCOL_PRI_BT2020 => From2020To709,
            AVColorPrimaries.AVCOL_PRI_BT709 => Identity,
            AVColorPrimaries.AVCOL_PRI_SMPTE432 => new ColorMatrix(
                1.224745f, -0.224904f, 0, -0.042058f, 1.042081f, 0, -0.019642f, -0.078655f, 1.098537f),
            _ => throw new NotSupportedException($"Unsupported HDR color primaries: {primaries}.")
        };
        return new HdrFrameMetadata
        {
            Nonlinear = nonlinear, Linear = linear,
            Offset = [full ? 0 : 16 * codeScale, 128 * codeScale, 128 * codeScale],
            BitDepth = depth, Transfer = hlg ? HdrTransfer.Hlg : HdrTransfer.Pq,
            // HLG 使用 BT.2100 的 1000 nit、gamma=1.2 参考显示，不依赖系统 HDR 状态。
            PeakNits = hlg ? 1000 : peakNits
        };
    }

    internal static float ReadPeakNits(AVFrame* frame)
    {
        var light = FF.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_CONTENT_LIGHT_LEVEL);
        if (light != null && light->size >= (ulong)sizeof(AVContentLightMetadata))
        {
            var peak = ((AVContentLightMetadata*)light->data)->MaxCLL;
            if (peak is > 0 and <= 10000) return peak;
        }
        var mastering = FF.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA);
        if (mastering != null && mastering->size >= (ulong)sizeof(AVMasteringDisplayMetadata))
        {
            var data = (AVMasteringDisplayMetadata*)mastering->data;
            if (data->has_luminance != 0 && data->max_luminance.den != 0)
            {
                var peak = data->max_luminance.num / (float)data->max_luminance.den;
                if (peak is > 0 and <= 10000) return peak;
            }
        }
        return 1000;
    }
}
