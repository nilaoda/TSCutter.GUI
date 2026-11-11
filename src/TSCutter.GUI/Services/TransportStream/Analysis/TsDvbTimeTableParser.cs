using System;

namespace TSCutter.GUI.Services.TransportStream.Analysis;

internal static class TsDvbTimeTableParser
{
    public const int Pid = 0x0014;
    private const byte TdtTableId = 0x70;
    private const byte TotTableId = 0x73;

    public static bool TryParseUtc(ReadOnlySpan<byte> section, out DateTimeOffset utcTime)
    {
        utcTime = default;
        if (section.Length < 8 || section[0] is not (TdtTableId or TotTableId) ||
            (section[1] & 0xF0) != 0x70)
            return false;

        var sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        var totalLength = 3 + sectionLength;
        if (totalLength > section.Length || totalLength < 8)
            return false;

        // TDT 没有 CRC，固定只有 5 字节 UTC；TOT 末尾带 MPEG-2 CRC32。
        if (section[0] == TdtTableId)
        {
            if (sectionLength != 5)
                return false;
        }
        else if (totalLength < 14 || (section[8] & 0xF0) != 0xF0)
        {
            return false;
        }
        else
        {
            var descriptorLength = ((section[8] & 0x0F) << 8) | section[9];
            if (10 + descriptorLength + 4 != totalLength ||
                !HasValidMpegCrc(section[..totalLength]))
                return false;
        }

        return TryParseMjdUtc(section.Slice(3, 5), out utcTime);
    }

    /// <summary>解析 DVB 的 40 位 UTC 时间（16 位 MJD + 6 位 BCD 时分秒），TDT/TOT 与 EIT 共用。</summary>
    public static bool TryParseMjdUtc(ReadOnlySpan<byte> value, out DateTimeOffset utcTime)
    {
        utcTime = default;
        if (value.Length < 5)
            return false;
        var mjd = (value[0] << 8) | value[1];
        if (!TryReadBcd(value[2], out var hour) ||
            !TryReadBcd(value[3], out var minute) ||
            !TryReadBcd(value[4], out var second) ||
            hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        try
        {
            // MJD 0 对应 1858-11-17。直接从历元加天数可避免浮点历法公式的边界误差。
            utcTime = new DateTimeOffset(1858, 11, 17, 0, 0, 0, TimeSpan.Zero)
                .AddDays(mjd)
                .AddHours(hour)
                .AddMinutes(minute)
                .AddSeconds(second);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    internal static bool TryReadBcd(byte value, out int result)
    {
        var high = value >> 4;
        var low = value & 0x0F;
        if (high > 9 || low > 9)
        {
            result = 0;
            return false;
        }
        result = high * 10 + low;
        return true;
    }

    internal static bool HasValidMpegCrc(ReadOnlySpan<byte> section)
    {
        uint crc = uint.MaxValue;
        foreach (var value in section)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
        }
        return crc == 0;
    }
}
