using TSCutter.GUI.Models.TransportStream.Analysis;
using TSCutter.GUI.Models.TransportStream.Processing;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Utils.TransportStream.Packets;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

public sealed class TsExportProgramTableProbeTests
{
    [Theory]
    [InlineData(188)]
    [InlineData(192)]
    public void CompletePatAndSplitPmtAreRecognized(int stride)
    {
        byte[] data = [.. TableProbeData.Section(0, TableProbeData.Pat()),
            .. TableProbeData.Section(0x1000, TableProbeData.Pmt(descriptorBytes: 200))];
        if (stride == 192)
            data = data.Chunk(188).SelectMany(packet => new byte[] { 1, 2, 3, 4 }.Concat(packet)).ToArray();
        using var input = new MemoryStream(data);
        Assert.True(TsExportProgramTableProbe.HasTables(input, [(0, data.Length)],
            TsPacketLayout.FromPacketOffset(stride, 0), CancellationToken.None));
    }

    [Fact]
    public void CutAfterPatAndShortClipNeverBorrowTablesOutsideTheRange()
    {
        byte[] data = [.. TableProbeData.Section(0, TableProbeData.Pat()),
            .. TableProbeData.Section(0x1000, TableProbeData.Pmt()), .. TableProbeData.Padding(),
            .. TableProbeData.Section(0, TableProbeData.Pat(), 1),
            .. TableProbeData.Section(0x1000, TableProbeData.Pmt(), 1)];
        using var input = new MemoryStream(data);
        var layout = new TsPacketLayout(188, 0);
        Assert.False(TsExportProgramTableProbe.HasTables(input, [(188, 3 * 188)], layout, CancellationToken.None));
        Assert.True(TsExportProgramTableProbe.HasTables(input, [(188, data.Length)], layout, CancellationToken.None));
        Assert.False(TsExportProgramTableProbe.HasTables(input, [(2 * 188, 3 * 188)], layout, CancellationToken.None));
    }

    [Fact]
    public void CompletePatRequiresEverySectionAndCorrespondingProgramMap()
    {
        var pat = TableProbeData.Pat();
        pat[7] = 1;
        TsPsiSectionBuilder.WriteCrc(pat);
        var second = TsPsiSectionBuilder.BuildPat(1, 0, [new(2, 0x1001)]);
        second[6] = second[7] = 1;
        TsPsiSectionBuilder.WriteCrc(second);
        byte[] incomplete = [.. TableProbeData.Section(0, pat), .. TableProbeData.Section(0x1000, TableProbeData.Pmt())];
        Assert.False(Probe(incomplete));
        byte[] missingPmt = [.. incomplete, .. TableProbeData.Section(0, second, 1)];
        Assert.False(Probe(missingPmt));
        Assert.True(Probe([.. missingPmt, .. TableProbeData.Section(0x1001, TableProbeData.Pmt(program: 2))]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CorruptOrDiscontinuousPmtIsNotAccepted(bool corruptCrc)
    {
        var pmt = TableProbeData.Pmt(descriptorBytes: 200);
        if (corruptCrc) pmt[^1] ^= 1;
        var packets = TableProbeData.Section(0x1000, pmt);
        if (!corruptCrc) packets[188 + 3] = 0x12;
        Assert.False(Probe([.. TableProbeData.Section(0, TableProbeData.Pat()), .. packets]));
    }

    [Fact]
    public void ProbeStopsAtItsByteBudgetAndHonorsCancellation()
    {
        var length = (TsExportProgramTableProbe.MaximumProbeBytes / 188 + 1) * 188;
        using var input = new MemoryStream();
        while (input.Length < length) input.Write(TableProbeData.Padding());
        input.Write(TableProbeData.Section(0, TableProbeData.Pat()));
        input.Write(TableProbeData.Section(0x1000, TableProbeData.Pmt()));
        Assert.False(TsExportProgramTableProbe.HasTables(input, [(0, input.Length)], new(188, 0), CancellationToken.None));
        Assert.InRange(input.Position, 0, TsExportProgramTableProbe.MaximumProbeBytes);
        Assert.Throws<OperationCanceledException>(() => TsExportProgramTableProbe.HasTables(
            input, [(0, input.Length)], new(188, 0), new CancellationToken(true)));
    }

    [Fact]
    public async Task MergeChecksTheActualSortedAndCombinedOutputPrefix()
    {
        byte[] source = [.. TableProbeData.Padding(), .. TableProbeData.Padding(),
            .. TableProbeData.Section(0, TableProbeData.Pat()),
            .. TableProbeData.Section(0x1000, TableProbeData.Pmt()), .. TableProbeData.Padding()];
        using var files = new TableProbeData.Files(source);
        var request = new TsClipMergeRequest
        {
            SourcePath = files.Source, OutputPath = files.Output,
            Ranges = [new(2 * 188, -1, 2, 5), new(0, 188, 0, 1)]
        };
        Assert.True(await TsExportProgramTableProbe.HasTablesAsync(request, CancellationToken.None));
        request = new TsClipMergeRequest
        {
            SourcePath = files.Source, OutputPath = files.Output,
            Ranges = [new(0, 2 * 188, 0, 2), new(4 * 188, -1, 4, 5)]
        };
        Assert.False(await TsExportProgramTableProbe.HasTablesAsync(request, CancellationToken.None));
    }

    private static bool Probe(byte[] data)
    {
        using var input = new MemoryStream(data);
        return TsExportProgramTableProbe.HasTables(input, [(0, data.Length)], new(188, 0), CancellationToken.None);
    }
}

internal static class TableProbeData
{
    public static byte[] Pat() => TsPsiSectionBuilder.BuildPat(1, 0, [new(1, 0x1000)]);
    public static byte[] Pmt(int program = 1, int descriptorBytes = 0) => TsPsiSectionBuilder.BuildPmt(
        program, 0, 0x100, new byte[descriptorBytes], [new(0x100, new TsStreamDefinition { StreamType = 0x1b })]);

    public static byte[] Padding()
    {
        var packet = new byte[188];
        packet.AsSpan().Fill(0xff);
        packet[0] = 0x47; packet[1] = 0x1f; packet[2] = 0xff; packet[3] = 0x10;
        return packet;
    }

    public static byte[] Section(int pid, byte[] section, int continuity = 0)
    {
        using var output = new MemoryStream();
        var offset = 0;
        while (offset < section.Length)
        {
            var packet = Padding();
            packet[1] = (byte)(pid >> 8 | (offset == 0 ? 0x40 : 0));
            packet[2] = (byte)pid; packet[3] = (byte)(0x10 | (continuity++ & 15));
            var payload = offset == 0 ? 5 : 4;
            if (offset == 0) packet[4] = 0;
            var count = Math.Min(188 - payload, section.Length - offset);
            section.AsSpan(offset, count).CopyTo(packet.AsSpan(payload));
            output.Write(packet);
            offset += count;
        }
        return output.ToArray();
    }

    public sealed class Files : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "ts-tables-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(DirectoryPath, "source.ts");
        public string Output => Path.Combine(DirectoryPath, "output.ts");
        public Files(byte[] source)
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllBytes(Source, source);
        }
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }
}
