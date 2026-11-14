using System;

namespace TSCutter.GUI.Utils.Media.DolbyE;

/// <summary>一个 SMPTE 337M 突发：负载按杜比 E 解码器要求的大端字排列，时间戳为突发起点（Pa 所在样本）。</summary>
public sealed record S337mBurst(byte[] Payload, int WordBits, int DataType, long Timestamp);

/// <summary>
/// 从一个 AES 对（左右两路 32 位左对齐样本）中拆出 SMPTE 337M 突发。同步字 Pa/Pb 支持 16、20、24 位，
/// 与容器的位深无关：16 位突发装在 20 位样本的高 16 位时也能识别。突发可以跨越多次输入。
/// </summary>
public sealed class S337mDeframer
{
    /// <summary>杜比 E 的数据类型。</summary>
    public const int DolbyEDataType = 28;
    // 最长的杜比 E 突发（30 fps 以下每帧约 3648 字）留出余量，防止错误长度占用过多内存。
    private const int MaxPayloadWords = 8192;

    private enum State { Search, Preamble, Payload }

    private State state = State.Search;
    private int wordBits;
    private long timestamp;
    private int dataType;
    private byte[] payload = [];
    private int wordsNeeded;
    private int wordsReceived;

    public void Reset()
    {
        state = State.Search;
        payload = [];
        wordsNeeded = wordsReceived = 0;
    }

    /// <summary>送入一对样本；完成一个突发时返回它。</summary>
    public S337mBurst? Push(uint left, uint right, long sampleTimestamp)
    {
        switch (state)
        {
            case State.Search:
                var bits = DetectSync(left, right);
                if (bits != 0)
                {
                    wordBits = bits;
                    timestamp = sampleTimestamp;
                    state = State.Preamble;
                }
                return null;
            case State.Preamble:
                dataType = (int)((left >> 16) & 0x1F);
                var lengthBits = right >> (32 - wordBits);
                var words = (int)((lengthBits + (uint)wordBits - 1) / (uint)wordBits);
                if (words <= 0 || words > MaxPayloadWords)
                {
                    state = State.Search;
                    return null;
                }
                wordsNeeded = words;
                wordsReceived = 0;
                payload = new byte[words * WordBytes(wordBits)];
                state = State.Payload;
                return null;
            default:
                // 突发未收完又出现同步字：前一个突发残缺（传输丢包），从新突发重新开始。
                var restart = DetectSync(left, right);
                if (restart != 0 && wordsReceived > 0)
                {
                    wordBits = restart;
                    timestamp = sampleTimestamp;
                    payload = [];
                    state = State.Preamble;
                    return null;
                }
                Append(left);
                if (wordsReceived < wordsNeeded)
                    Append(right);
                if (wordsReceived < wordsNeeded)
                    return null;
                state = State.Search;
                var burst = new S337mBurst(payload, wordBits, dataType, timestamp);
                payload = [];
                return burst;
        }
    }

    /// <summary>返回同步字的位数，不是同步字时返回 0。</summary>
    public static int DetectSync(uint left, uint right)
    {
        if ((left >> 8) == 0x96F872 && (right >> 8) == 0xA54E1F)
            return 24;
        if ((left >> 12) == 0x6F872 && (right >> 12) == 0x54E1F)
            return 20;
        if ((left >> 16) == 0xF872 && (right >> 16) == 0x4E1F)
            return 16;
        return 0;
    }

    private static int WordBytes(int bits) => (bits + 7) >> 3;

    private void Append(uint sample)
    {
        var word = sample >> (32 - wordBits);
        var offset = wordsReceived * WordBytes(wordBits);
        switch (wordBits)
        {
            case 16:
                payload[offset] = (byte)(word >> 8);
                payload[offset + 1] = (byte)word;
                break;
            case 20:
                var aligned = word << 4;
                payload[offset] = (byte)(aligned >> 16);
                payload[offset + 1] = (byte)(aligned >> 8);
                payload[offset + 2] = (byte)aligned;
                break;
            default:
                payload[offset] = (byte)(word >> 16);
                payload[offset + 1] = (byte)(word >> 8);
                payload[offset + 2] = (byte)word;
                break;
        }
        wordsReceived++;
    }

    /// <summary>按 337M 组成突发的 32 位左对齐样本，供测试构造数据。</summary>
    internal static uint[] Compose(int bits, int dataType, ReadOnlySpan<uint> words)
    {
        var shift = 32 - bits;
        uint pa = bits switch { 16 => 0xF872u, 20 => 0x6F872u, _ => 0x96F872u };
        uint pb = bits switch { 16 => 0x4E1Fu, 20 => 0x54E1Fu, _ => 0xA54E1Fu };
        var pc = bits switch { 16 => (uint)dataType, 20 => (uint)dataType << 4, _ => (uint)dataType << 8 };
        var result = new uint[4 + words.Length + (words.Length & 1)];
        result[0] = pa << shift;
        result[1] = pb << shift;
        result[2] = pc << shift;
        result[3] = (uint)(words.Length * bits) << shift;
        for (var i = 0; i < words.Length; i++)
            result[4 + i] = words[i] << shift;
        return result;
    }
}
