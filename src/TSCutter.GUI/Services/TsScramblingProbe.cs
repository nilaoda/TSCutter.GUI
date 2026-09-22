using System;
using System.IO;
using TSCutter.GUI.Utils;

namespace TSCutter.GUI.Services;

internal static class TsScramblingProbe
{
    internal const int MaximumProbeBytes = 4 * 1024 * 1024;

    public static bool HasScrambledPayload(string path)
    {
        using var input = File.OpenRead(path);
        var buffer = new byte[(int)Math.Min(input.Length, MaximumProbeBytes)];
        var length = input.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return HasScrambledPayload(buffer.AsSpan(0, length));
    }

    internal static bool HasScrambledPayload(ReadOnlySpan<byte> data)
    {
        // Also accept M2TS prefixes and RS-protected TS packets.
        foreach (var stride in new[] { 188, 192, 204 })
        {
            for (var start = 0; start + stride * 3 + TsUtil.TsPacketSize <= data.Length; start++)
            {
                if (data[start] != TsUtil.TsSyncByte ||
                    data[start + stride] != TsUtil.TsSyncByte ||
                    data[start + stride * 2] != TsUtil.TsSyncByte ||
                    data[start + stride * 3] != TsUtil.TsSyncByte)
                    continue;

                for (var offset = start; offset + TsUtil.TsPacketSize <= data.Length; offset += stride)
                {
                    var info = TsPacketParser.Parse(data.Slice(offset, TsUtil.TsPacketSize));
                    if (!info.IsValid)
                        break;
                    if (info.Pid != 0x1fff && info.HasPayload && info.ScramblingControl >= 2)
                        return true;
                    start = offset;
                }
            }
        }
        return false;
    }
}
