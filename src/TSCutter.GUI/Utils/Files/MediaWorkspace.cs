using System;
using System.IO;

namespace TSCutter.GUI.Utils.Files;

internal enum MediaWorkspaceError
{
    OverlappingDirectories, UnsafeDirectory
}

// 文件规则只报告错误类型，由调用界面选择对应语言的提示。
internal sealed class MediaWorkspaceException(MediaWorkspaceError error, string message) : IOException(message)
{
    internal MediaWorkspaceError Error { get; } = error;
}

internal sealed class MediaWorkspace
{
    public MediaWorkspace(string inputDirectory, string outputDirectory)
    {
        InputDirectory = GetRoot(inputDirectory);
        OutputDirectory = GetRoot(outputDirectory);
        DemandSeparateDirectories(InputDirectory, OutputDirectory);
    }

    public string InputDirectory
    {
        get;
    }
    public string OutputDirectory
    {
        get;
    }

    internal static void DemandSeparateDirectories(string input, string output)
    {
        if (MediaFileUtil.PathsEqual(input, output) || MediaFileUtil.IsWithinDirectory(input, output) ||
            MediaFileUtil.IsWithinDirectory(output, input))
            throw new MediaWorkspaceException(MediaWorkspaceError.OverlappingDirectories, "Choose separate input and output directories.");
    }

    public string? ResolveInput(string relativePath) => Resolve(InputDirectory, relativePath, mustExist: true);

    public string? ResolveOutput(string relativePath) => Resolve(OutputDirectory, relativePath, mustExist: false);

    // 文件缺失交给读取方处理，工程才能提示用户重新定位共享目录内的来源。
    public bool AllowsInput(string path) => AllowsPath(InputDirectory, path, mustExist: false);

    public bool AllowsOutput(string path) => AllowsPath(OutputDirectory, path, mustExist: false);

    public bool AllowsOutputDirectory(string path) => Directory.Exists(path) &&
        (string.Equals(path, OutputDirectory, StringComparison.Ordinal) ? IsSafeDirectory(path) : AllowsOutput(path));

    private static bool AllowsPath(string root, string path, bool mustExist)
    {
        if (!IsNormalized(path) || !IsWithin(root, path) || path == root)
            return false;
        // 比对实际要打开的路径，避免宽松的相对路径比较把两个不同目录当成同一个。
        return string.Equals(Resolve(root, Path.GetRelativePath(root, path), mustExist), path, StringComparison.Ordinal);
    }

    private static bool IsNormalized(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path) && Path.GetFullPath(path) == path;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsWithin(string root, string path)
    {
        if (string.Equals(root, path, StringComparison.Ordinal))
            return true;
        if (!Path.EndsInDirectorySeparator(root))
            root += Path.DirectorySeparatorChar;
        return path.Length > root.Length && path.StartsWith(root, StringComparison.Ordinal);
    }

    private static string GetRoot(string path)
    {
        var directory = new DirectoryInfo(MediaFileUtil.CanonicalDirectory(Path.GetFullPath(path)));
        if (!directory.Exists)
            throw new DirectoryNotFoundException(directory.FullName);
        if (!IsSafeDirectory(directory.FullName))
            throw new MediaWorkspaceException(MediaWorkspaceError.UnsafeDirectory, "A Web UI workspace directory cannot be a symbolic link or junction.");
        return Path.TrimEndingDirectorySeparator(directory.FullName);
    }

    private static bool IsSafeDirectory(string path)
    {
        try
        {
            for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            {
                if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? Resolve(string root, string relativePath, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || !IsSafeDirectory(root))
            return null;

        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = root;
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            if (part.Length == 0 || part is "." or ".." || part.Contains(':') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                (OperatingSystem.IsWindows() && (part.EndsWith('.') || part.EndsWith(' '))))
                return null;

            path = Path.Combine(path, part);
            var last = index == parts.Length - 1;
            try
            {
                var attributes = File.GetAttributes(path);
                if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    (!last && !attributes.HasFlag(FileAttributes.Directory)))
                    return null;
            }
            catch (IOException error) when (!mustExist && error is FileNotFoundException or DirectoryNotFoundException)
            {
                // 不存在的后缀仍逐段检查名称，实际存在性由读取或创建操作决定。
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
        return path;
    }
}
