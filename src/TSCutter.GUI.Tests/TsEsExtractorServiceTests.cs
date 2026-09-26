using TSCutter.GUI.Models;
using TSCutter.GUI.Services;
using TSCutter.GUI.Utils;
using Xunit;

namespace TSCutter.GUI.Tests;

public sealed class TsEsExtractorServiceTests : IDisposable
{
    private const int VideoPid = 0x0101;
    private const int AudioPid = 0x0102;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ts-es-{Guid.NewGuid():N}");

    public TsEsExtractorServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ExtractAsync_StripsPesHeaderAndPreservesElementaryBytes()
    {
        var input = Path.Combine(_directory, "source.ts");
        var output = Path.Combine(_directory, "video.h264");
        var elementary = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84, 0x21 };
        await File.WriteAllBytesAsync(input, BuildPacket(VideoPid, 0, true, BuildPes(elementary)));

        var result = await ExtractAsync(input, new TsEsExtractionOutput { Pid = VideoPid, OutputPath = output });

        Assert.Equal(elementary, await File.ReadAllBytesAsync(output));
        Assert.Equal(elementary.Length, result.BytesWritten);
        Assert.Equal(0, result.AnomalyCount);
    }

    [Fact]
    public async Task ExtractAsync_HandlesPesHeaderSplitAcrossPackets()
    {
        var input = Path.Combine(_directory, "split.ts");
        var output = Path.Combine(_directory, "audio.aac");
        var header = BuildPesHeader(optionalHeaderLength: 12);
        var elementary = new byte[] { 0xFF, 0xF1, 0x50, 0x80, 0x01, 0x7F, 0xFC };
        var packets = Concat(
            BuildPacket(AudioPid, 0, true, header[..8]),
            BuildPacket(AudioPid, 1, false, Concat(header[8..], elementary)));
        await File.WriteAllBytesAsync(input, packets);

        await ExtractAsync(input, new TsEsExtractionOutput { Pid = AudioPid, OutputPath = output });

        Assert.Equal(elementary, await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task ExtractAsync_ExtractsMultiplePidsInOnePass()
    {
        var input = Path.Combine(_directory, "multi.ts");
        var videoOutput = Path.Combine(_directory, "video.h265");
        var audioOutput = Path.Combine(_directory, "audio.aac");
        var video = new byte[] { 0, 0, 1, 0x26, 0x01, 0xAA };
        var audio = new byte[] { 0xFF, 0xF1, 0x4C, 0x80 };
        await File.WriteAllBytesAsync(input, Concat(
            BuildPacket(VideoPid, 0, true, BuildPes(video, 0xE0)),
            BuildPacket(AudioPid, 0, true, BuildPes(audio, 0xC0))));

        var result = await new TsEsExtractorService().ExtractAsync(input,
        [
            new TsEsExtractionOutput { Pid = VideoPid, OutputPath = videoOutput },
            new TsEsExtractionOutput { Pid = AudioPid, OutputPath = audioOutput }
        ], 0);

        Assert.Equal(video, await File.ReadAllBytesAsync(videoOutput));
        Assert.Equal(audio, await File.ReadAllBytesAsync(audioOutput));
        Assert.Equal(2, result.Tracks.Length);
    }

    [Fact]
    public async Task ExtractAsync_SkipsDuplicateAndUnreliableContinuationPayloads()
    {
        var input = Path.Combine(_directory, "damaged.ts");
        var output = Path.Combine(_directory, "video.h264");
        var first = BuildPacket(VideoPid, 0, true, BuildPes([0x11]));
        var packets = Concat(
            first,
            first,
            BuildPacket(VideoPid, 2, false, [0x22]),
            BuildPacket(VideoPid, 3, true, BuildPes([0x33])),
            BuildPacket(VideoPid, 4, false, [0x44], transportError: true),
            BuildPacket(VideoPid, 5, false, [0x55]),
            BuildPacket(VideoPid, 6, true, BuildPes([0x66])));
        await File.WriteAllBytesAsync(input, packets);

        var result = await ExtractAsync(input, new TsEsExtractionOutput { Pid = VideoPid, OutputPath = output });

        Assert.Equal(new byte[] { 0x11, 0x33, 0x66 }, await File.ReadAllBytesAsync(output));
        var track = Assert.Single(result.Tracks);
        Assert.Equal(1, track.DuplicatePackets);
        Assert.Equal(1, track.ContinuityErrors);
        Assert.Equal(1, track.TransportErrors);
    }

    [Fact]
    public async Task ExtractAsync_AdaptationOnlyPacketDoesNotAdvanceContinuity()
    {
        var input = Path.Combine(_directory, "adaptation-only.ts");
        var output = Path.Combine(_directory, "video.h264");
        await File.WriteAllBytesAsync(input, Concat(
            BuildPacket(VideoPid, 0, true, BuildPes([0x11])),
            BuildAdaptationOnlyPacket(VideoPid, 7),
            BuildPacket(VideoPid, 1, false, [0x22])));

        var result = await ExtractAsync(input, new TsEsExtractionOutput { Pid = VideoPid, OutputPath = output });

        Assert.Equal(new byte[] { 0x11, 0x22 }, await File.ReadAllBytesAsync(output));
        Assert.Equal(0, Assert.Single(result.Tracks).ContinuityErrors);
    }

    [Fact]
    public async Task ExtractAsync_ConflictingDuplicateIsNotUsedAsNewPesStart()
    {
        var input = Path.Combine(_directory, "conflicting-duplicate.ts");
        var output = Path.Combine(_directory, "video.h264");
        await File.WriteAllBytesAsync(input, Concat(
            BuildPacket(VideoPid, 0, true, BuildPes([0x11])),
            BuildPacket(VideoPid, 0, true, BuildPes([0x22])),
            BuildPacket(VideoPid, 1, true, BuildPes([0x33]))));

        var result = await ExtractAsync(input, new TsEsExtractionOutput { Pid = VideoPid, OutputPath = output });

        Assert.Equal(new byte[] { 0x11, 0x33 }, await File.ReadAllBytesAsync(output));
        var track = Assert.Single(result.Tracks);
        Assert.Equal(1, track.DuplicatePackets);
        Assert.Equal(1, track.ContinuityErrors);
    }

    [Fact]
    public async Task ExtractAsync_RespectsLeadingSyncOffset()
    {
        var input = Path.Combine(_directory, "offset.ts");
        var output = Path.Combine(_directory, "video.h264");
        var prefix = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
        var elementary = new byte[] { 0, 0, 1, 0x65 };
        await File.WriteAllBytesAsync(input, Concat(
            prefix, BuildPacket(VideoPid, 0, true, BuildPes(elementary))));

        await new TsEsExtractorService().ExtractAsync(input,
            [new TsEsExtractionOutput { Pid = VideoPid, OutputPath = output }], prefix.Length);

        Assert.Equal(elementary, await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task ExtractAsync_RecoversAfterByteLevelSyncLoss()
    {
        var input = Path.Combine(_directory, "resync.ts");
        var output = Path.Combine(_directory, "video.h264");
        await File.WriteAllBytesAsync(input, Concat(
            BuildPacket(VideoPid, 0, true, BuildPes([0x11])),
            [0xAA, 0xBB, 0xCC],
            BuildPacket(VideoPid, 1, true, BuildPes([0x22])),
            BuildPacket(VideoPid, 2, true, BuildPes([0x33])),
            BuildPacket(VideoPid, 3, true, BuildPes([0x44])),
            BuildPacket(VideoPid, 4, true, BuildPes([0x55]))));

        var result = await ExtractAsync(input, new TsEsExtractionOutput { Pid = VideoPid, OutputPath = output });

        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55 }, await File.ReadAllBytesAsync(output));
        Assert.Equal(3, result.SyncLossBytes);
        Assert.Equal(1, result.AnomalyCount);
    }

    [Fact]
    public async Task ExtractAsync_CancellationRemovesIncompleteOutputs()
    {
        var input = Path.Combine(_directory, "cancel.ts");
        var output = Path.Combine(_directory, "video.h264");
        await File.WriteAllBytesAsync(input, BuildPacket(VideoPid, 0, true, BuildPes([0x11])));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TsEsExtractorService().ExtractAsync(input,
                [new TsEsExtractionOutput { Pid = VideoPid, OutputPath = output }], 0,
                cancellationToken: cancellation.Token));

        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ExtractAsync_RejectsDuplicateOutputPathsBeforeCreatingFiles()
    {
        var input = Path.Combine(_directory, "collision.ts");
        var output = Path.Combine(_directory, "same.bin");
        await File.WriteAllBytesAsync(input, BuildPacket(VideoPid, 0, true, BuildPes([0x11])));

        var exception = await Assert.ThrowsAsync<TsEsExtractionException>(() =>
            new TsEsExtractorService().ExtractAsync(input,
            [
                new TsEsExtractionOutput { Pid = VideoPid, OutputPath = output },
                new TsEsExtractionOutput { Pid = AudioPid, OutputPath = output }
            ], 0));

        Assert.Equal(TsEsExtractionErrorCode.DuplicateOutputPath, exception.Code);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task ExtractAsync_RejectsDuplicatePidBeforeCreatingFiles()
    {
        var input = Path.Combine(_directory, "duplicate-pid.ts");
        var firstOutput = Path.Combine(_directory, "first.bin");
        var secondOutput = Path.Combine(_directory, "second.bin");
        await File.WriteAllBytesAsync(input, BuildPacket(VideoPid, 0, true, BuildPes([0x11])));

        var exception = await Assert.ThrowsAsync<TsEsExtractionException>(() =>
            new TsEsExtractorService().ExtractAsync(input,
            [
                new TsEsExtractionOutput { Pid = VideoPid, OutputPath = firstOutput },
                new TsEsExtractionOutput { Pid = VideoPid, OutputPath = secondOutput }
            ], 0));

        Assert.Equal(TsEsExtractionErrorCode.DuplicatePid, exception.Code);
        Assert.False(File.Exists(firstOutput));
        Assert.False(File.Exists(secondOutput));
    }

    [Theory]
    [InlineData(TsStreamTypes.H264, null, ".h264")]
    [InlineData(TsStreamTypes.Hevc, null, ".h265")]
    [InlineData(TsStreamTypes.Aac, null, ".aac")]
    [InlineData(TsStreamTypes.AacLatm, null, ".latm")]
    [InlineData(TsStreamTypes.Avs3, null, ".avs3")]
    [InlineData(TsStreamTypes.Av3a, null, ".av3a")]
    [InlineData(TsStreamTypes.Mpeg1Audio, TsMpegAudioLayer.LayerII, ".mp2")]
    public void GetFileExtension_UsesResolvedCodec(
        byte streamType, TsMpegAudioLayer? layer, string expected)
    {
        Assert.Equal(expected, TsElementaryStreamUtil.GetFileExtension(streamType, layer));
    }

    private static Task<TsEsExtractionResult> ExtractAsync(
        string input, TsEsExtractionOutput output) =>
        new TsEsExtractorService().ExtractAsync(input, [output], 0);

    private static byte[] BuildPes(ReadOnlySpan<byte> elementary, byte streamId = 0xE0)
        => Concat(BuildPesHeader(0, streamId), elementary.ToArray());

    private static byte[] BuildPesHeader(int optionalHeaderLength, byte streamId = 0xE0)
    {
        var header = new byte[9 + optionalHeaderLength];
        header[2] = 1;
        header[3] = streamId;
        header[6] = 0x80;
        header[8] = (byte)optionalHeaderLength;
        return header;
    }

    private static byte[] BuildPacket(
        int pid, int continuityCounter, bool payloadStart, ReadOnlySpan<byte> payload,
        bool transportError = false)
    {
        if (payload.Length is < 1 or > 182)
            throw new ArgumentOutOfRangeException(nameof(payload));
        var packet = new byte[188];
        packet.AsSpan().Fill(0xFF);
        packet[0] = 0x47;
        packet[1] = (byte)((pid >> 8) & 0x1F);
        if (transportError)
            packet[1] |= 0x80;
        if (payloadStart)
            packet[1] |= 0x40;
        packet[2] = (byte)pid;
        packet[3] = (byte)(0x30 | (continuityCounter & 0x0F));
        var adaptationLength = 183 - payload.Length;
        packet[4] = (byte)adaptationLength;
        if (adaptationLength > 0)
            packet[5] = 0;
        payload.CopyTo(packet.AsSpan(5 + adaptationLength));
        return packet;
    }

    private static byte[] BuildAdaptationOnlyPacket(int pid, int continuityCounter)
    {
        var packet = new byte[188];
        packet.AsSpan().Fill(0xFF);
        packet[0] = 0x47;
        packet[1] = (byte)((pid >> 8) & 0x1F);
        packet[2] = (byte)pid;
        packet[3] = (byte)(0x20 | (continuityCounter & 0x0F));
        packet[4] = 183;
        packet[5] = 0;
        return packet;
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        var result = new byte[arrays.Sum(item => item.Length)];
        var offset = 0;
        foreach (var array in arrays)
        {
            array.CopyTo(result, offset);
            offset += array.Length;
        }
        return result;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // 测试清理不覆盖断言结果。
        }
    }
}
