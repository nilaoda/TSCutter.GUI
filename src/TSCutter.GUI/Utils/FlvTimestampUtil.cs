using System;
using System.Buffers.Binary;
using System.IO;

namespace TSCutter.GUI.Utils;

internal static class FlvTimestampUtil
{
    private const int MiB = 1024 * 1024;
    private const int MaximumTailBytes = 64 * MiB;

    // 对照 FFmpeg 从尾部标签推算出的 duration，或补足录制中断导致的未知时长。
    // 尾部不完整时按需扩大搜索范围，最多 64 MiB；失败时沿用解复用器结果。
    public static long? TryReadFinalTimestamp(string path, Action checkInterrupted)
    {
        try
        {
            using var input = MediaFileUtil.OpenRead(path, bufferSize: 4096, allowConcurrentWrite: true);
            Span<byte> header = stackalloc byte[11];
            input.ReadExactly(header[..9]);
            if (!header[..3].SequenceEqual("FLV"u8) || header[3] != 1)
                return null;
            var dataOffset = BinaryPrimitives.ReadUInt32BigEndian(header[5..9]);
            if (dataOffset < 9)
                return null;

            var length = input.Length;
            return ReadCompleteTail(input, dataOffset, length, checkInterrupted) ??
                   FindCompleteTail(input, dataOffset, length, checkInterrupted);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private static long? ReadCompleteTail(FileStream input, uint dataOffset, long end, Action checkInterrupted)
    {
        Span<byte> header = stackalloc byte[11];
        Span<byte> previousSize = stackalloc byte[4];
        for (var count = 0; count < 64 && end >= dataOffset + 19L; count++)
        {
            checkInterrupted();
            input.Position = end - 4;
            input.ReadExactly(previousSize);
            var size = BinaryPrimitives.ReadUInt32BigEndian(previousSize);
            var position = end - 4 - size;
            if (size < 11 || position < dataOffset + 4L)
                return null;
            input.Position = position;
            input.ReadExactly(header);
            if (!IsTagHeader(header, size))
                return null;

            var timestamp = ReadTimestamp(header);
            if (timestamp > 0)
                return timestamp;
            end = position;
        }
        return null;
    }

    private static long? FindCompleteTail(FileStream input, uint dataOffset, long length, Action checkInterrupted)
    {
        var minimumPosition = Math.Max(dataOffset + 4L, length - MaximumTailBytes);
        if (minimumPosition >= length)
            return null;

        // 固定 1 MiB 块，加上跨块标签头的 10 字节重叠区。
        var buffer = new byte[MiB + 10];
        var upper = length - 10;
        foreach (var rangeMiB in new[] { 1, 4, 16, 64 })
        {
            var lower = Math.Max(minimumPosition, length - (long)rangeMiB * MiB);
            while (upper > lower)
            {
                checkInterrupted();
                var blockStart = Math.Max(lower, upper - MiB);
                var readEnd = upper + 10;
                var count = (int)(readEnd - blockStart);
                input.Position = blockStart;
                input.ReadExactly(buffer.AsSpan(0, count));
                var block = buffer.AsSpan(0, count);
                for (var position = upper - 1; position >= blockStart; position--)
                {
                    if ((position & 4095) == 0)
                        checkInterrupted();
                    // 先在内存中筛选标签头，避免对载荷中的随机长度反复执行文件读取。
                    var header = block.Slice((int)(position - blockStart), 11);
                    if (header[0] is not (8 or 9 or 18) || ReadUInt24(header[8..]) != 0 || ReadTimestamp(header) == 0)
                        continue;
                    var end = position + 15 + ReadUInt24(header[1..]);
                    if (end > length)
                        continue;
                    // 连续验证三个完整标签；块外的 footer 和标签头按位置仅读取所需字节。
                    if (TryReadTag(input, block, blockStart, minimumPosition, end, out var tagPosition, out var timestamp) &&
                        tagPosition == position &&
                        TryReadTag(input, block, blockStart, minimumPosition, position, out var previousPosition, out _) &&
                        TryReadTag(input, block, blockStart, minimumPosition, previousPosition, out _, out _))
                    {
                        checkInterrupted();
                        return timestamp;
                    }
                }
                upper = blockStart;
            }
            if (upper == minimumPosition)
                break;
        }
        return null;
    }

    private static bool TryReadTag(FileStream input, ReadOnlySpan<byte> block, long blockStart,
        long minimumPosition, long end, out long position, out long timestamp)
    {
        position = 0;
        timestamp = 0;
        if (end - minimumPosition < 15)
            return false;
        Span<byte> previousSize = stackalloc byte[4];
        ReadAt(input, block, blockStart, end - 4, previousSize);
        var size = BinaryPrimitives.ReadUInt32BigEndian(previousSize);
        var start = end - 4 - size;
        // FLV DataSize 为 24 位，标签长度还包含 11 字节头部。
        if (size < 11 || size > 0xFFFFFF + 11 || start < minimumPosition)
            return false;
        Span<byte> header = stackalloc byte[11];
        ReadAt(input, block, blockStart, start, header);
        if (!IsTagHeader(header, size))
            return false;
        position = start;
        timestamp = ReadTimestamp(header);
        return true;
    }

    private static void ReadAt(FileStream input, ReadOnlySpan<byte> block, long blockStart,
        long position, Span<byte> destination)
    {
        var offset = position - blockStart;
        if (offset >= 0 && offset <= block.Length - destination.Length)
            block.Slice((int)offset, destination.Length).CopyTo(destination);
        else
        {
            input.Position = position;
            input.ReadExactly(destination);
        }
    }

    private static bool IsTagHeader(ReadOnlySpan<byte> header, uint size) =>
        ReadUInt24(header[1..]) + 11 == size && header[0] is 8 or 9 or 18 && ReadUInt24(header[8..]) == 0;

    private static long ReadTimestamp(ReadOnlySpan<byte> header) => ReadUInt24(header[4..]) | ((long)header[7] << 24);

    private static int ReadUInt24(ReadOnlySpan<byte> bytes) => bytes[0] << 16 | bytes[1] << 8 | bytes[2];
}
