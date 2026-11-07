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
