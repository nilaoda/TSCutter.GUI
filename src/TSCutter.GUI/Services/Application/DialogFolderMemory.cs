using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HanumanInstitute.MvvmDialogs.FileSystem;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Utils.Files;

namespace TSCutter.GUI.Services.Application;

/// <summary>检查记忆目录是否可用时的等待规则；单元测试可替换检查函数并缩短时间。</summary>
internal sealed record DialogFolderProbeOptions
{
    // 本地盘（含 USB 外置机械硬盘）从休眠唤醒可能要十秒左右；对话框打开到那里本来也要等，基本等它醒来。
    public TimeSpan LocalTimeout { get; init; } = TimeSpan.FromSeconds(15);
    // 已断开的网络共享可能二三十秒才报错，只等较短时间就回退。
    public TimeSpan NetworkTimeout { get; init; } = TimeSpan.FromSeconds(3);
    // 检查超过这个时间仍未返回时显示忙碌指针，避免一闪而过。
    public TimeSpan BusyDelay { get; init; } = TimeSpan.FromMilliseconds(200);
    public Func<string, bool> FolderExists { get; init; } = Directory.Exists;
    public Func<string, bool> IsNetworkPath { get; init; } = DialogFolderMemory.IsNetworkPath;
    // 返回值在等待结束时释放；为 null 时不显示忙碌状态。
    public Func<IDisposable?>? ShowBusy { get; init; }
}

/// <summary>
/// 分别记住打开类（导入）和保存类（导出）对话框上次所在的目录，两者互不影响，跨重启保存在配置中。
/// 目录不存在或检查超时时按顺序回退，但不清除记住的值，移动硬盘或网络盘恢复后仍可继续使用。
/// </summary>
public sealed class DialogFolderMemory
{
    private readonly Func<AppConfig> _config;
    private readonly Action? _save;
    private readonly DialogFolderProbeOptions _probe;
    private readonly Lock _gate = new();
    // 本次运行中检查超时的目录，只在内存中保存，重启后重新按路径类型判断。
    private readonly HashSet<string> _timedOutFolders = new(MediaFileUtil.PathComparer);
    private readonly Dictionary<string, PendingProbe> _pendingProbes = new(MediaFileUtil.PathComparer);

    private sealed record PendingProbe(Task<bool> IsNetwork, Task<bool> Exists);

    /// <summary>启动后接到应用配置；在此之前（例如单元测试）只记在一份不写盘的配置里。</summary>
    public static DialogFolderMemory Shared { get; set; } = new(new AppConfig());

    public DialogFolderMemory(IConfigurationService configuration, Func<IDisposable?>? showBusy = null)
        : this(() => configuration.CurrentConfig, configuration.Save, new DialogFolderProbeOptions { ShowBusy = showBusy })
    {
    }

    public DialogFolderMemory(AppConfig config) : this(() => config, null)
    {
    }

    internal DialogFolderMemory(Func<AppConfig> config, Action? save, DialogFolderProbeOptions? probe = null)
    {
        _config = config;
        _save = save;
        _probe = probe ?? new DialogFolderProbeOptions();
    }

    /// <summary>打开类对话框的起始目录：上次导入的目录，不可用时交给系统决定。</summary>
    public async Task<IDialogStorageFolder?> ImportStartAsync() =>
        ToDialogFolder(await ResolveImportAsync());

    /// <summary>
    /// 保存类对话框的起始目录。默认先用上次导出的目录，再用 <paramref name="defaultFolder"/>（通常是源文件所在目录）；
    /// 设置为“源文件所在目录”时两者顺序对调。都不可用时交给系统决定。
    /// </summary>
    public async Task<IDialogStorageFolder?> ExportStartAsync(string? defaultFolder) =>
        ToDialogFolder(await ResolveExportAsync(defaultFolder));

    internal Task<string?> ResolveImportAsync() =>
        FirstExistingAsync(DialogFolderPaths.Candidates(_config().LastImportFolder));

    internal Task<string?> ResolveExportAsync(string? defaultFolder)
    {
        var config = _config();
        return FirstExistingAsync(config.SaveDialogsStartInSourceFolder
            ? DialogFolderPaths.Candidates(defaultFolder, config.LastExportFolder)
            : DialogFolderPaths.Candidates(config.LastExportFolder, defaultFolder));
    }

    /// <summary>记住打开的文件、光盘镜像或光盘文件夹所在的上级目录。</summary>
    public void RememberImport(string? path) => Remember(DialogFolderPaths.ContainingFolder(path), export: false);

    /// <summary>记住保存的文件所在的目录。</summary>
    public void RememberExport(string? filePath) => Remember(DialogFolderPaths.ContainingFolder(filePath), export: true);

    /// <summary>记住选择的输出文件夹本身。</summary>
    public void RememberExportFolder(string? folder) => Remember(DialogFolderPaths.Normalize(folder), export: true);

    // folder 已整理过；与记住的值相同时不重复写配置。
    private void Remember(string? folder, bool export)
    {
        var config = _config();
        if (folder is null ||
            MediaFileUtil.PathComparer.Equals(export ? config.LastExportFolder : config.LastImportFolder, folder))
            return;
        if (export)
            config.LastExportFolder = folder;
        else
            config.LastImportFolder = folder;
        _save?.Invoke();
    }

    private async Task<string?> FirstExistingAsync(IReadOnlyList<string> candidates)
    {
        IDisposable? busy = null;
        try
        {
            foreach (var candidate in candidates)
            {
                if (await ExistsAsync(candidate, () => busy ??= _probe.ShowBusy?.Invoke()))
                    return candidate;
            }
            return null;
        }
        finally
        {
            busy?.Dispose();
        }
    }

    // 判断路径类型和检查是否存在都放在后台线程，避免离线的网络盘或休眠的硬盘卡住界面。
    // 先按较短的网络超时等待；已确认是本地盘时继续等到本地超时。超时的检查留在后台自行结束。
    // 本次运行中超时过的目录只按网络超时等待：挂载成本地盘的网络存储离线时，最多只白等一次本地超时。
    private async Task<bool> ExistsAsync(string folder, Action showBusy)
    {
        bool timedOutBefore;
        lock (_gate)
            timedOutBefore = _timedOutFolders.Contains(folder);
        var probe = StartProbe(folder);
        if (await WaitAsync(probe.Exists, _probe.BusyDelay))
            return Answered(folder, probe.Exists.Result);
        showBusy();
        if (await WaitAsync(probe.Exists, _probe.NetworkTimeout - _probe.BusyDelay))
            return Answered(folder, probe.Exists.Result);
        if (timedOutBefore || !probe.IsNetwork.IsCompletedSuccessfully || probe.IsNetwork.Result)
            return TimedOut(folder);
        return await WaitAsync(probe.Exists, _probe.LocalTimeout - _probe.NetworkTimeout)
            ? Answered(folder, probe.Exists.Result)
            : TimedOut(folder);
    }

    // 同一目录同时只保留一个后台检查：目录卡住时反复打开对话框也不会占用越来越多的线程。
    // 检查结束即移除，不缓存结果，下次重新检查。检查失败不应妨碍打开对话框，任何异常都按不可用处理。
    private PendingProbe StartProbe(string folder)
    {
        lock (_gate)
        {
            if (_pendingProbes.TryGetValue(folder, out var pending) && !pending.Exists.IsCompleted)
                return pending;
            var isNetwork = Task.Run(() =>
            {
                try
                {
                    return _probe.IsNetworkPath(folder);
                }
                catch (Exception)
                {
                    return true;
                }
            });
            var exists = isNetwork.ContinueWith(_ =>
            {
                try
                {
                    return _probe.FolderExists(folder);
                }
                catch (Exception)
                {
                    return false;
                }
            }, TaskScheduler.Default);
            var probe = new PendingProbe(isNetwork, exists);
            _pendingProbes[folder] = probe;
            exists.ContinueWith(_ =>
            {
                lock (_gate)
                {
                    if (_pendingProbes.TryGetValue(folder, out var current) && ReferenceEquals(current, probe))
                        _pendingProbes.Remove(folder);
                }
            }, TaskScheduler.Default);
            return probe;
        }
    }

    // 及时返回说明目录所在的存储已恢复，之后重新按路径类型等待。
    private bool Answered(string folder, bool exists)
    {
        lock (_gate)
            _timedOutFolders.Remove(folder);
        return exists;
    }

    private bool TimedOut(string folder)
    {
        lock (_gate)
            _timedOutFolders.Add(folder);
        return false;
    }

    /// <summary>
    /// 是否为网络路径：UNC 路径、映射的网络驱动器，或挂载在网络文件系统上的目录。
    /// 只读取系统的驱动器或挂载信息，不访问目录本身；在后台线程调用。
    /// </summary>
    internal static bool IsNetworkPath(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            if (folder.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                return true;
            // \\?\C:\ 与 \\.\C:\ 是本地路径的设备写法，去掉前缀后按盘符判断。
            if (folder.StartsWith(@"\\?\", StringComparison.Ordinal) || folder.StartsWith(@"\\.\", StringComparison.Ordinal))
                folder = folder[4..];
            else if (folder.StartsWith(@"\\", StringComparison.Ordinal) || folder.StartsWith("//", StringComparison.Ordinal))
                return true;
            var root = Path.GetPathRoot(folder);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        // 其他系统按最长匹配的挂载点判断。
        var mount = DriveInfo.GetDrives()
            .Where(drive => IsUnder(folder, drive.Name))
            .MaxBy(drive => drive.Name.Length);
        return mount?.DriveType == DriveType.Network;
    }

    internal static bool IsUnder(string folder, string mountPoint)
    {
        var root = Path.TrimEndingDirectorySeparator(mountPoint);
        // macOS 的卷默认不区分大小写，路径与挂载点的大小写可能不一致。
        var comparison = OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return root == "/" || folder.Equals(root, comparison) ||
               folder.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    // 检查完成时计时器随即释放，不必等到超时。
    private static async Task<bool> WaitAsync(Task task, TimeSpan timeout)
    {
        if (!task.IsCompleted && timeout > TimeSpan.Zero)
        {
            try
            {
                await task.WaitAsync(timeout);
            }
            catch (TimeoutException)
            {
            }
        }
        return task.IsCompleted;
    }

    private static IDialogStorageFolder? ToDialogFolder(string? folder) =>
        folder is null ? null : new DesktopDialogStorageFolder(folder);
}
