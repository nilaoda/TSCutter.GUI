using System;
using System.IO;
using System.Text;

namespace TSCutter.GUI.Utils.Files;

internal static class MediaFileUtil
{
    // macOS 通常使用不区分大小写的文件系统；为避免覆盖源文件，
    // 即使卷区分大小写，也保守拒绝仅大小写不同的源路径和输出路径。
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static bool PathsEqual(string first, string second) =>
        PathComparer.Equals(CanonicalPath(first), CanonicalPath(second));

    // 光盘目录中的 VOB/M2TS/导航文件也属于源数据，不能作为其他队列项的输出。
    internal static bool ConflictsWithSource(string output, string source) =>
        PathsEqual(output, source) || Directory.Exists(source) && IsWithinDirectory(output, source);

    internal static bool IsWithinDirectory(string path, string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(CanonicalDirectory(Path.GetFullPath(directory)));
        if (OperatingSystem.IsMacOS())
            root = root.Normalize(NormalizationForm.FormC);
        if (!Path.EndsInDirectorySeparator(root))
            root += Path.DirectorySeparatorChar;
        var target = CanonicalPath(path);
        return target.Length > root.Length && PathComparer.Equals(target[..root.Length], root);
    }

    internal static string CanonicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath) && new FileInfo(fullPath).ResolveLinkTarget(true) is { } target)
            return CanonicalPath(target.FullName);
        var parent = Path.GetDirectoryName(fullPath);
        var canonicalPath = parent is null ? fullPath : Path.Combine(CanonicalDirectory(parent), Path.GetFileName(fullPath));
        return OperatingSystem.IsMacOS() ? canonicalPath.Normalize(NormalizationForm.FormC) : canonicalPath;
    }

    private static string CanonicalDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        var parent = directory.Parent;
        // Windows 的盘符根目录和 UNC 共享根目录不能按普通目录查询链接目标。
        if (parent is null) return directory.FullName;
        if (directory.Exists && directory.ResolveLinkTarget(true) is { } target)
            return CanonicalDirectory(target.FullName);
        return Path.Combine(CanonicalDirectory(parent.FullName), directory.Name);
    }

    /// <summary>
    /// 以只读方式打开媒体源文件，允许其他读取以及移动、重命名和删除。
    /// 仅在需要读取正在写入的文件时显式允许并发写入；调用方负责释放返回的流。
    /// </summary>
    public static FileStream OpenRead(string path, int bufferSize = 4096,
        FileOptions options = FileOptions.None, bool allowConcurrentWrite = false)
    {
        var share = FileShare.Read | FileShare.Delete;
        if (allowConcurrentWrite)
            share |= FileShare.Write;
        return new FileStream(path, FileMode.Open, FileAccess.Read, share, bufferSize, options);
    }
}
