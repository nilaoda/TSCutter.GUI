using System.Buffers.Binary;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Utils.Media;
using Xunit;

namespace TSCutter.GUI.Tests.Preview;

public sealed class HdrMetadataProbeTests
{
    private static readonly byte[] Vivid = Convert.FromHexString("2600040005010f8bb2dce79552");
    private static readonly byte[] Plus = Convert.FromHexString(
        "b5003c000104014000000000000000000000236408000028079c500190c801959001fa58029ad003f2f805771807900010");

    [Theory]
    [InlineData(AVCodecID.AV_CODEC_ID_HEVC, 39)]
    [InlineData(AVCodecID.AV_CODEC_ID_HEVC, 40)]
    [InlineData(AVCodecID.AV_CODEC_ID_H264, 6)]
    public void RegisteredVividMetadataIsReadFromSei(AVCodecID codec, int type)
    {
        var probe = new HdrMetadataProbe(codec);
        probe.ReadPacket(Sei(Vivid, codec, type));
        Assert.Equal((byte?)1, probe.VividVersion);
        Assert.Equal("HDR Vivid, Version 1", Describe(probe));
    }

    [Theory]
    [InlineData(AVCodecID.AV_CODEC_ID_AVS2)]
    [InlineData(AVCodecID.AV_CODEC_ID_AVS3)]
    public void AvsHdrPictureExtensionContainsTheRegisteredVividPayload(AVCodecID codec)
    {
        var probe = new HdrMetadataProbe(codec);
        probe.ReadPacket([0, 0, 1, 0xb5, 0x55, .. Vivid]);
        Assert.Equal("HDR Vivid, Version 1", Describe(probe));
        var unrelated = new HdrMetadataProbe(codec);
        unrelated.ReadPacket([0, 0, 1, 0xb5, 0x50, .. Vivid]);
        Assert.Empty(Describe(unrelated));
        var zeroStatistics = new HdrMetadataProbe(codec);
        zeroStatistics.ReadPacket([0, 0, 1, 0xb5, 0x55, .. Vivid[..6], 0, 0, 0, 0, 0, 0, 0]);
        Assert.Equal((byte?)1, zeroStatistics.VividVersion);
    }

    [Fact]
    public void MultipleMessagesIncludePayloadType128BeforeTheRegisteredMetadata()
    {
        var prefix = Sei([], payloadType: 128);
        var vivid = Sei(Vivid);
        var probe = new HdrMetadataProbe(AVCodecID.AV_CODEC_ID_HEVC);
        probe.ReadPacket([.. prefix[..^1], .. vivid[5..]]);
        Assert.Equal((byte?)1, probe.VividVersion);
    }

    [Fact]
    public void HvcCSeiAndLengthPrefixedPacketsAreBothInspected()
    {
        var configuration = new byte[28];
        configuration[0] = 1;
        configuration[21] = 3;
        configuration[22] = 1;
        configuration[23] = 39;
        configuration[25] = 1;
        var nal = Sei(Vivid)[3..];
        BinaryPrimitives.WriteUInt16BigEndian(configuration.AsSpan(26), (ushort)nal.Length);
        var probe = new HdrMetadataProbe(AVCodecID.AV_CODEC_ID_HEVC);
        probe.ReadConfiguration([.. configuration, .. nal]);
        Assert.Equal((byte?)1, probe.VividVersion);
        var packetProbe = new HdrMetadataProbe(AVCodecID.AV_CODEC_ID_HEVC);
        configuration[22] = 0;
        packetProbe.ReadConfiguration(configuration.AsSpan(0, 23));
        var packet = new byte[nal.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(packet, nal.Length);
        nal.CopyTo(packet, 4);
        packetProbe.ReadPacket(packet);
        packetProbe.ReadPacket([0, 0, 0, 20, 0x4e]);
        Assert.Equal((byte?)1, packetProbe.VividVersion);
    }

    [Fact]
    public void TruncatedUnknownAndUnregisteredMessagesDoNotImplyVivid()
    {
        for (var size = 0; size < Vivid.Length; size++)
        {
            var truncated = new HdrMetadataProbe(AVCodecID.AV_CODEC_ID_HEVC);
            truncated.ReadPacket(Sei(Vivid[..size]));
            Assert.Null(truncated.VividVersion);
        }
        foreach (var index in new[] { 0, 2, 4, 5 })
        {
            var unknown = (byte[])Vivid.Clone();
            unknown[index]++;
            var probe = new HdrMetadataProbe(AVCodecID.AV_CODEC_ID_HEVC);
            probe.ReadPacket(Sei(unknown));
            Assert.Null(probe.VividVersion);
        }
        var unregistered = new HdrMetadataProbe(AVCodecID.AV_CODEC_ID_HEVC);
        unregistered.ReadPacket(Sei(Vivid, payloadType: 5));
        Assert.Null(unregistered.VividVersion);
        unregistered.ReadConfiguration([1, 0, 0]);
        unregistered.ReadPacket([0, 0, 1, 0x4e, 1, 4, 255, 255]);
        Assert.Empty(Describe(unregistered));
    }

    [Fact]
    public void StaticAndDynamicFormatsShareOneMediaInfoStyleField()
    {
        var probe = new HdrMetadataProbe(AVCodecID.AV_CODEC_ID_HEVC);
        probe.ReadPacket(Sei(new byte[24], payloadType: 137));
        probe.ReadPacket(Sei(Vivid));
        Assert.Equal("SMPTE ST 2086, HDR10 compatible / HDR Vivid, Version 1", Describe(probe));
        Assert.Equal("SMPTE ST 2086 / HDR Vivid, Version 1", probe.Describe(
            AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67, AVColorPrimaries.AVCOL_PRI_BT2020, 10));
        Assert.StartsWith("Dolby Vision / SMPTE ST 2086", probe.Describe(
            AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084, AVColorPrimaries.AVCOL_PRI_BT2020, 10, "Dolby Vision"));
        Assert.DoesNotContain("HDR10 compatible", probe.Describe(
            AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084, AVColorPrimaries.AVCOL_PRI_BT2020, 8));
    }

    [Fact]
    public void HlgAlternativeTransferDoesNotImplyVivid()
    {
        var probe = new HdrMetadataProbe(AVCodecID.AV_CODEC_ID_HEVC);
        Assert.Empty(Describe(probe));
        probe.ReadPacket(Sei([18], payloadType: 147));
        Assert.Equal("HLG", Describe(probe));
        Assert.Equal(AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67,
            probe.EffectiveTransfer(AVColorTransferCharacteristic.AVCOL_TRC_BT2020_10));
    }

    [NativeRuntimeFact]
    public void Hdr10PlusUsesTheValidatedApplicationVersion()
    {
        var probe = new HdrMetadataProbe(AVCodecID.AV_CODEC_ID_HEVC);
        probe.ReadPacket(Sei(Plus));
        Assert.Equal((byte?)1, probe.Hdr10PlusVersion);
        Assert.Equal("SMPTE ST 2094 App 4, Version 1, HDR10+", Describe(probe));
        var truncated = new HdrMetadataProbe(AVCodecID.AV_CODEC_ID_HEVC);
        truncated.ReadPacket(Sei(Plus[..10]));
        Assert.Null(truncated.Hdr10PlusVersion);
    }

    [NativeMediaTheory(sampleVariable: "TSCUTTER_HDR_TEST_SAMPLES")]
    [InlineData("hdr-vivid-hevc.mp4", "SMPTE ST 2086, HDR10 compatible / HDR Vivid, Version 1")]
    [InlineData("hdr-vivid-avs2.ts", "HDR Vivid, Version 1")]
    [InlineData("hlg-hevc.ts", "HLG")]
    [InlineData("hdr10plus.mkv", "SMPTE ST 2086, HDR10 compatible / SMPTE ST 2094 App 4, Version 1, HDR10+")]
    public void RealSamplesReportTheirBitstreamMetadata(string name, string expected)
    {
        var directory = Environment.GetEnvironmentVariable("TSCUTTER_HDR_TEST_SAMPLES")!;
        var text = MediaInfoBuilder.Build(Path.Combine(directory, name));
        Assert.Contains("HDR format                         : " + expected + Environment.NewLine, text);
        if (name == "hlg-hevc.ts")
        {
            Assert.DoesNotContain("HDR Vivid", text);
            Assert.Contains("Transfer characteristics           : HLG", text);
        }
    }

    private static string Describe(HdrMetadataProbe probe) => probe.Describe(
        AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084, AVColorPrimaries.AVCOL_PRI_BT2020, 10);

    private static byte[] Sei(byte[] payload, AVCodecID codec = AVCodecID.AV_CODEC_ID_HEVC, int type = 39, byte payloadType = 4)
    {
        var result = new List<byte> { 0, 0, 1 };
        result.Add(codec == AVCodecID.AV_CODEC_ID_HEVC ? (byte)(type << 1) : (byte)type);
        if (codec == AVCodecID.AV_CODEC_ID_HEVC)
            result.Add(1);
        var zeros = 0;
        ReadOnlySpan<byte> message = [payloadType, (byte)payload.Length, .. payload, 0x80];
        foreach (var value in message)
        {
            if (zeros == 2 && value <= 3)
            {
                result.Add(3);
                zeros = 0;
            }
            result.Add(value);
            zeros = value == 0 ? zeros + 1 : 0;
        }
        return result.ToArray();
    }
}
