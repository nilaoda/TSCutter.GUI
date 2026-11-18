using System.Collections.Generic;
using System.IO;
using System.Linq;
using TSCutter.GUI.Utils.Files;

namespace TSCutter.GUI.Models.Application;

/// <summary>
/// 对话框记忆目录的字符串规则。只整理字符串，不访问文件系统，离线的网络盘或移动硬盘不会拖慢界面。
/// </summary>
public static class DialogFolderPaths
{
    /// <summary>整理为完整路径并去掉末尾分隔符（盘符根目录保留）；空白返回 null。</summary>
    public static string? Normalize(string? folder) =>
        string.IsNullOrWhiteSpace(folder) ? null : RecentItemList.NormalizePath(folder);

    /// <summary>
    /// 一个打开或保存过的路径应记住的目录：文件、光盘镜像和光盘文件夹都取所在的上级目录，
    /// 下次对话框打开时能直接看到它；已经是盘符根目录时取它本身。
    /// </summary>
    public static string? ContainingFolder(string? path)
    {
        var normalized = Normalize(path);
        if (normalized is null)
            return null;
        var parent = Path.GetDirectoryName(normalized);
        return string.IsNullOrEmpty(parent) ? normalized : parent;
    }

    /// <summary>按先后顺序整理候选目录，去掉空白和重复项。</summary>
    public static List<string> Candidates(params string?[] folders)
    {
        var result = new List<string>();
        foreach (var folder in folders.Select(Normalize))
        {
            if (folder is not null && !result.Any(existing => MediaFileUtil.PathComparer.Equals(existing, folder)))
                result.Add(folder);
        }
        return result;
    }
}
