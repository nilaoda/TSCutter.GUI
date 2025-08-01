﻿using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

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

    public static async Task CopyFileAsync(FileInfo startFile, string destinationFile, long start = 0, long end = -1, Action<double, long>? progress = null, CancellationToken cancellationToken = default)
    {
        if (end < 0 || end < start)
            end = startFile.Length;

        const int bufferSize = 1024 * 1024; // 1024 KB buffer
        var totalBytes = end - start;
        var buffer = new byte[bufferSize];
        var bytesRead = 0;
        var totalBytesRead = 0L;

        await using var sourceStream = new FileStream(startFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var destinationStream = new FileStream(destinationFile, FileMode.Create, FileAccess.Write);

        sourceStream.Seek(start, SeekOrigin.Begin);

        while (totalBytesRead < totalBytes)
        {
            // Check if cancellation is requested
            cancellationToken.ThrowIfCancellationRequested();

            var bytesToRead = (int)Math.Min(bufferSize, totalBytes - totalBytesRead);
            bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken);
            if (bytesRead <= 0)
                break;

            await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalBytesRead += bytesRead;

            var progressPercentage = totalBytesRead * 100.0 / totalBytes;
            progress?.Invoke(progressPercentage, bytesRead);
        }

        // Final progress report (100%)
        progress?.Invoke(100, bytesRead);
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