using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Services;

internal static class TsClipCopyService
{
    public static async Task CopyAsync(string sourcePath, string outputPath, long start, long end,
        Action<double, long>? progress, CancellationToken token)
    {
        var layout = await Task.Run(() => TsPacketLayout.Detect(sourcePath, token, Math.Max(0, start)), token).ConfigureAwait(false);
        layout.RequireSupported();
        if (layout.IsM2ts)
        {
            var length = new FileInfo(sourcePath).Length;
            if (start == 0) start = layout.PacketOffset;
            if (end < 0) end = layout.AlignPosition(length);
            if (start < layout.PacketOffset || end <= start || end > length ||
                !layout.IsAligned(start) || !layout.IsAligned(end))
                throw new TsClipMergeException(TsClipMergeErrorCode.InvalidRange);
        }
        await CommonUtil.CopyFileAsync(new FileInfo(sourcePath), outputPath, start, end, progress, token)
            .ConfigureAwait(false);
    }
}
