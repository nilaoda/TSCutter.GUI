using System.Buffers.Binary;
using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using TSCutter.GUI.Utils;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class TsBinaryMergeServiceTests
{
    private const int PacketSize = TsStreamAnalyzer.PacketSize;

    [Fact]
    public async Task DirectMergePreservesEveryInputByte()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var first = CreatePackets(0, 40);
            var second = CreatePackets(40, 30);
            var firstPath = Path.Combine(directory, "1.ts");
            var secondPath = Path.Combine(directory, "2.ts");
            var outputPath = Path.Combine(directory, "merged.ts");
            await File.WriteAllBytesAsync(firstPath, first);
            await File.WriteAllBytesAsync(secondPath, second);

            var result = await new TsBinaryMergeService().MergeAsync(
                [firstPath, secondPath], outputPath, null, false);

            Assert.Equal(first.Length + second.Length, result.OutputBytes);
            Assert.Equal(first.Concat(second), await File.ReadAllBytesAsync(outputPath));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task OverlapAnalysisRemovesOnlyVerifiedSuffixPrefixMatch()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var firstPath = Path.Combine(directory, "part1.ts");
            var secondPath = Path.Combine(directory, "part2.ts");
            var outputPath = Path.Combine(directory, "merged.ts");
            await File.WriteAllBytesAsync(firstPath, CreatePackets(0, 200));
            await File.WriteAllBytesAsync(secondPath, CreatePackets(120, 180));

            var service = new TsBinaryMergeService();
            var analysis = await service.AnalyzeOverlapsAsync(
                [firstPath, secondPath], 200L * PacketSize);
            var join = Assert.Single(analysis.Joins);
            Assert.True(join.HasReliableOverlap);
            Assert.Equal(80L * PacketSize, join.OverlapBytes);

            var result = await service.MergeAsync(
                [firstPath, secondPath], outputPath, analysis, false);

            Assert.Equal(80L * PacketSize, result.RemovedOverlapBytes);
            Assert.Equal(CreatePackets(0, 300), await File.ReadAllBytesAsync(outputPath));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task UnmatchedJoinRequiresExplicitDirectAppendFallback()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var firstPath = Path.Combine(directory, "first.ts");
            var secondPath = Path.Combine(directory, "second.ts");
            var outputPath = Path.Combine(directory, "merged.ts");
            await File.WriteAllBytesAsync(firstPath, CreatePackets(0, 80));
            await File.WriteAllBytesAsync(secondPath, CreatePackets(200, 80));

            var service = new TsBinaryMergeService();
            var analysis = await service.AnalyzeOverlapsAsync(
                [firstPath, secondPath], 80L * PacketSize);
            Assert.True(analysis.HasUnmatchedJoins);

            var exception = await Assert.ThrowsAsync<TsBinaryMergeException>(() =>
                service.MergeAsync(
                    [firstPath, secondPath], outputPath, analysis, false));
            Assert.Equal(TsBinaryMergeErrorCode.UnmatchedJoin, exception.Code);

            var result = await service.MergeAsync(
                [firstPath, secondPath], outputPath, analysis, true);
            Assert.Equal(0, result.RemovedOverlapBytes);
            Assert.Equal(1, result.UnmatchedJoinCount);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task FullyCoveredFileDoesNotReplaceTheNextJoinReference()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var firstPath = Path.Combine(directory, "1.ts");
            var coveredPath = Path.Combine(directory, "2.ts");
            var thirdPath = Path.Combine(directory, "3.ts");
            var outputPath = Path.Combine(directory, "merged.ts");
            await File.WriteAllBytesAsync(firstPath, CreatePackets(0, 200));
            await File.WriteAllBytesAsync(coveredPath, CreatePackets(136, 64));
            await File.WriteAllBytesAsync(thirdPath, CreatePackets(160, 140));

            var service = new TsBinaryMergeService();
            var analysis = await service.AnalyzeOverlapsAsync(
                [firstPath, coveredPath, thirdPath], 200L * PacketSize);

            Assert.True(analysis.Joins[0].IsFullyContained);
            Assert.Equal(0, analysis.Joins[1].PreviousSourceIndex);
            Assert.Equal(40L * PacketSize, analysis.Joins[1].OverlapBytes);
            await service.MergeAsync(
                [firstPath, coveredPath, thirdPath], outputPath, analysis, false);
            Assert.Equal(CreatePackets(0, 300), await File.ReadAllBytesAsync(outputPath));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task DirectMergeHandlesOneThousandSmallHlsSegments()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var paths = new string[1000];
            for (var index = 0; index < paths.Length; index++)
            {
                paths[index] = Path.Combine(directory, $"segment{index + 1}.ts");
                await File.WriteAllBytesAsync(paths[index], CreatePackets(index * 5, 5));
            }
            var outputPath = Path.Combine(directory, "merged.ts");

            var result = await new TsBinaryMergeService().MergeAsync(
                paths, outputPath, null, false);

            Assert.Equal(paths.Length, result.SourceCount);
            Assert.Equal(1000L * 5 * PacketSize, result.OutputBytes);
            Assert.Equal(CreatePackets(0, 5000), await File.ReadAllBytesAsync(outputPath));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("1.ts", "2.ts", -1)]
    [InlineData("2.ts", "10.ts", -1)]
    [InlineData("segment09.ts", "segment10.ts", -1)]
    [InlineData("part100.ts", "part20.ts", 1)]
    public void NaturalComparerUsesNumericFileNameSegments(
        string left,
        string right,
        int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(
            NaturalStringComparer.Instance.Compare(left, right)));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ts-binary-merge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static byte[] CreatePackets(int startIndex, int count)
    {
        var result = new byte[count * PacketSize];
        for (var packetIndex = 0; packetIndex < count; packetIndex++)
        {
            var value = startIndex + packetIndex;
            var packet = result.AsSpan(packetIndex * PacketSize, PacketSize);
            packet[0] = 0x47;
            packet[1] = 0x41;
            packet[2] = 0;
            packet[3] = (byte)(0x10 | (value & 0x0F));
            BinaryPrimitives.WriteInt32LittleEndian(packet[4..8], value);
            for (var index = 8; index < packet.Length; index++)
                packet[index] = (byte)(value * 31 + index * 17);
        }
        return result;
    }
}
