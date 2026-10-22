using System.Buffers.Binary;
using TSCutter.GUI.Models.TransportStream.Processing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Utils.TransportStream.Packets;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

public sealed class TsProgramTableSupplementTests
{
    [Theory]
    [InlineData(188)]
    [InlineData(192)]
    public async Task SingleRangeAddsTablesAndPreservesOtherPacketsAndArrivalHeaders(int stride)
    {
        var pat = TableProbeData.Pat();
        var pmt = TableProbeData.Pmt(descriptorBytes: 200);
        byte[] media = TableProbeData.Padding();
        media[1] = 1; media[2] = 0; media[3] = 0x1a;
        byte[] data = [.. TableProbeData.Section(0, pat), .. TableProbeData.Section(0x1000, pmt),
            .. media, .. TableProbeData.Section(0, pat, 8), .. TableProbeData.Section(0x1000, pmt, 9), .. media];
        if (stride == 192) data = AsM2ts(data);
        using var files = new TableProbeData.Files(data);
        var request = new TsClipMergeRequest
        {
            SourcePath = files.Source, OutputPath = files.Output,
            Ranges = [new(3 * stride, -1, 0, 1)]
        };
        var result = await new TsClipMergeService().MergeAsync(request, addProgramTables: true);
        var output = File.ReadAllBytes(files.Output);
        Assert.Equal(output.Length, result.OutputBytes);
        const int prefixPackets = 3;
        Assert.Equal(data.Length, output.Length);
        var layout = new TsPacketLayout(stride, stride == 192 ? 4 : 0);
        using var stream = new MemoryStream(output);
        Assert.True(TsExportProgramTableProbe.HasTables(stream, [(0, output.Length)], layout, CancellationToken.None));
        // 音视频包逐字节保持一致；新增节目表只影响其自身 PID 的连续计数。
        Assert.Equal(data.AsSpan(3 * stride, stride).ToArray(), output.AsSpan(prefixPackets * stride, stride).ToArray());
        Assert.Equal(data.AsSpan(data.Length - stride, stride).ToArray(), output.AsSpan(output.Length - stride, stride).ToArray());
        var last = new Dictionary<int, int>();
        for (var index = 0; index < output.Length / stride; index++)
        {
            var packet = layout.GetPacketBody(output.AsSpan(), index * stride);
            var pid = (packet[1] & 31) << 8 | packet[2];
            if (pid is not (0 or 0x1000)) continue;
            if (last.TryGetValue(pid, out var previous)) Assert.Equal((previous + 1) & 15, packet[3] & 15);
            last[pid] = packet[3] & 15;
        }
        if (stride == 192)
        {
            var firstArrival = data.AsSpan(3 * stride, 4).ToArray();
            for (var i = 0; i < prefixPackets; i++)
                Assert.Equal(firstArrival, output.AsSpan(i * stride, 4).ToArray());
        }
    }

    [Fact]
    public async Task LatestTablesBeforeTheCutWinOverDifferentTablesAfterIt()
    {
        var first = TableProbeData.Pat();
        var second = TsPsiSectionBuilder.BuildPat(2, 1, [new(2, 0x1001), new(4, 0x1002)]);
        var next = TsPsiSectionBuilder.BuildPat(3, 2, [new(3, 0x1003)]);
        var secondPmt = TableProbeData.Pmt(program: 2, descriptorBytes: 200);
        byte[] source = [.. TableProbeData.Section(0, first), .. TableProbeData.Section(0x1000, TableProbeData.Pmt()),
            .. TableProbeData.Section(0, second, 1), .. TableProbeData.Section(0x1001, secondPmt),
            .. TableProbeData.Section(0x1002, TableProbeData.Pmt(program: 4)), .. TableProbeData.Padding(),
            .. TableProbeData.Section(0, next, 2), .. TableProbeData.Section(0x1003, TableProbeData.Pmt(program: 3))];
        using var files = new TableProbeData.Files(source);
        var tables = await TsExportProgramTableProbe.ReadTablesNearCutAsync(files.Source, 6 * 188, CancellationToken.None);
        Assert.Equal(3, tables.Count);
        Assert.Equal(second, tables[0].Data);
        Assert.Equal(0x1001, tables[1].Pid);
        Assert.Equal(secondPmt, tables[1].Data);
        Assert.Equal(0x1002, tables[2].Pid);
    }

    [Fact]
    public async Task MalformedNewPatDoesNotReplaceTheLastCompleteTables()
    {
        var pat = TableProbeData.Pat();
        var invalid = (byte[])pat.Clone();
        invalid[5] += 2;
        invalid[10] = 0xe0;
        invalid[11] = 0;
        TsPsiSectionBuilder.WriteCrc(invalid);
        using var files = new TableProbeData.Files([.. TableProbeData.Section(0, pat),
            .. TableProbeData.Section(0x1000, TableProbeData.Pmt()),
            .. TableProbeData.Section(0, invalid, 1), .. TableProbeData.Padding()]);
        var tables = await TsExportProgramTableProbe.ReadTablesNearCutAsync(files.Source, 3 * 188, CancellationToken.None);
        Assert.Equal(2, tables.Count);
        Assert.Equal(pat, tables[0].Data);
    }

    [Fact]
    public async Task FileStartCanUseNearbyTablesAndMissingTablesNeverOverwriteAnOutput()
    {
        byte[] source = [.. TableProbeData.Padding(), .. TableProbeData.Padding(),
            .. TableProbeData.Section(0, TableProbeData.Pat()), .. TableProbeData.Section(0x1000, TableProbeData.Pmt())];
        using var files = new TableProbeData.Files(source);
        Assert.Equal(2, (await TsExportProgramTableProbe.ReadTablesNearCutAsync(files.Source, 0, CancellationToken.None)).Count);
        File.WriteAllBytes(files.Source, [.. TableProbeData.Padding(), .. TableProbeData.Padding()]);
        byte[] previous = [1, 2, 3];
        File.WriteAllBytes(files.Output, previous);
        var error = await Assert.ThrowsAsync<TsClipMergeException>(() => new TsClipMergeService().MergeAsync(new()
        {
            SourcePath = files.Source, OutputPath = files.Output,
            Ranges = [new(0, -1, 0, 1)]
        }, addProgramTables: true));
        Assert.Equal(TsClipMergeErrorCode.ProgramTablesUnavailable, error.Code);
        Assert.Equal(previous, File.ReadAllBytes(files.Output));
    }

    private static byte[] AsM2ts(byte[] data)
    {
        var output = new byte[data.Length / 188 * 192];
        for (var index = 0; index < data.Length / 188; index++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(index * 192), 0x80000000u | (uint)(index * 100));
            data.AsSpan(index * 188, 188).CopyTo(output.AsSpan(index * 192 + 4));
        }
        return output;
    }
}
