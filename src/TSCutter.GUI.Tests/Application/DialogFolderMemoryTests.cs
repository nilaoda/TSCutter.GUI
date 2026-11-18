using System.Reflection;
using System.Text.Json;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.Avalonia;
using HanumanInstitute.MvvmDialogs.FrameworkDialogs;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.ViewModels;
using TSCutter.GUI.ViewModels.Application;
using Xunit;

namespace TSCutter.GUI.Tests.Application;

/// <summary>
/// 打开类与保存类对话框分别记住目录：互不影响、按顺序回退、检查超时不卡住、目录失效时不清除记住的值，
/// 以及配置读写和设置页的选项。目录都在临时文件夹中生成，结束后删除。
/// </summary>
public sealed class DialogFolderMemoryTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("tscutter-dialog-folders-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root.FullName, name)).FullName;

    private string MissingFolder(string name) => Path.Combine(_root.FullName, name);

    [Fact]
    public void FilesDiscImagesAndDiscFoldersRememberTheirContainingFolder()
    {
        var sources = Folder("sources");
        Assert.Equal(sources, DialogFolderPaths.ContainingFolder(Path.Combine(sources, "input.ts")));
        Assert.Equal(sources, DialogFolderPaths.ContainingFolder(Path.Combine(sources, "disc.iso")));
        // 光盘文件夹（含末尾分隔符）记住它的上级目录，下次能在列表里直接看到它。
        Assert.Equal(sources, DialogFolderPaths.ContainingFolder(
            Path.Combine(sources, "disc") + Path.DirectorySeparatorChar));
        // 盘符根目录没有上级目录，取它本身。
        var root = Path.GetPathRoot(_root.FullName)!;
        Assert.Equal(root, DialogFolderPaths.ContainingFolder(root));
        Assert.Null(DialogFolderPaths.ContainingFolder("  "));
        Assert.Null(DialogFolderPaths.Normalize(null));
        Assert.Equal(sources, DialogFolderPaths.Normalize(sources + Path.DirectorySeparatorChar));
    }

    [Fact]
    public async Task ImportAndExportFoldersAreRememberedSeparately()
    {
        var sources = Folder("sources");
        var outputs = Folder("outputs");
        var config = new AppConfig();
        var saves = 0;
        var memory = new DialogFolderMemory(() => config, () => saves++);

        memory.RememberImport(Path.Combine(sources, "input.ts"));
        memory.RememberExport(Path.Combine(outputs, "clip.ts"));
        Assert.Equal(sources, config.LastImportFolder);
        Assert.Equal(outputs, config.LastExportFolder);
        Assert.Equal(2, saves);

        // 同一目录再次记录时不重复写配置；多选的另一个文件不会改动导出目录。
        memory.RememberImport(Path.Combine(sources, "second.ts"));
        memory.RememberExportFolder(outputs + Path.DirectorySeparatorChar);
        Assert.Equal(2, saves);

        Assert.Equal(sources, await memory.ResolveImportAsync());
        Assert.Equal(outputs, await memory.ResolveExportAsync(sources));
        Assert.Equal(sources, (await memory.ImportStartAsync())?.LocalPath);
        Assert.Equal(outputs, (await memory.ExportStartAsync(sources))?.LocalPath);
    }

    [Fact]
    public async Task ExportStartFallsBackInOrderAndKeepsTheRememberedFolder()
    {
        var sources = Folder("sources");
        var config = new AppConfig();
        var memory = new DialogFolderMemory(() => config, null);

        // 还没有导出过：用调用方给出的默认目录；都没有时交给系统决定。
        Assert.Equal(sources, await memory.ResolveExportAsync(sources));
        Assert.Null(await memory.ResolveExportAsync(null));
        Assert.Null(await memory.ResolveImportAsync());
        Assert.Null(await memory.ImportStartAsync());

        // 记住的目录不存在（例如移动硬盘已拔出）时回退到默认目录，但不清除记住的值。
        var unplugged = MissingFolder("unplugged");
        memory.RememberExportFolder(unplugged);
        memory.RememberImport(Path.Combine(unplugged, "input.ts"));
        Assert.Equal(sources, await memory.ResolveExportAsync(sources));
        Assert.Null(await memory.ResolveImportAsync());
        Assert.Equal(unplugged, config.LastExportFolder);
        Assert.Equal(unplugged, config.LastImportFolder);

        // 目录恢复后继续使用。
        Directory.CreateDirectory(unplugged);
        Assert.Equal(unplugged, await memory.ResolveExportAsync(sources));
        Assert.Equal(unplugged, await memory.ResolveImportAsync());
    }

    [Fact]
    public async Task SourceFolderPreferenceComesFirstButStillFallsBackToTheLastExport()
    {
        var sources = Folder("sources");
        var outputs = Folder("outputs");
        var config = new AppConfig { SaveDialogsStartInSourceFolder = true, LastExportFolder = outputs };
        var memory = new DialogFolderMemory(() => config, null);

        Assert.Equal(sources, await memory.ResolveExportAsync(sources));
        // 没有源文件（或源文件目录已不可用）时用上次导出的目录。
        Assert.Equal(outputs, await memory.ResolveExportAsync(null));
        Assert.Equal(outputs, await memory.ResolveExportAsync(MissingFolder("gone")));
        // 导入目录不受这个选项影响。
        Assert.Null(await memory.ResolveImportAsync());
    }

    // 测试用的缩短时间：网络盘只等 150 毫秒，本地盘最多等 5 秒。
    private static DialogFolderProbeOptions Probe(Func<string, bool> exists, Func<string, bool> isNetwork,
        Func<IDisposable?>? showBusy = null) => new()
    {
        BusyDelay = TimeSpan.FromMilliseconds(20),
        NetworkTimeout = TimeSpan.FromMilliseconds(150),
        LocalTimeout = TimeSpan.FromSeconds(5),
        FolderExists = exists,
        IsNetworkPath = isNetwork,
        ShowBusy = showBusy
    };

    [Fact]
    public async Task OfflineNetworkFoldersTimeOutQuicklyAndFallBack()
    {
        var sources = Folder("sources");
        var offline = MissingFolder("offline-share");
        using var release = new ManualResetEventSlim();
        var offlineChecks = 0;
        var config = new AppConfig { LastExportFolder = offline, LastImportFolder = offline };
        var memory = new DialogFolderMemory(() => config, null, Probe(folder =>
        {
            // 模拟已断开的网络共享：检查迟迟不返回。
            if (folder == offline)
            {
                Interlocked.Increment(ref offlineChecks);
                release.Wait(TimeSpan.FromSeconds(30));
            }
            return folder == sources;
        }, folder => folder == offline));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(sources, await memory.ResolveExportAsync(sources));
        Assert.Null(await memory.ResolveImportAsync());
        // 只等网络超时，远不到本地盘的 5 秒。
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
        Assert.Equal(offline, config.LastExportFolder);
        // 上一次检查还卡着时复用它，不再占用新的线程。
        Assert.Equal(1, Volatile.Read(ref offlineChecks));
        release.Set();
    }

    [Fact]
    public async Task WakingLocalDisksAreWaitedForWithABusyCursor()
    {
        var sources = Folder("sources");
        var sleeping = Folder("sleeping-disk");
        var busyShown = 0;
        var busyReleased = 0;
        var config = new AppConfig { LastExportFolder = sleeping };
        var memory = new DialogFolderMemory(() => config, null, Probe(folder =>
        {
            // 模拟休眠的本地硬盘：比网络超时慢，但在本地超时之内醒来。
            if (folder == sleeping)
                Thread.Sleep(600);
            return Directory.Exists(folder);
        }, _ => false, () =>
        {
            busyShown++;
            return new Release(() => busyReleased++);
        }));

        Assert.Equal(sleeping, await memory.ResolveExportAsync(sources));
        Assert.Equal(1, busyShown);
        Assert.Equal(1, busyReleased);

        // 很快返回时不显示忙碌指针。
        config.LastExportFolder = sources;
        Assert.Equal(sources, await memory.ResolveExportAsync(null));
        Assert.Equal(1, busyShown);
    }

    [Fact]
    public async Task FoldersThatTimedOutOnceOnlyGetTheShortWaitUntilTheyAnswerAgain()
    {
        var sources = Folder("sources");
        // 挂载成本地盘的网络存储：路径类型判断为本地，离线时检查一直不返回。
        var mounted = Folder("mounted-storage");
        var offline = true;
        using var release = new ManualResetEventSlim();
        var config = new AppConfig { LastExportFolder = mounted };
        var memory = new DialogFolderMemory(() => config, null, Probe(folder =>
        {
            if (folder == mounted && Volatile.Read(ref offline))
                release.Wait(TimeSpan.FromSeconds(30));
            return Directory.Exists(folder);
        }, _ => false) with { LocalTimeout = TimeSpan.FromMilliseconds(900) });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(sources, await memory.ResolveExportAsync(sources));
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(800), $"first wait {watch.Elapsed}");

        // 同一次运行中再遇到它，只等网络超时。
        watch.Restart();
        Assert.Equal(sources, await memory.ResolveExportAsync(sources));
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(700), $"second wait {watch.Elapsed}");

        // 恢复在线后照常使用，并清除超时记录。
        Volatile.Write(ref offline, false);
        release.Set();
        Assert.Equal(mounted, await memory.ResolveExportAsync(sources));
        Assert.Equal(mounted, config.LastExportFolder);
    }

    [Fact]
    public async Task DialogsWhoseWindowClosedWhileLocatingCountAsCancelled()
    {
        // 定位目录期间所属窗口已关闭：对话框服务找不到窗口，按取消处理而不是抛出异常。
        var dialogs = new DialogService(DispatchProxy.Create<IDialogManager, ClosedOwnerDialogs>());
        var owner = new ViewModelBase();
        // 直接调用对话框服务会抛出异常，统一入口需要接住它。
        await Assert.ThrowsAsync<ViewNotRegisteredException>(() => dialogs.ShowSaveFileDialogAsync(owner, new SaveFileDialogSettings()));
        Assert.Empty(await dialogs.ShowOpenFilesAtImportFolderAsync(owner, new OpenFileDialogSettings()));
        Assert.Null(await dialogs.ShowOpenFolderAtImportFolderAsync(owner, new OpenFolderDialogSettings()));
        Assert.Null(await dialogs.ShowSaveFileAtExportFolderAsync(owner, new SaveFileDialogSettings(), null));
        Assert.Null(await dialogs.ShowOpenFolderAtExportFolderAsync(owner, new OpenFolderDialogSettings(), null));
    }

    [Fact]
    public void NetworkPathsAreRecognisedWithoutTouchingTheFolder()
    {
        Assert.False(DialogFolderMemory.IsNetworkPath(_root.FullName));
        if (!OperatingSystem.IsWindows())
            return;
        Assert.True(DialogFolderMemory.IsNetworkPath(@"\\nas\share\videos"));
        Assert.True(DialogFolderMemory.IsNetworkPath("//nas/share/videos"));
        Assert.True(DialogFolderMemory.IsNetworkPath(@"\\?\UNC\nas\share\videos"));
        Assert.False(DialogFolderMemory.IsNetworkPath(@"\\?\" + _root.FullName));
    }

    public class ClosedOwnerDialogs : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "ShowFrameworkDialogAsync"
                ? throw new ViewNotRegisteredException("owner closed")
                : throw new NotSupportedException(targetMethod?.Name);
    }

    private sealed class Release(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    [Fact]
    public void FoldersAndPreferenceSurviveSerializationAndDefaultToEmpty()
    {
        var defaults = JsonSerializer.Deserialize("{}", AppJsonContext.Default.AppConfig)!;
        Assert.Null(defaults.LastImportFolder);
        Assert.Null(defaults.LastExportFolder);
        Assert.False(defaults.SaveDialogsStartInSourceFolder);

        var sources = Folder("sources");
        var outputs = Folder("outputs");
        var config = new AppConfig
        {
            LastImportFolder = sources + Path.DirectorySeparatorChar,
            LastExportFolder = outputs,
            SaveDialogsStartInSourceFolder = true
        };
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig),
            AppJsonContext.Default.AppConfig)!;
        Assert.Equal(sources, restored.LastImportFolder);
        Assert.Equal(outputs, restored.LastExportFolder);
        Assert.True(restored.SaveDialogsStartInSourceFolder);

        var blank = JsonSerializer.Deserialize("""{"LastImportFolder":"  ","LastExportFolder":""}""",
            AppJsonContext.Default.AppConfig)!;
        Assert.Null(blank.LastImportFolder);
        Assert.Null(blank.LastExportFolder);
    }

    [Fact]
    public void SettingsChooseTheSaveDialogFolderOnlyWhenCommitted()
    {
        var configuration = new MemoryConfiguration();
        var viewModel = new SettingsWindowViewModel(configuration, new KeyLocalization());
        Assert.Equal(0, viewModel.SaveDialogFolderIndex);

        viewModel.SaveDialogFolderIndex = 1;
        Assert.True(viewModel.SaveDialogsStartInSourceFolder);
        // 下拉框重建选项时写入的 -1 不改变选择。
        viewModel.SaveDialogFolderIndex = -1;
        Assert.True(viewModel.SaveDialogsStartInSourceFolder);
        Assert.False(configuration.CurrentConfig.SaveDialogsStartInSourceFolder);
        viewModel.ApplyCommand.Execute(null);
        Assert.True(configuration.CurrentConfig.SaveDialogsStartInSourceFolder);

        var reopened = new SettingsWindowViewModel(configuration, new KeyLocalization());
        Assert.Equal(1, reopened.SaveDialogFolderIndex);
        reopened.SaveDialogFolderIndex = 0;
        reopened.ApplyCommand.Execute(null);
        Assert.False(configuration.CurrentConfig.SaveDialogsStartInSourceFolder);
    }

    private sealed class MemoryConfiguration : IConfigurationService
    {
        public AppConfig CurrentConfig { get; } = new();
        public void Load() { }
        public void Save() { }
        public void ApplyTheme(string theme) { }
        public void ApplyLanguage(string language) { }
    }

    private sealed class KeyLocalization : ILocalizationService
    {
        public List<SupportedLang> SupportedLanguages { get; } = [new("en-US", "English")];
        public string CurrentLanguageCode => "en-US";
        public void SwitchLanguage(string code) { }
        public string GetString(string key) => key;
        public event Action? LanguageChanged { add { } remove { } }
    }
}
