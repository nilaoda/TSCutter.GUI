using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TSCutter.GUI.Models.Editing;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.Services.Editing;

/// <summary>
/// 上次未正常退出的实例留下的恢复文件，只保留提示所需的摘要；恢复时重新读取完整内容，
/// 提示期间不必把各份文件中的缩略图一直留在内存里。
/// </summary>
internal sealed record ProjectRecoveryEntry(string Path, AppInstanceIdentity Owner, DateTime SavedUtc,
    string? ProjectPath, string? SourcePath, int ClipCount, int QueueCount)
{
    public static ProjectRecoveryEntry Create(string path, AppInstanceIdentity owner, ProjectRecoveryFile file)
    {
        var project = file.Project;
        return new ProjectRecoveryEntry(path, owner, file.SavedUtc, file.ProjectPath,
            project.Source?.Path ?? project.Queue?.FirstOrDefault()?.Source?.Path,
            project.Clips?.Count ?? 0, project.Queue?.Count ?? 0);
    }
}

/// <summary>
/// 恢复文件的读写。每个实例只写自己的一份（文件名为实例标识），多开时互不覆盖；
/// 写入先落到同目录的临时文件并刷到磁盘，再整体替换，断电时不会留下写了一半的文件。
/// </summary>
internal sealed class ProjectRecoveryStore
{
    public const string Extension = ".tscut-recovery";
    private const string TemporaryExtension = ".tmp";
    // 工程文件上限 16 MB；恢复文件另含外层字段，读取时留出余量。
    private const int MaximumBytes = 32 * 1024 * 1024;

    private readonly AppInstanceIdentity _owner;
    private readonly Func<AppInstanceIdentity, bool> _isRunning;

    public ProjectRecoveryStore(string directory, AppInstanceIdentity owner, Func<AppInstanceIdentity, bool> isRunning)
    {
        DirectoryPath = directory;
        _owner = owner;
        _isRunning = isRunning;
    }

    /// <summary>
    /// 应用数据目录下的 Recovery 文件夹：Windows 为 %LOCALAPPDATA%\TSCutter.GUI\Recovery，
    /// macOS 为 ~/Library/Application Support/TSCutter.GUI/Recovery。
    /// </summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify),
        "TSCutter.GUI", "Recovery");

    public static ProjectRecoveryStore Default { get; } =
        new(DefaultDirectory, AppInstanceIdentity.Current, AppInstanceIdentity.IsRunning);

    public string DirectoryPath { get; }

    /// <summary>本实例的恢复文件。</summary>
    public string OwnFilePath => Path.Combine(DirectoryPath, _owner.FileKey + Extension);

    public static byte[] Serialize(ProjectRecoveryFile file) =>
        JsonSerializer.SerializeToUtf8Bytes(file, AppJsonContext.Default.ProjectRecoveryFile);

    /// <summary>替换本实例的恢复文件。在后台线程调用。</summary>
    public void Write(byte[] content)
    {
        if (content.Length > MaximumBytes)
            throw new InvalidDataException("The recovery snapshot is too large to save.");
        Directory.CreateDirectory(DirectoryPath);
        var temporaryPath = Path.Combine(DirectoryPath,
            $"{_owner.FileKey}.{Guid.NewGuid():N}{TemporaryExtension}");
        try
        {
            // 内容已整体在内存中，直接写入，不再经过文件流缓冲。
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, bufferSize: 0))
            {
                stream.Write(content);
                // 先确保内容落盘再替换，断电后看到的要么是旧文件，要么是完整的新文件。
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, OwnFilePath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    /// <summary>删除本实例的恢复文件；文件不存在时什么也不做。</summary>
    public void DeleteOwn() => TryDelete(OwnFilePath);

    public static ProjectRecoveryFile Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length > MaximumBytes)
            throw new NotSupportedException("The recovery file exceeds this version's read limit.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        // 先检查外层版本号。新版的工程结构可能无法按当前类型反序列化。
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(nameof(ProjectRecoveryFile.FormatVersion), out var version) &&
            version.ValueKind == JsonValueKind.Number && version.TryGetInt64(out var formatVersion) &&
            formatVersion > ProjectRecoveryFile.CurrentFormatVersion)
            throw new NotSupportedException($"Unsupported recovery format version: {formatVersion}.");
        var file = root.Deserialize(AppJsonContext.Default.ProjectRecoveryFile)
                   ?? throw new InvalidDataException("The recovery file is empty.");
        if (file.Project is null)
            throw new InvalidDataException("The recovery file has no project.");
        return file;
    }

    /// <summary>
    /// 列出已不在运行的实例留下的恢复文件，最近保存的在前。
    /// 损坏的文件顺带删除；更新版本程序写的文件和超过当前读取上限的文件保留不动。
    /// 已退出实例遗留的临时文件一并清理。
    /// </summary>
    public IReadOnlyList<ProjectRecoveryEntry> FindOrphans()
    {
        var result = new List<ProjectRecoveryEntry>();
        IEnumerable<string> files;
        try
        {
            if (!Directory.Exists(DirectoryPath))
                return result;
            files = Directory.EnumerateFiles(DirectoryPath).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Failed to list recovery files: {exception.Message}");
            return result;
        }

        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            var isRecovery = name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);
            var isTemporary = name.EndsWith(TemporaryExtension, StringComparison.OrdinalIgnoreCase);
            if (!isRecovery && !isTemporary)
                continue;
            var key = isRecovery ? name[..^Extension.Length] : name[..Math.Max(0, name.IndexOf('.'))];
            if (!AppInstanceIdentity.TryParseFileKey(key, out var owner) || owner == _owner || _isRunning(owner))
                continue;
            if (isTemporary)
            {
                TryDelete(path);
                continue;
            }
            try
            {
                result.Add(ProjectRecoveryEntry.Create(path, owner, Read(path)));
            }
            catch (NotSupportedException)
            {
                // 需要较新版本读取的快照保留不动。
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                Console.WriteLine($"Discarding unreadable recovery file: {exception.Message}");
                TryDelete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.WriteLine($"Failed to read recovery file: {exception.Message}");
            }
        }
        return result.OrderByDescending(entry => entry.SavedUtc).ToList();
    }

    /// <summary>
    /// 把一份遗留的恢复文件改名为本实例的恢复文件。另一个实例同时认领时只有一方成功。
    /// 恢复成功后由本实例继续维护；恢复未完成时用 <see cref="Release"/> 放回原处。
    /// </summary>
    public bool Claim(ProjectRecoveryEntry entry)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            // 不覆盖本实例已有的恢复文件，以免丢掉当前未保存的内容。
            File.Move(entry.Path, OwnFilePath, overwrite: false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Failed to claim recovery file: {exception.Message}");
            return false;
        }
    }

    /// <summary>把认领的恢复文件放回原处，下次启动时仍会提示。</summary>
    public void Release(ProjectRecoveryEntry entry)
    {
        try
        {
            if (File.Exists(OwnFilePath))
                File.Move(OwnFilePath, entry.Path, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Failed to release recovery file: {exception.Message}");
        }
    }

    /// <summary>删除一份遗留的恢复文件。</summary>
    public static void Discard(ProjectRecoveryEntry entry) => TryDelete(entry.Path);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"Failed to delete recovery file: {exception.Message}");
        }
    }
}
