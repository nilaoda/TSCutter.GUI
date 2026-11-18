using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.Utils.Media;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

/// <summary>
/// 开放 GOP 起点的前导画面能否丢弃：H.264 按切片的 nal_ref_idc，HEVC 只有 RASL/RADL，MPEG-2 的 B 帧总能丢弃，
/// 其他编码不能判断。TS/光盘的起始码格式与 MP4/Matroska 的长度前缀格式（长度字段字节数取自 avcC/hvcC）都要识别。
/// </summary>
public sealed class LeadingPictureTests
{
    [Fact]
    public void H264LeadingPicturesCanBeDroppedOnlyWhenNoSliceIsAReference()
    {
        // AUD、SEI 不是切片，不影响判断；切片 nal_ref_idc 为 0 时可以丢弃。
        Assert.True(CanDrop(AVCodecID.AV_CODEC_ID_H264, [0, 0, 0, 1, 0x09, 0xF0, 0, 0, 1, 0x06, 0x05, 0, 0, 1, 0x01, 0x9A], []));
        Assert.False(CanDrop(AVCodecID.AV_CODEC_ID_H264, [0, 0, 0, 1, 0x21, 0x9A], []));
        // 同一画面的多个切片中只要有一个是参考切片就不能丢弃。
        Assert.False(CanDrop(AVCodecID.AV_CODEC_ID_H264, [0, 0, 1, 0x01, 0x9A, 0, 0, 1, 0x41, 0x9A], []));
        // 没有切片时无法判断。
        Assert.False(CanDrop(AVCodecID.AV_CODEC_ID_H264, [0, 0, 1, 0x09, 0xF0], []));

        byte[] avcC4 = [1, 100, 0, 40, 0xFF];
        byte[] avcC2 = [1, 100, 0, 40, 0xFD];
        Assert.True(CanDrop(AVCodecID.AV_CODEC_ID_H264, [0, 0, 0, 2, 0x01, 0x9A], avcC4));
        Assert.False(CanDrop(AVCodecID.AV_CODEC_ID_H264, [0, 0, 0, 2, 0x21, 0x9A], avcC4));
        Assert.True(CanDrop(AVCodecID.AV_CODEC_ID_H264, [0, 2, 0x06, 0x05, 0, 2, 0x01, 0x9A], avcC2));
        // 长度为 256 字节的 NAL：长度字段 00 00 01 00 看起来像起始码，仍按长度前缀解析。
        Assert.True(CanDrop(AVCodecID.AV_CODEC_ID_H264, Prefixed(0x01, 256), avcC4));
        Assert.False(CanDrop(AVCodecID.AV_CODEC_ID_H264, Prefixed(0x21, 256), avcC4));
        // 长度字段超出包长时视为损坏，不能丢弃。
        Assert.False(CanDrop(AVCodecID.AV_CODEC_ID_H264, [0, 0, 0, 9, 0x01, 0x9A], avcC4));
    }

    [Fact]
    public void HevcLeadingPicturesCanBeDroppedOnlyWhenTheyAreRaslOrRadl()
    {
        // RASL_N（8）、RADL_R（7）可以丢弃；前缀 SEI（39）不是切片。
        Assert.True(CanDrop(AVCodecID.AV_CODEC_ID_HEVC, [0, 0, 1, 0x4E, 0x01, 0x05, 0, 0, 1, 0x10, 0x01, 0xAF], []));
        Assert.True(CanDrop(AVCodecID.AV_CODEC_ID_HEVC, [0, 0, 1, 0x0E, 0x01, 0xAF], []));
        // TRAIL_R（1）不是前导画面类型，不能丢弃。
        Assert.False(CanDrop(AVCodecID.AV_CODEC_ID_HEVC, [0, 0, 1, 0x02, 0x01, 0xAF], []));

        var hvcC = new byte[23];
        hvcC[0] = 1;
        hvcC[21] = 0x0F;
        Assert.True(CanDrop(AVCodecID.AV_CODEC_ID_HEVC, [0, 0, 0, 3, 0x12, 0x01, 0xAF], hvcC));
        Assert.False(CanDrop(AVCodecID.AV_CODEC_ID_HEVC, [0, 0, 0, 3, 0x02, 0x01, 0xAF], hvcC));
        Assert.True(CanDrop(AVCodecID.AV_CODEC_ID_HEVC, Prefixed(0x12, 300), hvcC));
    }

    /// <summary>4 字节长度前缀加一个 NAL：首字节为 NAL 头，其余填充。</summary>
    private static byte[] Prefixed(byte header, int length)
    {
        var packet = new byte[4 + length];
        packet[2] = (byte)(length >> 8);
        packet[3] = (byte)length;
        packet[4] = header;
        packet.AsSpan(5).Fill(0x9A);
        return packet;
    }

    [Fact]
    public void OtherCodecsFollowTheirReferenceRules()
    {
        Assert.True(CanDrop(AVCodecID.AV_CODEC_ID_MPEG2VIDEO, [0, 0, 1, 0x00], []));
        Assert.True(CanDrop(AVCodecID.AV_CODEC_ID_MPEG1VIDEO, [0, 0, 1, 0x00], []));
        Assert.False(CanDrop(AVCodecID.AV_CODEC_ID_VC1, [0, 0, 1, 0x0D], []));
    }

    private static bool CanDrop(AVCodecID codec, byte[] packet, byte[] extradata) =>
        LeadingPicture.CanDrop(codec, packet, extradata);
}
