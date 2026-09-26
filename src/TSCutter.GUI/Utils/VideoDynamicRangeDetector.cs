using Sdcb.FFmpeg.Raw;
using TSCutter.GUI.Models;

namespace TSCutter.GUI.Utils;

internal static class VideoDynamicRangeDetector
{
    public static VideoDynamicRange Detect(
        AVColorTransferCharacteristic colorTransfer,
        bool hasDolbyVisionMetadata,
        bool hasHdrMetadata,
        uint codecTag)
    {
        if (hasDolbyVisionMetadata || IsDolbyVisionCodecTag(codecTag))
            return VideoDynamicRange.DolbyVision;
        if (colorTransfer == AVColorTransferCharacteristic.AribStdB67)
            return VideoDynamicRange.Hlg;
        if (colorTransfer == AVColorTransferCharacteristic.Smpte2084 || hasHdrMetadata)
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
