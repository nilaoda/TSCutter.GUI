using TSCutter.GUI.Tests.FFmpeg;
using System.Text;
using FFmpeg.AutoGen.Abstractions;
using Xunit;
using TSCutter.GUI.Models.TransportStream.Analysis;
using TSCutter.GUI.Utils.Media;
using TSCutter.GUI.Utils.TransportStream.Analysis;
using TSCutter.GUI.Utils.TransportStream.Packets;

namespace TSCutter.GUI.Tests.Preview;

public sealed class MediaInfoBuilderTests
{
    [Fact]
    public void AudioVividSeparates714BedFromFourObjects()
    {
        var text = new StringBuilder();
        Assert.True(MediaInfoBuilder.WriteAv3aChannels(text, [2, 2, 1, 2, 10, 4, 0, 1, 6, 64]));
        var result = text.ToString();
        Assert.Contains("Channels + Objects", result);
        Assert.Contains("Channel(s)                         : 12 channels", result);
        Assert.Contains("Channel layout                     : 7.1.4", result);
        Assert.Contains("Number of objects                  : 4", result);
        Assert.DoesNotContain("16 channels", result);
    }

    [Fact]
    public void AudioVividDistinguishesSpeakersObjectsAndAmbisonics()
    {
        var text = new StringBuilder();
        Assert.True(MediaInfoBuilder.WriteAv3aChannels(text, [2, 2, 0, 0, 8, 0, 0, 1, 1, 128]));
        Assert.Contains("5.1.4", text.ToString());
        Assert.DoesNotContain("objects", text.ToString());
        text.Clear();
        Assert.True(MediaInfoBuilder.WriteAv3aChannels(text, [2, 2, 1, 1, 0, 4, 0, 1, 1, 128]));
        Assert.Contains("Number of objects                  : 4", text.ToString());
        Assert.DoesNotContain("Channel layout", text.ToString());
        text.Clear();
        Assert.True(MediaInfoBuilder.WriteAv3aChannels(text, [2, 2, 0, 3, 0, 0, 3, 1, 1, 128]));
        Assert.Contains("16 channels", text.ToString());
        Assert.Contains("Ambisonics (order 3)", text.ToString());
    }

    [Fact]
    public void InvalidAudioVividConfigurationLeavesGenericChannelInfoAvailable()
    {
        var text = new StringBuilder();
        byte[] valid = [2, 2, 1, 2, 10, 4, 0, 1, 6, 64];
        Assert.False(MediaInfoBuilder.WriteAv3aChannels(text, valid.AsSpan(0, 9)));
        foreach (var (index, value) in new[] { (0, 3), (3, 4), (4, 11), (5, 0), (7, 3) })
        {
            var invalid = (byte[])valid.Clone();
            invalid[index] = (byte)value;
            Assert.False(MediaInfoBuilder.WriteAv3aChannels(text, invalid));
        }
        Assert.Empty(text.ToString());
    }

    [Fact]
    public void DolbyVisionProfile5MatchesItsConfigurationRecord()
    {
        var info = MediaInfoBuilder.ParseDolbyVisionConfiguration([1, 0, 5, 7, 1, 0, 1, 0, 0], AVCodecID.AV_CODEC_ID_HEVC);
        Assert.Equal(5, info!.Profile);
        Assert.Equal("Dolby Vision, Version 1.0, Profile 5, dvhe.05.07, BL+RPU, no metadata compression", info.Description);
        Assert.Equal("dvh1", MediaInfoBuilder.VideoCodecId(0x31687664, AVCodecID.AV_CODEC_ID_HEVC));
    }

    [Theory]
    [InlineData(7, 1, 1, 0, "BL+EL+RPU", "HDR10 compatible", "no metadata compression")]
    [InlineData(8, 0, 1, 1, "BL+RPU", "HDR10 compatible", "limited metadata compression")]
    [InlineData(8, 0, 4, 3, "BL+RPU", "HLG compatible", "extended metadata compression")]
    public void DolbyVisionPreservesLayersCompatibilityAndCompression(
        byte profile, byte enhancement, byte compatibility, byte compression,
        string layers, string expectedCompatibility, string expectedCompression)
    {
        var info = MediaInfoBuilder.ParseDolbyVisionConfiguration(
            [1, 0, profile, 6, 1, enhancement, 1, compatibility, compression], AVCodecID.AV_CODEC_ID_HEVC)!;
        Assert.Contains(layers, info.Description);
        Assert.Contains(expectedCompatibility, info.Description);
        Assert.Contains(expectedCompression, info.Description);
    }

    [Fact]
    public void MissingOrTruncatedDolbyVisionFieldsAreNotInvented()
    {
        Assert.Null(MediaInfoBuilder.ParseDolbyVisionConfiguration([1, 0, 5], AVCodecID.AV_CODEC_ID_HEVC));
        Assert.Null(MediaInfoBuilder.ParseDolbyVisionConfiguration([1, 0, 5, 7, 2, 0, 1, 0], AVCodecID.AV_CODEC_ID_HEVC));
        var oldRecord = MediaInfoBuilder.ParseDolbyVisionConfiguration([1, 0, 5, 7, 1, 0, 1, 0], AVCodecID.AV_CODEC_ID_HEVC)!;
        Assert.DoesNotContain("compression", oldRecord.Description);
        Assert.Equal(((int)AVCodecID.AV_CODEC_ID_HEVC).ToString(), MediaInfoBuilder.VideoCodecId(0, AVCodecID.AV_CODEC_ID_HEVC));
    }

    [Theory]
    [InlineData(188)]
    [InlineData(192)]
    [InlineData(204)]
    public void EncryptedTsUsesClearTablesWithoutNativeCodecProbing(int stride)
    {
        var path = TemporaryPath();
        try
        {
            File.WriteAllBytes(path, CreateTs(stride));
            var text = MediaInfoBuilder.BuildEncryptedTs(path, TsMediaInfoProbe.Read(path)!);
            Assert.Contains("512 (0x200)", text);
            Assert.Contains("34 (0x22)", text);
            Assert.Contains("MPEG Audio", text);
            Assert.Contains("High@L4.1", text);
            Assert.Contains("Ericsson", text);
            Assert.Contains("English", text);
            Assert.Equal(3, text.Split("Encrypted").Length - 1);
            Assert.DoesNotContain("MP3", text);
            Assert.DoesNotContain("pixels", text);
            Assert.DoesNotContain("NaN", text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DvbPrivateAudioUsesItsDescriptorInsteadOfBeingReportedAsData()
    {
        var path = TemporaryPath();
        try
        {
            File.WriteAllBytes(path, CreateTs(188, audioType: 6, audioExtraDescriptors: [0x6a, 0]));
            var text = MediaInfoBuilder.BuildEncryptedTs(path, TsMediaInfoProbe.Read(path)!);
            Assert.Contains("AC-3", text);
            Assert.Contains("Codec ID                           : 6", text);
            Assert.DoesNotContain("TS stream type 0x06", text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MultipleDeclaredLanguagesDoNotImplyATimelineChange()
    {
        var path = TemporaryPath();
        try
        {
            File.WriteAllBytes(path, CreateTs(188, audioExtraDescriptors: [0x0a, 4, (byte)'f', (byte)'r', (byte)'a', 0]));
            var text = MediaInfoBuilder.BuildEncryptedTs(path, TsMediaInfoProbe.Read(path)!);
            Assert.Contains("English / French", text);
            Assert.DoesNotContain("changes detected", text);
        }
        finally { File.Delete(path); }
    }

    [NativeRuntimeFact]
    public void EncryptedTsWithADamagedPrefixFallsBackAfterFormatDetectionFails()
    {
        var path = TemporaryPath();
        try
        {
            using (var file = File.Create(path))
            {
                file.Position = 3 * 1024 * 1024;
                file.Write(CreateTs(188));
            }
            var text = MediaInfoBuilder.Build(path);
            Assert.Contains("MPEG-TS", text);
            Assert.Contains("High@L4.1", text);
            Assert.Contains("512 (0x200)", text);
        }
        finally { File.Delete(path); }
    }

    [NativeRuntimeFact]
    public void WavWithTsPayloadAndTsSuffixStillUsesTheWavDemuxer()
    {
        var path = TemporaryPath();
        try
        {
            var ts = CreateTs(188);
            const int audioBytes = 48_000 * 2;
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + audioBytes + 8 + ts.Length);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1); // PCM
                writer.Write((short)1); // mono
                writer.Write(48_000);
                writer.Write(96_000);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(audioBytes);
                writer.Write(new byte[audioBytes]);
                writer.Write(Encoding.ASCII.GetBytes("JUNK"));
                writer.Write(ts.Length);
                writer.Write(ts);
            }
            var text = MediaInfoBuilder.Build(path);
            Assert.Contains("Format                             : wav", text);
            Assert.Contains("PCM_S16LE", text);
            Assert.Contains("ID                                 : 1 (0x1)", text);
            Assert.DoesNotContain("MPEG-TS", text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void HeadAndTailRetainMetadataChanges()
    {
        var path = TemporaryPath();
        try
        {
            using (var file = File.Create(path))
            {
                file.Write(CreateTs(188, "TES", "ENC6"));
                file.Position = ((long)TsMediaInfoProbe.WindowBytes * 3 / 188) * 188;
                file.Write(CreateTs(188, "eng", "ENC3"));
            }
            var text = MediaInfoBuilder.BuildEncryptedTs(path, TsMediaInfoProbe.Read(path)!);
            Assert.Contains("English / TES (changes detected)", text);
            Assert.Contains("ENC3 / ENC6 (changes detected)", text);
            Assert.Contains("Unknown", text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PmtSplitAtTheHeadWindowBoundaryIsStillAssembled()
    {
        var path = TemporaryPath();
        try
        {
            var initial = CreateTs(188);
            initial[188 + 17] ^= 1; // 原有 PMT 无效，只有窗口边界处的 PMT 可用。
            var pmt = TsPsiSectionBuilder.BuildPmt(1, 0, 512, new byte[200], [
                new(512, new TsStreamDefinition { StreamType = 0x1b })]);
            var boundary = (long)TsMediaInfoProbe.WindowBytes / 188 * 188;
            using (var file = File.Create(path))
            {
                file.Write(initial);
                file.Position = boundary - 188;
                var packet = new byte[188];
                Array.Fill(packet, (byte)0xff);
                packet[0] = 0x47; packet[1] = 0x40; packet[2] = 32; packet[3] = 0x11; packet[4] = 0;
                pmt.AsSpan(0, 183).CopyTo(packet.AsSpan(5));
                file.Write(packet);
                Array.Fill(packet, (byte)0xff);
                packet[0] = 0x47; packet[1] = 0; packet[2] = 32; packet[3] = 0x12;
                pmt.AsSpan(183).CopyTo(packet.AsSpan(4));
                file.Write(packet);
            }
            var probe = TsMediaInfoProbe.Read(path);
            Assert.NotNull(probe);
            Assert.Contains(512, probe.Streams.Keys);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void InvalidPmtCrcCannotSupplyStreamMetadata()
    {
        var path = TemporaryPath();
        try
        {
            var data = CreateTs(188);
            data[188 + 17] ^= 1;
            File.WriteAllBytes(path, data);
            var probe = TsMediaInfoProbe.Read(path);
            Assert.NotNull(probe);
            Assert.Empty(probe.Streams);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TsSuffixDoesNotMakeOtherDataATransportStream()
    {
        var path = TemporaryPath();
        try
        {
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("RIFF data without TS packets"));
            Assert.Null(TsMediaInfoProbe.Read(path));
        }
        finally { File.Delete(path); }
    }

    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), $"media-info-{Guid.NewGuid():N}.ts");

    private static byte[] CreateTs(int stride, string language = "eng", string service = "ENC6",
        byte audioType = 3, byte[]? audioExtraDescriptors = null)
    {
        var pat = TsPsiSectionBuilder.BuildPat(1, 0, [new(1, 32)]);
        var pmt = TsPsiSectionBuilder.BuildPmt(1, 0, 512, [], [
            new(512, new TsStreamDefinition { StreamType = 0x1b, Descriptors = [0x28, 4, 100, 0, 41, 0x3f] }),
            new(34, new TsStreamDefinition { StreamType = audioType,
                Descriptors = [0x0a, 4, .. Encoding.ASCII.GetBytes(language), 0, .. audioExtraDescriptors ?? []] })]);
        var name = Encoding.ASCII.GetBytes(service);
        byte[] serviceDescriptor = [0x48, (byte)(name.Length + 3), 1, 0, (byte)name.Length, .. name];
        var sdt = TsPsiSectionBuilder.BuildSdt(1, 1, 0, [new(1, serviceDescriptor, false, false, 4, true)]);
        var nit = Convert.FromHexString("40B017FFFFC10000F00A40084572696373736F6EF00000000000");
        TsPsiSectionBuilder.WriteCrc(nit);
        var result = new byte[stride * 6];
        WritePacket(0, 0, pat);
        WritePacket(1, 32, pmt);
        WritePacket(2, 17, sdt);
        WritePacket(3, 16, nit);
        WritePacket(4, 512, null);
        WritePacket(5, 34, null);
        return result;

        void WritePacket(int index, int pid, byte[]? section)
        {
            var packet = result.AsSpan(index * stride + (stride == 192 ? 4 : 0), 188);
            packet.Fill(0xff);
            packet[0] = 0x47;
            packet[1] = (byte)((pid >> 8) | (section is null ? 0 : 0x40));
            packet[2] = (byte)pid;
            packet[3] = section is null ? (byte)0x90 : (byte)0x10;
            if (section is not null)
            {
                packet[4] = 0;
                section.CopyTo(packet[5..]);
            }
        }
    }
}
