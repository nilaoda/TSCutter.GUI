using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Platform.Storage;

namespace TSCutter.GUI.Services.Application;

// Windows IFileDialog 必须在 STA 中创建和显示，避免跨 COM apartment 代理与窗口激活消息互相等待。
internal sealed class WindowsPickerStorageProvider(IStorageProvider inner) : IStorageProvider
{
    internal static void Install()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var previous = AvaloniaLocator.Current.GetService<IStorageProviderFactory>();
        AvaloniaLocator.CurrentMutable.Bind<IStorageProviderFactory>().ToConstant(new Factory(previous));
    }

    private sealed class Factory(IStorageProviderFactory? previous) : IStorageProviderFactory
    {
        public IStorageProvider CreateProvider(TopLevel topLevel)
        {
            var provider = previous?.CreateProvider(topLevel)
                ?? topLevel.PlatformImpl?.TryGetFeature(typeof(IStorageProvider)) as IStorageProvider;
            return provider?.GetType().FullName == "Avalonia.Win32.Win32StorageProvider"
                ? new WindowsPickerStorageProvider(provider) : provider!;
        }
    }

    public bool CanOpen => inner.CanOpen;
    public bool CanSave => inner.CanSave;
    public bool CanPickFolder => inner.CanPickFolder;
    public Task<IReadOnlyList<IStorageFile>> OpenFilePickerAsync(FilePickerOpenOptions options) =>
        RunPickerAsync(() => inner.OpenFilePickerAsync(options));
    public Task<IStorageFile?> SaveFilePickerAsync(FilePickerSaveOptions options) =>
        RunPickerAsync(() => inner.SaveFilePickerAsync(options));
    public Task<SaveFilePickerResult> SaveFilePickerWithResultAsync(FilePickerSaveOptions options) =>
        RunPickerAsync(() => inner.SaveFilePickerWithResultAsync(options));
    public Task<IReadOnlyList<IStorageFolder>> OpenFolderPickerAsync(FolderPickerOpenOptions options) =>
        RunPickerAsync(() => inner.OpenFolderPickerAsync(options));
    public Task<IStorageBookmarkFile?> OpenFileBookmarkAsync(string bookmark) => inner.OpenFileBookmarkAsync(bookmark);
    public Task<IStorageBookmarkFolder?> OpenFolderBookmarkAsync(string bookmark) => inner.OpenFolderBookmarkAsync(bookmark);
    public Task<IStorageFile?> TryGetFileFromPathAsync(Uri path) => inner.TryGetFileFromPathAsync(path);
    public Task<IStorageFolder?> TryGetFolderFromPathAsync(Uri path) => inner.TryGetFolderFromPathAsync(path);
    public Task<IStorageFolder?> TryGetWellKnownFolderAsync(WellKnownFolder folder) => inner.TryGetWellKnownFolderAsync(folder);

    internal static Task<T> RunPickerAsync<T>(Func<Task<T>> show)
    {
        if (!OperatingSystem.IsWindows())
            return show();
        return Task.Factory.StartNew(show, CancellationToken.None, TaskCreationOptions.DenyChildAttach, PickerScheduler.Instance).Unwrap();
    }

    [SupportedOSPlatform("windows")]
    private sealed class PickerScheduler : TaskScheduler
    {
        internal static readonly PickerScheduler Instance = new();

        protected override void QueueTask(Task task)
        {
            // Avalonia 的选择器通过当前调度器创建 LongRunning 任务；嵌套任务也必须使用 STA。
            var thread = new Thread(() => TryExecuteTask(task)) { IsBackground = true, Name = "Windows file picker" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override IEnumerable<Task> GetScheduledTasks() => [];
    }
}
