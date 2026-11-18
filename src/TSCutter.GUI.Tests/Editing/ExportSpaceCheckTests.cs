using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using Splat;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Discs;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Models.Media;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.ViewModels.Editing;
using TSCutter.GUI.Views.Editing;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

public sealed class ExportSpaceCheckTests : IDisposable
{
    private const long Mb = 1L << 20;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tscutter-space-{Guid.NewGuid():N}");

    public ExportSpaceCheckTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private string CreateSource(long length)
    {
        var path = Path.Combine(_directory, $"source-{Guid.NewGuid():N}.ts");
        using var stream = new FileStream(path, FileMode.CreateNew);
        stream.SetLength(length);
        return path;
    }

    private string Output(string name = "out.ts") => Path.Combine(_directory, name);

    [Fact]
    public void ByteRangeIsExactAndOpenEndUsesTheFileLength()
    {
        var source = CreateSource(10_000);
        var closed = ExportSpaceCheck.Estimate(new ExportSizeTask(Output(), source,
            [new ExportSizeSegment(1_000, 5_000, 0, 10)], ExportSizeAccuracy.Exact));
        Assert.Equal(new ExportSpaceDemand(4_000, 0), closed);
        var open = ExportSpaceCheck.Estimate(new ExportSizeTask(Output(), source,
            [new ExportSizeSegment(1_000, -1, 0, null)], ExportSizeAccuracy.Exact));
        Assert.Equal(new ExportSpaceDemand(9_000, 0), open);
        var merged = ExportSpaceCheck.Estimate(new ExportSizeTask(Output(), source,
            [new ExportSizeSegment(0, 2_000, 0, 2), new ExportSizeSegment(6_000, 7_000, 6, 7)], ExportSizeAccuracy.Exact));
        Assert.Equal(3_000, merged!.Value.Bytes);
    }

    [Fact]
    public void SmartCutAndRemuxKeepAMarginAndM2tsAddsThePacketHeader()
    {
        var source = CreateSource(1_000_000);
        var ts = ExportSpaceCheck.Estimate(new ExportSizeTask(Output(), source,
            [new ExportSizeSegment(0, 188_000, 0, 10)], ExportSizeAccuracy.Approximate))!.Value;
        Assert.Equal(188_000, ts.Bytes);
        Assert.Equal(18_800, ts.MarginBytes);
        var m2ts = ExportSpaceCheck.Estimate(new ExportSizeTask(Output("out.m2ts"), source,
            [new ExportSizeSegment(0, 188_000, 0, 10)], ExportSizeAccuracy.Approximate, M2tsOutput: true))!.Value;
        Assert.Equal(192_000, m2ts.Bytes);
    }

    [Fact]
    public void MissingPositionsFallBackToTheShareOfTheSourceDuration()
    {
        var source = CreateSource(10_000);
        var task = new ExportSizeTask(Output("out.mkv"), source, [new ExportSizeSegment(-1, -1, 10, 35)],
            ExportSizeAccuracy.Approximate);
        var demand = ExportSpaceCheck.Estimate(task, path => path == source ? 100 : null)!.Value;
        Assert.Equal(2_500, demand.Bytes);
        Assert.Equal(625, demand.MarginBytes);
        // 来源时长未知（例如队列中别的文件）时无法估算，不参与检查。
        Assert.Null(ExportSpaceCheck.Estimate(task));
        var toEnd = task with { Segments = [new ExportSizeSegment(-1, -1, 50, null)] };
        Assert.Equal(5_000, ExportSpaceCheck.Estimate(toEnd, _ => 100)!.Value.Bytes);
        // 起点位置未知时记录为 0：能按时长估算时不当作整个文件。
        var unknownStart = task with { Segments = [new ExportSizeSegment(0, -1, 90, null)] };
        Assert.Equal(1_000, ExportSpaceCheck.Estimate(unknownStart, _ => 100)!.Value.Bytes);
        Assert.Equal(10_000, ExportSpaceCheck.Estimate(unknownStart)!.Value.Bytes);
        // 按字节复制的 0 到结尾就是整个文件。
        Assert.Equal(10_000, ExportSpaceCheck.Estimate(unknownStart with { Accuracy = ExportSizeAccuracy.Exact }, _ => 100)!.Value.Bytes);
    }

    [Fact]
    public void DiscTitlesUseTitleBytesOrTheDvdMaximumRate()
    {
        var bluray = new DiscInput(DiscKind.BluRay, _directory, 1, 1, TimeSpan.FromSeconds(100),
            [new DiscSegment(0, 0, 0, 0, 600_000), new DiscSegment(0, 0, 0, 600_000, 1_000_000)]);
        // 光盘标题的片段位置不是文件字节，始终按时长比例计算。
        var blurayDemand = ExportSpaceCheck.Estimate(new ExportSizeTask(Output("out.mkv"), _directory,
            [new ExportSizeSegment(5, 10, 20, 30)], ExportSizeAccuracy.Approximate, Disc: bluray))!.Value;
        Assert.Equal(100_000, blurayDemand.Bytes);
        Assert.Equal(25_000, blurayDemand.MarginBytes);

        var dvd = new DiscInput(DiscKind.Dvd, _directory, 1, 1, TimeSpan.FromSeconds(3600), []);
        var dvdDemand = ExportSpaceCheck.Estimate(new ExportSizeTask(Output("out.vob"), _directory,
            [new ExportSizeSegment(-1, -1, 0, 100)], ExportSizeAccuracy.Approximate, Disc: dvd))!.Value;
        Assert.Equal(126_000_000, dvdDemand.Bytes);

        var unknown = bluray with { Segments = [new DiscSegment(0, 0, 0)] };
        Assert.Null(ExportSpaceCheck.Estimate(new ExportSizeTask(Output("out.mkv"), _directory,
            [new ExportSizeSegment(-1, -1, 0, 10)], ExportSizeAccuracy.Approximate, Disc: unknown)));
    }

    [Fact]
    public void TranscodedAudioAddsItsBitrateOverTheClipDuration()
    {
        var source = CreateSource(10_000_000);
        var plan = new AudioRebuildPlan
        {
            Tracks =
            [
                new DolbyETrackSettings
                {
                    Pid = 0x100, Transcode = true,
                    Outputs = [new DolbyEOutput { Codec = AudioOutputCodec.Ac3, BitrateKbps = 448 }]
                }
            ]
        };
        var task = new ExportSizeTask(Output(), source, [new ExportSizeSegment(0, 1_000_000, 0, 10)],
            ExportSizeAccuracy.Exact, AudioRebuild: plan);
        var demand = ExportSpaceCheck.Estimate(task)!.Value;
        var audio = demand.Bytes - 1_000_000;
        Assert.InRange(audio, 560_000, 640_000);
        Assert.InRange(demand.MarginBytes, audio / 4 - 1, audio / 4 + 1);

        plan.Tracks[0].Outputs[0].Codec = AudioOutputCodec.Flac;
        var lossless = ExportSpaceCheck.Estimate(task)!.Value.Bytes - 1_000_000;
        // FLAC 按未压缩的 8 声道 PCM 计算上限。
        Assert.True(lossless > 10 * 8 * 48_000 * 3);
    }

    [Fact]
    public void ShortagesAreSummedPerVolumeAndUnknownSpaceIsSkipped()
    {
        var source = CreateSource(1_000 * Mb);
        var volumeA = Path.Combine(_directory, "a");
        var volumeB = Path.Combine(_directory, "b");
        var unknown = Path.Combine(_directory, "c");
        var mountedA = Path.Combine(_directory, "d");
        DiskSpaceInfo? Probe(string path) => Path.GetDirectoryName(path) switch
        {
            var dir when dir == volumeA => new DiskSpaceInfo("A", "A:", 900 * Mb),
            // 同一个卷经挂载文件夹访问：标识相同，显示的根目录不同。
            var dir when dir == mountedA => new DiskSpaceInfo("A", "B:/mnt/a", 900 * Mb),
            var dir when dir == volumeB => new DiskSpaceInfo("B", "B:", 10_000 * Mb),
            _ => null
        };
        ExportSizeTask Make(string directory, string name, long length) => new(Path.Combine(directory, name), source,
            [new ExportSizeSegment(0, length, 0, 1)], ExportSizeAccuracy.Exact);
        var tasks = new[]
        {
            Make(volumeA, "1.ts", 400 * Mb), Make(volumeB, "2.ts", 900 * Mb), Make(volumeA, "3.ts", 400 * Mb),
            Make(unknown, "4.ts", 1_000 * Mb)
        };
        // 两项各自都放得下，合计加上保留量后超过可用空间。
        var shortage = Assert.Single(ExportSpaceCheck.FindShortages(tasks, null, Probe));
        Assert.Equal("A:", shortage.VolumeRoot);
        Assert.Equal(800 * Mb + ExportSpaceCheck.ReserveBytes, shortage.RequiredBytes);
        Assert.Equal(900 * Mb, shortage.AvailableBytes);
        Assert.Equal(2, shortage.OutputCount);
        Assert.Empty(ExportSpaceCheck.FindShortages(tasks.Take(2), null, Probe));
        var mounted = Assert.Single(ExportSpaceCheck.FindShortages([tasks[0], Make(mountedA, "5.ts", 400 * Mb)], null, Probe));
        Assert.Equal("A:", mounted.VolumeRoot);
        Assert.Equal(2, mounted.OutputCount);
    }

    [Fact]
    public void UnreadableSourcesAreSkippedInsteadOfFailingTheCheck()
    {
        var missing = Path.Combine(_directory, "missing.ts");
        var tasks = new[] { new ExportSizeTask(Output(), missing, [new ExportSizeSegment(0, -1, 0, null)], ExportSizeAccuracy.Exact) };
        Assert.Empty(ExportSpaceCheck.FindShortages(tasks, null, _ => new DiskSpaceInfo("A", "A:", 0)));
    }

    [Fact]
    public async Task SlowFreeSpaceReadsTimeOutWithoutWarning()
    {
        var source = CreateSource(1_000);
        using var release = new ManualResetEventSlim();
        var tasks = new[] { new ExportSizeTask(Output(), source, [new ExportSizeSegment(0, 1_000, 0, 1)], ExportSizeAccuracy.Exact) };
        try
        {
            var shortages = await ExportSpaceCheck.FindShortagesAsync(tasks, null, _ =>
            {
                release.Wait(TimeSpan.FromSeconds(10));
                return new DiskSpaceInfo("A", "A:", 0);
            }, TimeSpan.FromMilliseconds(100));
            Assert.Empty(shortages);
        }
        finally { release.Set(); }
    }

    [Fact]
    public void FreeSpaceIsReadFromTheNearestExistingFolder()
    {
        var output = Path.Combine(_directory, "not-created", "deeper", "out.ts");
        var space = DiskSpaceUtil.TryGetFreeSpace(output);
        Assert.NotNull(space);
        Assert.True(space.Value.AvailableBytes > 0);
        Assert.False(string.IsNullOrEmpty(space.Value.VolumeRoot));
        Assert.Equal(space.Value.VolumeId, DiskSpaceUtil.TryGetVolumeId(Output()));
    }

    [Fact]
    public void FreeSpaceFollowsParentDirectorySymlinks()
    {
        if (OperatingSystem.IsWindows())
            return;
        var parent = Path.Combine(_directory, "nested");
        Directory.CreateDirectory(parent);
        var alias = Path.Combine(parent, "linked");
        Directory.CreateSymbolicLink(alias, AppContext.BaseDirectory);
        try
        {
            var actual = Path.Combine(AppContext.BaseDirectory, "not-created", "out.ts");
            var linked = Path.Combine(alias, "not-created", "out.ts");
            var expected = DiskSpaceUtil.TryGetFreeSpace(actual);
            Assert.NotNull(expected);
            Assert.Equal(expected.Value.VolumeId, DiskSpaceUtil.TryGetVolumeId(linked));
            Assert.Equal(expected.Value.VolumeId, DiskSpaceUtil.TryGetFreeSpace(linked)?.VolumeId);
        }
        finally
        {
            Directory.Delete(alias);
        }
    }

    [TSCutter.GUI.Tests.FFmpeg.NativeRuntimeFact]
    public unsafe void CustomAvioReportsDiskFullAsNoSpaceToTheMuxer()
    {
        using var writer = M2tsAvioWriteContext.Open(new DiskFullStream(), null);
        new Span<byte>(writer.Io->buffer, 192).Fill(0x47);
        writer.Io->buf_ptr = writer.Io->buffer + 192;
        NativeMethods.avio_flush(writer.Io);
        // 封装器据此抛出的错误码仍能识别为磁盘写满。
        Assert.Equal(DiskSpaceUtil.FFmpegNoSpace, writer.Io->error);
        Assert.True(DiskSpaceUtil.IsDiskFull(FFmpegException.FromErrorCode(writer.Io->error)));
        var complete = Assert.Throws<IOException>(writer.Complete);
        Assert.True(DiskSpaceUtil.IsDiskFull(complete));
    }

    private sealed class DiskFullStream : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer) => throw DiskFull();
    }

    private static IOException DiskFull() =>
        new("full", OperatingSystem.IsWindows() ? unchecked((int)0x80070070) : 28);

    [Fact]
    public void DiskFullErrorsAreRecognizedThroughWrappers()
    {
        var full = new IOException("full", OperatingSystem.IsWindows() ? unchecked((int)0x80070070) : 28);
        Assert.True(DiskSpaceUtil.IsDiskFull(full));
        Assert.True(DiskSpaceUtil.IsDiskFull(new InvalidOperationException("rebuild failed", full)));
        Assert.True(DiskSpaceUtil.IsDiskFull(new AggregateException(new IOException("other"), full)));
        Assert.True(DiskSpaceUtil.IsDiskFull(new FFmpegException("write failed", -28)));
        Assert.False(DiskSpaceUtil.IsDiskFull(new IOException("sharing violation", unchecked((int)0x80070020))));
        Assert.False(DiskSpaceUtil.IsDiskFull(new FFmpegException("invalid data", -1094995529)));
        Assert.False(DiskSpaceUtil.IsDiskFull(null));
    }
}

[Collection("Avalonia UI")]
public sealed class ExportSpaceWarningTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ShortageAsksAndOnlyContinuesWhenConfirmed(bool confirm)
    {
        await RunAsync(async (dialogs, manager) =>
        {
            manager.OnShown = warning => { if (confirm) warning.Confirm(); };
            var result = await ExportSpaceWarningViewModel.ConfirmAsync(dialogs, new OutputWindowViewModel(), [Demand(100 * 1024 * 1024)],
                null, probe: _ => new DiskSpaceInfo("Q", "Q:", 50 * 1024 * 1024));
            Assert.Equal(confirm, result);
            var shown = Assert.Single(manager.Shown);
            Assert.Single(shown.Volumes);
        });
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    [InlineData("zh-TW")]
    public async Task ShortageLineIsLocalized(string language)
    {
        await RunAsync((_, _) =>
        {
            App.LocalizationService.SwitchLanguage(language);
            foreach (var count in new[] { 1, 4 })
            {
                var line = ExportSpaceWarningViewModel.FormatShortage(new ExportSpaceShortage("Q:", 300 * 1024 * 1024,
                    50 * 1024 * 1024, count));
                Assert.StartsWith("Q:", line);
                Assert.Contains("300.00MB", line);
                Assert.Contains("50.00MB", line);
                // 多个输出时显示文件数，单个输出不显示。
                Assert.Equal(count > 1, line.Contains('4'));
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task EnoughOrUnknownSpaceDoesNotAsk()
    {
        await RunAsync(async (dialogs, manager) =>
        {
            var owner = new OutputWindowViewModel();
            Assert.True(await ExportSpaceWarningViewModel.ConfirmAsync(dialogs, owner, [Demand(1024)], null,
                probe: _ => new DiskSpaceInfo("Q", "Q:", long.MaxValue)));
            Assert.True(await ExportSpaceWarningViewModel.ConfirmAsync(dialogs, owner, [Demand(1024)], null, probe: _ => null));
            // 等待期间导出内容变化时不再继续，也不弹出提示。
            Assert.False(await ExportSpaceWarningViewModel.ConfirmAsync(dialogs, owner, [Demand(1024)], null, () => false,
                _ => new DiskSpaceInfo("Q", "Q:", 0)));
            Assert.Empty(manager.Shown);
        });
    }

    [Fact]
    public async Task WarningWindowDefaultsToCancel()
    {
        await RunAsync((_, _) =>
        {
            var warning = new ExportSpaceWarningViewModel { Volumes = ["Q:"] };
            var view = new ExportSpaceWarningView { DataContext = warning };
            try
            {
                view.Show();
                Dispatcher.UIThread.RunJobs();
                var cancel = view.FindControl<Button>("CancelButton")!;
                var proceed = view.FindControl<Button>("ContinueButton")!;
                Assert.True(cancel.IsDefault);
                Assert.True(cancel.IsCancel);
                Assert.False(proceed.IsDefault);
                proceed.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(warning.DialogResult);
            }
            finally { view.Close(); }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ExportWindowDescribesEachOutputForTheCheck() => await RunAsync((_, _) =>
    {
        var plan = new AudioRebuildPlan
        {
            Merges = [new ChannelMergeSettings { Merge = true, Sources = [new() { Pid = 0x101 }, new() { Pid = 0x102 }, new() { Pid = 0x103 }],
                Outputs = [new AudioOutput()] }]
        };
        var batch = new OutputWindowViewModel();
        batch.SetBatchTasks(
        [
            new BatchTask("a.ts", Output("1.ts"), 0, 100, AudioRebuild: plan),
            new BatchTask("a.ts", Output("2.mkv"), 0, 100, RemuxSettings: new RemuxExportSettings { Container = "mkv" },
                AudioRebuild: plan),
            new BatchTask("a.ts", Output("3.m2ts"), 0, 100, FrameAccurate: new FrameAccurateExportSettings { Container = "m2ts" },
                AudioRebuild: plan)
        ]);
        var described = batch.DescribeSpaceTasks();
        Assert.Equal(3, described.Count);
        Assert.Equal(ExportSizeAccuracy.Exact, described[0].Accuracy);
        Assert.Same(plan, described[0].AudioRebuild);
        // 重新封装为 MKV 不经过音轨重组。
        Assert.Null(described[1].AudioRebuild);
        Assert.Equal(ExportSizeAccuracy.Approximate, described[2].Accuracy);
        Assert.True(described[2].M2tsOutput);
        Assert.Same(plan, described[2].AudioRebuild);

        var raw = new OutputWindowViewModel
        {
            IsRawByteRange = true, SourceFilePath = "a.ts", OutputPath = Output(), StartPosition = 10, EndPosition = 20,
            AudioRebuild = plan
        };
        var rawTask = Assert.Single(raw.DescribeSpaceTasks());
        Assert.Equal(new ExportSizeSegment(10, 20, 0, null), Assert.Single(rawTask.Segments));
        Assert.Null(rawTask.AudioRebuild);

        var merge = new OutputWindowViewModel();
        merge.SetFrameAccurateTask(new FrameAccurateExportRequest(Output(),
            [new FrameAccurateExportSegment("a.ts", 0, 900_000, new FrameAccurateExportSettings { Container = "ts" })]), "");
        Assert.Empty(merge.DescribeSpaceTasks());
        merge.MergeSizeSegments = [new ExportSizeSegment(0, 100, 0, 10), new ExportSizeSegment(200, 300, 20, 30)];
        Assert.Equal(2, Assert.Single(merge.DescribeSpaceTasks()).Segments.Count);
        return Task.CompletedTask;
    });

    [Fact]
    public async Task QueueSkipsTheRestOfAFullVolumeAndContinuesElsewhere() => await RunAsync(async (_, _) =>
    {
        // 其他卷或无法识别卷的位置上的任务都应照常执行。
        var otherVolume = OtherVolumeDirectory() ?? UnknownVolumeDirectory();
        var tasks = new List<BatchTask>
        {
            new("a.ts", Output("1.ts"), 0, 100),
            new("a.ts", Output("2.ts"), 0, 100),
            new("a.ts", Output("3.ts"), 0, 100)
        };
        if (otherVolume is not null)
            tasks.Add(new BatchTask("a.ts", Path.Combine(otherVolume, "4.ts"), 0, 100));
        var output = new OutputWindowViewModel();
        output.SetBatchTasks(tasks);
        var attempted = new List<string>();
        output.RunBatchTaskAsync = task =>
        {
            attempted.Add(Path.GetFileName(task.DestinationPath));
            return task.DestinationPath == Output("1.ts")
                ? Task.FromException(new IOException("full", OperatingSystem.IsWindows() ? unchecked((int)0x80070070) : 28))
                : Task.CompletedTask;
        };
        await (Task)typeof(OutputWindowViewModel).GetMethod("RunBatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(output, null)!;
        string[] expectedAttempts = otherVolume is null ? ["1.ts"] : ["1.ts", "4.ts"];
        int[] expectedCompleted = otherVolume is null ? [] : [3];
        Assert.Equal(expectedAttempts, attempted);
        Assert.Equal([0, 1, 2], output.FailedTaskIndices);
        Assert.Equal(expectedCompleted, output.CompletedTaskIndices);
        Assert.True(DiskSpaceUtil.IsDiskFull(output.Exception));
    });

    // 另一个卷上已存在的目录：Windows 取其他盘符，其他系统取不同挂载点；找不到时返回 null。
    private static string? OtherVolumeDirectory()
    {
        var own = DiskSpaceUtil.TryGetVolumeId(Output());
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    continue;
                var root = drive.RootDirectory.FullName;
                var volume = DiskSpaceUtil.TryGetVolumeId(Path.Combine(root, "probe.ts"));
                if (volume is not null && !MediaFileUtil.PathComparer.Equals(volume, own))
                    return root;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    // Windows 上未分配的盘符：无法识别所在卷；其他系统返回 null。
    private static string? UnknownVolumeDirectory()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var used = DriveInfo.GetDrives().Select(drive => char.ToUpperInvariant(drive.Name[0])).ToHashSet();
        return Enumerable.Range('G', 'Z' - 'G' + 1).Select(letter => (char)letter).Where(letter => !used.Contains(letter))
            .Select(letter => $"{letter}:\\").FirstOrDefault();
    }

    // 只计算路径、不写入文件的输出位置。
    private static string Output(string name = "out.ts") => Path.Combine(Path.GetTempPath(), name);

    // 只用于计算的输出，来源文件长度不会被读取（字节范围已给出）。
    private static ExportSizeTask Demand(long bytes) => new(Path.Combine(Path.GetTempPath(), "space-check-out.ts"),
        "source.ts", [new ExportSizeSegment(0, bytes, 0, 1)], ExportSizeAccuracy.Exact);

    private static async Task RunAsync(Func<IDialogService, SpaceDialogManager, Task> action)
    {
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        await session.Dispatch<bool>(async () =>
        {
            using var resolver = new ModernDependencyResolver();
            using var scope = resolver.WithResolver();
            new App().Initialize();
            var manager = DispatchProxy.Create<IDialogManager, SpaceDialogManager>();
            var dialogs = new DialogService(manager, viewModelFactory: type => Locator.Current.GetService(type));
            await action(dialogs, (SpaceDialogManager)(object)manager);
            return true;
        }, CancellationToken.None);
    }
}

public class SpaceDialogManager : DispatchProxy
{
    public List<ExportSpaceWarningViewModel> Shown { get; } = [];
    public Action<ExportSpaceWarningViewModel>? OnShown { get; set; }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name != "ShowDialogAsync" || args![1] is not ExportSpaceWarningViewModel vm)
            throw new NotSupportedException(method.ToString());
        Shown.Add(vm);
        OnShown?.Invoke(vm);
        return Task.FromResult(vm.DialogResult);
    }
}
