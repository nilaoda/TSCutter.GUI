using System.Buffers;
using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using TSCutter.GUI.Utils;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class TsClipMergeSplitPesTests
{
    private const int PacketSize = 188;
    private const long Wrap = 1L << 33;

    [Theory]
    [InlineData(3, false, 0, false)]
    [InlineData(10, false, 0, true)]
    [InlineData(13, false, Wrap - 27_000, false)]
    [InlineData(18, true, 0, true)]
    [InlineData(1, true, 0, false)]
    public async Task SplitPtsAndDtsAreObservedAndRewrittenBeforeCopying(
        int firstPayloadBytes, bool hasDts, long basePts, bool duplicate)
    {
        using var files = new SampleFiles();
        var original = Packet(0, basePts + 900_000, hasDts);
        var split = Split(original, firstPayloadBytes, firstPayloadBytes == 1 ? 1 : 184);
        var segment = new List<byte[]> { split[0] };
        if (duplicate) segment.Add(split[0]);
        for (var index = 1; index < split.Length; index++)
        {
            segment.Add(split[index]);
            if (duplicate && index == 1) segment.Add(split[index]);
            if (index + 1 < split.Length) segment.Add(NullPacket());
        }
        segment.Add(Packet(split.Length, basePts + 909_000, hasDts));
        segment.Add(Packet(split.Length + 1, basePts + 918_000, hasDts));
        await MergeAsync(files, segment, hasDts, basePts);
        var output = await File.ReadAllBytesAsync(files.Output);
        var payload = ReadPesPayload(output, 5 * PacketSize, 256);
        Assert.Equal(Modulo(basePts + 45_000), TsTimestampFieldCodec.ReadPesTimestamp(payload.AsSpan(9, 5)));
        if (hasDts)
            Assert.Equal(Modulo(basePts + 43_000), TsTimestampFieldCodec.ReadPesTimestamp(payload.AsSpan(14, 5)));
        var headerLength = hasDts ? 19 : 14;
        Assert.Equal(original[(4 + headerLength)..], payload[headerLength..]);
        if (duplicate)
        {
            Assert.Equal(output.AsSpan(5 * PacketSize, PacketSize).ToArray(),
                output.AsSpan(6 * PacketSize, PacketSize).ToArray());
            Assert.Equal(output.AsSpan(7 * PacketSize, PacketSize).ToArray(),
                output.AsSpan(8 * PacketSize, PacketSize).ToArray());
        }
    }

    [Fact]
    public async Task InterleavedVideoAndAudioSplitHeadersUseIndependentState()
    {
        using var files = new SampleFiles();
        var previous = Enumerable.Range(0, 10).SelectMany(index => new[]
        {
            Packet(index, index * 9_000L), Packet(index, index * 9_000L + 1_000, hasDts: true, pid: 257)
        }).ToArray();
        var video = Split(Packet(0, 900_000), 12);
        var audio = Split(Packet(0, 901_000, hasDts: true, pid: 257), 18);
        byte[][] segment = [video[0], audio[0], video[1], audio[1],
            Packet(2, 909_000), Packet(2, 910_000, hasDts: true, pid: 257), Packet(3, 918_000)];
        await File.WriteAllBytesAsync(files.Source, previous.Concat(segment).SelectMany(p => p).ToArray());
        await new TsClipMergeService().MergeAsync(new TsClipMergeRequest
        {
            SourcePath = files.Source, OutputPath = files.Output, VideoPid = 256, AudioPids = [257],
            Ranges = [new(0, 10 * PacketSize, 0, 0.4), new(20 * PacketSize, (20 + segment.Length) * PacketSize, 10, 10.2)]
        });
        var output = await File.ReadAllBytesAsync(files.Output);
        var videoPayload = ReadPesPayload(output, 10 * PacketSize, 256);
        var audioPayload = ReadPesPayload(output, 10 * PacketSize, 257);
        Assert.Equal(45_000, TsTimestampFieldCodec.ReadPesTimestamp(videoPayload.AsSpan(9, 5)));
        Assert.Equal(46_000, TsTimestampFieldCodec.ReadPesTimestamp(audioPayload.AsSpan(9, 5)));
        Assert.Equal(44_000, TsTimestampFieldCodec.ReadPesTimestamp(audioPayload.AsSpan(14, 5)));
    }

    [Fact]
    public async Task SplitHeaderAcrossTheCopyBufferKeepsTheNextReadPosition()
    {
        using var files = new SampleFiles();
        var rented = ArrayPool<byte>.Shared.Rent(PacketSize * 4096);
        var packetsInBuffer = rented.Length / PacketSize;
        ArrayPool<byte>.Shared.Return(rented);
        var segment = new List<byte[]> { Packet(0, 900_000), Packet(1, 909_000) };
        while (segment.Count < packetsInBuffer - 1) segment.Add(NullPacket());
        var original = Packet(2, 918_000, hasDts: true);
        segment.AddRange(Split(original, 18));
        segment.Add(Packet(4, 927_000));
        await MergeAsync(files, segment);
        var output = await File.ReadAllBytesAsync(files.Output);
        var start = (5 + packetsInBuffer - 1) * PacketSize;
        var payload = ReadPesPayload(output, start, 256);
        Assert.Equal(63_000, TsTimestampFieldCodec.ReadPesTimestamp(payload.AsSpan(9, 5)));
        Assert.Equal(61_000, TsTimestampFieldCodec.ReadPesTimestamp(payload.AsSpan(14, 5)));
        Assert.Equal(original[23..], payload[19..]);
        Assert.Equal(72_000, TsTimestampFieldCodec.ReadPesTimestamp(output.AsSpan(output.Length - PacketSize + 13, 5)));
    }

    [Fact]
    public async Task SplitHeadersAtThePreviousSegmentEndStillDetermineTheJoin()
    {
        using var files = new SampleFiles();
        var first = new List<byte[]> { Packet(0, 0) };
        first.AddRange(Split(Packet(1, 9_000), 10));
        first.AddRange(Split(Packet(3, 18_000), 3));
        var second = new[] { Packet(5, 900_000), Packet(6, 909_000) };
        await File.WriteAllBytesAsync(files.Source, first.Concat(new[] { NullPacket() }).Concat(second)
            .SelectMany(p => p).ToArray());
        await new TsClipMergeService().MergeAsync(new TsClipMergeRequest
        {
            SourcePath = files.Source, OutputPath = files.Output, VideoPid = 256,
            Ranges = [new(0, first.Count * PacketSize, 0, 0.2),
                new((first.Count + 1) * PacketSize, -1, 10, 10.1)]
        });
        var output = await File.ReadAllBytesAsync(files.Output);
        Assert.Equal(27_000, TsTimestampFieldCodec.ReadPesTimestamp(output.AsSpan(first.Count * PacketSize + 13, 5)));
    }

    [Fact]
    public async Task ShortPsiPayloadIsNotMistakenForAnIncompletePesHeader()
    {
        using var files = new SampleFiles();
        var shortPsi = new byte[PacketSize];
        shortPsi[0] = 0x47; shortPsi[1] = 0x50; shortPsi[2] = 0;
        shortPsi[3] = 0x30; shortPsi[4] = 182; shortPsi[187] = 0;
        var nextPsi = new byte[PacketSize];
        nextPsi[0] = 0x47; nextPsi[1] = 0x50; nextPsi[2] = 0; nextPsi[3] = 0x11;
        nextPsi[4] = 0; nextPsi[5] = 2; nextPsi[6] = 0xB0;
        await MergeAsync(files, [Packet(0, 900_000), Packet(1, 909_000), shortPsi, nextPsi, Packet(2, 918_000)]);
        var output = await File.ReadAllBytesAsync(files.Output);
        Assert.Equal(shortPsi, output.AsSpan(7 * PacketSize, PacketSize).ToArray());
        Assert.Equal(nextPsi, output.AsSpan(8 * PacketSize, PacketSize).ToArray());
    }

    [Fact]
    public async Task SplitHeaderOutsideTheSelectedRangeCannotBeUsedForRewriting()
    {
        using var files = new SampleFiles();
        var first = Enumerable.Range(0, 10).Select(index => Packet(index, index * 9_000L));
        var split = Split(Packet(0, 900_000, hasDts: true), 18);
        var data = first.Concat(split).SelectMany(p => p).ToArray();
        await File.WriteAllBytesAsync(files.Source, data);
        var exception = await Assert.ThrowsAsync<TsClipMergeException>(() => new TsClipMergeService().MergeAsync(
            new TsClipMergeRequest
            {
                SourcePath = files.Source, OutputPath = files.Output, VideoPid = 256,
                Ranges = [new(0, 5 * PacketSize, 0, 0.4), new(10 * PacketSize, 11 * PacketSize, 10, 10.1)]
            }));
        Assert.Equal(TsClipMergeErrorCode.InvalidRange, exception.Code);
        Assert.False(File.Exists(files.Output));
        Assert.Equal(data, await File.ReadAllBytesAsync(files.Source));
    }

    private static async Task MergeAsync(SampleFiles files, IReadOnlyList<byte[]> segment,
        bool hasDts = false, long basePts = 0)
    {
        var previous = Enumerable.Range(0, 10).Select(index => Packet(index, basePts + index * 9_000L, hasDts));
        await File.WriteAllBytesAsync(files.Source, previous.Concat(segment).SelectMany(p => p).ToArray());
        await new TsClipMergeService().MergeAsync(new TsClipMergeRequest
        {
            SourcePath = files.Source, OutputPath = files.Output, VideoPid = 256,
            Ranges = [new(0, 5 * PacketSize, 0, 0.4), new(10 * PacketSize, -1, 10, 10.2)]
        });
    }

    private static byte[] Packet(int continuity, long pts, bool hasDts = false, int pid = 256)
    {
        var packet = new byte[PacketSize];
        packet.AsSpan().Fill(0xA5);
        packet[0] = 0x47; packet[1] = (byte)(0x40 | (pid >> 8)); packet[2] = (byte)pid;
        packet[3] = (byte)(0x10 | (continuity & 15));
        new byte[] { 0, 0, 1, 0xE0, 0, 0, 0x80, (byte)(hasDts ? 0xC0 : 0x80),
            (byte)(hasDts ? 10 : 5) }.CopyTo(packet, 4);
        WriteTimestamp(packet.AsSpan(13, 5), pts, hasDts ? 3 : 2);
        if (hasDts) WriteTimestamp(packet.AsSpan(18, 5), pts - 2_000, 1);
        return packet;
    }

    private static byte[][] Split(byte[] original, int firstBytes, int followingBytes = 184)
    {
        var packets = new List<byte[]>();
        var payload = original[4..];
        var consumed = 0;
        while (consumed < payload.Length)
        {
            var count = Math.Min(payload.Length - consumed, consumed == 0 ? firstBytes : followingBytes);
            var packet = new byte[PacketSize];
            packet[0] = 0x47; packet[1] = (byte)(original[1] & (consumed == 0 ? 0xFF : 0xBF));
            packet[2] = original[2]; packet[3] = (byte)((count < 184 ? 0x30 : 0x10) |
                ((original[3] + packets.Count) & 15));
            if (count < 184) packet[4] = (byte)(183 - count);
            payload.AsSpan(consumed, count).CopyTo(packet.AsSpan(PacketSize - count));
            consumed += count;
            packets.Add(packet);
        }
        return packets.ToArray();
    }

    private static byte[] ReadPesPayload(byte[] output, int start, int pid)
    {
        var bytes = new List<byte>();
        var continuity = -1;
        for (var offset = start; offset + PacketSize <= output.Length; offset += PacketSize)
        {
            var packet = output.AsSpan(offset, PacketSize);
            if (((packet[1] & 31) << 8 | packet[2]) != pid) continue;
            if ((packet[3] & 15) == continuity) continue;
            if (bytes.Count > 0 && (packet[1] & 0x40) != 0) break;
            continuity = packet[3] & 15;
            var payloadOffset = 4 + ((packet[3] & 0x20) != 0 ? packet[4] + 1 : 0);
            bytes.AddRange(packet[payloadOffset..].ToArray());
        }
        return bytes.ToArray();
    }

    private static byte[] NullPacket()
    {
        var packet = new byte[PacketSize];
        packet[0] = 0x47; packet[1] = 0x1F; packet[2] = 0xFF; packet[3] = 0x10;
        return packet;
    }

    private static void WriteTimestamp(Span<byte> bytes, long pts, int prefix)
    {
        pts = Modulo(pts);
        bytes[0] = (byte)(((long)prefix << 4) | 1L | ((pts >> 29) & 14)); bytes[1] = (byte)(pts >> 22);
        bytes[2] = (byte)(((pts >> 14) & 254) | 1); bytes[3] = (byte)(pts >> 7);
        bytes[4] = (byte)(((pts << 1) & 254) | 1);
    }

    private static long Modulo(long pts) => pts & (Wrap - 1);

    private sealed class SampleFiles : IDisposable
    {
        public string Source { get; } = Path.Combine(Path.GetTempPath(), $"split-pes-{Guid.NewGuid():N}.ts");
        public string Output => Source + ".merged.ts";
        public void Dispose() { File.Delete(Source); File.Delete(Output); }
    }
}
