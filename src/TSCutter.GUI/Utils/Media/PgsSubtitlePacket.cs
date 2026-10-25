using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace TSCutter.GUI.Utils.Media;

internal static class PgsSubtitlePacket
{
    private const int MaximumPacketBytes = 64 * 1024 * 1024;

    // 窗口可能从普通显示集合中间开始；等待完整的 epoch 或 acquisition point，
    // 避免把缺少对象、调色板的续接事件交给解码器。
    public static bool HasAcquisitionPoint(ReadOnlySpan<byte> data)
    {
        while (data.Length >= 3)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[1..]);
            if (length > data.Length - 3)
                return false;
            if (data[0] == 0x16 && length >= 11 && (data[10] & 0xc0) != 0)
                return true;
            data = data[(3 + length)..];
        }
        return false;
    }

    // 部分 Matroska 使用 zlib 压缩字幕包；未启用 zlib 的 FFmpeg 会返回原始压缩数据。
    // 只处理 PGS 的 zlib 封装，不改变其中的 RLE 图形数据。
    public static byte[]? Decompress(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2 || (data[0] & 0x0f) != 8 || data[0] >> 4 > 7 ||
            (data[0] * 256 + data[1]) % 31 != 0)
            return null;
        using var input = new MemoryStream(data.ToArray(), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = zlib.Read(buffer)) > 0)
        {
            if (output.Length + count > MaximumPacketBytes)
                throw new InvalidDataException("PGS zlib packet");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
}
