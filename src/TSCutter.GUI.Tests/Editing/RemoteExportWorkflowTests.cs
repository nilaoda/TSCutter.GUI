using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Classic.Avalonia.Theme;
using Classic.CommonControls.Dialogs;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using HanumanInstitute.MvvmDialogs.FileSystem;
using Splat;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Editing;
using TSCutter.GUI.Utils.Files;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.ViewModels.Editing;
using TSCutter.GUI.ViewModels.Media;
using TSCutter.GUI.ViewModels.TransportStream.Processing;
using TSCutter.GUI.ViewModels.TransportStream.Repair;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Services.TransportStream.Processing;
using TSCutter.GUI.Models.TransportStream.Processing;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

[Collection("Avalonia UI")]
public sealed class RemoteExportWorkflowTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [NativeMediaTheory("keyframes")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeparateExportsUseTheAssignedFolderAndAvoidExistingNames(bool restricted)
    {
        var root = Directory.CreateTempSubdirectory("tscutter-remote-exports-");
        using var session = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        try
        {
            var input = root.CreateSubdirectory("media").FullName;
            var output = root.CreateSubdirectory("account-output").FullName;
            var source = Path.Combine(input, "source.ts");
            File.Copy(Path.Combine(NativeTestEnvironment.FixtureDirectory("keyframes"), "fixed.ts"), source);
            var original = File.ReadAllBytes(source);
            await session.Dispatch<bool>(async () =>
            {
                using var resolver = new ModernDependencyResolver();
                using var scope = resolver.WithResolver();
                var locale = new LocalizationService();
                Locator.CurrentMutable.RegisterConstant<ILocalizationService>(locale);
                var access = MediaFileAccess.Current;
                if (restricted)
                    MediaFileAccess.ConfigureForProcess(new MediaFileAccess(new MediaWorkspace(input, output), error => error.ToString()));
                var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var app = new ThemeTestApplication { ApplicationLifetime = lifetime };
                using var applicationScope = BindApplication(app);
                app.RegisterServices();
                app.Initialize();
                locale.SwitchLanguage("en-US");
                var owner = new ClassicWindow();
                lifetime.MainWindow = owner;
                owner.Show();
                var manager = DispatchProxy.Create<IDialogManager, BatchDialogs>();
                var dialogs = new DialogService(manager, viewModelFactory: type => Activator.CreateInstance(type)!);
                var config = new RemoveModeWorkflowTests.MemoryConfiguration();
                var folderMemory = DialogFolderMemory.Shared;
                DialogFolderMemory.Shared = new DialogFolderMemory(config.CurrentConfig);
                var recovery = new ProjectRecoveryStore(Path.Combine(root.FullName, "recovery"), AppInstanceIdentity.Current, _ => false);
                var main = new MainWindowViewModel(dialogs, config)
                {
                    VideoPath = source, RecoveryStore = recovery
                };
                var filter = new TsServiceFilterWindowViewModel(dialogs) { FilePath = source };
                var extractor = new TsEsExtractorWindowViewModel(dialogs) { FilePath = source };
                var mux = new MediaMuxWindowViewModel(dialogs);
                try
                {
                    Assert.True(await (Task<bool>)typeof(MainWindowViewModel).GetMethod("LoadVideoAsync", Private)!.Invoke(main, [null])!);
                    var directory = restricted ? output : input;
                    foreach (var _ in new[] { 1, 2 })
                        main.Clips.Add(new PickedClip
                        {
                            InFileInfo = new FileInfo(source), StartPosition = 0, EndPosition = original.Length,
                            StartTime = 0, EndTime = main.DurationMax
                        });
                    var name = ClipOutputNaming.Generate(config.CurrentConfig.ClipOutputTemplate, source, "", 1, 0, main.DurationMax, "ts");
                    var existing = Path.Combine(directory, name);
                    File.WriteAllText(existing, "existing");
                    var exporting = main.ExportAllCommand.ExecuteAsync(null);
                    var confirmation = owner.OwnedWindows.Single(window => window.IsVisible);
                    Assert.Contains(directory, string.Join("\n", confirmation.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text)));
                    confirmation.Close(MessageBoxResult.Yes);
                    await exporting.WaitAsync(TimeSpan.FromSeconds(15));
                    Assert.All(main.Clips, clip =>
                    {
                        Assert.Equal(ClipExportStatus.Done, clip.ExportStatus);
                        Assert.Equal(directory, clip.OutputFileInfo!.DirectoryName);
                        Assert.True(clip.OutputFileInfo.Length > 0);
                    });
                    Assert.Equal(2, main.Clips.Select(clip => clip.OutputFilePathStr).Distinct().Count());
                    Assert.Equal("existing", File.ReadAllText(existing));
                    Assert.Equal(original, File.ReadAllBytes(source));
                    await filter.ProbeCommand.ExecuteAsync(null);
                    await extractor.ProbeCommand.ExecuteAsync(null);
                    await mux.AddPathsAsync([source]);
                    Assert.Equal(directory, filter.OutputFolder);
                    Assert.Equal(directory, extractor.OutputFolder);
                    Assert.Equal(directory, Path.GetDirectoryName(mux.OutputPath));
                    var timelinePath = (string)typeof(TsTimelineRepairWindowViewModel)
                        .GetMethod("BuildDefaultOutputPath", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [source])!;
                    Assert.Equal(directory, Path.GetDirectoryName(timelinePath));
                    File.WriteAllText(timelinePath, "existing repair");
                    var nextTimelinePath = (string)typeof(TsTimelineRepairWindowViewModel)
                        .GetMethod("BuildDefaultOutputPath", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [source])!;
                    Assert.Equal(Path.Combine(directory, "source_timeline_fixed_2.ts"), nextTimelinePath);
                    var mergePath = (string)typeof(TsBinaryMergeWindowViewModel)
                        .GetMethod("BuildDefaultOutputPath", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [source])!;
                    Assert.Equal(directory, Path.GetDirectoryName(mergePath));
                    if (restricted)
                    {
                        var catalog = await new TsStreamAnalyzer().AnalyzeAsync(source);
                        var program = catalog.Programs.Values.First();
                        await Assert.ThrowsAsync<TsFilterException>(() => new TsServiceFilterService().FilterAsync(source, catalog,
                            [new TsServiceFilterOutput { ServiceId = program.ProgramNumber, OutputPath = existing }]));
                        await Assert.ThrowsAsync<TsEsExtractionException>(() => new TsEsExtractorService().ExtractAsync(source,
                            [new TsEsExtractionOutput { Pid = program.Streams.Keys.First(), OutputPath = existing }], catalog.SyncOffset));
                        Assert.Equal("existing", File.ReadAllText(existing));
                        var exported = Assert.IsType<string>(main.Clips[0].OutputFilePathStr);
                        await CloseAsync(main);
                        main = new MainWindowViewModel(dialogs, config) { RecoveryStore = recovery };
                        await (Task)typeof(MainWindowViewModel).GetMethod("OpenVideoPathAsync", Private)!.Invoke(main, [exported])!;
                        Assert.Equal(exported, main.VideoPath);
                        Assert.True(main.DurationMax > 0);
                        foreach (var _ in new[] { 1, 2 })
                            main.Clips.Add(new PickedClip
                            {
                                InFileInfo = new FileInfo(exported), StartPosition = 0, EndPosition = new FileInfo(exported).Length,
                                StartTime = 0, EndTime = main.DurationMax
                            });
                        var project = (CutterProject)typeof(MainWindowViewModel).GetMethod("CaptureProject", Private)!
                            .Invoke(main, [false])!;
                        CutterProjectService.PopulateSources(project);
                        var imported = Path.Combine(input, "imported.tscut");
                        var originalProject = CutterProjectService.Serialize(project);
                        File.WriteAllText(imported, originalProject);
                        await CloseAsync(main);
                        main = new MainWindowViewModel(dialogs, config) { RecoveryStore = recovery };
                        await (Task)typeof(MainWindowViewModel).GetMethod("OpenProjectPathAsync", Private)!.Invoke(main, [imported])!;
                        var saved = Path.Combine(output, "editable.tscut");
                        var stub = (BatchDialogs)(object)manager;
                        stub.SavePath = saved;
                        main.UpdateClipDetails(main.Clips[0], "Saved in media folder", "", "");
                        await main.SaveProjectCommand.ExecuteAsync(null);
                        Assert.Equal(0, stub.SaveDialogCount);
                        Assert.Equal("Saved in media folder", (await CutterProjectService.LoadAsync(imported)).Clips[0].Name);
                        var updatedProject = File.ReadAllText(imported);
                        await main.SaveProjectAsCommand.ExecuteAsync(null);
                        Assert.Equal(1, stub.SaveDialogCount);
                        await CloseAsync(main);
                        main = new MainWindowViewModel(dialogs, config) { RecoveryStore = recovery };
                        await (Task)typeof(MainWindowViewModel).GetMethod("OpenProjectPathAsync", Private)!.Invoke(main, [saved])!;
                        Assert.Equal(2, main.Clips.Count);
                        main.UpdateClipDetails(main.Clips[0], "Edited after reopening", "", "");
                        await main.SaveProjectCommand.ExecuteAsync(null);
                        Assert.Equal(1, stub.SaveDialogCount);
                        Assert.Equal("Edited after reopening", (await CutterProjectService.LoadAsync(saved)).Clips[0].Name);
                        Assert.Equal(updatedProject, File.ReadAllText(imported));
                    }
                }
                finally
                {
                    filter.OnClosed();
                    extractor.OnClosed();
                    mux.OnClosed();
                    await CloseAsync(main);
                    DialogFolderMemory.Shared = folderMemory;
                    lifetime.MainWindow = null;
                    owner.Close();
                    MediaFileAccess.ConfigureForProcess(access);
                }
                return true;
            }, CancellationToken.None);
        }
        finally { root.Delete(recursive: true); }
    }

    private static async Task CloseAsync(MainWindowViewModel main)
    {
        main.DiscardRecoveryOnExit();
        main.Close();
        await main.KeyFrameStrip.IdleTask;
        await (Task)typeof(MainWindowViewModel).GetField("previewDisposalTask", Private)!.GetValue(main)!;
    }

    private static IDisposable BindApplication(Avalonia.Application app)
    {
        var type = typeof(AvaloniaObject).Assembly.GetType("Avalonia.AvaloniaLocator")!;
        var scope = (IDisposable)type.GetMethod("EnterScope")!.Invoke(null, null)!;
        var locator = type.GetProperty("CurrentMutable")!.GetValue(null)!;
        var binding = locator.GetType().GetMethod("Bind")!.MakeGenericMethod(typeof(Avalonia.Application)).Invoke(locator, null)!;
        var bind = binding.GetType().GetMethod("ToConstant")!;
        if (bind.IsGenericMethodDefinition)
            bind = bind.MakeGenericMethod(typeof(Avalonia.Application));
        bind.Invoke(binding, [app]);
        return scope;
    }

    public class BatchDialogs : DispatchProxy
    {
        public string SavePath { get; set; } = "";
        public int SaveDialogCount { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "ShowFrameworkDialogAsync")
            {
                SaveDialogCount++;
                return Task.FromResult<object?>(new DesktopDialogStorageFile(SavePath));
            }
            if (method.Name == "ShowDialogAsync" && args![1] is OutputWindowViewModel output)
                return ExportAsync(output);
            if (method.Name == "ShowDialogAsync" && args![1] is TsExportWarningViewModel warning)
            {
                warning.Select(TsProgramTableExportChoice.Original);
                return Task.FromResult(warning.DialogResult);
            }
            if (method.Name == "ShowDialogAsync" && args![1] is ExportPreparationWindowViewModel preparation)
                return PreparationDialogManager.ObserveAsync(preparation);
            throw new NotSupportedException(method.ToString());
        }

        private static async Task<bool?> ExportAsync(OutputWindowViewModel output)
        {
            await output.OutputCommand.ExecuteAsync(null);
            Assert.Null(output.Exception);
            Assert.True(output.DialogResult);
            return output.DialogResult;
        }
    }
}
