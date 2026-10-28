using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Utils.Application;
using TSCutter.GUI.Utils.Files;

namespace TSCutter.GUI.Utils;

public static partial class CommonUtil
{
    [GeneratedRegex(@"^\d+:\d+:\d+\.\d+$")]
    private static partial Regex TimeStrRegex();

    public static string FormatSeconds(double seconds, bool forFile = false)
    {
        var timeSpan = TimeSpan.FromSeconds(seconds);
        var ch = forFile ? '.' : ':';
        return $"{timeSpan.Hours:D2}{ch}{timeSpan.Minutes:D2}{ch}{timeSpan.Seconds:D2}.{timeSpan.Milliseconds:D3}";
    }

    public static bool TryParseFormattedTime(string input, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(input) || !TimeStrRegex().IsMatch(input))
            return false;

        char ch = input.Contains('.') && !input.Contains(':') ? '.' : ':';
        var parts = input.Split(ch);

        if (parts.Length != 3 || !parts[2].Contains('.'))
            return false;

        var lastParts = parts[2].Split('.');
        if (lastParts.Length != 2)
            return false;

        if (int.TryParse(parts[0], out int hours) &&
            int.TryParse(parts[1], out int minutes) &&
            int.TryParse(lastParts[0], out int secondsPart) &&
            int.TryParse(lastParts[1], out int milliseconds))
        {
            seconds = new TimeSpan(0, hours, minutes, secondsPart, milliseconds).TotalSeconds;
            return true;
        }

        return false;
    }

    public static Task CopyFileAsync(FileInfo startFile, string destinationFile, long start = 0, long end = -1, Action<double, long>? progress = null, CancellationToken cancellationToken = default, ExportTsCheckSession? checkSession = null) =>
        Task.Run(() => CopyFileCoreAsync(startFile, destinationFile, start, end, progress, cancellationToken, checkSession), cancellationToken);

    private static async Task CopyFileCoreAsync(FileInfo startFile, string destinationFile, long start, long end, Action<double, long>? progress, CancellationToken cancellationToken, ExportTsCheckSession? checkSession)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (end < 0 || end < start)
            end = startFile.Length;

        const int bufferSize = 4 * 1024 * 1024; // 大块顺序 IO，减少每块异步读写的调度开销。
        var totalBytes = end - start;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var bytesRead = 0;
        var totalBytesRead = 0L;

        try
        {
            await using var sourceStream = MediaFileUtil.OpenRead(startFile.FullName);
            cancellationToken.ThrowIfCancellationRequested();
            if (start < 0)
                throw new ArgumentOutOfRangeException(nameof(start));
            var sourceLength = sourceStream.Length;
            if (end < start || start > sourceLength || end > sourceLength)
                throw new EndOfStreamException();
            // 提供检测会话时，成功写出的字节交给检测器，不在导出后重读输出文件。
            await using Stream destinationStream = checkSession is null
                ? new FileStream(destinationFile, FileMode.Create, FileAccess.Write)
                : new TsDetectingWriteStream(
                    new FileStream(destinationFile, FileMode.Create, FileAccess.Write), checkSession);

            sourceStream.Seek(start, SeekOrigin.Begin);

            while (totalBytesRead < totalBytes)
            {
                // Check if cancellation is requested
                cancellationToken.ThrowIfCancellationRequested();

                var bytesToRead = (int)Math.Min(bufferSize, totalBytes - totalBytesRead);
                bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0)
                    throw new EndOfStreamException();

                await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                totalBytesRead += bytesRead;

                var progressPercentage = totalBytesRead * 100.0 / totalBytes;
                progress?.Invoke(progressPercentage, bytesRead);
            }

            // Final progress report (100%)
            progress?.Invoke(100, 0);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
    
    public static string FormatFileSize(double fileSize)
    {
        return fileSize switch
        {
            < 0 => throw new ArgumentOutOfRangeException(nameof(fileSize)),
            >= 1024 * 1024 * 1024 => $"{fileSize / (1024 * 1024 * 1024):########0.00}GB",
            >= 1024 * 1024 => $"{fileSize / (1024 * 1024):####0.00}MB",
            >= 1024 => $"{fileSize / 1024:####0.00}KB",
            _ => $"{fileSize:####0.00}B"
        };
    }

    public static string FormatBitrate(double bitsPerSecond)
    {
        if (bitsPerSecond < 0)
            throw new ArgumentOutOfRangeException(nameof(bitsPerSecond));
        return bitsPerSecond >= 1_000_000
            ? $"{bitsPerSecond / 1_000_000:0.##} Mb/s"
            : $"{bitsPerSecond / 1_000:0.##} kb/s";
    }

    public static string FormatOptionalBitrate(double bitsPerSecond) => bitsPerSecond > 0
        ? FormatBitrate(bitsPerSecond)
        : "-";

    public static void OpenFileLocation(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Win32Util.OpenFolderInExplorer(filePath);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // macOS
            Process.Start("open", $"-R \"{filePath}\"");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Linux
            Process.Start("xdg-open", $"\"{Path.GetDirectoryName(filePath)}\"");
        }
        else
        {
            throw new PlatformNotSupportedException("Not supported");
        }
    }
}
