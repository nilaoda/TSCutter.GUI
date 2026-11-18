using System;
using FFmpeg.AutoGen.Abstractions;

namespace TSCutter.GUI.Utils.Media;

/// <summary>
/// 开放 GOP 的前导画面：排在关键帧之后、显示时间却更早，解码需要上一个 GOP 的参考帧。
/// 从该关键帧开始剪辑时只能丢弃它们；只有它们不被其他画面参考，丢弃后其余画面才能正确解码。
/// </summary>
internal static class LeadingPicture
{
    /// <summary>
    /// 前导画面能否丢弃：MPEG-1/2 的 B 帧从不作参考；H.264 要求各切片的 nal_ref_idc 为 0；
    /// HEVC 的前导画面（RASL/RADL）按规范不被其后的画面参考。其他编码无法判断，按不能丢弃处理。
    /// </summary>
    public static bool CanDrop(AVCodecID codec, ReadOnlySpan<byte> packet, ReadOnlySpan<byte> extradata) => codec switch
    {
        AVCodecID.AV_CODEC_ID_MPEG1VIDEO or AVCodecID.AV_CODEC_ID_MPEG2VIDEO => true,
        AVCodecID.AV_CODEC_ID_H264 => AllSlicesDroppable(packet, LengthSize(extradata, 4), hevc: false),
        AVCodecID.AV_CODEC_ID_HEVC => AllSlicesDroppable(packet, LengthSize(extradata, 21), hevc: true),
        _ => false
    };

    /// <summary>
    /// NAL 长度字段的字节数，取自 MP4/Matroska 的 avcC/hvcC 配置记录（首字节为 1）；没有配置记录时为 0，表示起始码格式（TS、光盘）。
    /// 不能按包开头是否像起始码判断：长度在 256 到 511 字节之间的长度字段正好是 00 00 01 xx。
    /// </summary>
    private static int LengthSize(ReadOnlySpan<byte> extradata, int offset) =>
        extradata.Length > offset && extradata[0] == 1 ? (extradata[offset] & 3) + 1 : 0;

    private static bool AllSlicesDroppable(ReadOnlySpan<byte> packet, int lengthSize, bool hevc)
    {
        var slices = 0;
        var annexB = lengthSize == 0;
        var offset = 0;
        while (offset < packet.Length)
        {
            int header;
            if (annexB)
            {
                var start = packet[offset..].IndexOf((ReadOnlySpan<byte>)[0, 0, 1]);
                if (start < 0 || offset + start + 3 >= packet.Length)
                    break;
                header = offset + start + 3;
                offset = header + 1;
            }
            else
            {
                if (offset + lengthSize >= packet.Length)
                    break;
                var length = 0;
                foreach (var value in packet.Slice(offset, lengthSize))
                    length = length << 8 | value;
                header = offset + lengthSize;
                if (length <= 0 || length > packet.Length - header)
                    return false;
                offset = header + length;
            }
            var nal = packet[header];
            if (hevc)
            {
                var type = (nal >> 1) & 0x3F;
                if (type > 31)
                    continue;
                // RADL_N、RADL_R、RASL_N、RASL_R。
                if (type is not (6 or 7 or 8 or 9))
                    return false;
            }
            else
            {
                var type = nal & 0x1F;
                if (type is < 1 or > 5)
                    continue;
                if ((nal >> 5 & 3) != 0)
                    return false;
            }
            slices++;
        }
        return slices > 0;
    }
}
