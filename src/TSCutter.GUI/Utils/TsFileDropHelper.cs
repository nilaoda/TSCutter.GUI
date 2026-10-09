using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TSCutter.GUI.Utils;

internal static class TsFileDropHelper
{
    public static StringComparer PathComparer => OperatingSystem.IsLinux()
        ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public static bool HasTsExtension(string path) =>
        string.Equals(Path.GetExtension(path), ".ts", StringComparison.OrdinalIgnoreCase);

    public static bool IsSupportedPath(string path) =>
        Directory.Exists(path) || File.Exists(path) && HasTsExtension(path);

    // 文件夹只枚举第一层；调用方在后台执行，避免网络目录阻塞界面。
    public static string[] ExpandPaths(IReadOnlyList<string> paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (File.Exists(path) && HasTsExtension(path))
            {
                files.Add(path);
                continue;
            }
            if (!Directory.Exists(path)) continue;
            try
            {
                files.AddRange(Directory.EnumerateFiles(path)
                    .Where(HasTsExtension)
                    .OrderBy(item => Path.GetFileName(item) ?? string.Empty, NaturalStringComparer.Instance)
                    .ThenBy(item => item, NaturalStringComparer.Instance));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return files.ToArray();
    }
}
