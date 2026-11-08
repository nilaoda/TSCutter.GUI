using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TSCutter.GUI.Utils.Editing;

// 保存压缩包前缀、完整内容摘要和长度，不持有载荷，也不按关键包分配数组。
internal readonly record struct PacketEndSignature(
    ulong A, ulong B, ulong C, ulong D,
    ulong HashA, ulong HashB, ulong HashC, ulong HashD, int Length)
{
    public bool IsValid => Length >= 32;
    public static PacketEndSignature Create(ReadOnlySpan<byte> data)
    {
        if (data.Length < 32) return default;
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        return new(Read(data), Read(data[8..]), Read(data[16..]), Read(data[24..]),
            Read(hash), Read(hash[8..]), Read(hash[16..]), Read(hash[24..]), data.Length);
    }

    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, A);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], B);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], C);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], D);
    }

    /// <summary>文本形式：包长度与 32 字节前缀、32 字节摘要的十六进制，用于在片段、工程与导出任务之间传递。</summary>
    public string ToText()
    {
        Span<byte> bytes = stackalloc byte[64];
        WriteTo(bytes);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], HashA);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], HashB);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[48..], HashC);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[56..], HashD);
        return $"{Length}:{Convert.ToHexString(bytes)}";
    }

    /// <summary>无效签名返回 null，便于直接保存到可空的文本字段。</summary>
    public string? ToTextOrNull() => IsValid ? ToText() : null;

    public static bool TryParse(string? text, out PacketEndSignature signature)
    {
        signature = default;
        var separator = text?.IndexOf(':') ?? -1;
        if (separator <= 0 || text!.Length - separator - 1 != 128 ||
            !int.TryParse(text.AsSpan(0, separator), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var length) || length < 32)
            return false;
        Span<byte> bytes = stackalloc byte[64];
        if (Convert.FromHexString(text.AsSpan(separator + 1), bytes, out _, out var written) != System.Buffers.OperationStatus.Done ||
            written != 64)
            return false;
        signature = new(Read(bytes), Read(bytes[8..]), Read(bytes[16..]), Read(bytes[24..]),
            Read(bytes[32..]), Read(bytes[40..]), Read(bytes[48..]), Read(bytes[56..]), length);
        return true;
    }

    public bool MatchesHash(ReadOnlySpan<byte> hash) => Read(hash) == HashA
        && Read(hash[8..]) == HashB && Read(hash[16..]) == HashC && Read(hash[24..]) == HashD;
    private static ulong Read(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt64LittleEndian(bytes);
}
