using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TSCutter.GUI.Utils.Media;

/// <summary>
/// 帧精确剪辑使用的最小 H.264/HEVC 码流工具：遍历 NAL、识别 IDR 与参数集、读取 H.264 参数集编号、
/// 在 Annex B 与长度前缀格式之间转换。不解析或改写切片内容。
/// </summary>
internal static class VideoBitstream
{
    public static int NalType(ReadOnlySpan<byte> nal, bool hevc) =>
        nal.IsEmpty ? -1 : hevc ? (nal[0] >> 1) & 0x3F : nal[0] & 0x1F;

    // HEVC 的 IDR_W_RADL/IDR_N_LP 是闭合的衔接点；CRA、BLA 的 RASL 前导画面会参考之前的 GOP，按开放 GOP 处理。
    public static bool IsIdr(int type, bool hevc) => hevc ? type is 19 or 20 : type == 5;
    public static bool IsAud(int type, bool hevc) => hevc ? type == 35 : type == 9;

    /// <summary>参数集在附加数据中的顺序；非参数集返回 -1。</summary>
    public static int ParameterSetOrder(int type, bool hevc) => hevc
        ? type switch { 32 => 0, 33 => 1, 34 => 2, _ => -1 }
        : type switch { 7 => 1, 8 => 2, _ => -1 };

    public static bool IsSps(int type, bool hevc) => type == (hevc ? 33 : 7);

    /// <summary>
    /// 逐个取出 NAL 的位置，不分配集合；复制区每个包都要遍历，避免产生托管垃圾。
    /// lengthSize 为 0 表示 Annex B；否则为 avcC/hvcC 声明的长度字段字节数，长度不合法时抛出。
    /// </summary>
    public ref struct NalEnumerator
    {
        private readonly ReadOnlySpan<byte> data;
        private readonly int lengthSize;
        private int position;

        public NalEnumerator(ReadOnlySpan<byte> data, int lengthSize)
        {
            this.data = data;
            this.lengthSize = lengthSize;
            position = lengthSize == 0 ? FindStartCode(data, 0) : 0;
        }

        public Range Current { get; private set; }

        public readonly NalEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            if (lengthSize == 0)
            {
                while (position >= 0)
                {
                    var payload = position + 3;
                    var next = FindStartCode(data, payload);
                    var end = next < 0 ? data.Length : next;
                    // 去掉 NAL 末尾的 trailing_zero_8bits，避免四字节起始码的首个零被算入前一 NAL。
                    while (end > payload && data[end - 1] == 0)
                        end--;
                    position = next;
                    if (end > payload)
                    {
                        Current = new Range(payload, end);
                        return true;
                    }
                }
                return false;
            }
            if (position == data.Length)
                return false;
            if (position + lengthSize > data.Length)
                throw new InvalidDataException("Trailing bytes after NAL units.");
            var size = 0;
            for (var i = 0; i < lengthSize; i++)
                size = (size << 8) | data[position + i];
            position += lengthSize;
            if (size <= 0 || size > data.Length - position)
                throw new InvalidDataException("Invalid NAL length.");
            Current = new Range(position, position + size);
            position += size;
            return true;
        }
    }

    public static NalEnumerator EnumerateNals(ReadOnlySpan<byte> data, int lengthSize) => new(data, lengthSize);

    public static List<Range> SplitNals(ReadOnlySpan<byte> data, int lengthSize)
    {
        var result = new List<Range>();
        foreach (var range in EnumerateNals(data, lengthSize))
            result.Add(range);
        return result;
    }

    public static bool Contains(ReadOnlySpan<byte> data, int lengthSize, bool hevc, Func<int, bool> predicate)
    {
        foreach (var range in EnumerateNals(data, lengthSize))
            if (predicate(NalType(data[range], hevc)))
                return true;
        return false;
    }

    public static bool ContainsIdr(ReadOnlySpan<byte> data, int lengthSize, bool hevc)
    {
        foreach (var range in EnumerateNals(data, lengthSize))
            if (IsIdr(NalType(data[range], hevc), hevc))
                return true;
        return false;
    }

    /// <summary>HEVC 非 IDR 的随机访问点：BLA_W_LP/BLA_W_RADL/BLA_N_LP（16–18）或 CRA（21）。</summary>
    public static bool ContainsOpenIrap(ReadOnlySpan<byte> data, int lengthSize)
    {
        foreach (var range in EnumerateNals(data, lengthSize))
            if (NalType(data[range], true) is >= 16 and <= 18 or 21)
                return true;
        return false;
    }

    /// <summary>
    /// 把 HEVC 的 CRA 切片原地改为 BLA_W_LP（只改 NAL 头中的类型，切片头语法两者相同）。
    /// 解码器在 BLA 处像 IDR 一样开始新的编码视频序列：重新计算 POC，丢弃（此处已不存在的）RASL 画面。
    /// </summary>
    public static void ConvertCraToBla(Span<byte> data, int lengthSize)
    {
        foreach (var range in EnumerateNals(data, lengthSize))
        {
            var start = range.Start.GetOffset(data.Length);
            if (NalType(data[range], true) == 21)
                data[start] = (byte)((data[start] & 0x81) | (16 << 1));
        }
    }

    public static bool ContainsSps(ReadOnlySpan<byte> data, int lengthSize, bool hevc)
    {
        foreach (var range in EnumerateNals(data, lengthSize))
            if (IsSps(NalType(data[range], hevc), hevc))
                return true;
        return false;
    }

    /// <summary>转为 Annex B（每个 NAL 前加四字节起始码，可插入额外 NAL）后的字节数。</summary>
    public static int AnnexBSize(ReadOnlySpan<byte> data, int lengthSize, IReadOnlyList<byte[]>? prefix = null)
    {
        var size = 0;
        if (prefix is not null)
            foreach (var nal in prefix)
                size = checked(size + 4 + nal.Length);
        foreach (var range in EnumerateNals(data, lengthSize))
            size = checked(size + 4 + (range.End.Value - range.Start.Value));
        return size;
    }

    /// <summary>按出现顺序拼接为 Annex B，可在访问单元分隔符之后插入参数集等额外 NAL。</summary>
    public static byte[] ToAnnexB(ReadOnlySpan<byte> data, int lengthSize, bool hevc, IReadOnlyList<byte[]>? prefix = null)
    {
        var output = new byte[AnnexBSize(data, lengthSize, prefix)];
        WriteAnnexB(data, lengthSize, hevc, prefix, output);
        return output;
    }

    /// <summary>写入 Annex B 到目标缓冲区（长度须为 <see cref="AnnexBSize"/>），供直接写入原生包，不经过托管数组。</summary>
    public static void WriteAnnexB(ReadOnlySpan<byte> data, int lengthSize, bool hevc, IReadOnlyList<byte[]>? prefix,
        Span<byte> destination)
    {
        var target = destination;
        var nals = EnumerateNals(data, lengthSize);
        var pending = nals.MoveNext();
        // 访问单元分隔符必须位于访问单元开头，插入的参数集放在其后。
        if (pending && IsAud(NalType(data[nals.Current], hevc), hevc))
        {
            Write(ref target, data[nals.Current]);
            pending = nals.MoveNext();
        }
        if (prefix is not null)
            foreach (var nal in prefix)
                Write(ref target, nal);
        for (; pending; pending = nals.MoveNext())
            Write(ref target, data[nals.Current]);
        if (!target.IsEmpty)
            throw new ArgumentException("Destination size does not match the Annex B size.", nameof(destination));

        static void Write(ref Span<byte> destination, ReadOnlySpan<byte> nal)
        {
            BinaryPrimitives.WriteInt32BigEndian(destination, 1);
            nal.CopyTo(destination[4..]);
            destination = destination[(4 + nal.Length)..];
        }
    }

    /// <summary>
    /// 四字节长度前缀原地改为四字节起始码，NAL 顺序与内容不变，结果与不插入参数集的 <see cref="ToAnnexB"/> 相同。
    /// 先校验全部长度再改写，长度不合法时抛出且不修改数据。
    /// </summary>
    public static void ConvertToAnnexBInPlace(Span<byte> data)
    {
        foreach (var _ in EnumerateNals(data, 4)) { }
        for (var position = 0; position < data.Length;)
        {
            var size = BinaryPrimitives.ReadInt32BigEndian(data[position..]);
            BinaryPrimitives.WriteInt32BigEndian(data[position..], 1);
            position += 4 + size;
        }
    }

    /// <summary>从 avcC/hvcC 或 Annex B 附加数据中取出参数集；返回长度字段字节数（Annex B 为 0）。</summary>
    public static int ReadParameterSets(ReadOnlySpan<byte> extradata, bool hevc, List<byte[]> sets)
    {
        if (extradata.IsEmpty)
            return 0;
        if (extradata[0] != 1)
        {
            foreach (var range in SplitNals(extradata, 0))
                AddParameterSet(extradata[range], hevc, sets);
            return 0;
        }
        try
        {
            if (hevc)
            {
                var lengthSize = (extradata[21] & 3) + 1;
                var position = 23;
                for (var array = 0; array < extradata[22]; array++)
                {
                    var count = BinaryPrimitives.ReadUInt16BigEndian(extradata[(position + 1)..]);
                    position += 3;
                    for (var i = 0; i < count; i++)
                    {
                        var size = BinaryPrimitives.ReadUInt16BigEndian(extradata[position..]);
                        AddParameterSet(extradata.Slice(position + 2, size), hevc, sets);
                        position += 2 + size;
                    }
                }
                return lengthSize;
            }
            var avcLength = (extradata[4] & 3) + 1;
            var offset = 5;
            for (var kind = 0; kind < 2; kind++)
            {
                var count = kind == 0 ? extradata[offset++] & 0x1F : extradata[offset++];
                for (var i = 0; i < count; i++)
                {
                    var size = BinaryPrimitives.ReadUInt16BigEndian(extradata[offset..]);
                    AddParameterSet(extradata.Slice(offset + 2, size), hevc, sets);
                    offset += 2 + size;
                }
            }
            return avcLength;
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("Invalid decoder configuration record.", exception);
        }
    }

    /// <summary>加入不重复的参数集，保持 VPS、SPS、PPS 的顺序。</summary>
    public static void AddParameterSet(ReadOnlySpan<byte> nal, bool hevc, List<byte[]> sets)
    {
        var order = ParameterSetOrder(NalType(nal, hevc), hevc);
        if (order < 0)
            return;
        foreach (var existing in sets)
            if (nal.SequenceEqual(existing))
                return;
        var index = sets.FindIndex(set => ParameterSetOrder(NalType(set, hevc), hevc) > order);
        sets.Insert(index < 0 ? sets.Count : index, nal.ToArray());
    }

    public static IEnumerable<byte[]> OfType(IEnumerable<byte[]> sets, bool hevc, int type) =>
        sets.Where(set => NalType(set, hevc) == type);

    /// <summary>H.264 seq_parameter_set_id 位于 profile/constraint/level 三个字节之后。</summary>
    public static int ReadSpsId(ReadOnlySpan<byte> sps) => (int)new BitReader(Unescape(sps[1..]), 24).ReadUe();

    public static (int PpsId, int SpsId) ReadPpsIds(ReadOnlySpan<byte> pps)
    {
        var reader = new BitReader(Unescape(pps[1..]), 0);
        var ppsId = (int)reader.ReadUe();
        return (ppsId, (int)reader.ReadUe());
    }

    /// <summary>HEVC SPS 的 general_tier_flag：两字节 NAL 头与 VPS 编号字节之后的第一个 profile 字节。</summary>
    public static bool IsHevcHighTier(ReadOnlySpan<byte> sps) => sps.Length > 3 && (sps[3] & 0x20) != 0;

    /// <summary>MPEG-2 包内首个图像之前的头信息：是否带序列头、图像类型（1=I、2=P、3=B）、GOP 头的 closed_gop。</summary>
    public readonly record struct Mpeg2Picture(bool SequenceHeader, int PictureType, bool? ClosedGop);

    public static Mpeg2Picture ReadMpeg2(ReadOnlySpan<byte> data)
    {
        var sequence = false;
        bool? closed = null;
        for (var start = FindStartCode(data, 0); start >= 0 && start + 5 < data.Length; start = FindStartCode(data, start + 3))
        {
            switch (data[start + 3])
            {
                case 0xB3:
                    sequence = true;
                    break;
                case 0xB8 when start + 7 < data.Length:
                    // time_code 25 位之后依次为 closed_gop、broken_link。
                    closed = (data[start + 7] & 0x40) != 0;
                    break;
                case 0x00:
                    return new Mpeg2Picture(sequence, (data[start + 5] >> 3) & 7, closed);
            }
        }
        return new Mpeg2Picture(sequence, 0, closed);
    }

    /// <summary>
    /// 把首个 GOP 头标为 closed_gop=1、broken_link=0。只在已移除该 I 帧之后的前导 B 帧时使用，此时该标志成立。
    /// </summary>
    public static bool CloseMpeg2Gop(Span<byte> data)
    {
        for (var start = FindStartCode(data, 0); start >= 0 && start + 7 < data.Length; start = FindStartCode(data, start + 3))
        {
            if (data[start + 3] == 0x00)
                return false;
            if (data[start + 3] != 0xB8)
                continue;
            data[start + 7] = (byte)((data[start + 7] | 0x40) & ~0x20);
            return true;
        }
        return false;
    }

    private static byte[] Unescape(ReadOnlySpan<byte> data)
    {
        // 只需要开头几个字段，限制拷贝长度。
        var limit = Math.Min(data.Length, 64);
        var output = new List<byte>(limit);
        var zeros = 0;
        for (var i = 0; i < limit; i++)
        {
            if (zeros >= 2 && data[i] == 3)
            {
                zeros = 0;
                continue;
            }
            zeros = data[i] == 0 ? zeros + 1 : 0;
            output.Add(data[i]);
        }
        return output.ToArray();
    }

    private static int FindStartCode(ReadOnlySpan<byte> data, int from)
    {
        if (from < 0 || from >= data.Length)
            return -1;
        var index = data[from..].IndexOf((ReadOnlySpan<byte>)[0, 0, 1]);
        return index < 0 ? -1 : index + from;
    }

    private ref struct BitReader(byte[] data, int bitOffset)
    {
        private int position = bitOffset;

        private int ReadBit()
        {
            if (position >= data.Length * 8)
                throw new InvalidDataException("Truncated parameter set.");
            var bit = (data[position >> 3] >> (7 - (position & 7))) & 1;
            position++;
            return bit;
        }

        public uint ReadUe()
        {
            var zeros = 0;
            while (ReadBit() == 0)
                if (++zeros > 31)
                    throw new InvalidDataException("Invalid Exp-Golomb code.");
            uint value = 0;
            for (var i = 0; i < zeros; i++)
                value = (value << 1) | (uint)ReadBit();
            return (1u << zeros) - 1 + value;
        }
    }
}
