using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TSCutter.GUI.Models.Discs;
using TSCutter.GUI.Utils.Files;

namespace TSCutter.GUI.Models.Application;

public enum RecentItemKind
{
    Video,
    Disc,
    Project
}

/// <summary>
/// 最近打开的一项：视频文件、光盘（镜像或文件夹）或工程。
/// 光盘额外记住上次选择的标题，重新打开时在标题列表中预先选中；工程记住保存或打开时的片段数。
/// </summary>
public sealed class RecentItem
{
    public RecentItemKind Kind { get; set; }
    public string Path { get; set; } = string.Empty;
    public DateTime LastOpenedUtc { get; set; }
    // 光盘：类型、所选标题的读取路径（镜像或光盘根目录）与标题号（蓝光为播放列表编号）。
    public DiscKind? DiscKind { get; set; }
    public string? DiscInputPath { get; set; }
    public int? DiscTitle { get; set; }
    // 工程：打开或保存时的片段数。
    public int? ClipCount { get; set; }

    public RecentItem Clone() => (RecentItem)MemberwiseClone();
}

/// <summary>最近打开列表的规则：同一路径只保留一项并移到最前，最多保留 <see cref="MaxCount"/> 项。</summary>
public static class RecentItemList
{
    public const int MaxCount = 10;

    /// <summary>把新打开的一项放到最前；返回新的列表，不修改传入的列表。</summary>
    public static List<RecentItem> Record(IEnumerable<RecentItem>? items, RecentItem item)
    {
        var added = item.Clone();
        added.Path = NormalizePath(added.Path);
        var result = new List<RecentItem> { added };
        result.AddRange(Normalize(items).Where(existing => !SamePath(existing.Path, added.Path)));
        return result.Take(MaxCount).ToList();
    }

    public static List<RecentItem> Remove(IEnumerable<RecentItem>? items, string path) =>
        Normalize(items).Where(existing => !SamePath(existing.Path, path)).ToList();

    /// <summary>
    /// 读入配置时整理：丢弃空路径与未知类型，去掉重复项（保留靠前的一项），最多保留 <see cref="MaxCount"/> 项。
    /// 沿用保存时的顺序而不按时间重排，系统时间被调整过时顺序也不会乱。
    /// </summary>
    public static List<RecentItem> Normalize(IEnumerable<RecentItem>? items)
    {
        var result = new List<RecentItem>();
        if (items is null)
            return result;
        foreach (var item in items.Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Path) &&
                                                 Enum.IsDefined(item.Kind)))
        {
            var copy = item.Clone();
            copy.Path = NormalizePath(copy.Path);
            if (result.Any(existing => SamePath(existing.Path, copy.Path)))
                continue;
            result.Add(copy);
            if (result.Count == MaxCount)
                break;
        }
        return result;
    }

    public static bool SamePath(string first, string second) =>
        MediaFileUtil.PathComparer.Equals(NormalizePath(first), NormalizePath(second));

    // 只做字符串层面的整理，不访问文件系统：离线的网络盘或移动硬盘不会拖慢启动。
    private static string NormalizePath(string path)
    {
        try
        {
            var fullPath = System.IO.Path.GetFullPath(path.Trim());
            return System.IO.Path.GetPathRoot(fullPath) == fullPath
                ? fullPath
                : System.IO.Path.TrimEndingDirectorySeparator(fullPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }
}
