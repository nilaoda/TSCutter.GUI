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
