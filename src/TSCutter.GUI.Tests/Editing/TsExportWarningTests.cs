using System.Reflection;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using HanumanInstitute.MvvmDialogs.FileSystem;
using TSCutter.GUI.Models.Editing;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Classic.Avalonia.Theme;
using Splat;
using TSCutter.GUI.Models.TransportStream.Processing;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Utils.Application;
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
            var clip = new PickedClip { InFileInfo = new FileInfo(files.Source), Name = "片段一",
                StartPosition = mode == "complete" ? 0 : 2 * 188, EndPosition = 6 * 188, StartTime = 1, EndTime = 2 };
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
    public string OutputPath { get; set; } = "";
    public string Mode { get; set; } = "";
    public List<string> Events { get; } = [];

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "ShowFrameworkDialogAsync")
        {
            Events.Add("path");
            Assert.False(File.Exists(OutputPath));
            return Task.FromResult<object?>(Mode == "save-cancel" ? null : new DesktopDialogStorageFile(OutputPath));
        }
        if (method.Name == "ShowDialogAsync" && args![1] is ExportPreparationWindowViewModel preparation)
            return PreparationDialogManager.ObserveAsync(preparation);
        if (method.Name == "ShowDialogAsync" && args![1] is TsExportWarningViewModel warning)
        {
            Events.Add("warning");
            Assert.Single(Events);
            Assert.Contains("片段一", warning.FileNames.Single());
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
