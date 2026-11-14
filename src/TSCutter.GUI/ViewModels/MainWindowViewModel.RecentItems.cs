using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using TSCutter.GUI.Models.Application;
using TSCutter.GUI.Models.Discs;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.ViewModels;

/// <summary>“文件 → 最近打开”中的一项。</summary>
public sealed class RecentItemRow(RecentItem item, int index, bool exists, long? fileSize)
{
    public RecentItem Item { get; } = item;
    public bool IsMissing { get; } = !exists;
    public string Detail { get; } = exists ? DescribeDetail(item, fileSize) : LocalizationManager.Instance.String_Recent_Missing;
    public string TimeText { get; } = FormatTime(item.LastOpenedUtc);
    // 悬停提示第二行：详情与打开时间，详情为空时不留多余的分隔符。
    public string Summary => string.Join(" · ", new[] { Detail, TimeText }.Where(text => text.Length > 0));
    // 菜单前 9 项带数字访问键；文件名中的下划线需要转义，否则会被当作访问键。
    public string MenuHeader { get; } = (index < 9 ? $"_{index + 1} " : "   ") + DisplayName(item.Path).Replace("_", "__") +
                                        (item.Kind == RecentItemKind.Disc ? $"  ({LocalizationManager.Instance.String_Recent_KindDisc})" : "");
    public string ToolTip => $"{Item.Path}\n{Summary}";

    private static string DisplayName(string path) => Path.GetFileName(path) is { Length: > 0 } name ? name : path;

    private static string DescribeDetail(RecentItem item, long? fileSize) => item.Kind switch
    {
        RecentItemKind.Project when item.ClipCount is { } count =>
            string.Format(LocalizationManager.Instance.String_Recent_ClipCount, count),
        RecentItemKind.Disc when item.DiscTitle is { } title => item.DiscKind == DiscKind.BluRay
            ? $"{LocalizationManager.Instance.String_Disc_KindBluRay} · {title:D5}.mpls"
            : $"{LocalizationManager.Instance.String_Disc_KindDvd} · {string.Format(LocalizationManager.Instance.String_Recent_DiscTitle, title)}",
        RecentItemKind.Video when fileSize is { } size => CommonUtil.FormatFileSize(size),
        _ => string.Empty
    };

    private static string FormatTime(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var today = DateTime.Today;
        var time = local.ToString("t", CultureInfo.CurrentCulture);
        if (local.Date == today)
            return string.Format(LocalizationManager.Instance.String_Recent_Today, time);
        if (local.Date == today.AddDays(-1))
            return string.Format(LocalizationManager.Instance.String_Recent_Yesterday, time);
        return local.ToString("d", CultureInfo.CurrentCulture);
    }
}

public partial class MainWindowViewModel
{
    private int _recentRefreshVersion;

    public ObservableCollection<RecentItemRow> RecentItems { get; } = [];
    public bool HasRecentItems => RecentItems.Count > 0;
    // 未打开文件时在黑色预览区显示打开提示，打开或加载中隐藏。
    public bool ShowRecentStartPage => !HasCurrentFile && !_isLoadingVideo;
    public string StartShortcutHint => string.Format(LocalizationManager.Instance.String_Recent_StartShortcut,
        OperatingSystem.IsMacOS() ? "⌘O" : "Ctrl+O");

    /// <summary>
    /// 按配置重新生成列表。检查文件是否存在放在后台线程：离线的网络盘可能要等很久才返回，不能卡住界面。
    /// </summary>
    internal async Task RefreshRecentItemsAsync()
    {
        var version = ++_recentRefreshVersion;
        var items = _configService.CurrentConfig.RememberRecentItems
            ? _configService.CurrentConfig.RecentItems.Select(item => item.Clone()).ToList()
            : [];
        var states = await Task.Run(() => items.Select(ProbeRecentItem).ToList());
        if (version != _recentRefreshVersion)
            return;
        RecentItems.Clear();
        for (var index = 0; index < items.Count; index++)
            RecentItems.Add(new RecentItemRow(items[index], index, states[index].Exists, states[index].Size));
        OnPropertyChanged(nameof(HasRecentItems));
        OnPropertyChanged(nameof(StartShortcutHint));
        ClearRecentItemsCommand.NotifyCanExecuteChanged();
    }

    private static (bool Exists, long? Size) ProbeRecentItem(RecentItem item)
    {
        try
        {
            if (File.Exists(item.Path))
                return (true, new FileInfo(item.Path).Length);
            return (item.Kind == RecentItemKind.Disc && Directory.Exists(item.Path), null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return (false, null);
        }
    }

    private void RecordRecentItem(RecentItem item)
    {
        var config = _configService.CurrentConfig;
        if (!config.RememberRecentItems)
            return;
        item.LastOpenedUtc = DateTime.UtcNow;
        config.RecentItems = RecentItemList.Record(config.RecentItems, item);
        _configService.Save();
        _ = RefreshRecentItemsAsync();
    }

    private void RecordRecentProject(string projectPath, int clipCount) =>
        RecordRecentItem(new RecentItem { Kind = RecentItemKind.Project, Path = projectPath, ClipCount = clipCount });

    private void ForgetRecentItem(string path)
    {
        var config = _configService.CurrentConfig;
        config.RecentItems = RecentItemList.Remove(config.RecentItems, path);
        _configService.Save();
        _ = RefreshRecentItemsAsync();
    }

    [RelayCommand(CanExecute = nameof(CanLoadVideo))]
    private async Task OpenRecentItemAsync(RecentItemRow? row)
    {
        if (row is null || _isLoadingVideo)
            return;
        var item = row.Item;
        var exists = await Task.Run(() => ProbeRecentItem(item).Exists);
        if (!exists)
        {
            if (await ShowConfirmAsync(string.Format(LocalizationManager.Instance.String_Recent_MissingConfirm, item.Path)))
                ForgetRecentItem(item.Path);
            else
                _ = RefreshRecentItemsAsync();
            return;
        }
        switch (item.Kind)
        {
            case RecentItemKind.Project:
                await OpenProjectPathAsync(item.Path);
                break;
            case RecentItemKind.Disc:
                await OpenDiscAsync(item.Path, item);
                break;
            default:
                await OpenVideoPathAsync(item.Path);
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(HasRecentItems))]
    private void ClearRecentItems()
    {
        _configService.CurrentConfig.RecentItems = [];
        _configService.Save();
        _ = RefreshRecentItemsAsync();
    }
}
