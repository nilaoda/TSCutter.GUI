using System;

namespace TSCutter.GUI.Utils.Media.DolbyE;

/// <summary>
/// 杜比 E 帧头中识别声道组所需的字段。输入为 SMPTE 337M 突发的负载：16 位字占 2 字节，20、24 位字占 3 字节，均为大端，
/// 20 位字左对齐。帧头可能带扰码密钥，元数据按密钥异或后读取。
/// </summary>
public readonly record struct DolbyEFrameHeader(int WordBits, bool KeyPresent, int ProgramConfig, int FrameRateCode)
{
    public int SampleRate => DolbyEConfiguration.SampleRateForCode(FrameRateCode);
    public int ChannelCount => DolbyEConfiguration.ChannelCount(ProgramConfig);

    public static bool TryParse(ReadOnlySpan<byte> payload, out DolbyEFrameHeader header)
    {
        header = default;
        if (payload.Length < 3)
            return false;
        var sync = (payload[0] << 16) | (payload[1] << 8) | payload[2];
        int bits;
        if ((sync & 0xFFFFFE) == 0x7888E)
            bits = 24;
        else if ((sync & 0xFFFFE0) == 0x788E0)
            bits = 20;
        else if ((sync & 0xFFFE00) == 0x78E00)
            bits = 16;
        else
            return false;

        var wordBytes = (bits + 7) >> 3;
        var keyPresent = ((sync >> (24 - bits)) & 1) != 0;
        var index = 1;
        uint key = 0;
        if (keyPresent)
        {
            var offset = index * wordBytes;
            if (offset + 3 > payload.Length)
                return false;
            key = (uint)((payload[offset] << 16) | (payload[offset + 1] << 8) | payload[offset + 2]) >> (24 - bits);
            index++;
        }

        var reader = new WordBitReader(payload, index, bits, key);
        if (!reader.TrySkip(4) || !reader.TryRead(10, out var metadataWords) || metadataWords == 0)
            return false;
        // 元数据段从同一个字开始：先跳过刚读过的 14 位。
        reader = new WordBitReader(payload, index, bits, key);
        if (!reader.TrySkip(14) || !reader.TryRead(6, out var programConfig) || !reader.TryRead(4, out var frameRate) ||
            !reader.TryRead(4, out var originalFrameRate))
            return false;
        if (!DolbyEConfiguration.IsValid((int)programConfig) ||
            DolbyEConfiguration.SampleRateForCode((int)frameRate) == 0 ||
            DolbyEConfiguration.SampleRateForCode((int)originalFrameRate) == 0)
            return false;
        header = new DolbyEFrameHeader(bits, keyPresent, (int)programConfig, (int)frameRate);
        return true;
    }

    /// <summary>按字逐位读取（高位在前），每个字先与密钥异或。</summary>
    private ref struct WordBitReader
    {
        private readonly ReadOnlySpan<byte> data;
        private readonly int bits;
        private readonly uint key;
        private int word;
        private int used;
        private uint current;

        public WordBitReader(ReadOnlySpan<byte> data, int firstWord, int bits, uint key)
        {
            this.data = data;
            this.bits = bits;
            this.key = key;
            word = firstWord;
            used = bits;
        }

        public bool TrySkip(int count) => TryRead(count, out _);

        public bool TryRead(int count, out uint value)
        {
            value = 0;
            while (count > 0)
            {
                if (used == bits)
                {
                    if (!TryLoad())
                        return false;
                    used = 0;
                }
                var take = Math.Min(count, bits - used);
                var shift = bits - used - take;
                value = (value << take) | ((current >> shift) & ((1u << take) - 1));
                used += take;
                count -= take;
            }
            return true;
        }

        private bool TryLoad()
        {
            var bytes = (bits + 7) >> 3;
            var offset = word * bytes;
            if (offset + bytes > data.Length)
                return false;
            uint raw = bits switch
            {
                16 => (uint)((data[offset] << 8) | data[offset + 1]),
                20 => (uint)((data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2]) >> 4,
                _ => (uint)((data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2])
            };
            current = raw ^ key;
            word++;
            return true;
        }
    }
}
