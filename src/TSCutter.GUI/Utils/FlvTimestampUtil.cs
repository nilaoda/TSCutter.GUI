using System;
using System.Buffers.Binary;
using System.IO;

namespace TSCutter.GUI.Utils;

internal static class FlvTimestampUtil
{
    // 对照 FFmpeg 从尾部标签推算出的 duration，或补足录制中断导致的未知时长。
    // 尾部不完整时只搜索最后 1 MiB；失败时沿用解复用器结果。
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
        var start = Math.Max(dataOffset + 4L, length - 1024 * 1024);
        if (start >= length)
            return null;
        var buffer = new byte[(int)(length - start)];
        input.Position = start;
        input.ReadExactly(buffer);
        for (var end = buffer.Length; end >= 45; end--)
        {
            if ((end & 4095) == 0)
                checkInterrupted();
            // 连续验证三个标签的头、长度和 PreviousTagSize，避免误认压缩载荷里的字节。
            if (TryReadTag(buffer, end, out var position, out var timestamp) && timestamp > 0 &&
                TryReadTag(buffer, position, out var previousPosition, out _) &&
                TryReadTag(buffer, previousPosition, out _, out _))
                return timestamp;
        }
        return null;
    }

    private static bool TryReadTag(ReadOnlySpan<byte> buffer, int end, out int position, out long timestamp)
    {
        position = 0;
        timestamp = 0;
        if (end < 15)
            return false;
        var size = BinaryPrimitives.ReadUInt32BigEndian(buffer[(end - 4)..end]);
        var start = end - 4L - size;
        if (size < 11 || start < 0)
            return false;
        var header = buffer.Slice((int)start, 11);
        if (!IsTagHeader(header, size))
            return false;
        position = (int)start;
        timestamp = ReadTimestamp(header);
        return true;
    }

    private static bool IsTagHeader(ReadOnlySpan<byte> header, uint size) =>
        ReadUInt24(header[1..]) + 11 == size && header[0] is 8 or 9 or 18 && ReadUInt24(header[8..]) == 0;

    private static long ReadTimestamp(ReadOnlySpan<byte> header) => ReadUInt24(header[4..]) | ((long)header[7] << 24);

    private static int ReadUInt24(ReadOnlySpan<byte> bytes) => bytes[0] << 16 | bytes[1] << 8 | bytes[2];
}
