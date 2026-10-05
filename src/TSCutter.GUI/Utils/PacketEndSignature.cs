using System;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TSCutter.GUI.Utils;

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

    public bool MatchesHash(ReadOnlySpan<byte> hash) => Read(hash) == HashA
        && Read(hash[8..]) == HashB && Read(hash[16..]) == HashC && Read(hash[24..]) == HashD;
    private static ulong Read(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadUInt64LittleEndian(bytes);
}
