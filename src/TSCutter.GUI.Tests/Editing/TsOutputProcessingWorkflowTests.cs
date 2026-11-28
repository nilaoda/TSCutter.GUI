using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FFmpeg.AutoGen.Abstractions;
using Splat;
using TSCutter.GUI.Models.Discs;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Services.Editing.AudioRebuild;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Media;
using TSCutter.GUI.Utils.TransportStream.Packets;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.ViewModels.Editing;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

[Collection("Avalonia UI")]
public sealed class TsOutputProcessingWorkflowTests
{
    private const int PacketSize = 188;
    private const long Wrap = 1L << 33;

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task RecordingPureStuffingRemovalRequiresAnActualChange(bool m2ts, bool hasStuffing)
    {
        var directory = Directory.CreateTempSubdirectory("tscutter-stuffing-");
        try
        {
            const int pid = 0x0101;
            var ext = m2ts ? ".m2ts" : ".ts";
            var source = Path.Combine(directory.FullName, "source" + ext);
            var destination = Path.Combine(directory.FullName, "output" + ext);
            var payload = Enumerable.Range(0, 12).Select(index =>
            {
                var packet = TsOutputPostProcessorTests.PureStuffingPacket(pid);
                packet[3] = (byte)(0x10 | index);
                return packet;
            }).ToArray();
            var bodies = hasStuffing
                ? payload.SelectMany(packet => new[] { packet, TsOutputPostProcessorTests.PureStuffingPacket(pid) })
                : payload;
            var data = m2ts
                ? bodies.SelectMany((packet, index) => new byte[] { 0, 0, 0, (byte)index }.Concat(packet)).ToArray()
                : bodies.SelectMany(packet => packet).ToArray();
            await File.WriteAllBytesAsync(source, data);

            var configuration = new RemoveModeWorkflowTests.MemoryConfiguration();
            configuration.CurrentConfig.TsRemoveStuffingPackets = true;
            await RunAsync(configuration, async () =>
            {
                var output = new OutputWindowViewModel { SourceFilePath = source, OutputPath = destination, EndPosition = data.Length };
                output.SetProgramTableChoice([], TsProgramTableExportChoice.Original);
                await output.OutputCommand.ExecuteAsync(null);

                Assert.Null(output.Exception);
                var removedStuffing = hasStuffing && !m2ts;
                Assert.Equal(removedStuffing ? payload.SelectMany(packet => packet).ToArray() : data,
                    await File.ReadAllBytesAsync(destination));
                Assert.Equal(removedStuffing ? (ExportKind.TsPacket, ExportModifiers.StuffingPacketsRemoved)
                    : (ExportKind.TsCopy, ExportModifiers.None), Describe(output.Outcomes, destination));
            });
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// 设置中开启起始时间归零与去除空包后，关键帧剪辑与智能剪辑的输出：时间戳整体前移到从 0 开始、包数与各时间戳的相对关系不变、
    /// 没有空包，导出记录不再称为字节复制并带上两个标签。两项关闭时关键帧剪辑仍与源文件逐字节相同。
    /// </summary>
    [NativeMediaFact("frameaccurate")]
    public async Task ProcessedExportsStartAtZero()
    {
        var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "h264.ts");
        var directory = Path.Combine(Path.GetTempPath(), $"tscutter-ts-processing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            int videoIndex;
            List<int> indices;
            long startPts, endPts;
            using (var input = InterruptibleInputFormatContext.Open(source, static () => false))
            {
                input.LoadStreamInfo();
                videoIndex = input.GetVideoStream().Index;
                indices = input.Streams.Where(stream => stream.Codecpar?.CodecType is AVMediaType.AVMEDIA_TYPE_VIDEO
                    or AVMediaType.AVMEDIA_TYPE_AUDIO).Select(stream => stream.Index).ToList();
                var pts = input.ReadPackets().Where(packet => packet.StreamIndex == videoIndex)
                    .Select(packet => packet.Pts).ToList();
                pts.Sort();
                (startPts, endPts) = (pts[pts.Count / 4 + 3], pts[pts.Count * 3 / 4]);
            }
            var length = new FileInfo(source).Length / PacketSize * PacketSize;
            var configuration = new RemoveModeWorkflowTests.MemoryConfiguration();
            List<ExportTaskOutcome> outcomes = [];

            async Task ExportAsync(string plain, string smart)
            {
                var output = new OutputWindowViewModel();
                output.SetBatchTasks(
                [
                    new(source, plain, 0, length),
                    new(source, smart, 0, -1, FrameAccurate: new FrameAccurateExportSettings
                    {
                        Container = "ts", VideoIndex = videoIndex, StreamIndices = indices
                    }, StartPts: startPts, EndPts: endPts)
                ]);
                output.ConfirmProgramTableWarningAsync = (_, _) => Task.FromResult(TsProgramTableExportChoice.Original);
                await output.OutputCommand.ExecuteAsync(null);
                Assert.Null(output.Exception);
                Assert.Equal([0, 1], output.CompletedTaskIndices);
                outcomes.AddRange(output.Outcomes);
            }

            var plainOff = Path.Combine(directory, "plain-off.ts");
            var smartOff = Path.Combine(directory, "smart-off.ts");
            var plainOn = Path.Combine(directory, "plain-on.ts");
            var smartOn = Path.Combine(directory, "smart-on.ts");
            await RunAsync(configuration, async () =>
            {
                await ExportAsync(plainOff, smartOff);
                configuration.CurrentConfig.TsZeroStartTime = true;
                configuration.CurrentConfig.TsRemoveNullPackets = true;
                await ExportAsync(plainOn, smartOn);
            });

            var processed = ExportModifiers.StartTimeZeroed | ExportModifiers.NullPacketsRemoved;
            Assert.Equal((ExportKind.TsCopy, ExportModifiers.None), Describe(outcomes, plainOff));
            Assert.Equal((ExportKind.FrameAccurate, ExportModifiers.None), Describe(outcomes, smartOff));
            Assert.Equal((ExportKind.TsPacket, processed), Describe(outcomes, plainOn));
            Assert.Equal((ExportKind.FrameAccurate, processed), Describe(outcomes, smartOn));
            var sourceBytes = await File.ReadAllBytesAsync(source);
            Assert.Equal(sourceBytes[..(int)length], await File.ReadAllBytesAsync(plainOff));

            foreach (var (off, on) in new[] { (plainOff, plainOn), (smartOff, smartOn) })
            {
                var before = Timestamps(await File.ReadAllBytesAsync(off), out _);
                var after = Timestamps(await File.ReadAllBytesAsync(on), out var remainingNulls);
                Assert.Equal(0, remainingNulls);
                Assert.Equal(before.Count, after.Count);
                Assert.Equal(0, after.Min(item => item.Value));
                // 与未处理的输出相比，每个时间戳都减去同一平移量（单节目素材只有一个时间基准）。
                var offset = Modulo(before[0].Value - after[0].Value);
                Assert.Equal(before.Select(item => (item.Pid, Modulo(item.Value - offset))), after);
                using var input = InterruptibleInputFormatContext.Open(on, static () => false);
                input.LoadStreamInfo();
                Assert.Equal(
                    CountVideoPackets(off), input.ReadPackets().Count(packet => packet.StreamIndex == input.GetVideoStream().Index));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 智能剪辑需要修正 DTS 时由封装器重新生成 TS：这条路径同样经过输出处理，从 0 开始、没有空包，
    /// 选中的画面不变，导出检测收到处理后的字节且没有错误。
    /// </summary>
    [NativeMediaFact("frameaccurate")]
    public async Task MuxerOutputIsProcessedToo()
    {
        var fixture = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "h264.ts");
        var directory = Path.Combine(Path.GetTempPath(), $"tscutter-ts-processing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "late-dts.ts");
            var output = Path.Combine(directory, "output.ts");
            FrameAccurateTsOutputTests.WriteLateDtsSource(fixture, source);
            var frames = FrameAccurateExportTests.DecodeFrames(fixture);
            var settings = FrameAccurateExportTests.Settings(source);
            var request = new FrameAccurateExportRequest(output, [new(source, frames[40], frames[100], settings)])
            {
                NormalizeTimestamps = true
            };
            var check = new ExportTsCheckSession(output);
            var setup = new AudioRebuildSetup(null, source, output, check, null,
                TsOutputProcessing.ZeroStartTime | TsOutputProcessing.RemoveNullPackets);
            TsOutputFile.Current = setup;
            try
            {
                await new FrameAccurateExportService().ExportAsync(request);
            }
            finally
            {
                TsOutputFile.Current = null;
            }
            check.Complete();

            Assert.Null(setup.Error);
            Assert.Equal(0, setup.StreamCount);
            Assert.Equal(1, setup.ProcessedStreamCount);
            var timestamps = Timestamps(await File.ReadAllBytesAsync(output), out var nullPackets);
            Assert.Equal(0, nullPackets);
            Assert.Equal(0, timestamps.Min(item => item.Value));
            Assert.True(timestamps.Max(item => item.Value) < 90_000 * 60);
            var decoded = FrameAccurateExportTests.Decode(output, out var decodeErrors);
            Assert.Equal(0, decodeErrors);
            Assert.Equal(Enumerable.Range(40, 61), decoded.Select(frame => frame.Number));
            Assert.Equal(TsExportCheckState.Completed, check.State);
            Assert.Equal(0, check.Result!.TotalErrorCount);
            Assert.Equal(new FileInfo(output).Length / PacketSize, check.Result.PacketCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 去除空包时，TS 按字节剪辑的片段大小、选中片段的合计和导出队列都显示为“≤”上限，之后新增的片段也一样；
    /// 关闭后恢复为准确大小。
    /// </summary>
    [NativeMediaFact("frameaccurate")]
    public async Task RemovingNullPacketsShowsSizesAsUpperBounds()
    {
        var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "h264.ts");
        var configuration = new RemoveModeWorkflowTests.MemoryConfiguration();
        await RunAsync(configuration, async () =>
        {
            var main = new MainWindowViewModel(null!, configuration) { VideoPath = source };
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                Assert.True(await (Task<bool>)typeof(MainWindowViewModel).GetMethod("LoadVideoAsync", flags)!.Invoke(main, [null])!);
                Assert.True(main.IsTsTrackExport);
                PickedClip Clip(long start, long end) => new()
                {
                    InFileInfo = new FileInfo(source), StartPosition = start, EndPosition = end,
                    StartTime = start / 1000.0, EndTime = end / 1000.0, IsSelected = true
                };
                var first = Clip(0, 1880);
                var second = Clip(1880, 5640);
                main.Clips.Add(first);
                main.Clips.Add(second);
                main.ExportQueue.Add(new ExportQueueItem { IsTsSource = true, EstimatedBytes = 1880 });
                var strings = LocalizationManager.Instance;
                string Exact(long bytes) => strings.String_SizePrefix + CommonUtil.FormatFileSize(bytes);
                string Bound(long bytes) => strings.String_SizePrefix + string.Format(strings.String_SizeUpperBound,
                    CommonUtil.FormatFileSize(bytes));
                void Apply(bool removeNulls)
                {
                    // 与关闭设置窗口后相同：按新设置刷新片段与队列。
                    configuration.CurrentConfig.TsRemoveNullPackets = removeNulls;
                    typeof(MainWindowViewModel).GetMethod("NotifyTsTracksChanged", flags)!.Invoke(main, [false]);
                }

                Assert.Equal(Exact(1880), first.EstimatedSizeStr);
                Assert.Equal(Exact(5640), main.SelectedClipEstimatedSizeStr);
                Assert.Equal(Exact(1880), main.ExportQueue[0].EstimatedSizeStr);

                Apply(true);
                Assert.True(main.ShowRawClipSizeEstimate);
                Assert.Equal(Bound(1880), first.EstimatedSizeStr);
                Assert.Equal(Bound(3760), second.EstimatedSizeStr);
                Assert.Equal(Bound(5640), main.SelectedClipEstimatedSizeStr);
                Assert.Equal(Bound(1880), main.ExportQueue[0].EstimatedSizeStr);
                var third = Clip(5640, 7520);
                main.Clips.Add(third);
                Assert.Equal(Bound(1880), third.EstimatedSizeStr);

                Apply(false);
                Assert.Equal(Exact(1880), first.EstimatedSizeStr);
                Assert.Equal(Exact(7520), main.SelectedClipEstimatedSizeStr);
                Assert.Equal(Exact(1880), main.ExportQueue[0].EstimatedSizeStr);
            }
            finally
            {
                main.Close();
                await main.KeyFrameStrip.IdleTask;
                await (Task)typeof(MainWindowViewModel).GetField("previewDisposalTask", flags)!.GetValue(main)!;
            }
        });
    }

    /// <summary>
    /// 导出设置对话框：两项都没开启时提示可在设置中开启（TS 原包导出始终提示，蓝光光盘只在选择 M2TS 时提示，DVD 不提示）；
    /// 开启后改为说明已开启的处理。
    /// </summary>
    [Fact]
    public Task ExportSettingsTellWhereToTurnProcessingOn() => RunAsync(new RemoveModeWorkflowTests.MemoryConfiguration(), () =>
    {
        RemuxExportTrackChoice[] Tracks() => [new() { Index = 0, IsSelected = true, CanChange = false, Description = "video" }];

        var ts = new RemuxExportOptionsViewModel();
        ts.InitializeTs("ts", Tracks(), false);
        Assert.True(ts.ShowsProcessingAvailableHint);
        var processed = new RemuxExportOptionsViewModel();
        processed.InitializeTs("ts", Tracks(), false, "notice");
        Assert.False(processed.ShowsProcessingAvailableHint);
        Assert.True(processed.HasTsProcessingNotice);

        RemuxExportOptionsViewModel Disc(DiscKind kind, string notice = "")
        {
            var dialog = new RemuxExportOptionsViewModel();
            var input = new DiscInput(kind, "disc", 1, 1, TimeSpan.FromMinutes(1), []);
            dialog.Initialize(new RemuxExportSettings { Container = kind == DiscKind.Dvd ? "vob" : "m2ts", Disc = input },
                Tracks(), false, notice);
            return dialog;
        }

        var bluray = Disc(DiscKind.BluRay);
        Assert.True(bluray.ShowsProcessingAvailableHint);
        Assert.False(bluray.ShowsRemuxProcessingNotice);
        bluray.Container = "mkv";
        Assert.False(bluray.ShowsProcessingAvailableHint);
        var blurayProcessed = Disc(DiscKind.BluRay, "notice");
        Assert.False(blurayProcessed.ShowsProcessingAvailableHint);
        Assert.True(blurayProcessed.ShowsRemuxProcessingNotice);
        Assert.False(Disc(DiscKind.Dvd).ShowsProcessingAvailableHint);

        // 对话框中显示这行提示，悬停时有简短说明。
        var view = new TSCutter.GUI.Views.Editing.RemuxExportOptionsView { DataContext = ts };
        view.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var hint = view.GetVisualDescendants().OfType<TextBlock>().Single(block => block.IsEffectivelyVisible &&
                block.Text == LocalizationManager.Instance.String_TsTracks_ProcessingAvailable);
            Assert.StartsWith("如有需要", hint.Text);
            var tip = Assert.IsType<TextBlock>(ToolTip.GetTip(hint));
            Assert.Equal(320, tip.MaxWidth);
        }
        finally
        {
            view.Close();
        }
        return Task.CompletedTask;
    });

    private static int CountVideoPackets(string path)
    {
        using var input = InterruptibleInputFormatContext.Open(path, static () => false);
        input.LoadStreamInfo();
        var video = input.GetVideoStream().Index;
        return input.ReadPackets().Count(packet => packet.StreamIndex == video);
    }

    /// <summary>按出现顺序列出全部 PCR 基数与 PES 头中的 PTS/DTS。</summary>
    internal static List<(int Pid, long Value)> Timestamps(byte[] data, out int nullPackets)
    {
        var result = new List<(int, long)>();
        nullPackets = 0;
        for (var offset = 0; offset + PacketSize <= data.Length; offset += PacketSize)
        {
            var packet = data.AsSpan(offset, PacketSize);
            var pid = (packet[1] & 0x1F) << 8 | packet[2];
            if (pid == 0x1FFF)
            {
                nullPackets++;
                continue;
            }
            if (TsTimestampFieldCodec.TryReadPcr(packet, out var pcr, out _, out _))
                result.Add((pid, pcr));
            if (pid >= 0x20 && TsTimestampFieldCodec.TryLocatePesTimestamps(packet, out var pts, out var dts))
            {
                result.Add((pid, TsTimestampFieldCodec.ReadPesTimestamp(packet.Slice(pts, 5))));
                if (dts >= 0)
                    result.Add((pid, TsTimestampFieldCodec.ReadPesTimestamp(packet.Slice(dts, 5))));
            }
        }
        return result;
    }

    private static long Modulo(long value) => (value % Wrap + Wrap) % Wrap;

    private static (ExportKind?, ExportModifiers) Describe(IEnumerable<ExportTaskOutcome> outcomes, string path)
    {
        var outcome = outcomes.Single(item => item.OutputPath == path);
        Assert.True(outcome.Success);
        return (outcome.Kind, outcome.Modifiers);
    }

    private static async Task RunAsync(IConfigurationService configuration, Func<Task> action)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            var locale = new LocalizationService();
            locale.SwitchLanguage("zh-CN");
            Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
            Locator.CurrentMutable.RegisterConstant(configuration);
            await action();
            return true;
        }, CancellationToken.None);
    }
}
