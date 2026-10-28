using FFmpeg.AutoGen.Abstractions;

namespace TSCutter.GUI.Utils.Media;

internal static class RemuxCodecTag
{
    private const uint Hvc1 = 0x31637668;
    internal const uint Hev1 = 0x31766568;
    private const uint Dvhe = 0x65687664;
    private const uint Dvh1 = 0x31687664;

    public static unsafe uint Select(string container, AVCodecParameters* parameters)
    {
        // 非 MP4 输出清除源 FourCC；MKV 通过原生 CodecID 保存 HEVC。
        if (container != "mp4" || parameters->codec_id != AVCodecID.AV_CODEC_ID_HEVC)
            return 0;
        // MKV 和部分 MP4 通过侧数据携带杜比配置，不能仅凭 dvhe／dvh1 标记识别。
        for (var index = 0; parameters->coded_side_data != null && index < parameters->nb_coded_side_data; index++)
        {
            var side = parameters->coded_side_data[index];
            if (side.type == AVPacketSideDataType.AV_PKT_DATA_DOVI_CONF && side.data != null && side.size >= 8)
                return Dvh1;
        }
        return parameters->codec_tag is Dvhe or Dvh1 ? Dvh1 : Hvc1;
    }
}
