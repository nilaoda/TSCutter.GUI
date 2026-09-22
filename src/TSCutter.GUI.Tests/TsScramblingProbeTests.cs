using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using Xunit;

namespace TSCutter.GUI.Tests;

public class TsScramblingProbeTests
{
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
    public void EncryptedInputFailsBeforeNativeInitialization()
    {
        var path = Path.GetTempFileName();
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
        var path = Path.GetTempFileName();
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
