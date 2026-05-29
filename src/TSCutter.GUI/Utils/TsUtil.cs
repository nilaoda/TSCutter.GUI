using System;
using System.IO;

namespace TSCutter.GUI.Utils;

public static class TsUtil
{
    public const int TsPacketSize = 188;
    public const byte TsSyncByte = 0x47;

    /// <summary>
    /// Searches within maxSearchBytes range for a valid TS sync header offset.
    /// Returns -1 if not found.
    /// Validation: checks that 3 consecutive 188-byte spaced positions all start with 0x47.
    /// </summary>
    public static long FindSyncOffset(string filePath, long maxSearchBytes = 5 * 1024 * 1024)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var maxScan = Math.Min(maxSearchBytes, fs.Length);

        for (long i = 0; i < maxScan; i++)
        {
            fs.Seek(i, SeekOrigin.Begin);
            var b = fs.ReadByte();
            if (b == -1) break;

            if ((byte)b == TsSyncByte && VerifySyncPositions(fs, i))
                return i;
        }

        return -1;
    }

    private static bool VerifySyncPositions(FileStream fs, long offset)
    {
        for (int j = 1; j <= 3; j++)
        {
            var pos = offset + j * TsPacketSize;
            if (pos >= fs.Length) return false;

            fs.Seek(pos, SeekOrigin.Begin);
            var b = fs.ReadByte();
            if (b == -1 || (byte)b != TsSyncByte)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Counts complete TS packets starting from syncOffset.
    /// </summary>
    public static long CountPackets(string filePath, long syncOffset)
    {
        var fileLength = new FileInfo(filePath).Length;
        return (fileLength - syncOffset) / TsPacketSize;
    }

    /// <summary>
    /// Converts a packet index to byte offset in the file.
    /// </summary>
    public static long PacketToOffset(long packetIndex, long syncOffset)
    {
        return syncOffset + packetIndex * TsPacketSize;
    }
}