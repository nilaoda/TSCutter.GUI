using FFmpeg.AutoGen.Abstractions;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Utils.Media;

internal static class RemuxCodecTag
{
    internal const uint Hvc1 = 0x31637668;
    internal const uint Hev1 = 0x31766568;
    private const uint Dvhe = 0x65687664;
    private const uint Dvh1 = 0x31687664;

    public static unsafe uint Select(string container, AVCodecParameters* parameters)
    {
        // 非 MP4 输出清除源 FourCC；MKV 通过原生 CodecID 保存 HEVC。
        if (container != "mp4" || parameters->codec_id != AVCodecID.AV_CODEC_ID_HEVC)
            return 0;
        // MKV 和部分 MP4 通过侧数据携带杜比配置，不能仅凭 dvhe／dvh1 标记识别。
        // 基础层兼容编号为 0（如 Profile 5）只能按杜比视界解码，标记 dvh1；基础层兼容 HDR10/SDR/HLG 的配置
        // （如 8.1/8.2/8.4）保留 hvc1，不支持杜比视界的播放器仍可按基础层播放，配置记录照常写入 dvcC/dvvC。
        for (var index = 0; parameters->coded_side_data != null && index < parameters->nb_coded_side_data; index++)
        {
            var side = parameters->coded_side_data[index];
            // FFmpeg 9 的配置记录为独立字节字段，第 8 个字节是 dv_bl_signal_compatibility_id。
            if (side.type == AVPacketSideDataType.AV_PKT_DATA_DOVI_CONF && side.data != null && side.size >= 8)
                return side.data[7] == 0 ? Dvh1 : Hvc1;
        }
        return parameters->codec_tag is Dvhe or Dvh1 ? Dvh1 : Hvc1;
    }

    /// <summary>
    /// 音频只声明了声道数、没有声道布局时（相机 MOV 的 lpcm 常见）按声道数补上默认布局，与 ffmpeg 命令行的推测一致；
    /// MP4 写入 PCM（ipcm 与 chnl）时必须有明确的布局，否则写入失败（Invalid argument）。
    /// </summary>
    public static unsafe void FillChannelLayout(AVCodecParameters* parameters)
    {
        if (parameters->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO ||
            parameters->ch_layout.order != AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC || parameters->ch_layout.nb_channels <= 0)
            return;
        var channels = parameters->ch_layout.nb_channels;
        FF.av_channel_layout_uninit(&parameters->ch_layout);
        FF.av_channel_layout_default(&parameters->ch_layout, channels);
    }
}
