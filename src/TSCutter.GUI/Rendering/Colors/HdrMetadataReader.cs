using System;
using FFmpeg.AutoGen.Abstractions;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Rendering.Colors;

internal sealed unsafe class HdrMetadataReader
{
    private const int MaximumCachedMetadataBytes = 64 * 1024;
    private byte[] dolbyBytes = [];
    private int dolbyLength;
    private HdrFrameMetadata? dolbyMetadata, genericMetadata;
    private ColorKey? previousColor;

    internal HdrFrameMetadata? Read(AVFrame* frame)
    {
        var side = FF.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DOVI_METADATA);
        if (side != null)
        {
            // 比较实际内容而非原生指针：FFmpeg 会复用地址，RPU 也可能逐帧变化。
            if (side->data != null && side->size <= MaximumCachedMetadataBytes)
            {
                var bytes = new ReadOnlySpan<byte>(side->data, (int)side->size);
                if (dolbyMetadata is not null && bytes.Length == dolbyLength
                    && bytes.SequenceEqual(dolbyBytes.AsSpan(0, dolbyLength))) return dolbyMetadata;
            }
            if (DolbyVisionMetadata.Read(frame) is { } dolby)
            {
                var result = HdrFrameMetadata.FromDolby(dolby);
                if (side->size <= MaximumCachedMetadataBytes)
                {
                    var length = (int)side->size;
                    if (dolbyBytes.Length < length) dolbyBytes = new byte[length];
                    new ReadOnlySpan<byte>(side->data, length).CopyTo(dolbyBytes);
                    dolbyLength = length;
                    dolbyMetadata = result;
                }
                return result;
            }
        }
        if (frame->color_trc is not (AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084
            or AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67)) return null;
        var descriptor = FF.av_pix_fmt_desc_get((AVPixelFormat)frame->format);
        if (descriptor == null) throw new NotSupportedException("Unsupported HDR pixel format.");
        var key = new ColorKey(frame->color_trc, frame->color_primaries, frame->colorspace,
            frame->color_range, descriptor->comp[0].depth, HdrFrameMetadata.ReadPeakNits(frame));
        if (previousColor != key)
        {
            genericMetadata = HdrFrameMetadata.Create(key.Transfer, key.Primaries, key.Matrix, key.Range, key.Depth, key.Peak);
            previousColor = key;
        }
        return genericMetadata;
    }

    private readonly record struct ColorKey(AVColorTransferCharacteristic Transfer, AVColorPrimaries Primaries,
        AVColorSpace Matrix, AVColorRange Range, int Depth, float Peak);
}
