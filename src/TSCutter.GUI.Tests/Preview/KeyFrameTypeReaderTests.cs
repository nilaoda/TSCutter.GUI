using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.Utils.Media;
using Xunit;

namespace TSCutter.GUI.Tests.Preview;

public sealed class KeyFrameTypeReaderTests
{
    [Fact]
    public void AnnexBReadsFirstPictureAfterParameterSetsAndSei()
    {
        Assert.Equal("IDR", KeyFrameTypeReader.Read(
            new byte[] { 0, 0, 0, 1, 0x67, 0x12, 0, 0, 1, 0x06, 0x34, 0, 0, 1, 0x65 }, AVCodecID.AV_CODEC_ID_H264));
        Assert.Equal("CRA", KeyFrameTypeReader.Read(
            new byte[] { 0, 0, 1, 0x40, 1, 0x12, 0, 0, 0, 1, 0x4e, 1, 0x34, 0, 0, 1, 0x2a, 1 }, AVCodecID.AV_CODEC_ID_HEVC));
        // 普通 I 帧也可能使用非 IDR 的 NAL，不能根据包上的 key 标记臆测为 IDR。
        Assert.Null(KeyFrameTypeReader.Read(new byte[] { 0, 0, 1, 0x61 }, AVCodecID.AV_CODEC_ID_H264));
    }

    /// <summary>
    /// 逐行刷新的 H.264 只有恢复点 SEI 标出的 P/B 条带“关键帧”，标为 RP；非 IDR 的 I 条带仍交给解码器报告 I。
    /// 条带头读取需跳过防竞争字节。
    /// </summary>
    [Fact]
    public void H264RecoveryPointsWithPredictedSlicesAreReportedSeparately()
    {
        // first_mb_in_slice = 0；slice_type 依次为 P(5)、B(6)、I(7)、P(0)。
        Assert.Equal(KeyFrameTypeReader.RecoveryPoint, KeyFrameTypeReader.Read(
            new byte[] { 0, 0, 1, 0x06, 0x06, 0x01, 0x80, 0, 0, 1, 0x41, 0x98 }, AVCodecID.AV_CODEC_ID_H264));
        Assert.Equal(KeyFrameTypeReader.RecoveryPoint, KeyFrameTypeReader.Read(
            new byte[] { 0, 0, 1, 0x01, 0x9C }, AVCodecID.AV_CODEC_ID_H264));
        Assert.Null(KeyFrameTypeReader.Read(new byte[] { 0, 0, 1, 0x61, 0x88 }, AVCodecID.AV_CODEC_ID_H264));
        Assert.Equal(KeyFrameTypeReader.RecoveryPoint, KeyFrameTypeReader.Read(
            new byte[] { 0, 0, 1, 0x21, 0xC0 }, AVCodecID.AV_CODEC_ID_H264));
        // first_mb_in_slice = 65535 时条带头含 00 00 03。
        Assert.Equal(KeyFrameTypeReader.RecoveryPoint, KeyFrameTypeReader.Read(
            new byte[] { 0, 0, 1, 0x41, 0x00, 0x00, 0x03, 0x80, 0x00, 0x0C }, AVCodecID.AV_CODEC_ID_H264));
        // 长度前缀格式同样适用；截断的条带头不臆测类型。
        Assert.Equal(KeyFrameTypeReader.RecoveryPoint, KeyFrameTypeReader.Read(
            new byte[] { 0, 0, 0, 2, 0x41, 0x98 }, AVCodecID.AV_CODEC_ID_H264, 4));
        Assert.Null(KeyFrameTypeReader.Read(new byte[] { 0, 0, 1, 0x41, 0x00 }, AVCodecID.AV_CODEC_ID_H264));
    }

    /// <summary>
    /// MPEG-2 帧内刷新：序列头（及扩展、GOP 头）之后的图像是 P 帧时标为 RP；带序列头的 I 帧、
    /// 没有序列头的 P 帧和 B 帧都不是入口，截断或首个起始码已是条带时也不臆测。
    /// </summary>
    [Fact]
    public void Mpeg2PredictedPicturesAfterSequenceHeadersAreRecoveryPoints()
    {
        byte[] sequence = [0, 0, 1, 0xB3, 0x14, 0x00, 0xB4, 0x13, 0, 0, 1, 0xB5, 0x14, 0x8A];
        byte[] gop = [0, 0, 1, 0xB8, 0x00, 0x08, 0x00, 0x40];
        // picture_coding_type 位于第 6 字节的 bit5..3：I = 0x08、P = 0x10、B = 0x18。
        static byte[] Picture(byte type) => [0, 0, 1, 0x00, 0x00, type, 0xFF, 0xF8, 0, 0, 1, 0x01, 0x22];

        Assert.Equal(KeyFrameTypeReader.RecoveryPoint, KeyFrameTypeReader.Read(
            [.. sequence, .. gop, .. Picture(0x10)], AVCodecID.AV_CODEC_ID_MPEG2VIDEO));
        Assert.Equal(KeyFrameTypeReader.RecoveryPoint, KeyFrameTypeReader.Read(
            [.. sequence, .. Picture(0x10)], AVCodecID.AV_CODEC_ID_MPEG2VIDEO));
        Assert.Null(KeyFrameTypeReader.Read([.. sequence, .. gop, .. Picture(0x08)], AVCodecID.AV_CODEC_ID_MPEG2VIDEO));
        Assert.Null(KeyFrameTypeReader.Read([.. sequence, .. Picture(0x18)], AVCodecID.AV_CODEC_ID_MPEG2VIDEO));
        Assert.Null(KeyFrameTypeReader.Read(Picture(0x10), AVCodecID.AV_CODEC_ID_MPEG2VIDEO));
        Assert.Null(KeyFrameTypeReader.Read([.. sequence, 0, 0, 1, 0x01, 0x22, .. Picture(0x10)],
            AVCodecID.AV_CODEC_ID_MPEG2VIDEO));
        Assert.Null(KeyFrameTypeReader.Read([.. sequence, 0, 0, 1, 0x00, 0x00], AVCodecID.AV_CODEC_ID_MPEG2VIDEO));
    }

    [Fact]
    public void LengthPrefixedPacketsSupportAllPrefixWidthsAndHevcAccessTypes()
    {
        foreach (var lengthSize in new[] { 1, 2, 3, 4 })
        {
            foreach (var (nalType, expected) in new[] { (16, "BLA"), (18, "BLA"), (19, "IDR"), (20, "IDR"), (21, "CRA") })
            {
                var packet = new byte[lengthSize + 2];
                packet[lengthSize - 1] = 2;
                packet[lengthSize] = (byte)(nalType << 1);
                packet[lengthSize + 1] = 1;
                Assert.Equal(expected, KeyFrameTypeReader.Read(packet, AVCodecID.AV_CODEC_ID_HEVC, lengthSize));
            }
            var avc = new byte[lengthSize + 1];
            avc[lengthSize - 1] = 1;
            avc[lengthSize] = 0x65;
            Assert.Equal("IDR", KeyFrameTypeReader.Read(avc, AVCodecID.AV_CODEC_ID_H264, lengthSize));
        }
    }

    [Fact]
    public void TruncatedUnknownAndNonAccessPicturesDoNotInventSubtypes()
    {
        foreach (var packet in new byte[][] { [], [0, 0, 1], [0, 0, 1, 0x2a], [0, 0, 1, 0x2a, 0],
                     [0, 0, 1, 0xaa, 1], [0, 0, 1, 0x02, 1, 0, 0, 1, 0x2a, 1] })
            Assert.Null(KeyFrameTypeReader.Read(packet, AVCodecID.AV_CODEC_ID_HEVC));
        foreach (var packet in new byte[][] { [0, 0, 0], [0, 0, 0, 0], [0xff, 0xff, 0xff, 0xff, 0x2a, 1] })
            Assert.Null(KeyFrameTypeReader.Read(packet, AVCodecID.AV_CODEC_ID_HEVC, 4));
        Assert.Null(KeyFrameTypeReader.Read(new byte[] { 0, 0, 1, 0x65 }, AVCodecID.AV_CODEC_ID_MPEG2VIDEO));
    }
}
