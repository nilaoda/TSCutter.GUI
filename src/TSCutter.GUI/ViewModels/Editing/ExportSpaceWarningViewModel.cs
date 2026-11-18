using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using HanumanInstitute.MvvmDialogs;
using TSCutter.GUI.Services.Editing;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Files;

namespace TSCutter.GUI.ViewModels.Editing;

/// <summary>导出前目标磁盘剩余空间可能不足的提示：列出各卷需要与可用的空间，由用户选择取消或仍然导出。</summary>
public sealed class ExportSpaceWarningViewModel : ViewModelBase, IModalDialogViewModel
{
    public IReadOnlyList<string> Volumes { get; set; } = [];
    public bool? DialogResult { get; private set; }

    internal void Confirm() => DialogResult = true;

    private static readonly TimeSpan BusyDelay = TimeSpan.FromMilliseconds(200);

    // 同一卷上有多个输出（如队列）时附带文件数。
    internal static string FormatShortage(ExportSpaceShortage shortage) => string.Format(
        shortage.OutputCount > 1
            ? LocalizationManager.Instance.String_ExportSpace_VolumeFiles
            : LocalizationManager.Instance.String_ExportSpace_Volume,
        shortage.VolumeRoot, CommonUtil.FormatFileSize(shortage.RequiredBytes),
        CommonUtil.FormatFileSize(Math.Max(0, shortage.AvailableBytes)), shortage.OutputCount);

    /// <summary>
    /// 检查本次导出的目标磁盘空间；空间足够或无法判断时直接返回，不打扰用户。
    /// 不足时提示，用户选择仍然导出才返回 true。等待期间导出内容有变化（<paramref name="isCurrent"/> 为 false）时返回 false。
    /// </summary>
    internal static async Task<bool> ConfirmAsync(IDialogService dialogs, INotifyPropertyChanged owner,
        IReadOnlyList<ExportSizeTask> tasks, Func<string, double?>? sourceDuration, Func<bool>? isCurrent = null,
        Func<string, DiskSpaceInfo?>? probe = null)
    {
        var check = ExportSpaceCheck.FindShortagesAsync(tasks, sourceDuration, probe);
        // 读取较慢（如网络盘）时显示忙碌指针并暂时禁用窗口，避免等待期间重复发起导出。
        IDisposable? busy = null;
        if (await Task.WhenAny(check, Task.Delay(BusyDelay)) != check)
            busy = BusyCursor.ShowOnActiveWindow();
        IReadOnlyList<ExportSpaceShortage> shortages;
        try { shortages = await check; }
        finally { busy?.Dispose(); }
        if (!(isCurrent?.Invoke() ?? true))
            return false;
        if (shortages.Count == 0)
            return true;
        var warning = dialogs.CreateViewModel<ExportSpaceWarningViewModel>();
        warning.Volumes = shortages.Select(FormatShortage).ToArray();
        await dialogs.ShowDialogAsync(owner, warning);
        return warning.DialogResult == true && (isCurrent?.Invoke() ?? true);
    }
}
