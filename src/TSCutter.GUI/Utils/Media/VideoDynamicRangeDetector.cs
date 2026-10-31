using TSCutter.GUI.FFmpeg;
using FFmpeg.AutoGen.Abstractions;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;
using TSCutter.GUI.Models.Preview;

namespace TSCutter.GUI.Utils.Media;

internal static class VideoDynamicRangeDetector
{
    public static VideoDynamicRange Detect(CodecParameters codecParameters)
    {
        var hasDolbyVisionConfig = HasCodecSideData(codecParameters, AVPacketSideDataType.AV_PKT_DATA_DOVI_CONF);
        var hasHdrMetadata =
            HasCodecSideData(codecParameters, AVPacketSideDataType.AV_PKT_DATA_MASTERING_DISPLAY_METADATA)
            || HasCodecSideData(codecParameters, AVPacketSideDataType.AV_PKT_DATA_CONTENT_LIGHT_LEVEL)
            || HasCodecSideData(codecParameters, AVPacketSideDataType.AV_PKT_DATA_DYNAMIC_HDR10_PLUS);
        return Detect(
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

    public static VideoDynamicRange Detect(
        AVColorTransferCharacteristic colorTransfer,
        bool hasDolbyVisionMetadata,
        bool hasHdrMetadata,
        uint codecTag)
    {
        if (hasDolbyVisionMetadata || IsDolbyVisionCodecTag(codecTag))
            return VideoDynamicRange.DolbyVision;
        if (colorTransfer == AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67)
            return VideoDynamicRange.Hlg;
        if (colorTransfer == AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 || hasHdrMetadata)
            return VideoDynamicRange.Hdr;
        return VideoDynamicRange.Standard;
    }

    private static bool IsDolbyVisionCodecTag(uint codecTag) =>
        codecTag == MakeCodecTag('d', 'v', 'h', 'e')
        || codecTag == MakeCodecTag('d', 'v', 'h', '1')
        || codecTag == MakeCodecTag('d', 'v', 'a', 'v')
        || codecTag == MakeCodecTag('d', 'v', 'a', '1');

    internal static uint MakeCodecTag(char a, char b, char c, char d) =>
        (uint)a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);
}
