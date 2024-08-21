using System;
using System.IO;
using System.Threading.Tasks;

namespace TSCutter.GUI.Utils;

public static class CommonUtil
{
    public static string FormatSeconds(double seconds, bool forFile = false)
    {
        var timeSpan = TimeSpan.FromSeconds(seconds);
        var ch = forFile ? '.' : ':';
        return $"{timeSpan.Hours:D2}{ch}{timeSpan.Minutes:D2}{ch}{timeSpan.Seconds:D2}.{timeSpan.Milliseconds:D3}";
    }

    public static async Task CopyFileAsync(FileInfo startFile, string destinationFile, long start = 0, long end = -1, Action<double>? progress = null)
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
            var bytesToRead = (int)Math.Min(bufferSize, totalBytes - totalBytesRead);
            bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, bytesToRead));
            if (bytesRead <= 0)
                break;

            await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead));
            totalBytesRead += bytesRead;

            var progressPercentage = totalBytesRead * 100.0 / totalBytes;
            progress?.Invoke(progressPercentage);
        }

        // Final progress report (100%)
        progress?.Invoke(100);
    }
}