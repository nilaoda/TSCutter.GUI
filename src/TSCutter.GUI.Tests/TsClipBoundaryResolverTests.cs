using TSCutter.GUI.Utils;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class TsClipBoundaryResolverTests
{
    private const int VideoPid = 256;
    private const int PacketSize = 188;

    [Fact]
    public async Task CompressedFrameSpanningALaterPesIsKeptUntilItsWholeContentIsVerified()
    {
        var selected = Pes(0, 90_000);
        var continuation = Pes(1, 100_000);
        for (var index = 18; index < PacketSize; index++)
        {
            selected[index] = (byte)index;
            continuation[index] = (byte)(index + 17);
        }
        var compressed = selected[18..].Concat(continuation[18..]).ToArray();
        using var source = new Sample(selected, continuation, Pes(2, 110_000));
        Assert.Equal(2 * PacketSize, await ResolveEndAsync(source.Path,
            0, 90_000, signature: PacketEndSignature.Create(compressed)));
    }

    [Fact]
    public async Task ContentMismatchOrMissingPacketSignatureCannotProduceABoundary()
    {
        var selected = Pes(0, 90_000);
        var compressed = selected[18..];
        var signature = PacketEndSignature.Create(compressed);
        selected[^1] ^= 1;
        using var source = new Sample(selected, Pes(1, 100_000));
        await Assert.ThrowsAsync<InvalidDataException>(() => ResolveEndAsync(
            source.Path, 0, 90_000, signature: signature));
        await Assert.ThrowsAsync<InvalidDataException>(() => ResolveEndAsync(
            source.Path, 0, 90_000, signature: default(PacketEndSignature)));
    }

    [Fact]
    public async Task ParserNormalizedAnnexBPrefixDoesNotRequireChangingSourceBytes()
    {
        var selected = Pes(0, 90_000);
        selected[18] = 0; selected[19] = 0; selected[20] = 1;
        var compressed = new byte[] { 0 }.Concat(selected[18..]).ToArray();
        using var source = new Sample(selected, Pes(1, 100_000));
        Assert.Equal(PacketSize, await ResolveEndAsync(source.Path,
            0, 90_000, signature: PacketEndSignature.Create(compressed)));
    }

    [Fact]
    public async Task PesStuffingDoesNotBecomePartOfTheCompressedFrame()
    {
        var selected = Pes(0, 90_000);
        var continuation = Pes(1, 100_000);
        // PES 长度包含固定头后三字节、PTS 和 40 字节码流；余下字节是填充。
        selected[8] = 0; selected[9] = 48;
        var compressed = selected[18..58].Concat(continuation[18..]).ToArray();
        using var source = new Sample(selected, continuation, Pes(2, 110_000));
        Assert.Equal(2 * PacketSize, await ResolveEndAsync(source.Path,
            0, 90_000, signature: PacketEndSignature.Create(compressed)));
    }

    [Fact]
    public async Task IncludesTheWholeSelectedPesAndReorderedLeadingPictures()
    {
        using var source = new Sample(
            Packet(8191, 0), Pes(0, 90_000), Packet(VideoPid, 1),
            Packet(257, 0), Pes(2, 86_400), Packet(VideoPid, 3),
            Pes(4, 84_600), Pes(5, 88_200), Pes(6, 97_200));

        var end = await ResolveEndAsync(source.Path, PacketSize, 90_000);
        Assert.Equal(8 * PacketSize, end);
        var output = source.Path + ".cut";
        try
        {
            await CommonUtil.CopyFileAsync(new FileInfo(source.Path), output, PacketSize, end);
            var original = await File.ReadAllBytesAsync(source.Path);
            Assert.Equal(original[PacketSize..(int)end], await File.ReadAllBytesAsync(output));
        }
        finally { File.Delete(output); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(300)]
    public async Task TimestampWrapKeepsEarlierFramesAndStopsAtALaterFrame(long offset)
    {
        const long wrap = 1L << 33;
        using var source = new Sample(Pes(0, wrap - 100), Pes(1, wrap - 200), Pes(2, 100));
        var end = await ResolveEndAsync(source.Path, 0, wrap - 100 + offset);
        Assert.Equal(offset < 200 ? 2 * PacketSize : 3 * PacketSize, end);
    }

    [Fact]
    public async Task SplitPesHeaderStopsAtItsFirstPacketRatherThanTheContinuation()
    {
        var later = Pes(2, 100_000);
        var first = Packet(VideoPid, 2);
        first[1] |= 0x40;
        first[3] |= 0x20;
        first[4] = 180; // 本包仅剩三个 PES 字节。
        first[5] = 0;
        later.AsSpan(4, 3).CopyTo(first.AsSpan(185));
        var continuation = Packet(VideoPid, 3);
        later.AsSpan(7, 16).CopyTo(continuation.AsSpan(4));
        using var source = new Sample(Pes(0, 90_000), Packet(VideoPid, 1), first, continuation);
        Assert.Equal(2 * PacketSize,
            await ResolveEndAsync(source.Path, 0, 90_000));
    }

    [Fact]
    public async Task MissingPtsIsIncludedUntilAComparableTimestampOrEof()
    {
        var noPts = Pes(1, 0);
        noPts[11] = 0;
        noPts[12] = 0;
        using var source = new Sample(Pes(0, 90_000), noPts, Pes(2, 100_000));
        Assert.Equal(2 * PacketSize,
            await ResolveEndAsync(source.Path, 0, 90_000));
        using var last = new Sample(Pes(0, 90_000), Packet(VideoPid, 1), noPts);
        // 最后一个包改成连续计数，结尾固定在源文件当前 EOF。
        using (var file = new FileStream(last.Path, FileMode.Open, FileAccess.Write))
        {
            file.Position = 2 * PacketSize + 3;
            file.WriteByte(0x12);
        }
        Assert.Equal(3 * PacketSize,
            await ResolveEndAsync(last.Path, 0, 90_000));
    }

    [Fact]
    public async Task DuplicatePacketsDoNotRestartTheSelectedPes()
    {
        var selected = Pes(15, 90_000);
        using var source = new Sample(selected, selected, Packet(VideoPid, 0), Pes(1, 100_000));
        Assert.Equal(3 * PacketSize,
            await ResolveEndAsync(source.Path, 0, 90_000));
    }

    [Fact]
    public async Task InterleavedPidAndAdaptationOnlyPacketsDoNotAffectPesContinuity()
    {
        var adaptation = Packet(VideoPid, 12);
        adaptation[3] = 0x2C;
        adaptation[4] = 183;
        adaptation[5] = 0;
        using var source = new Sample(Pes(0, 90_000), Pes(8, 100_000, 257),
            adaptation, Packet(VideoPid, 1), Pes(2, 100_000));
        Assert.Equal(4 * PacketSize,
            await ResolveEndAsync(source.Path, 0, 90_000));
    }

    [Fact]
    public async Task ExplicitDiscontinuityAllowsTheContinuityCounterToReset()
    {
        var original = Pes(9, 100_000);
        var reset = Packet(VideoPid, 9);
        reset[1] |= 0x40;
        reset[3] |= 0x20;
        reset[4] = 1;
        reset[5] = 0x80;
        original.AsSpan(4, 182).CopyTo(reset.AsSpan(6));
        using var source = new Sample(Pes(0, 90_000), reset);
        Assert.Equal(PacketSize, await ResolveEndAsync(source.Path, 0, 90_000));
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("scrambled")]
    [InlineData("sync")]
    [InlineData("marker")]
    [InlineData("truncated-header")]
    public async Task UnreliableBoundaryIsRejectedInsteadOfSilentlyDroppingTheSelectedFrame(string damage)
    {
        var next = Pes(1, 100_000);
        switch (damage)
        {
            case "gap": next[3] = 0x12; break;
            case "scrambled": next[3] |= 0x80; break;
            case "sync": next[0] = 0; break;
            case "marker": next[13] &= 0xFE; break;
            case "truncated-header":
                next[3] |= 0x20;
                next[4] = 180;
                next[5] = 0;
                next[185] = 0; next[186] = 0; next[187] = 1;
                break;
        }
        using var source = new Sample(Pes(0, 90_000), next);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ResolveEndAsync(source.Path, 0, 90_000));
    }

    [Fact]
    public async Task CancelledScanDoesNotReadTheSource()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ResolveEndAsync("missing.ts", 0, 90_000, cancellation.Token));
    }

    [Fact]
    public async Task SlowReadsDoNotConsumeTheScanProcessingBudget()
    {
        var clock = new ScanTestTimeProvider(TimeSpan.Zero);
        using var stream = new SlowReadStream(MultiBufferSample(), clock);
        var signature = PacketEndSignature.Create(Enumerable.Repeat((byte)0xFF, 170).ToArray());

        var end = await TsClipBoundaryResolver.ResolveEndCoreAsync(stream, 0, 90_000,
            signature, CancellationToken.None, clock);

        Assert.Equal(1024 * PacketSize, end);
        Assert.Equal(3, stream.ReadCount);
        Assert.True(clock.WaitedForReads >= TimeSpan.FromMinutes(6));
    }

    [Fact]
    public async Task ProcessingBudgetAccumulatesAcrossBuffersAndStopsBeforeAnotherRead()
    {
        // 每次处理缓冲耗时 600 ms；两个缓冲累计超时，不能在每次读取后重置预算。
        var clock = new ScanTestTimeProvider(TimeSpan.FromMilliseconds(600));
        using var stream = new SlowReadStream(MultiBufferSample(), clock);
        var signature = PacketEndSignature.Create(Enumerable.Repeat((byte)0xFF, 170).ToArray());

        await Assert.ThrowsAsync<TimeoutException>(() => TsClipBoundaryResolver.ResolveEndCoreAsync(
            stream, 0, 90_000, signature, CancellationToken.None, clock));

        Assert.Equal(2, stream.ReadCount);
    }

    [Fact]
    public async Task UserCancellationStillInterruptsAPendingRead()
    {
        using var stream = new PendingReadStream(Pes(0, 90_000));
        using var cancellation = new CancellationTokenSource();
        var signature = PacketEndSignature.Create(Enumerable.Repeat((byte)0xFF, 170).ToArray());
        var scan = TsClipBoundaryResolver.ResolveEndCoreAsync(stream, 0, 90_000,
            signature, cancellation.Token, TimeProvider.System);
        await stream.ReadStarted.Task;

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
    }

    [Fact]
    public async Task MissingSuccessorCannotTurnTheScanLimitIntoAFalseEndOfFile()
    {
        using var source = new Sample(Pes(0, 90_000));
        var block = Enumerable.Range(0, 512).SelectMany(_ => Packet(8191, 0)).ToArray();
        await using (var output = new FileStream(source.Path, FileMode.Append, FileAccess.Write))
            for (long written = PacketSize; written <= TsClipBoundaryResolver.MaximumScanBytes;
                 written += block.Length)
                await output.WriteAsync(block);
        await Assert.ThrowsAsync<InvalidDataException>(() => ResolveEndAsync(source.Path, 0, 90_000));
    }

    [Fact]
    public async Task BoundaryPastTheReadBufferIsLocatedWithoutLosingPacketAlignment()
    {
        var packets = Enumerable.Range(0, 1024).Select(index =>
            index == 0 ? Pes(0, 90_000) : Packet(VideoPid, index & 15)).ToList();
        packets.Add(Pes(0, 100_000));
        using var source = new Sample(packets.ToArray());
        Assert.Equal(1024 * PacketSize,
            await ResolveEndAsync(source.Path, 0, 90_000));
    }

    private static Task<long> ResolveEndAsync(string path, long position, long pts,
        CancellationToken cancellationToken = default, PacketEndSignature? signature = null) =>
        TsClipBoundaryResolver.ResolveEndAsync(path, position, pts,
            signature ?? PacketEndSignature.Create(Enumerable.Repeat((byte)0xFF, 170).ToArray()),
            cancellationToken);

    private static byte[] Packet(int pid, int continuity)
    {
        var packet = new byte[PacketSize];
        Array.Fill(packet, (byte)0xFF);
        packet[0] = 0x47;
        packet[1] = (byte)(pid >> 8);
        packet[2] = (byte)pid;
        packet[3] = (byte)(0x10 | continuity);
        return packet;
    }

    private static byte[] Pes(int continuity, long pts, int pid = VideoPid)
    {
        var packet = Packet(pid, continuity);
        packet[1] |= 0x40;
        new byte[] { 0, 0, 1, 0xE0, 0, 0, 0x80, 0x80, 5 }.CopyTo(packet, 4);
        packet[13] = (byte)(0x21 | ((pts >> 29) & 0x0E));
        packet[14] = (byte)(pts >> 22);
        packet[15] = (byte)(((pts >> 14) & 0xFE) | 1);
        packet[16] = (byte)(pts >> 7);
        packet[17] = (byte)(((pts << 1) & 0xFE) | 1);
        return packet;
    }

    private static byte[] MultiBufferSample() => new[] { Pes(0, 90_000) }
        .Concat(Enumerable.Range(0, 1023).Select(_ => Packet(8191, 0)))
        .Append(Pes(1, 100_000)).SelectMany(packet => packet).ToArray();

    // 用虚拟时钟模拟读取和计算耗时，回归测试不需要真的等待磁盘唤醒。
    private sealed class ScanTestTimeProvider(TimeSpan sampleStep) : TimeProvider
    {
        private long timestamp;
        public TimeSpan WaitedForReads { get; private set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp()
        {
            var result = timestamp;
            timestamp += sampleStep.Ticks;
            return result;
        }
        public void WaitForRead()
        {
            var delay = TimeSpan.FromMinutes(2);
            timestamp += delay.Ticks;
            WaitedForReads += delay;
        }
    }

    private sealed class SlowReadStream(byte[] data, ScanTestTimeProvider clock) : MemoryStream(data)
    {
        public int ReadCount { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            clock.WaitForRead();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class PendingReadStream(byte[] data) : MemoryStream(data)
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class Sample : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            $"ts-clip-boundary-{Guid.NewGuid():N}.ts");
        public Sample(params byte[][] packets)
        {
            using var file = File.Create(Path);
            foreach (var packet in packets) file.Write(packet);
        }
        public void Dispose() => File.Delete(Path);
    }
}
