using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class TsClipMergeServiceTests
{
    private const int PacketSize = TsStreamAnalyzer.PacketSize;
    private const int MediaPid = 0x0100;
    private const long TimestampWrap = 1L << 33;

    [Fact]
    public async Task MergeMakesJoinContinuousAndPreservesInternalTransportDamage()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"ts-merge-{Guid.NewGuid():N}.ts");
        var outputPath = sourcePath + ".merged.ts";
        try
        {
            var packets = Enumerable.Range(0, 30)
                .Select(index => CreatePacket(index, index * 9_000L))
                .ToArray();
            // 第二段内部故意保留一个 CC 跳号和一个 TEI；合并只能修复片段接缝，不能掩盖它们。
            packets[25][3] = (byte)((packets[25][3] & 0xF0) | 0x0B);
            packets[26][1] |= 0x80;
            await File.WriteAllBytesAsync(sourcePath, packets.SelectMany(packet => packet).ToArray());

            var result = await new TsClipMergeService().MergeAsync(new TsClipMergeRequest
            {
                SourcePath = sourcePath,
                OutputPath = outputPath,
                Ranges =
                [
                    new TsClipMergeRange(0, 10 * PacketSize, 0, 1),
                    new TsClipMergeRange(20 * PacketSize, 30 * PacketSize, 2, 3)
                ]
            });

            var output = await File.ReadAllBytesAsync(outputPath);
            Assert.Equal(20 * PacketSize, output.Length);
            Assert.Equal(2, result.SegmentCount);
            Assert.True(result.RewrittenPcrCount > 0);
            Assert.True(result.RewrittenTimestampCount > 0);
            Assert.True(result.RewrittenContinuityCount > 0);

            var previous = output.AsSpan(9 * PacketSize, PacketSize);
            var joined = output.AsSpan(10 * PacketSize, PacketSize);
            Assert.Equal(9, previous[3] & 0x0F);
            Assert.Equal(10, joined[3] & 0x0F);
            Assert.Equal(9_000, ReadPcr(joined) - ReadPcr(previous));
            Assert.Equal(9_000, ReadPts(joined) - ReadPts(previous));

            var beforeGap = output.AsSpan(14 * PacketSize, PacketSize);
            var gapPacket = output.AsSpan(15 * PacketSize, PacketSize);
            Assert.NotEqual(((beforeGap[3] & 0x0F) + 1) & 0x0F, gapPacket[3] & 0x0F);
            Assert.NotEqual(0, output[16 * PacketSize + 1] & 0x80);

            var verification = await new TsStreamAnalyzer().AnalyzeAsync(
                outputPath,
                options: new TsStreamAnalyzeOptions
                {
                    Features = TsStreamAnalyzeFeatures.ContinuityValidation |
                               TsStreamAnalyzeFeatures.DetailedEvents
                });
            Assert.Equal(1, verification.Pids[MediaPid].ContinuityErrors);
            Assert.Equal(1, verification.Pids[MediaPid].TransportErrors);
            Assert.Contains(verification.Events, item => item.Type == TsCheckEventType.TransportError);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task CancelledMergeDeletesIncompleteOutput()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"ts-merge-{Guid.NewGuid():N}.ts");
        var outputPath = sourcePath + ".cancelled.ts";
        try
        {
            const int packetCount = 10_000;
            await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, packetCount)
                .SelectMany(index => CreatePacket(index, index * 900L)).ToArray());
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress<TsClipMergeProgress>(_ => cancellation.Cancel());

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new TsClipMergeService().MergeAsync(new TsClipMergeRequest
                {
                    SourcePath = sourcePath,
                    OutputPath = outputPath,
                    Ranges = [new TsClipMergeRange(0, packetCount * PacketSize, 0, 100)]
                }, progress, cancellation.Token));
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task OverlappingSelectionsAreWrittenOnceInTimelineOrder()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"ts-merge-{Guid.NewGuid():N}.ts");
        var outputPath = sourcePath + ".overlap.ts";
        try
        {
            await File.WriteAllBytesAsync(sourcePath, Enumerable.Range(0, 20)
                .SelectMany(index => CreatePacket(index, index * 9_000L)).ToArray());

            var result = await new TsClipMergeService().MergeAsync(new TsClipMergeRequest
            {
                SourcePath = sourcePath,
                OutputPath = outputPath,
                Ranges =
                [
                    new TsClipMergeRange(5 * PacketSize, 15 * PacketSize, 0.5, 1.5),
                    new TsClipMergeRange(0, 10 * PacketSize, 0, 1)
                ]
            });

            Assert.Equal(1, result.SegmentCount);
            Assert.Equal(15 * PacketSize, new FileInfo(outputPath).Length);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(outputPath);
        }
    }

    private static byte[] CreatePacket(int index, long timestamp90k)
    {
        var packet = new byte[PacketSize];
        packet.AsSpan().Fill(0xFF);
        packet[0] = 0x47;
        packet[1] = (byte)(0x40 | ((MediaPid >> 8) & 0x1F));
        packet[2] = (byte)(MediaPid & 0xFF);
        packet[3] = (byte)(0x30 | (index & 0x0F));
        packet[4] = 7;
        packet[5] = 0x10;
        WritePcr(packet.AsSpan(6, 6), timestamp90k);

        var payload = packet.AsSpan(12);
        payload[0] = 0;
        payload[1] = 0;
        payload[2] = 1;
        payload[3] = 0xE0;
        payload[4] = 0;
        payload[5] = 0;
        payload[6] = 0x80;
        payload[7] = 0x80;
        payload[8] = 5;
        WriteTimestamp(payload[9..14], timestamp90k);
        return packet;
    }

    private static long ReadPcr(ReadOnlySpan<byte> packet) =>
        ((long)packet[6] << 25) |
        ((long)packet[7] << 17) |
        ((long)packet[8] << 9) |
        ((long)packet[9] << 1) |
        ((long)packet[10] >> 7);

    private static long ReadPts(ReadOnlySpan<byte> packet)
    {
        var value = packet[21..26];
        return ((long)(value[0] & 0x0E) << 29) |
               ((long)value[1] << 22) |
               ((long)(value[2] & 0xFE) << 14) |
               ((long)value[3] << 7) |
               ((long)value[4] >> 1);
    }

    private static void WritePcr(Span<byte> value, long timestamp90k)
    {
        var raw = ModuloTimestamp(timestamp90k);
        value[0] = (byte)(raw >> 25);
        value[1] = (byte)(raw >> 17);
        value[2] = (byte)(raw >> 9);
        value[3] = (byte)(raw >> 1);
        value[4] = (byte)(((raw & 1) << 7) | 0x7E);
        value[5] = 0;
    }

    private static void WriteTimestamp(Span<byte> value, long timestamp90k)
    {
        var raw = ModuloTimestamp(timestamp90k);
        value[0] = (byte)(0x20 | (((raw >> 30) & 7) << 1) | 1);
        value[1] = (byte)(raw >> 22);
        value[2] = (byte)((((raw >> 15) & 0x7F) << 1) | 1);
        value[3] = (byte)(raw >> 7);
        value[4] = (byte)(((raw & 0x7F) << 1) | 1);
    }

    private static long ModuloTimestamp(long value)
    {
        value %= TimestampWrap;
        return value < 0 ? value + TimestampWrap : value;
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
