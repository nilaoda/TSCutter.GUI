using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using Xunit;

namespace TSCutter.GUI.Tests;

public class TsScramblingProbeTests
{
    [Theory]
    [InlineData(188, 0)]
    [InlineData(188, 17)]
    [InlineData(192, 4)]
    [InlineData(204, 17)]
    public void DetectsActualPacketLayoutAfterAPrefix(int stride, int prefix)
    {
        var layout = TsScramblingProbe.FindPacketLayout(CreatePackets(stride, prefix));
        Assert.Equal(stride, layout.PacketSize);
        Assert.Equal(prefix, layout.SyncOffset);
    }

    [Fact]
    public void SyncBytesWithoutValidHeadersCannotEstablishLayout()
    {
        var data = new byte[188 * 8];
        for (var offset = 0; offset < data.Length; offset += 188)
            data[offset] = 0x47;
        Assert.Equal((0, -1), TsScramblingProbe.FindPacketLayout(data));
    }

    [Fact]
    public void DamagedFirstPacketDoesNotHideLaterValidSynchronization()
    {
        var data = CreatePackets(188, 0);
        data[3] = 0x30;
        data[4] = 184;
        Assert.Equal((188, 188), TsScramblingProbe.FindPacketLayout(data));
    }

    [Theory]
    [InlineData("mts", 17)]
    [InlineData("m2ts", 65_200)]
    [InlineData("bin", 131_073)]
    [InlineData("mp4", 2_000_001)]
    [InlineData("ts", TsScramblingProbe.MaximumProbeBytes - 8 * 188)]
    public void ContentProbeIsIndependentOfSuffixAndFindsBoundedNonzeroStart(string extension, int prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ts-layout-{Guid.NewGuid():N}.{extension}");
        try
        {
            var data = CreatePackets(188, prefix);
            // 前缀含伪同步字节但无合法包头，不能抢占实际起点。
            for (var offset = 0; offset + 4 * 188 < prefix; offset += 188)
                data[offset] = 0x47;
            File.WriteAllBytes(path, data);
            var probe = TsScramblingProbe.Probe(path);
            Assert.True(probe.Is188ByteTransportStream);
            Assert.False(probe.HasScrambledPayload);
            Assert.Equal(prefix, probe.SyncOffset);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(192)]
    [InlineData(204)]
    public void TsSuffixDoesNotEnableUnsupportedPacketLayouts(int stride)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ts-layout-{Guid.NewGuid():N}.ts");
        try
        {
            File.WriteAllBytes(path, CreatePackets(stride, 65_200));
            Assert.False(TsScramblingProbe.Probe(path).Is188ByteTransportStream);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ScrambledPacketAcrossReadBoundaryIsNotSkipped()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ts-probe-{Guid.NewGuid():N}.ts");
        try
        {
            var data = new byte[188 * 400];
            var clearPacket = CreatePackets(188, 0).AsSpan(0, 188);
            for (var offset = 0; offset < data.Length; offset += 188)
                clearPacket.CopyTo(data.AsSpan(offset));
            data[188 * (TsScramblingProbe.ProbeBufferBytes / 188) + 3] = 0x90;
            File.WriteAllBytes(path, data);
            Assert.True(TsScramblingProbe.Probe(path).HasScrambledPayload);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void LayoutProbeDoesNotSearchBeyondItsBound()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ts-layout-{Guid.NewGuid():N}.bin");
        try
        {
            using (var output = File.Create(path))
            {
                output.SetLength(TsScramblingProbe.MaximumProbeBytes);
                output.Position = output.Length;
                output.Write(CreatePackets(188, 0));
            }
            var probe = TsScramblingProbe.Probe(path);
            Assert.False(probe.Is188ByteTransportStream);
            Assert.Equal(-1, probe.SyncOffset);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(4, 3)]
    [InlineData(17, 2)]
    public void DetectsScramblingAfterClearPackets(int prefix, int scrambling)
    {
        var data = CreatePackets(188, prefix);
        data[prefix + 188 * 5 + 3] = (byte)((scrambling << 6) | 0x10);
        Assert.True(TsScramblingProbe.HasScrambledPayload(data));
    }

    [Theory]
    [InlineData(192, 4)]
    [InlineData(204, 0)]
    public void DoesNotProbeUnsupportedPacketSizes(int stride, int prefix)
    {
        var data = CreatePackets(stride, prefix);
        for (var offset = prefix; offset + stride <= data.Length; offset += stride)
            data[offset + 3] = 0x90;
        Assert.False(TsScramblingProbe.HasScrambledPayload(data));
    }

    [Fact]
    public void FindsScramblingAfterLostSynchronization()
    {
        var first = CreatePackets(188, 0);
        var second = CreatePackets(188, 1);
        second[1 + 188 * 5 + 3] = 0x90;
        byte[] data = [.. first, .. second];
        Assert.True(TsScramblingProbe.HasScrambledPayload(data));
    }

    [Theory]
    [InlineData(0x10, 0x100)] // Clear payload
    [InlineData(0x50, 0x100)] // Reserved scrambling value
    [InlineData(0x90, 0x1fff)] // Null packet
    [InlineData(0xa0, 0x100)] // Adaptation only
    public void IgnoresPacketsWithoutScrambledPayload(byte header, int pid)
    {
        var data = CreatePackets(188, 0);
        data[1] = (byte)(pid >> 8);
        data[2] = (byte)pid;
        data[3] = header;
        data[4] = 183;
        Assert.False(TsScramblingProbe.HasScrambledPayload(data));
    }

    [Fact]
    public void RejectsUnsynchronizedData()
    {
        var data = new byte[4096];
        data[0] = 0x47;
        data[3] = 0x90;
        Assert.False(TsScramblingProbe.HasScrambledPayload(data));
        Assert.False(TsScramblingProbe.HasScrambledPayload(data.AsSpan(0, 3)));
    }

    [Fact]
    public void SkipsMp4EvenWhenItContainsTsLikeBytes()
    {
        var data = new byte[300_000];
        var falsePackets = CreatePackets(188, 0);
        falsePackets[3] = 0x90;
        falsePackets.CopyTo(data, 273_370);

        Assert.True(TsScramblingProbe.HasScrambledPayload(data));
        var path = Path.Combine(Path.GetTempPath(), $"ts-probe-{Guid.NewGuid():N}.mp4");
        try
        {
            File.WriteAllBytes(path, data);
            Assert.False(TsScramblingProbe.HasScrambledPayload(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("mp4")]
    [InlineData("mov")]
    public void SkipsNonTsFilesBeforeOpeningThem(string extension)
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"ts-probe-{Guid.NewGuid():N}.{extension}");
        Assert.False(TsScramblingProbe.HasScrambledPayload(missingPath));
    }

    [Fact]
    public void EncryptedInputFailsBeforeNativeInitialization()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ts-probe-{Guid.NewGuid():N}.ts");
        try
        {
            var data = CreatePackets(188, 0);
            data[3] = 0x90;
            File.WriteAllBytes(path, data);
            using var video = new VideoInstance(path);
            Assert.Throws<ScrambledTsException>(video.InitVideo);
            Assert.False(video.Inited);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FileProbeStopsAtItsByteLimit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ts-probe-{Guid.NewGuid():N}.ts");
        try
        {
            using (var output = File.Create(path))
            {
                output.SetLength(TsScramblingProbe.MaximumProbeBytes);
                output.Position = output.Length;
                var data = CreatePackets(188, 0);
                data[3] = 0x90;
                output.Write(data);
            }
            Assert.False(TsScramblingProbe.HasScrambledPayload(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] CreatePackets(int stride, int prefix)
    {
        var data = new byte[prefix + stride * 8];
        for (var offset = prefix; offset + stride <= data.Length; offset += stride)
        {
            data[offset] = 0x47;
            data[offset + 1] = 1;
            data[offset + 3] = 0x10;
        }
        return data;
    }
}
