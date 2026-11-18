using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Services.Application;

namespace TSCutter.GUI.Utils.Files;

/// <summary>
/// 输出位置所在的卷与当前用户可用的剩余空间。<see cref="VolumeId"/> 用于判断是否同一个卷
/// （同一个卷经盘符和挂载文件夹两种路径访问时相同），<see cref="VolumeRoot"/> 用于显示。
/// </summary>
internal readonly record struct DiskSpaceInfo(string VolumeId, string VolumeRoot, long AvailableBytes);

/// <summary>读取输出位置的剩余空间，并识别磁盘写满的错误。</summary>
internal static partial class DiskSpaceUtil
{
    // AVERROR(ENOSPC)：各平台的 ENOSPC 都是 28。
    internal const int FFmpegNoSpace = -28;

    /// <summary>
    /// 读取输出文件所在卷的可用空间；目标目录尚未创建时取最近的已存在上级目录。
    /// 网络路径不支持、权限不足等任何读取失败都返回 null，由调用方按“无法判断”处理。
    /// 可能访问网络，应在后台线程调用。
    /// </summary>
    public static DiskSpaceInfo? TryGetFreeSpace(string outputPath)
    {
        try
        {
            var directory = FindExistingDirectory(outputPath);
            if (directory is null)
                return null;
            return OperatingSystem.IsWindows() ? GetWindowsFreeSpace(directory) : GetMountFreeSpace(directory);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Free space was not read: {exception.Message}");
            return null;
        }
    }

    /// <summary>输出文件所在卷的标识，用于区分队列中写到不同磁盘的任务；读取失败时返回 null。</summary>
    public static string? TryGetVolumeId(string outputPath)
    {
        try
        {
            var directory = FindExistingDirectory(outputPath);
            if (directory is null)
                return null;
            return OperatingSystem.IsWindows() ? GetWindowsVolumeId(GetWindowsVolumeRoot(directory)) : FindMount(directory)?.Name;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Volume was not resolved: {exception.Message}");
            return null;
        }
    }

    /// <summary>异常（含内部异常）中是否有磁盘已满或磁盘配额用尽的写入错误。</summary>
    public static bool IsDiskFull(Exception? exception)
    {
        for (var depth = 0; exception is not null && depth < 16; depth++)
        {
            switch (exception)
            {
                case AggregateException aggregate:
                    return aggregate.InnerExceptions.Any(IsDiskFull);
                case IOException io when IsDiskFullCode(io.HResult):
                case FFmpegException { ErrorCode: FFmpegNoSpace }:
                    return true;
            }
            exception = exception.InnerException;
        }
        return false;
    }

    private static bool IsDiskFullCode(int hresult)
    {
        if (OperatingSystem.IsWindows())
            // ERROR_DISK_FULL、ERROR_HANDLE_DISK_FULL、ERROR_DISK_QUOTA_EXCEEDED。
            return hresult is unchecked((int)0x80070070) or unchecked((int)0x80070027) or unchecked((int)0x8007050F);
        // 其他系统的 IOException 以 errno 作为 HResult：ENOSPC 为 28，EDQUOT 在 macOS 为 69、Linux 为 122。
        return hresult == 28 || hresult == (OperatingSystem.IsMacOS() ? 69 : 122);
    }

    private static string? FindExistingDirectory(string outputPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        while (directory is not null && !Directory.Exists(directory))
            directory = Path.GetDirectoryName(directory);
        // Unix 按路径匹配挂载点，需先解析父目录的符号链接。
        return directory is null || OperatingSystem.IsWindows() ? directory : MediaFileUtil.CanonicalDirectory(directory);
    }

    [SupportedOSPlatform("windows")]
    private static DiskSpaceInfo? GetWindowsFreeSpace(string directory)
    {
        // 网络共享路径要求以分隔符结尾；返回的是当前用户可用的空间，已考虑磁盘配额。
        if (!GetDiskFreeSpaceEx(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar,
                out var available, out _, out _))
            return null;
        var root = GetWindowsVolumeRoot(directory);
        return new DiskSpaceInfo(GetWindowsVolumeId(root), root, (long)Math.Min(available, long.MaxValue));
    }

    // 卷根目录同时覆盖盘符、网络共享与挂载到文件夹的卷。
    [SupportedOSPlatform("windows")]
    private static unsafe string GetWindowsVolumeRoot(string directory)
    {
        const int capacity = 1024;
        var buffer = stackalloc char[capacity];
        return GetVolumePathName(directory, buffer, capacity)
            ? new string(buffer)
            : Path.GetPathRoot(directory) ?? directory;
    }

    // 本地卷取卷的 GUID 路径作为标识；网络共享等没有 GUID 路径时用卷根目录。
    [SupportedOSPlatform("windows")]
    private static unsafe string GetWindowsVolumeId(string root)
    {
        const int capacity = 64;
        var buffer = stackalloc char[capacity];
        return GetVolumeNameForVolumeMountPoint(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            buffer, capacity)
            ? new string(buffer)
            : root;
    }

    // 其他系统按最长匹配的挂载点读取。
    private static DiskSpaceInfo? GetMountFreeSpace(string directory) =>
        FindMount(directory) is { } mount ? new DiskSpaceInfo(mount.Name, mount.Name, mount.AvailableFreeSpace) : null;

    private static DriveInfo? FindMount(string directory) => DriveInfo.GetDrives()
        .Where(drive => DialogFolderMemory.IsUnder(directory, drive.Name))
        .MaxBy(drive => drive.Name.Length);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static partial bool GetDiskFreeSpaceEx(string directory, out ulong freeBytesAvailable,
        out ulong totalBytes, out ulong totalFreeBytes);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static unsafe partial bool GetVolumePathName(string fileName, char* volumePathName, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static unsafe partial bool GetVolumeNameForVolumeMountPoint(string volumeMountPoint, char* volumeName,
        uint bufferLength);
}
