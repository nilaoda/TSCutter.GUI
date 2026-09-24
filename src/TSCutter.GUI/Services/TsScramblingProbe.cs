using System;
using System.IO;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Services;

internal static class TsScramblingProbe
{
    internal const int MaximumProbeBytes = 4 * 1024 * 1024;

    public static bool HasScrambledPayload(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".m2ts", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".mts", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".m2t", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".mpegts", StringComparison.OrdinalIgnoreCase))
            return false;

        using var input = File.OpenRead(path);
        var buffer = new byte[(int)Math.Min(input.Length, MaximumProbeBytes)];
        var length = input.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return HasScrambledPayload(buffer.AsSpan(0, length));
    }

    internal static bool HasScrambledPayload(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var syncOffset = TsUtil.FindPacketSync(data[offset..]);
            if (syncOffset < 0)
                break;
            offset += syncOffset;

            for (; offset + TsUtil.TsPacketSize <= data.Length; offset += TsUtil.TsPacketSize)
            {
                var info = TsPacketParser.Parse(data.Slice(offset, TsUtil.TsPacketSize));
                if (!info.IsValid)
                {
                    offset++;
                    break;
                }
                if (info.Pid != 0x1fff && info.HasPayload && info.ScramblingControl >= 2)
                    return true;
            }
        }
        return false;
    }
}
