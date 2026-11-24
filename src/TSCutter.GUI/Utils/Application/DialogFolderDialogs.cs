using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using HanumanInstitute.MvvmDialogs;
using HanumanInstitute.MvvmDialogs.FileSystem;
using HanumanInstitute.MvvmDialogs.FrameworkDialogs;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Utils.Files;

namespace TSCutter.GUI.Utils.Application;

/// <summary>
/// 打开类与保存类对话框的统一入口：先按记忆的目录定位，再弹出对话框，确认后记住所选目录。
/// 定位时可能要等休眠的硬盘醒来，期间所属窗口若已关闭就不再弹出，按取消处理。
/// </summary>
internal static class DialogFolderDialogs
{
    public static async Task<IReadOnlyList<IDialogStorageFile>> ShowOpenFilesAtImportFolderAsync(
        this IDialogService dialogs, INotifyPropertyChanged owner, OpenFileDialogSettings settings)
    {
        settings.SuggestedStartLocation = await DialogFolderMemory.Shared.ImportStartAsync();
        var files = await ShowAsync(() => dialogs.ShowOpenFilesDialogAsync(owner, settings), []);
        // 多选时文件都在同一目录，取第一个即可。
        DialogFolderMemory.Shared.RememberImport(files.FirstOrDefault()?.LocalPath);
        return files;
    }

    public static async Task<IDialogStorageFolder?> ShowOpenFolderAtImportFolderAsync(
        this IDialogService dialogs, INotifyPropertyChanged owner, OpenFolderDialogSettings settings)
    {
        settings.SuggestedStartLocation = await DialogFolderMemory.Shared.ImportStartAsync();
        var folder = await ShowAsync(() => dialogs.ShowOpenFolderDialogAsync(owner, settings), null);
        DialogFolderMemory.Shared.RememberImport(folder?.LocalPath);
        return folder;
    }

    /// <param name="defaultFolder">没有导出记忆或按设置优先时使用的目录，通常是源文件所在目录。</param>
    public static async Task<IDialogStorageFile?> ShowSaveFileAtExportFolderAsync(
        this IDialogService dialogs, INotifyPropertyChanged owner, SaveFileDialogSettings settings, string? defaultFolder)
    {
        settings.SuggestedStartLocation = await DialogFolderMemory.Shared.ExportStartAsync(defaultFolder);
        var file = await ShowAsync(() => dialogs.ShowSaveFileDialogAsync(owner, settings), null);
        DialogFolderMemory.Shared.RememberExport(file?.LocalPath);
        return file;
    }

    /// <param name="defaultFolder">没有导出记忆或按设置优先时使用的目录，通常是当前输出文件夹。</param>
    public static async Task<IDialogStorageFolder?> ShowOpenFolderAtExportFolderAsync(
        this IDialogService dialogs, INotifyPropertyChanged owner, OpenFolderDialogSettings settings, string? defaultFolder)
    {
        settings.SuggestedStartLocation = MediaFileAccess.Current.DefaultOutputDirectory is { } outputDirectory
            ? new DesktopDialogStorageFolder(outputDirectory)
            : await DialogFolderMemory.Shared.ExportStartAsync(defaultFolder);
        var folder = await ShowAsync(() => dialogs.ShowOpenFolderDialogAsync(owner, settings), null);
        DialogFolderMemory.Shared.RememberExportFolder(folder?.LocalPath);
        return folder;
    }

    // 所属窗口已关闭时对话框服务找不到它，会抛出 ViewNotRegisteredException。
    private static async Task<T> ShowAsync<T>(Func<Task<T>> show, T cancelled)
    {
        try
        {
            return await show();
        }
        catch (ViewNotRegisteredException)
        {
            return cancelled;
        }
    }
}
