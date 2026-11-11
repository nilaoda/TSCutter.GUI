using System.Reflection;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using HanumanInstitute.MvvmDialogs.FileSystem;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Editing;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Classic.Avalonia.Theme;
using Splat;
using TSCutter.GUI.Models.TransportStream.Processing;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.ViewModels.Editing;
using TSCutter.GUI.Views.Editing;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

[Collection("Avalonia UI")]
public sealed class TsExportWarningTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SingleExportRequiresAChoiceBeforeTouchingTheOutput(bool proceed)
    {
        using var files = new TableProbeData.Files([.. TableProbeData.Padding(), .. TableProbeData.Padding()]);
        byte[] previous = [1, 2, 3];
        File.WriteAllBytes(files.Output, previous);
        await RunAsync(async () =>
        {
            var prompts = 0;
            var output = new OutputWindowViewModel { SourceFilePath = files.Source, OutputPath = files.Output, EndPosition = -1 };
            output.ConfirmProgramTableWarningAsync = (names, token) =>
            {
                prompts++;
                Assert.Equal([Path.GetFileName(files.Output)], names);
                Assert.Equal(previous, File.ReadAllBytes(files.Output));
                return Task.FromResult(proceed ? TsProgramTableExportChoice.Original : TsProgramTableExportChoice.Cancel);
            };
            await output.OutputCommand.ExecuteAsync(null);
            Assert.Equal(1, prompts);
            Assert.Null(output.Exception);
            Assert.Equal(proceed, output.DialogResult);
            Assert.Equal(proceed ? File.ReadAllBytes(files.Source) : previous, File.ReadAllBytes(files.Output));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchCollectsWarningsBeforeAnyFileIsWritten(bool proceed)
    {
        using var files = new TableProbeData.Files([.. TableProbeData.Padding(), .. TableProbeData.Padding()]);
        var second = Path.Combine(files.DirectoryPath, "second.ts");
        await RunAsync(async () =>
        {
            var prompts = 0;
            var output = new OutputWindowViewModel();
            output.SetBatchTasks([new(files.Source, files.Output, 0, -1), new(files.Source, second, 0, -1)]);
            output.ConfirmProgramTableWarningAsync = (names, token) =>
            {
                prompts++;
                Assert.Equal(2, names.Count);
                Assert.False(File.Exists(files.Output));
                Assert.False(File.Exists(second));
                return Task.FromResult(proceed ? TsProgramTableExportChoice.Original : TsProgramTableExportChoice.Cancel);
            };
            await output.OutputCommand.ExecuteAsync(null);
            Assert.Equal(1, prompts);
            Assert.Null(output.Exception);
            Assert.Equal(proceed, output.DialogResult);
            Assert.Equal(proceed ? 2 : 0, output.CompletedTaskIndices.Count);
            Assert.Equal(proceed, File.Exists(files.Output));
            Assert.Equal(proceed, File.Exists(second));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MergeCanReturnOrContinueWithoutChangingTheSelectedRanges(bool proceed)
    {
        using var files = new TableProbeData.Files(Enumerable.Range(0, 8).SelectMany(_ => TableProbeData.Padding()).ToArray());
        await RunAsync(async () =>
        {
            var output = new OutputWindowViewModel();
            output.SetMergeTask(new TsClipMergeRequest
            {
                SourcePath = files.Source, OutputPath = files.Output,
                Ranges = [new(0, 2 * 188, 0, 1), new(4 * 188, 6 * 188, 2, 3)]
            });
            output.ConfirmProgramTableWarningAsync = (names, token) =>
            {
                Assert.Single(names);
                Assert.False(File.Exists(files.Output));
                return Task.FromResult(proceed ? TsProgramTableExportChoice.Original : TsProgramTableExportChoice.Cancel);
            };
            await output.OutputCommand.ExecuteAsync(null);
            Assert.Null(output.Exception);
            Assert.Equal(proceed, output.DialogResult);
            Assert.Equal(proceed, File.Exists(files.Output));
            if (proceed) Assert.Equal(4 * 188, new FileInfo(files.Output).Length);
        });
    }

    [Fact]
    public async Task CancellationDuringConfirmationCannotStartAnExport()
    {
        using var files = new TableProbeData.Files([.. TableProbeData.Padding(), .. TableProbeData.Padding()]);
        await RunAsync(async () =>
        {
            var output = new OutputWindowViewModel { SourceFilePath = files.Source, OutputPath = files.Output, EndPosition = -1 };
            output.ConfirmProgramTableWarningAsync = (names, token) =>
            {
                output.CancelOutputCommand.Execute(null);
                return Task.FromResult(TsProgramTableExportChoice.Original);
            };
            await output.OutputCommand.ExecuteAsync(null);
            Assert.False(output.DialogResult);
            Assert.Null(output.Exception);
            Assert.False(File.Exists(files.Output));
        });
    }

    [Fact]
    public async Task CompleteTablesSkipThePromptAndTheDialogRecommendsSupplementing()
    {
        using var files = new TableProbeData.Files([.. TableProbeData.Section(0, TableProbeData.Pat()),
            .. TableProbeData.Section(0x1000, TableProbeData.Pmt()), .. TableProbeData.Padding(), .. TableProbeData.Padding()]);
        await RunAsync(async () =>
        {
            var output = new OutputWindowViewModel { SourceFilePath = files.Source, OutputPath = files.Output, EndPosition = -1 };
            output.ConfirmProgramTableWarningAsync = (_, _) => throw new InvalidOperationException("Unexpected warning");
            await output.OutputCommand.ExecuteAsync(null);
            Assert.True(output.DialogResult);
            Assert.Null(output.Exception);
            var owner = new ClassicWindow();
            owner.Show();
            try
            {
                foreach (var choice in Enum.GetValues<TsProgramTableExportChoice>())
                {
                    var warning = new TsExportWarningViewModel { FileNames = ["output.ts"] };
                    var dialog = new TsExportWarningView { DataContext = warning };
                    var task = dialog.ShowDialog(owner);
                    Assert.Equal("output.ts", dialog.FindControl<ItemsControl>("FilesList")!.Items.Single());
                    var back = dialog.FindControl<Button>("ReturnButton")!;
                    Assert.False(back.IsDefault);
                    Assert.True(back.IsCancel);
                    var supplement = dialog.FindControl<Button>("SupplementButton")!;
                    Assert.True(supplement.IsDefault);
                    Assert.Equal(LocalizationManager.Instance.String_TsExport_Supplement, supplement.Content);
                    Assert.Equal(LocalizationManager.Instance.String_TsExport_AdjustCut, back.Content);
                    Assert.Equal(LocalizationManager.Instance.String_TsExport_Continue,
                        dialog.FindControl<Button>("ContinueButton")!.Content);
                    var button = choice switch
                    {
                        TsProgramTableExportChoice.Supplement => supplement,
                        TsProgramTableExportChoice.Original => dialog.FindControl<Button>("ContinueButton")!,
                        _ => back
                    };
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await task;
                    Assert.Equal(choice, warning.Choice);
                    Assert.Equal(choice != TsProgramTableExportChoice.Cancel, warning.DialogResult);
                }
            }
            finally { owner.Close(); }
        });
    }

    [Theory]
    [InlineData("single")]
    [InlineData("batch")]
    [InlineData("merge")]
    public async Task SupplementChoiceAddsTablesToTheAffectedOutputs(string mode)
    {
        byte[] source = [.. TableProbeData.Section(0, TableProbeData.Pat()),
            .. TableProbeData.Section(0x1000, TableProbeData.Pmt()),
            .. Enumerable.Range(0, 12).SelectMany(_ => TableProbeData.Padding())];
        using var files = new TableProbeData.Files(source);
        await RunAsync(async () =>
        {
            var output = new OutputWindowViewModel
            {
                SourceFilePath = files.Source, OutputPath = files.Output, StartPosition = 2 * 188, EndPosition = 6 * 188
            };
            if (mode == "batch")
                output.SetBatchTasks([new(files.Source, files.Output, 2 * 188, 6 * 188)]);
            if (mode == "merge")
                output.SetMergeTask(new TsClipMergeRequest
                {
                    SourcePath = files.Source, OutputPath = files.Output,
                    Ranges = [new(2 * 188, 6 * 188, 0, 1), new(8 * 188, 12 * 188, 2, 3)]
                });
            output.ConfirmProgramTableWarningAsync = (_, _) => Task.FromResult(TsProgramTableExportChoice.Supplement);
            await output.OutputCommand.ExecuteAsync(null);
            Assert.Null(output.Exception);
            Assert.True(output.DialogResult);
            using var exported = File.OpenRead(files.Output);
            Assert.True(TSCutter.GUI.Services.Editing.TsExportProgramTableProbe.HasTables(exported,
                [(0, exported.Length)], new(188, 0), CancellationToken.None));
            Assert.Equal((mode == "merge" ? 10 : 6) * 188, exported.Length);
        });
    }

    [Fact]
    public async Task MixedQueueOnlySupplementsTheOutputThatLacksTables()
    {
        using var files = new TableProbeData.Files([.. TableProbeData.Section(0, TableProbeData.Pat()),
            .. TableProbeData.Section(0x1000, TableProbeData.Pmt()),
            .. Enumerable.Range(0, 12).SelectMany(_ => TableProbeData.Padding())]);
        var completeOutput = Path.Combine(files.DirectoryPath, "complete.ts");
        await RunAsync(async () =>
        {
            var output = new OutputWindowViewModel();
            output.SetBatchTasks([new(files.Source, completeOutput, 0, 6 * 188),
                new(files.Source, files.Output, 8 * 188, 12 * 188)]);
            var prompts = 0;
            output.ConfirmProgramTableWarningAsync = (names, _) =>
            {
                prompts++;
                Assert.Equal([Path.GetFileName(files.Output)], names);
                Assert.False(File.Exists(completeOutput));
                Assert.False(File.Exists(files.Output));
                return Task.FromResult(TsProgramTableExportChoice.Supplement);
            };
            await output.OutputCommand.ExecuteAsync(null);
            Assert.Equal(1, prompts);
            Assert.Null(output.Exception);
            Assert.Equal([0, 1], output.CompletedTaskIndices);
            Assert.Empty(output.FailedTaskIndices);
            Assert.Equal(File.ReadAllBytes(files.Source).AsSpan(0, 6 * 188).ToArray(), File.ReadAllBytes(completeOutput));
            Assert.False(output.AddsProgramTablesTo(completeOutput));
            Assert.True(output.AddsProgramTablesTo(files.Output));
            Assert.Equal(6 * 188, new FileInfo(files.Output).Length);
        });
    }

    [Fact]
    public async Task ProductionDialogFactoryCreatesTheExportWarning()
    {
        await RunAsync(() =>
        {
            new App().Initialize();
            var warning = App.DialogService.CreateViewModel<TsExportWarningViewModel>();
            Assert.NotNull(warning);
            Assert.NotNull(App.DialogService.CreateViewModel<TsTimestampWarningViewModel>());
            Assert.NotNull(App.DialogService.CreateViewModel<OutputWindowViewModel>());
            Assert.NotNull(App.DialogService.CreateViewModel<ExportPreparationWindowViewModel>());
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("save-cancel")]
    [InlineData("original")]
    [InlineData("supplement")]
    [InlineData("complete")]
    [InlineData("unnamed")]
    public async Task SingleExportConfirmsBeforePickingAPathAndKeepsTheChoice(string mode)
    {
        using var files = new TableProbeData.Files([.. TableProbeData.Section(0, TableProbeData.Pat()),
            .. TableProbeData.Section(0x1000, TableProbeData.Pmt()),
            .. Enumerable.Range(0, 8).SelectMany(_ => TableProbeData.Padding())]);
        await RunAsync(async () =>
        {
            var manager = DispatchProxy.Create<IDialogManager, ProgramTableDialogManager>();
            var stub = (ProgramTableDialogManager)(object)manager;
            stub.OutputPath = files.Output;
            stub.Mode = mode;
            new App().Initialize();
            var dialogs = new DialogService(manager, viewModelFactory: type => Locator.Current.GetService(type));
            var locale = Locator.Current.GetService<ILocalizationService>()!;
            var main = new MainWindowViewModel(dialogs, new ConfigurationService(locale));
            var clip = new PickedClip { InFileInfo = new FileInfo(files.Source), Name = mode == "unnamed" ? " " : "片段一",
                StartPosition = mode == "complete" ? 0 : 2 * 188, EndPosition = 6 * 188, StartTime = 1, EndTime = 2 };
            stub.ExpectedClipLabel = mode == "unnamed"
                ? $"#{clip.ClipID} · [ 00:00:01.000 – 00:00:02.000 ]" : clip.DisplayName;
            main.Clips.Add(clip);
            main.SelectedClip = clip;
            try
            {
                await (Task)typeof(MainWindowViewModel).GetMethod("SaveVideoAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(main, null)!;
                string[] expected = mode switch
                {
                    "cancel" => ["warning"],
                    "save-cancel" => ["warning", "path"],
                    "complete" => ["path", "output"],
                    _ => ["warning", "path", "output"]
                };
                Assert.Equal(expected, stub.Events);
                Assert.Equal(mode is not ("cancel" or "save-cancel"), File.Exists(files.Output));
                if (File.Exists(files.Output))
                {
                    Assert.Equal(mode == "supplement" ? 6 * 188 : clip.EndPosition - clip.StartPosition,
                        new FileInfo(files.Output).Length);
                    Assert.Equal(ClipExportStatus.Done, clip.ExportStatus);
                }
            }
            finally { main.Close(); }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MergeUsesTheConfirmedChoiceWithTheFinalOutputPath(bool supplement)
    {
        using var files = new TableProbeData.Files([.. TableProbeData.Section(0, TableProbeData.Pat()),
            .. TableProbeData.Section(0x1000, TableProbeData.Pmt()),
            .. Enumerable.Range(0, 12).SelectMany(_ => TableProbeData.Padding())]);
        await RunAsync(async () =>
        {
            var output = new OutputWindowViewModel
            {
                ConfirmProgramTableWarningAsync = (_, _) => throw new InvalidOperationException("Repeated warning")
            };
            output.SetMergeTask(new() { SourcePath = files.Source, OutputPath = files.Output,
                Ranges = [new(2 * 188, 6 * 188, 0, 1), new(8 * 188, 12 * 188, 2, 3)] });
            output.SetProgramTableChoice([], supplement ? TsProgramTableExportChoice.Supplement : TsProgramTableExportChoice.Original);
            await output.OutputCommand.ExecuteAsync(null);
            Assert.Null(output.Exception);
            Assert.True(output.DialogResult);
            Assert.Equal((supplement ? 10 : 8) * 188, new FileInfo(files.Output).Length);
        });
    }

    [Fact]
    public async Task ObsoletePreflightDoesNotOpenAWarningOrProgress()
    {
        using var files = new TableProbeData.Files([.. TableProbeData.Padding(), .. TableProbeData.Padding()]);
        await RunAsync(async () =>
        {
            var manager = DispatchProxy.Create<IDialogManager, ProgramTableDialogManager>();
            var stub = (ProgramTableDialogManager)(object)manager;
            new App().Initialize();
            var dialogs = new DialogService(manager, viewModelFactory: type => Locator.Current.GetService(type));
            var main = new MainWindowViewModel(dialogs, new ConfigurationService(Locator.Current.GetService<ILocalizationService>()!));
            var output = new OutputWindowViewModel { SourceFilePath = files.Source, OutputPath = files.Output, EndPosition = -1 };
            try
            {
                var pending = (Task<bool>)typeof(MainWindowViewModel)
                    .GetMethod("PrepareProgramTableExportAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(main, [output, new[] { "片段一" }, new Func<bool>(() => false)])!;
                Assert.False(await pending);
                Assert.Empty(stub.Events);
            }
            finally { main.Close(); }
        });
    }

    [NativeMediaTheory("frameaccurate")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BatchTimestampWarningIsConfirmedOncePerSource(bool proceed)
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ts-clock-warning-" + Guid.NewGuid().ToString("N")));
        var source = Path.Combine(directory.FullName, "source.ts");
        var first = Path.Combine(directory.FullName, "first.ts");
        var second = Path.Combine(directory.FullName, "second.ts");
        var invalidSource = Path.Combine(directory.FullName, "invalid.ts");
        var invalidOutput = Path.Combine(directory.FullName, "invalid-output.ts");
        try
        {
            var fixture = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "h264.ts");
            FrameAccurateTsOutputTests.WriteLateDtsSource(fixture, source);
            File.WriteAllBytes(invalidSource, [0, 1, 2]);
            var frames = FrameAccurateExportTests.DecodeFrames(fixture);
            var settings = FrameAccurateExportTests.Settings(source);
            await RunAsync(async () =>
            {
                var output = new OutputWindowViewModel();
                output.SetBatchTasks(
                [
                    new BatchTask(invalidSource, invalidOutput, 0, -1, FrameAccurate: settings, StartPts: frames[40], EndPts: frames[100]),
                    new BatchTask(source, first, 0, -1, FrameAccurate: settings, StartPts: frames[40], EndPts: frames[100]),
                    new BatchTask(source, second, 0, -1, FrameAccurate: settings, StartPts: frames[150], EndPts: frames[230])
                ]);
                var prompts = 0;
                output.ConfirmTimestampNormalizationAsync = (name, token) =>
                {
                    prompts++;
                    Assert.Equal(["source.ts"], name);
                    Assert.False(File.Exists(first));
                    Assert.False(File.Exists(second));
                    return Task.FromResult(proceed);
                };
                await output.OutputCommand.ExecuteAsync(null);
                Assert.Equal(1, prompts);
                if (proceed)
                    Assert.IsType<FFmpegException>(output.Exception);
                else
                    Assert.Null(output.Exception);
                Assert.Equal(proceed ? new[] { 0 } : [], output.FailedTaskIndices);
                Assert.False(File.Exists(invalidOutput));
                Assert.Equal(proceed ? 2 : 0, output.CompletedTaskIndices.Count);
                Assert.Equal(proceed, File.Exists(first));
                Assert.Equal(proceed, File.Exists(second));
            });
        }
        finally { directory.Delete(recursive: true); }
    }

    [NativeMediaTheory("frameaccurate")]
    [InlineData("timestamp-cancel")]
    [InlineData("timestamp-save-cancel")]
    [InlineData("timestamp-normalize")]
    [InlineData("normal-save")]
    [InlineData("normal-cancel")]
    public async Task TimestampWarningPrecedesTheSaveDialog(string mode)
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ts-clock-preflight-" + Guid.NewGuid().ToString("N")));
        var source = Path.Combine(directory.FullName, "recording.ts");
        var outputPath = Path.Combine(directory.FullName, "clip.ts");
        try
        {
            var fixture = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "h264.ts");
            if (mode.StartsWith("normal", StringComparison.Ordinal))
                File.Copy(fixture, source);
            else
                FrameAccurateTsOutputTests.WriteLateDtsSource(fixture, source);
            var frames = FrameAccurateExportTests.DecodeFrames(fixture);
            await RunAsync(async () =>
            {
                var manager = DispatchProxy.Create<IDialogManager, ProgramTableDialogManager>();
                var stub = (ProgramTableDialogManager)(object)manager;
                stub.OutputPath = outputPath;
                stub.ExpectedClipLabel = "recording.ts";
                stub.Mode = mode;
                stub.RejectPreparationDialog = true;
                var dialogs = new DialogService(manager, viewModelFactory: type => Activator.CreateInstance(type));
                var config = new RemoveModeWorkflowTests.MemoryConfiguration();
                config.CurrentConfig.ClipCutMode = ClipCutMode.FrameAccurate;
                var main = new MainWindowViewModel(dialogs, config) { VideoPath = source };
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                try
                {
                    Assert.True(await (Task<bool>)typeof(MainWindowViewModel).GetMethod("LoadVideoAsync", flags)!.Invoke(main, [null])!);
                    Assert.True(main.UsesFrameAccurateCut);
                    var clip = new PickedClip
                    {
                        InFileInfo = new FileInfo(source), StartPosition = 0, EndPosition = 188,
                        StartPts = frames[40], EndPts = frames[100], StartTime = 1, EndTime = 3,
                        RequiresFrameAccurate = true
                    };
                    main.Clips.Add(clip);
                    main.SelectedClip = clip;
                    await (Task)typeof(MainWindowViewModel).GetMethod("SaveVideoAsync", flags)!.Invoke(main, null)!;
                    Assert.Equal(mode switch
                    {
                        "timestamp-cancel" => new[] { "timestamp" },
                        "timestamp-save-cancel" => new[] { "timestamp", "path" },
                        "normal-save" => new[] { "path", "output" },
                        "normal-cancel" => new[] { "path" },
                        _ => new[] { "timestamp", "path", "output" }
                    }, stub.Events);
                    Assert.Equal(mode is "timestamp-normalize" or "normal-save", File.Exists(outputPath));
                    Assert.False(main.IsCheckingExportTimestamps);
                }
                finally { await CloseAndReleaseAsync(main); }
            });
        }
        finally { directory.Delete(recursive: true); }
    }

    /// <summary>
    /// 关闭主窗口并等待源文件释放：主预览在在途操作结束后关闭，导航条的解码实例由其后台循环在界面线程上释放。
    /// Windows 不能删除仍被打开的文件，测试会话结束前不等待，临时目录就删不掉。
    /// </summary>
    private static async Task CloseAndReleaseAsync(MainWindowViewModel main)
    {
        main.Close();
        await main.KeyFrameStrip.IdleTask;
        await (Task)typeof(MainWindowViewModel).GetField("previewDisposalTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(main)!;
    }

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

public class ProgramTableDialogManager : DispatchProxy
{
    public string ExpectedClipLabel { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string Mode { get; set; } = "";
    public bool RejectPreparationDialog { get; set; }
    public List<string> Events { get; } = [];
    /// <summary>导出结束后显示的结果窗口；不计入事件顺序。</summary>
    public List<ExportResultWindowViewModel> Results { get; } = [];

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "ShowDialogAsync" && args![1] is ExportResultWindowViewModel result)
        {
            Results.Add(result);
            return Task.FromResult<bool?>(true);
        }
        if (method.Name == "ShowFrameworkDialogAsync")
        {
            Events.Add("path");
            Assert.False(File.Exists(OutputPath));
            return Task.FromResult<object?>(Mode is "save-cancel" or "timestamp-save-cancel" or "normal-cancel" ? null : new DesktopDialogStorageFile(OutputPath));
        }
        if (method.Name == "ShowDialogAsync" && args![1] is ExportPreparationWindowViewModel preparation)
        {
            Assert.False(RejectPreparationDialog);
            return PreparationDialogManager.ObserveAsync(preparation);
        }
        if (method.Name == "ShowDialogAsync" && args![1] is TsTimestampWarningViewModel timestamp)
        {
            Events.Add("timestamp");
            Assert.Single(Events);
            Assert.Equal(ExpectedClipLabel, timestamp.FileNames.Single());
            Assert.False(File.Exists(OutputPath));
            if (Mode != "timestamp-cancel")
                timestamp.Confirm();
            return Task.FromResult(timestamp.DialogResult);
        }
        if (method.Name == "ShowDialogAsync" && args![1] is TsExportWarningViewModel warning)
        {
            Events.Add("warning");
            Assert.Single(Events);
            Assert.Equal(ExpectedClipLabel, warning.FileNames.Single());
            Assert.False(File.Exists(OutputPath));
            warning.Select(Mode switch
            {
                "cancel" => TsProgramTableExportChoice.Cancel,
                "supplement" => TsProgramTableExportChoice.Supplement,
                _ => TsProgramTableExportChoice.Original
            });
            return Task.FromResult(warning.DialogResult);
        }
        if (method.Name == "ShowDialogAsync" && args![1] is OutputWindowViewModel output)
        {
            Events.Add("output");
            output.ConfirmProgramTableWarningAsync = (_, _) => throw new InvalidOperationException("Repeated warning");
            output.ConfirmTimestampNormalizationAsync = (_, _) => throw new InvalidOperationException("Repeated timestamp warning");
            return RunOutputAsync(output);
        }
        throw new NotSupportedException(method.ToString());
    }

    private static async Task<bool?> RunOutputAsync(OutputWindowViewModel output)
    {
        await output.OutputCommand.ExecuteAsync(null);
        Assert.Null(output.Exception);
        Assert.True(output.DialogResult);
        return output.DialogResult;
    }
}
