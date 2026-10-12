using TSCutter.GUI.Services;
using TSCutter.GUI.Utils;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class TsClipCopyServiceTests
{
    [Theory]
    [InlineData(188)]
    [InlineData(192)]
    [InlineData(204)]
    public async Task LastPacketWithIncompleteTailKeepsTheSourceLayout(int packetSize)
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"ts-copy-tail-{Guid.NewGuid():N}.bin");
        var outputPath = sourcePath + ".cut";
        try
        {
            const int prefixBytes = 17;
            var data = new byte[prefixBytes + packetSize * 10 + 13];
            var layout = TsPacketLayout.FromPacketOffset(packetSize, prefixBytes);
            for (var index = 0; index < 10; index++)
            {
                var packet = layout.GetPacketBody(data.AsSpan(), prefixBytes + index * packetSize);
                packet.Fill(0xFF);
                packet[0] = 0x47;
                packet[1] = 0x01;
                packet[2] = 0x00;
                packet[3] = (byte)(0x10 | index);
            }
            await File.WriteAllBytesAsync(sourcePath, data);
            var start = prefixBytes + packetSize * 9;
            if (packetSize == TsPacketLayout.FecPacketSize)
            {
                await Assert.ThrowsAsync<NotSupportedException>(() => TsClipCopyService.CopyAsync(
                    sourcePath, outputPath, start, -1, null, CancellationToken.None));
                Assert.False(File.Exists(outputPath));
            }
            else
            {
                // M2TS 的 EOF 按整包截断；188 字节 TS 保留原有按字节复制的行为。
                var end = layout.IsM2ts ? -1 : start + packetSize;
                await TsClipCopyService.CopyAsync(sourcePath, outputPath, start, end, null, CancellationToken.None);
                Assert.Equal(data[start..(start + packetSize)], await File.ReadAllBytesAsync(outputPath));
            }
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }
}
