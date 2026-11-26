using Avalonia.Controls;
using Avalonia.Headless;
using Splat;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.TransportStream.Analysis;
using TSCutter.GUI.Models.TransportStream.Processing;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.TransportStream.Packets;
using TSCutter.GUI.ViewModels.Editing;
using TSCutter.GUI.Views.Editing;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

[Collection("Avalonia UI")]
public sealed class TsProgramTableRecoveryTests
{
    private static readonly byte[] AvsDescriptors = [0x3f, 4, 0x48, 0x41, 0x19, 0x20];

    [Theory]
    [InlineData("single", 188)]
    [InlineData("batch", 188)]
    [InlineData("merge", 188)]
    [InlineData("merge", 192)]
    public async Task CurrentProgramChoiceRebuildsEveryPatAndKeepsAvsPayloads(string mode, int stride)
    {
        var data = Sample();
        if (stride == 192)
            data = data.Chunk(188).SelectMany(packet => new byte[] { 1, 2, 3, 4 }.Concat(packet)).ToArray();
        using var files = new TableProbeData.Files(data);
        await RunAsync(async () =>
        {
            var output = new OutputWindowViewModel
            {
                SourceFilePath = files.Source, OutputPath = files.Output, EndPosition = -1, AudioDelayProgramId = 1
            };
            if (mode == "batch")
                output.SetBatchTasks([new(files.Source, files.Output, 0, -1, AudioDelayProgramId: 1)]);
            if (mode == "merge")
                output.SetMergeTask(new()
                {
                    SourcePath = files.Source, OutputPath = files.Output, AudioDelayProgramId = 1,
                    Ranges = [new(0, -1, 0, 1)]
                });
            output.ConfirmProgramTableWarningAsync = (_, _) =>
            {
                var recovery = Assert.Single(output.ProgramTableWarnings).Recovery;
                Assert.NotNull(recovery);
                Assert.Equal(2, recovery.ProgramCount);
                Assert.Equal(new[] { 1 }, recovery.CompletePrograms);
                Assert.False(recovery.CanSupplement);
                Assert.True(recovery.CanExportCurrentProgram);
                return Task.FromResult(TsProgramTableExportChoice.CurrentProgram);
            };
            if (mode == "single")
            {
                var missing = await output.FindMissingProgramTablesAsync(null, CancellationToken.None);
                output.SetProgramTableChoice(missing, TsProgramTableExportChoice.CurrentProgram);
                // 路径选择在准备之后完成，已确认的处理方式仍属于同一导出任务。
                output.OutputPath = Path.Combine(files.DirectoryPath, "chosen.ts");
            }
            await output.OutputCommand.ExecuteAsync(null);
            Assert.Null(output.Exception);
            Assert.True(output.DialogResult);
            var path = mode == "single" ? output.OutputPath! : files.Output;
            using var analyzer = new TsStreamAnalyzer();
            var catalog = await analyzer.AnalyzeAsync(path, options: new() { IncludeServiceMetadata = true });
            var program = Assert.Single(catalog.Programs).Value;
            Assert.Equal(1, program.ProgramNumber);
            Assert.Equal(0x42, program.StreamDefinitions[0x100].StreamType);
            Assert.Equal(AvsDescriptors, program.StreamDefinitions[0x100].Descriptors);
            var sourceData = File.ReadAllBytes(files.Source);
            var outputData = File.ReadAllBytes(path);
            Assert.Equal(stride, catalog.PacketSize);
            Assert.Equal("TV", catalog.Services[1].ServiceName);
            if (stride == 192)
            {
                sourceData = sourceData.Chunk(stride).SelectMany(packet => packet.Skip(4)).ToArray();
                outputData = outputData.Chunk(stride).SelectMany(packet => packet.Skip(4)).ToArray();
            }
            Assert.Equal(MediaPackets(sourceData), MediaPackets(outputData));
            var outcome = Assert.Single(output.Outcomes);
            Assert.True(outcome.Modifiers.HasFlag(ExportModifiers.ProgramOnly));
            Assert.False(outcome.Modifiers.HasFlag(ExportModifiers.TablesSupplemented));
            Assert.True(await TsExportProgramTableProbe.HasTablesAsync(path, 0, -1, CancellationToken.None));
            // 后续每个原始 PAT 都经过重建，不会重新列出缺少映射信息的节目。
            foreach (var packet in outputData.Chunk(188).Where(packet => Pid(packet) == 0))
            {
                Assert.Equal(13, ((packet[6] & 15) << 8) | packet[7]);
                Assert.Equal(1, (packet[13] << 8) | packet[14]);
            }
        });
    }

    [Theory]
    [InlineData("single")]
    [InlineData("batch")]
    [InlineData("merge")]
    public async Task CurrentProgramRecoveryUsesTheMappingNearTheCut(string mode)
    {
        using var files = new TableProbeData.Files(Sample());
        long start;
        var media = Enumerable.Range(0, 6).SelectMany(counter =>
        {
            var packet = Media(counter);
            packet[2] = 1;
            return packet;
        }).ToArray();
        using (var stream = new FileStream(files.Source, FileMode.Append))
        {
            var padding = Enumerable.Range(0, 4096).SelectMany(_ => TableProbeData.Padding()).ToArray();
            while (stream.Length < 66L * 1024 * 1024)
                stream.Write(padding);
            stream.Write(TableProbeData.Section(0, TsPsiSectionBuilder.BuildPat(7, 0,
                [new(1, 0x1000), new(2, 0x1001)])));
            stream.Write(TableProbeData.Section(0x1000, TsPsiSectionBuilder.BuildPmt(1, 1, 0x101, [],
                [new(0x101, new TsStreamDefinition { StreamType = 0x42, Descriptors = AvsDescriptors })])));
            start = stream.Position;
            stream.Write(media);
        }
        await RunAsync(async () =>
        {
            var output = new OutputWindowViewModel
            {
                SourceFilePath = files.Source, OutputPath = files.Output, StartPosition = start,
                EndPosition = -1, AudioDelayProgramId = 1
            };
            if (mode == "batch")
                output.SetBatchTasks([new(files.Source, files.Output, start, -1, AudioDelayProgramId: 1)]);
            if (mode == "merge")
                output.SetMergeTask(new()
                {
                    SourcePath = files.Source, OutputPath = files.Output, AudioDelayProgramId = 1,
                    Ranges = [new(start, -1, 0, 1)]
                });
            output.ConfirmProgramTableWarningAsync = (_, _) => Task.FromResult(TsProgramTableExportChoice.CurrentProgram);
            await output.OutputCommand.ExecuteAsync(null);
            Assert.Null(output.Exception);
            Assert.True(output.DialogResult);
            using var analyzer = new TsStreamAnalyzer();
            var catalog = await analyzer.AnalyzeAsync(files.Output);
            var program = Assert.Single(catalog.Programs).Value;
            Assert.Equal(AvsDescriptors, Assert.Single(program.StreamDefinitions).Value.Descriptors);
            Assert.Equal(0x101, Assert.Single(program.StreamDefinitions).Key);
            Assert.Equal(media, File.ReadAllBytes(files.Output).Chunk(188)
                .Where(packet => Pid(packet) == 0x101).SelectMany(packet => packet).ToArray());
        });
    }

    [Fact]
    public async Task WarningOnlyOffersAvailableActionsInEveryLanguage()
    {
        using var partial = new TableProbeData.Files(Sample());
        using var complete = new TableProbeData.Files(Sample(complete: true));
        using var missing = new TableProbeData.Files([.. TableProbeData.Padding(), .. TableProbeData.Padding()]);
        var ready = await TsExportProgramTableProbe.InspectNearCutAsync(complete.Source, 0, 1, CancellationToken.None);
        var current = await TsExportProgramTableProbe.InspectNearCutAsync(partial.Source, 0, 1, CancellationToken.None);
        var other = await TsExportProgramTableProbe.InspectNearCutAsync(partial.Source, 0, 2, CancellationToken.None);
        var unavailable = await TsExportProgramTableProbe.InspectNearCutAsync(missing.Source, 0, 1, CancellationToken.None);
        await RunAsync(async () =>
        {
            foreach (var language in new[] { "en-US", "zh-CN", "zh-TW" })
            {
                App.LocalizationService.SwitchLanguage(language);
                foreach (var (recoveries, supplement, onlyCurrent) in new[]
                         {
                             (new[] { ready }, true, false), (new[] { current }, false, true),
                             (new[] { other }, false, false), (new[] { unavailable }, false, false),
                             (new[] { ready, current }, false, true), (new[] { current, other }, false, false)
                         })
                {
                    var vm = new TsExportWarningViewModel { FileNames = ["output.ts"] };
                    vm.SetWarnings(recoveries.Select(item => new TsProgramTableWarning("output.ts", item)).ToArray());
                    var window = new TsExportWarningView { DataContext = vm };
                    try
                    {
                        window.Show();
                        window.UpdateLayout();
                        Assert.Equal(supplement, window.FindControl<Button>("SupplementButton")!.IsVisible);
                        Assert.Equal(onlyCurrent, window.FindControl<Button>("CurrentProgramButton")!.IsVisible);
                        Assert.False(string.IsNullOrWhiteSpace(vm.Message));
                        Assert.False(string.IsNullOrWhiteSpace(vm.Hint));
                        Assert.Equal(LocalizationManager.Instance.String_TsExport_Continue,
                            window.FindControl<Button>("ContinueButton")!.Content);
                        vm.Select(onlyCurrent ? TsProgramTableExportChoice.CurrentProgram : TsProgramTableExportChoice.Supplement);
                        Assert.Equal(supplement || onlyCurrent, vm.DialogResult);
                    }
                    finally { window.Close(); }
                }
            }
            await Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedSourceInvalidatesPreparedTablesBeforeReplacingOutput(bool currentProgram)
    {
        using var files = new TableProbeData.Files(Sample(complete: !currentProgram));
        File.WriteAllText(files.Output, "existing output");
        await RunAsync(async () =>
        {
            var output = new OutputWindowViewModel
            {
                SourceFilePath = files.Source, OutputPath = files.Output, StartPosition = 2 * 188,
                EndPosition = 4 * 188, AudioDelayProgramId = 1
            };
            var warnings = await output.FindMissingProgramTablesAsync(null, CancellationToken.None);
            output.SetProgramTableChoice(warnings, currentProgram ? TsProgramTableExportChoice.CurrentProgram : TsProgramTableExportChoice.Supplement);
            File.SetLastWriteTimeUtc(files.Source, File.GetLastWriteTimeUtc(files.Source).AddMinutes(1));
            await output.OutputCommand.ExecuteAsync(null);
            Assert.False(output.DialogResult);
            Assert.Equal(TsClipMergeErrorCode.SourceChanged, Assert.IsType<TsClipMergeException>(output.Exception).Code);
            Assert.Equal("existing output", File.ReadAllText(files.Output));
        });
    }

    [Fact]
    public async Task AppendedRecordingKeepsThePreparedTablesAndSelectedRange()
    {
        var data = Sample(complete: true);
        using var files = new TableProbeData.Files(data);
        var prepared = await TsExportProgramTableProbe.InspectNearCutAsync(files.Source, 0, 1, CancellationToken.None);
        using (var append = new FileStream(files.Source, FileMode.Append))
            append.Write(TableProbeData.Padding());
        await new TsClipMergeService().MergeAsync(new()
        {
            SourcePath = files.Source, OutputPath = files.Output,
            Ranges = [new(0, data.Length, 0, 1)]
        }, addProgramTables: true, preparedTables: prepared);
        Assert.True(await TsExportProgramTableProbe.HasTablesAsync(files.Output, 0, -1, CancellationToken.None));
        Assert.Equal(data.Length + 2 * 188, new FileInfo(files.Output).Length);
        Assert.Equal(MediaPackets(data), MediaPackets(File.ReadAllBytes(files.Output)));
    }

    private static byte[] Sample(bool complete = false)
    {
        var pat = TsPsiSectionBuilder.BuildPat(7, 0, complete ? [new(1, 0x1000)] : [new(1, 0x1000), new(2, 0x1001)]);
        var pmt = TsPsiSectionBuilder.BuildPmt(1, 0, 0x100, [],
            [new(0x100, new TsStreamDefinition { StreamType = 0x42, Descriptors = AvsDescriptors })]);
        return [.. TableProbeData.Section(0, pat), .. TableProbeData.Section(0x1000, pmt),
            .. Media(0), .. Media(1), .. TableProbeData.Section(0, pat, 1),
            .. TableProbeData.Section(0x1000, pmt, 1),
            .. TableProbeData.Section(0x11, TsPsiSectionBuilder.BuildSdt(7, 0, 1,
                [new(1, [0x48, 5, 1, 0, 2, (byte)'T', (byte)'V'], false, false, 4, false)])),
            .. Media(2), .. Media(3)];
    }

    private static byte[] Media(int counter)
    {
        var packet = TableProbeData.Padding();
        packet[1] = 1;
        packet[2] = 0;
        packet[3] = (byte)(0x10 | counter);
        packet[4] = (byte)counter;
        return packet;
    }

    private static int Pid(byte[] packet) => ((packet[1] & 31) << 8) | packet[2];
    private static byte[] MediaPackets(byte[] data) => data.Chunk(188).Where(packet => Pid(packet) == 0x100).SelectMany(packet => packet).ToArray();

    private static async Task RunAsync(Func<Task> action)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            locale.SwitchLanguage("zh-CN");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            await action();
            return true;
        }, CancellationToken.None);
    }
}
