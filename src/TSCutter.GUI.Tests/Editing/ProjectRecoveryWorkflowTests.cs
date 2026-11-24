using System.Reflection;
using Avalonia.Headless;
using Splat;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Tests.FFmpeg;
using TSCutter.GUI.Tests.UI;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.Views;
using Xunit;

namespace TSCutter.GUI.Tests.Editing;

/// <summary>
/// 主窗口的自动保存与恢复：标记片段后写入恢复文件，另一个实例按遗留文件恢复出相同的片段并标记为未保存；
/// 已打开的工程只在有修改时写入，恢复后仍指向原工程；关闭视频、关闭设置时删除恢复文件。
/// </summary>
[Collection("Avalonia UI")]
public sealed class ProjectRecoveryWorkflowTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly AppInstanceIdentity Crashed = new(5100, 638_100_000_000_000_000);
    private static readonly AppInstanceIdentity Restarted = new(5200, 638_100_000_100_000_000);

    [NativeMediaFact("frameaccurate")]
    public async Task ClipsMarkedOnAVideoAreRecoveredInTheNextInstance()
    {
        var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "h264.ts");
        var frames = FrameAccurateExportTests.DecodeFrames(source);
        var directory = Path.Combine(Path.GetTempPath(), "tscutter-recovery-ui-" + Guid.NewGuid().ToString("N"));
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        try
        {
            await headless.Dispatch<bool>(async () =>
            {
                using var resolver = new ModernDependencyResolver();
                using var scope = resolver.WithResolver();
                Locator.CurrentMutable.RegisterConstant<ILocalizationService>(new LocalizationService());
                var crashedStore = new ProjectRecoveryStore(directory, Crashed, _ => false);
                var restartedStore = new ProjectRecoveryStore(directory, Restarted, _ => false);

                var first = RemoveModeWorkflowTests.CreateViewModel(new RemoveModeWorkflowTests.MemoryConfiguration());
                first.RecoveryStore = crashedStore;
                first.VideoPath = source;
                Assert.True(await LoadVideoAsync(first));
                // 刚打开、还没有片段：没有需要恢复的内容。
                await first.SaveRecoveryAsync();
                Assert.False(File.Exists(crashedStore.OwnFilePath));

                await first.AddClipCommand.ExecuteAsync(null);
                await RemoveModeWorkflowTests.Settle();
                await first.SeekToKeyFrameAndDrawFrameAsync(frames[120]);
                await RemoveModeWorkflowTests.Settle();
                await first.MarkClipEndShortcutCommand.ExecuteAsync(null);
                await RemoveModeWorkflowTests.Settle();
                var marked = Assert.Single(first.Clips);

                await first.SaveRecoveryAsync();
                var written = ProjectRecoveryStore.Read(crashedStore.OwnFilePath);
                Assert.Null(written.ProjectPath);
                Assert.Equal(marked.EndPosition, Assert.Single(written.Project.Clips).EndPosition);
                Assert.Equal(marked.StartThumbnail is not null, written.Project.Clips[0].StartThumbnailJpeg is not null);
                // 内容没有变化时不重写。
                var writeTime = File.GetLastWriteTimeUtc(crashedStore.OwnFilePath);
                await Task.Delay(50);
                await first.SaveRecoveryAsync();
                Assert.Equal(writeTime, File.GetLastWriteTimeUtc(crashedStore.OwnFilePath));

                // 第一个实例“崩溃”：恢复文件留在原处，下一个实例启动时认领并恢复。
                var configuration = new RemoveModeWorkflowTests.MemoryConfiguration();
                var second = RemoveModeWorkflowTests.CreateViewModel(configuration);
                second.RecoveryStore = restartedStore;
                var entry = Assert.Single(restartedStore.FindOrphans());
                Assert.True(restartedStore.Claim(entry));
                Assert.True(await RestoreAsync(second, entry.ProjectPath, restartedStore.OwnFilePath));
                await RemoveModeWorkflowTests.Settle();
                var recovered = Assert.Single(second.Clips);
                Assert.Equal(1, recovered.DisplayNumber);
                Assert.Equal(marked.StartPosition, recovered.StartPosition);
                Assert.Equal(marked.EndPosition, recovered.EndPosition);
                Assert.Equal(marked.StartThumbnail is not null, recovered.StartThumbnail is not null);
                Assert.True(second.HasUnsavedProjectChanges);
                Assert.EndsWith($"({LocalizationManager.Instance.String_Recovery_TitleMarker})", second.WindowTitle);
                // 恢复后仍是未保存内容，继续写入本实例的恢复文件。
                await second.SaveRecoveryAsync();
                Assert.True(File.Exists(restartedStore.OwnFilePath));

                // 关闭自动保存：恢复文件随之删除。
                typeof(MainWindowViewModel).GetMethod("StartProjectRecovery", Private)!.Invoke(second, null);
                configuration.CurrentConfig.AutoSaveRecovery = false;
                await RecoveryChain(second);
                Assert.False(File.Exists(restartedStore.OwnFilePath));
                second.DiscardRecoveryOnExit();

                // 关闭视频（没有未保存的工程）：恢复文件删除。
                await first.CloseVideoClickCommand.ExecuteAsync(null);
                await RecoveryChain(first);
                Assert.False(File.Exists(crashedStore.OwnFilePath));
                first.Close();
                second.Close();
                return true;
            }, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [NativeMediaFact("frameaccurate")]
    public async Task OpenProjectsAreRecoveredOnlyWhenChangedAndKeepTheirPath()
    {
        var source = Path.Combine(NativeTestEnvironment.FixtureDirectory("frameaccurate"), "h264.ts");
        var frames = FrameAccurateExportTests.DecodeFrames(source);
        var directory = Path.Combine(Path.GetTempPath(), "tscutter-recovery-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var projectPath = Path.Combine(directory, "project.tscut");
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        try
        {
            await headless.Dispatch<bool>(async () =>
            {
                using var resolver = new ModernDependencyResolver();
                using var scope = resolver.WithResolver();
                Locator.CurrentMutable.RegisterConstant<ILocalizationService>(new LocalizationService());
                var recoveryDirectory = Path.Combine(directory, "Recovery");
                var crashedStore = new ProjectRecoveryStore(recoveryDirectory, Crashed, _ => false);
                var restartedStore = new ProjectRecoveryStore(recoveryDirectory, Restarted, _ => false);

                // 准备一个含一个片段的工程文件。
                var author = RemoveModeWorkflowTests.CreateViewModel(new RemoveModeWorkflowTests.MemoryConfiguration());
                author.VideoPath = source;
                Assert.True(await LoadVideoAsync(author));
                await author.AddClipCommand.ExecuteAsync(null);
                await RemoveModeWorkflowTests.Settle();
                var project = (CutterProject)typeof(MainWindowViewModel).GetMethod("CaptureProject", Private)!
                    .Invoke(author, [false])!;
                CutterProjectService.PopulateSources(project);
                await CutterProjectService.SaveAsync(projectPath, project);
                var previousId = Assert.Single(author.Clips).ClipID;
                var otherSource = Path.Combine(directory, "other.ts");
                File.Copy(source, otherSource);
                author.VideoPath = otherSource;
                Assert.True(await LoadVideoAsync(author));
                Assert.Empty(author.Clips);
                await author.AddClipCommand.ExecuteAsync(null);
                Assert.Equal("#1", Assert.Single(author.Clips).DisplayName);
                Assert.True(author.Clips[0].ClipID > previousId);
                author.Close();

                var editor = RemoveModeWorkflowTests.CreateViewModel(new RemoveModeWorkflowTests.MemoryConfiguration());
                editor.RecoveryStore = crashedStore;
                await (Task)typeof(MainWindowViewModel).GetMethod("OpenProjectPathAsync", Private)!
                    .Invoke(editor, [projectPath])!;
                await RemoveModeWorkflowTests.Settle();
                Assert.Equal(1, Assert.Single(editor.Clips).DisplayNumber);
                Assert.False(editor.HasUnsavedProjectChanges);
                // 工程没有修改：不写恢复文件。
                await editor.SaveRecoveryAsync();
                Assert.False(File.Exists(crashedStore.OwnFilePath));

                await editor.SeekToKeyFrameAndDrawFrameAsync(frames[200]);
                await RemoveModeWorkflowTests.Settle();
                await editor.AddClipCommand.ExecuteAsync(null);
                await RemoveModeWorkflowTests.Settle();
                Assert.Equal(2, editor.Clips.Count);
                await editor.SaveRecoveryAsync();
                var written = ProjectRecoveryStore.Read(crashedStore.OwnFilePath);
                Assert.Equal(Path.GetFullPath(projectPath), Path.GetFullPath(written.ProjectPath!));
                Assert.Equal(2, written.Project.Clips.Count);

                var restarted = RemoveModeWorkflowTests.CreateViewModel(new RemoveModeWorkflowTests.MemoryConfiguration());
                restarted.RecoveryStore = restartedStore;
                var entry = Assert.Single(restartedStore.FindOrphans());
                Assert.True(restartedStore.Claim(entry));
                Assert.True(await RestoreAsync(restarted, entry.ProjectPath, restartedStore.OwnFilePath));
                await RemoveModeWorkflowTests.Settle();
                Assert.Equal(new[] { 1, 2 }, restarted.Clips.Select(clip => clip.DisplayNumber));
                Assert.True(restarted.HasUnsavedProjectChanges);
                Assert.Contains("[project.tscut] (", restarted.WindowTitle);
                // 工程文件本身没有被改写。
                Assert.Single((await CutterProjectService.LoadAsync(projectPath)).Clips);

                restarted.DiscardRecoveryOnExit();
                editor.DiscardRecoveryOnExit();
                editor.Close();
                restarted.Close();
                return true;
            }, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ClosingTheWindowIsANormalExitAndRemovesTheRecoveryFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tscutter-recovery-ui-" + Guid.NewGuid().ToString("N"));
        using var headless = HeadlessUnitTestSession.StartNew(typeof(ThemeTestAppBuilder));
        try
        {
            await headless.Dispatch<bool>(async () =>
            {
                using var resolver = new ModernDependencyResolver();
                using var scope = resolver.WithResolver();
                Locator.CurrentMutable.RegisterConstant<ILocalizationService>(new LocalizationService());
                var store = new ProjectRecoveryStore(directory, Crashed, _ => false);
                var vm = RemoveModeWorkflowTests.CreateViewModel(new RemoveModeWorkflowTests.MemoryConfiguration());
                vm.RecoveryStore = store;
                store.Write(ProjectRecoveryStore.Serialize(new ProjectRecoveryFile { SavedUtc = DateTime.UtcNow }));
                var window = new MainWindow { DataContext = vm };
                window.Show();
                await RemoveModeWorkflowTests.Settle();
                // 关闭按钮、Alt+F4 与系统退出（关闭所有窗口）都经过窗口的关闭事件。
                window.Close();
                await RemoveModeWorkflowTests.Settle();
                Assert.False(window.IsVisible);
                Assert.False(File.Exists(store.OwnFilePath));
                Assert.Empty(new ProjectRecoveryStore(directory, Restarted, _ => false).FindOrphans());
                return true;
            }, CancellationToken.None);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static Task<bool> LoadVideoAsync(MainWindowViewModel vm) =>
        (Task<bool>)typeof(MainWindowViewModel).GetMethod("LoadVideoAsync", Private)!.Invoke(vm, [null])!;

    private static Task<bool> RestoreAsync(MainWindowViewModel vm, string? projectPath, string recoveryPath) =>
        (Task<bool>)typeof(MainWindowViewModel).GetMethod("OpenProjectCoreAsync", Private)!
            .Invoke(vm, [projectPath, recoveryPath])!;

    private static Task RecoveryChain(MainWindowViewModel vm) =>
        (Task)typeof(MainWindowViewModel).GetField("_recoveryChain", Private)!.GetValue(vm)!;
}
