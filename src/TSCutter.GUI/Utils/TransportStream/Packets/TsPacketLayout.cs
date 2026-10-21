using System;
using System.Buffers;
using System.IO;
using System.Threading;
using TSCutter.GUI.Services.Application;
using TSCutter.GUI.Services.TransportStream.Analysis;
using TSCutter.GUI.Utils.Files;

namespace TSCutter.GUI.Utils.TransportStream.Packets;

/// <summary>文件包步长与 188 字节传输包的位置分别记录，所有复制偏移均指向附加头起点。</summary>
public readonly record struct TsPacketLayout(int PacketSize, long SyncOffset)
{
    public const int M2tsHeaderSize = 4;
    public const int M2tsPacketSize = TsUtil.TsPacketSize + M2tsHeaderSize;
    internal const int FecPacketSize = 204;
    internal const int SyncProbePacketCount = 4;
    internal const int SyncProbeOverlapBytes = FecPacketSize * SyncProbePacketCount;
    internal static ReadOnlySpan<int> DetectablePacketSizes => [TsUtil.TsPacketSize, M2tsPacketSize, FecPacketSize];

    public bool IsM2ts => PacketSize == M2tsPacketSize;
    public int PrefixSize => GetPrefixSize(PacketSize);
    public long PacketOffset => SyncOffset - PrefixSize;
    public bool IsSupported => (PacketSize == TsUtil.TsPacketSize || IsM2ts) && PacketOffset >= 0;
    public string Extension => IsM2ts ? "m2ts" : "ts";

    private static int GetPrefixSize(int packetSize) => packetSize == M2tsPacketSize ? M2tsHeaderSize : 0;

    public static TsPacketLayout FromPacketOffset(int packetSize, long packetOffset) =>
        new(packetSize, packetOffset + GetPrefixSize(packetSize));

    public bool IsAligned(long position) => PacketSize > 0 && position >= PacketOffset &&
        (position - PacketOffset) % PacketSize == 0;

    public long AlignPosition(long position) => PacketSize <= 0 || position < PacketOffset
        ? -1 : PacketOffset + (position - PacketOffset) / PacketSize * PacketSize;

    // 从整包或读缓冲中取出包体，不复制数据；附加头不会进入 TS/PES 解析器。
    public ReadOnlySpan<byte> GetPacketBody(ReadOnlySpan<byte> buffer, int packetOffset = 0) =>
        buffer.Slice(packetOffset + PrefixSize, TsUtil.TsPacketSize);

    public Span<byte> GetPacketBody(Span<byte> buffer, int packetOffset = 0) =>
        buffer.Slice(packetOffset + PrefixSize, TsUtil.TsPacketSize);

    internal int FindPacketSync(ReadOnlySpan<byte> data) =>
        TsUtil.FindPacketSync(data, PacketSize, PrefixSize);

    public void RequireSupported(bool standardOnly = false)
    {
        if (PacketSize == 0 || PacketOffset < 0)
            throw new InvalidDataException(GetMessage("String.TsLayout.NoSync", "No valid TS packet layout was found."));
        if (!IsSupported || (standardOnly && PacketSize != TsUtil.TsPacketSize))
            throw new NotSupportedException(string.Format(
                standardOnly ? GetMessage("String.TsLayout.StandardOnly",
                    "This tool currently supports only 188-byte TS packets; {0}-byte packets are not supported.")
                    : GetMessage("String.TsLayout.Unsupported",
                        "Unsupported {0}-byte TS packets. Only 188-byte TS and 192-byte M2TS are supported."), PacketSize));
    }

    // 服务也可脱离界面运行；未注册本地化服务时仍保留明确的异常类型及提示。
    private static string GetMessage(string key, string fallback) =>
        App.LocalizationService?.GetString(key) ?? fallback;

    public static TsPacketLayout Detect(string path, CancellationToken token = default, long startPosition = 0)
    {
        using var input = MediaFileUtil.OpenRead(path);
        return Detect(input, token, startPosition);
    }

    internal static TsPacketLayout Detect(Stream input, CancellationToken token = default, long startPosition = 0)
    {
        var savedPosition = input.Position;
        const int capacity = TsScramblingProbe.ProbeBufferBytes;
        const int overlap = SyncProbeOverlapBytes;
        var buffer = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            input.Position = Math.Clamp(startPosition, 0, input.Length);
            var scanStart = input.Position;
            long bytesRead = 0;
            var carry = 0;
            while (bytesRead < TsScramblingProbe.MaximumProbeBytes)
            {
                token.ThrowIfCancellationRequested();
                var requested = (int)Math.Min(capacity - carry, TsScramblingProbe.MaximumProbeBytes - bytesRead);
                var read = input.ReadAtLeast(buffer.AsSpan(carry, requested), requested, throwOnEndOfStream: false);
                bytesRead += read;
                var length = carry + read;
                var layout = FindLayout(buffer.AsSpan(0, length), validateHeaders: false);
                if (layout.PacketSize != 0)
                    return new(layout.PacketSize, scanStart + bytesRead - length + layout.SyncOffset);
                if (read < requested)
                {
                    // 小文件不足四包时仍按实际步长校验，不能将一个 204 字节包误认成 188 字节包。
                    var shortLayout = FindShortLayout(buffer.AsSpan(0, length));
                    if (shortLayout.PacketSize != 0)
                        return new(shortLayout.PacketSize, scanStart + bytesRead - length + shortLayout.SyncOffset);
                    break;
                }
                carry = Math.Min(length, overlap);
                buffer.AsSpan(length - carry, carry).CopyTo(buffer);
            }
            // 起点靠近 EOF 时可能不足四包；回看有界窗口确认步长，避免单包和尾部残留字节无法判定布局。
            if (scanStart > 0)
            {
                token.ThrowIfCancellationRequested();
                input.Position = Math.Max(0, scanStart - overlap);
                var lookBehindStart = input.Position;
                var requested = (int)Math.Min(overlap * 2, input.Length - lookBehindStart);
                var read = input.ReadAtLeast(buffer.AsSpan(0, requested), requested, throwOnEndOfStream: false);
                var layout = FindLayout(buffer.AsSpan(0, read), validateHeaders: false);
                if (layout.PacketSize != 0)
                    return new(layout.PacketSize, lookBehindStart + layout.SyncOffset);
            }
            return default;
        }
        finally
        {
            input.Position = savedPosition;
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static (int PacketSize, int SyncOffset) FindLayout(ReadOnlySpan<byte> data, bool validateHeaders = true)
    {
        var packetSizes = DetectablePacketSizes;
        for (var offset = 0; offset + TsUtil.TsPacketSize * (SyncProbePacketCount - 1) < data.Length; offset++)
        {
            if (data[offset] != TsUtil.TsSyncByte)
                continue;
            foreach (var size in packetSizes)
            {
                if (offset < GetPrefixSize(size) ||
                    offset + size * (SyncProbePacketCount - 1) + (validateHeaders ? TsUtil.TsPacketSize : 1) > data.Length)
                    continue;
                var valid = true;
                for (var index = 0; index < SyncProbePacketCount; index++)
                {
                    if (data[offset + size * index] != TsUtil.TsSyncByte ||
                        validateHeaders && !TsPacketParser.Parse(data.Slice(offset + size * index, TsUtil.TsPacketSize)).IsValid)
                    {
                        valid = false;
                        break;
                    }
                }
                if (valid)
                    return (size, validateHeaders ? offset : PreferPacketSync(data, offset, size));
            }
        }
        return (0, -1);
    }

    // ATS 内的 0x47 也可能按 192 字节重复；优先选择同一附加头范围内有合法 TS 头的同步点。
    // 不跳到下一包，否则会漏报文件开头原有的损坏包。
    internal static int PreferPacketSync(ReadOnlySpan<byte> data, int syncOffset, int packetSize, int packetCount = SyncProbePacketCount)
    {
        if (packetSize != M2tsPacketSize || HasValidPacketHeaders(data, syncOffset, packetSize, packetCount))
            return syncOffset;
        for (var shift = 1; shift < M2tsHeaderSize; shift++)
            if (HasValidPacketHeaders(data, syncOffset + shift, packetSize, packetCount))
                return syncOffset + shift;
        return syncOffset;
    }

    private static bool HasValidPacketHeaders(ReadOnlySpan<byte> data, int syncOffset, int packetSize, int packetCount)
    {
        if (syncOffset + packetSize * (packetCount - 1) + TsUtil.TsPacketSize > data.Length)
            return false;
        for (var index = 0; index < packetCount; index++)
            if (!TsPacketParser.Parse(data.Slice(syncOffset + index * packetSize, TsUtil.TsPacketSize)).IsValid)
                return false;
        return true;
    }

    internal static (int PacketSize, int SyncOffset) FindShortLayout(ReadOnlySpan<byte> data)
    {
        var sizes = DetectablePacketSizes;
        // 先根据重复同步点确定步长，包头损坏也不能让最后一包被当成另一种布局。
        for (var offset = Math.Max(0, data.Length - SyncProbeOverlapBytes); offset + TsUtil.TsPacketSize <= data.Length; offset++)
        {
            if (data[offset] != TsUtil.TsSyncByte) continue;
            foreach (var size in sizes)
            {
                var count = Math.Min(SyncProbePacketCount, (data.Length - offset - TsUtil.TsPacketSize) / size + 1);
                if (count < 2 || offset < GetPrefixSize(size)) continue;
                var valid = true;
                for (var index = 1; index < count; index++)
                    if (data[offset + index * size] != TsUtil.TsSyncByte)
                    {
                        valid = false;
                        break;
                    }
                if (valid) return (size, PreferPacketSync(data, offset, size, count));
            }
        }
        // 仅一包时优先识别完整的 M2TS；否则其 188 字节包体会被误当作带前缀的 TS。
        if (data.Length == M2tsPacketSize && TsPacketParser.Parse(data[M2tsHeaderSize..]).IsValid)
            return (M2tsPacketSize, M2tsHeaderSize);
        for (var offset = Math.Max(0, data.Length - SyncProbeOverlapBytes); offset + TsUtil.TsPacketSize <= data.Length; offset++)
        {
            if (data[offset] != TsUtil.TsSyncByte)
                continue;
            foreach (var size in sizes)
            {
                var start = offset - GetPrefixSize(size);
                if (start < 0) continue;
                var count = Math.Min(SyncProbePacketCount, (data.Length - offset - TsUtil.TsPacketSize) / size + 1);
                if (count == 1 && data.Length - start != size)
                    continue;
                var valid = true;
                for (var index = 0; index < count; index++)
                    if (!TsPacketParser.Parse(data.Slice(offset + index * size, TsUtil.TsPacketSize)).IsValid)
                    {
                        valid = false;
                        break;
                    }
                if (valid)
                    return (size, offset);
            }
        }
        return default;
    }
}
