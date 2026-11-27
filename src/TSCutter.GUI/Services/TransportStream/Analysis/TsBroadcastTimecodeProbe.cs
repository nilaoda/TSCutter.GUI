using System;
using System.IO;
using TSCutter.GUI.Models.TransportStream.Analysis;
using TSCutter.GUI.Utils.TransportStream.Packets;

namespace TSCutter.GUI.Services.TransportStream.Analysis;

/// <summary>
/// Reads the first DVB TDT/TOT UTC time from the beginning of a transport stream.
/// The scan is bounded so opening a file never depends on locating a table later.
/// </summary>
internal static class TsBroadcastTimecodeProbe
{
    private const int MaximumBytes = 16 * 1024 * 1024;

    public static DateTimeOffset? TryReadFirstUtc(string path, long syncOffset = 0)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(syncOffset, SeekOrigin.Begin);
            var buffer = new byte[MaximumBytes];
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read < TsUtil.TsPacketSize)
                return null;

            var start = 0;
            while (start + TsUtil.TsPacketSize <= read && buffer[start] != TsUtil.TsSyncByte)
                start++;
            var assembler = new TsPsiSectionAssembler();
            DateTimeOffset? found = null;
            for (var offset = start; offset + TsUtil.TsPacketSize <= read; offset += TsUtil.TsPacketSize)
            {
                if (buffer[offset] != TsUtil.TsSyncByte)
                {
                    assembler.Discard();
                    continue;
                }
                var packet = buffer.AsSpan(offset, TsUtil.TsPacketSize);
                var info = TsPacketParser.Parse(packet);
                if (info.Error != TsPacketParseError.None || info.Pid != TsDvbTimeTableParser.Pid
                    || info.ScramblingControl != 0 || info.PayloadOffset >= TsUtil.TsPacketSize)
                    continue;
                var payload = packet[info.PayloadOffset..];
                assembler.Push(payload, info.PayloadStart, section =>
                {
                    if (found is null && TsDvbTimeTableParser.TryParseUtc(section, out var utc))
                        found = utc;
                });
                if (found is not null)
                    break;
            }
            return found;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to read broadcast time: {ex.Message}");
            return null;
        }
    }
}
