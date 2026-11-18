using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TSCutter.GUI.Utils.Media;

/// <summary>
/// 把重编码段的 HEVC 参数集改用源码流未用的编号，使两组参数集能放进同一个样本描述（hvc1 的 hvcC）：
/// VPS、SPS、PPS 的编号及相互引用改为新编号，切片头中的 slice_pic_parameter_set_id 随之改写。
/// 编码器与源的参数集编号通常都是 0，否则只能分成两个样本描述，而只读取首个样本描述或忽略中途切换的工具会用错参数集。
/// 只改写切片头的比特：切片头以 byte_alignment() 结束、末字节含对齐位 1，其后的切片数据（含防伪字节，
/// 入口点偏移按其计算）原样保留。解析覆盖编码器可能输出的切片头语法；遇到参考列表修改、屏幕内容编码扩展等
/// 改写不了的语法时抛出 <see cref="InvalidDataException"/>。
/// </summary>
internal sealed class HevcParameterSetRenumbering
{
    private readonly byte[][] encoded;
    private readonly Sps sps;
    private readonly Pps pps;

    /// <summary>改号后的编码器参数集（VPS、SPS、PPS），与源参数集一起放进样本描述。</summary>
    public IReadOnlyList<byte[]> ParameterSets { get; }

    public int VpsId { get; }
    public int SpsId { get; }
    public int PpsId { get; }

    /// <param name="encodedSets">编码器的参数集（各一个 VPS、SPS、PPS）。</param>
    /// <param name="originalSets">源参数集：新编号避开其中已用的编号。</param>
    public HevcParameterSetRenumbering(IReadOnlyList<byte[]> encodedSets, IReadOnlyList<byte[]> originalSets)
    {
        byte[] Single(int type) => encodedSets.Where(set => VideoBitstream.NalType(set, true) == type).ToList() is [var set]
            ? set : throw new InvalidDataException("Missing or repeated encoder parameter set.");
        var vps = Single(32);
        var spsNal = Single(33);
        var ppsNal = Single(34);
        encoded = [vps, spsNal, ppsNal];
        sps = Sps.Parse(Rbsp.Unescape(spsNal).Data);
        pps = Pps.Parse(Rbsp.Unescape(ppsNal).Data);
        VpsId = FirstUnused(originalSets.Where(set => VideoBitstream.NalType(set, true) == 32).Select(VpsIdOf), 16);
        SpsId = FirstUnused(originalSets.Where(set => VideoBitstream.NalType(set, true) == 33)
            .Select(set => Sps.ReadId(Rbsp.Unescape(set).Data)), 16);
        PpsId = FirstUnused(originalSets.Where(set => VideoBitstream.NalType(set, true) == 34)
            .Select(set => Pps.ReadIds(Rbsp.Unescape(set).Data).Id), 64);
        ParameterSets = [RewriteVps(vps), RewriteSps(spsNal), RewritePps(ppsNal)];
    }

    private static int VpsIdOf(byte[] vps) =>
        vps.Length > 2 ? vps[2] >> 4 : throw new InvalidDataException("Truncated VPS.");

    private static int FirstUnused(IEnumerable<int> used, int limit)
    {
        var set = used.ToHashSet();
        for (var id = 0; id < limit; id++)
            if (!set.Contains(id))
                return id;
        throw new InvalidDataException("No unused parameter set id.");
    }

    /// <summary>
    /// 改写编码器输出的一个包（Annex B）：切片改用新的 PPS 编号；带内参数集须与构造时的编码器参数集相同，
    /// 不再写入（hvc1 样本中不放参数集，已在样本描述中）；其余 NAL 不变。
    /// </summary>
    public byte[] Rewrite(ReadOnlySpan<byte> packet)
    {
        var output = new List<byte>(packet.Length + 16);
        foreach (var range in VideoBitstream.EnumerateNals(packet, 0))
        {
            var nal = packet[range];
            var type = VideoBitstream.NalType(nal, true);
            if (type is 32 or 33 or 34)
            {
                if (!nal.SequenceEqual(encoded[type - 32]))
                    throw new InvalidDataException("Encoder parameter sets changed.");
                continue;
            }
            output.AddRange((ReadOnlySpan<byte>)[0, 0, 0, 1]);
            if (type < 32)
                output.AddRange(RewriteSlice(nal, type));
            else
                output.AddRange(nal);
        }
        return output.ToArray();
    }

    private byte[] RewriteVps(byte[] nal)
    {
        var (data, _) = Rbsp.Unescape(nal);
        var writer = new BitWriter();
        writer.Copy(data, 0, 16);
        writer.Write(VpsId, 4);
        writer.Copy(data, 20, Rbsp.StopBit(data));
        return writer.Finish();
    }

    private byte[] RewriteSps(byte[] nal)
    {
        var (data, _) = Rbsp.Unescape(nal);
        var writer = new BitWriter();
        writer.Copy(data, 0, 16);
        writer.Write(VpsId, 4);
        writer.Copy(data, 20, sps.IdStart);
        writer.WriteUe(SpsId);
        writer.Copy(data, sps.IdEnd, Rbsp.StopBit(data));
        return writer.Finish();
    }

    private byte[] RewritePps(byte[] nal)
    {
        var (data, _) = Rbsp.Unescape(nal);
        var writer = new BitWriter();
        writer.Copy(data, 0, 16);
        writer.WriteUe(PpsId);
        writer.WriteUe(SpsId);
        writer.Copy(data, Pps.ReadIds(data).End, Rbsp.StopBit(data));
        return writer.Finish();
    }

    private byte[] RewriteSlice(ReadOnlySpan<byte> nal, int type)
    {
        // 切片头通常在前几百字节内（4K 波前并行的入口点也只有几十个），先只反转义开头一段，不够时再扩大。
        for (var limit = 1024; ; limit *= 4)
        {
            var (data, positions) = Rbsp.Unescape(nal[..Math.Min(nal.Length, limit)]);
            int idStart, idEnd, end;
            try
            {
                (idStart, idEnd, end) = SliceHeader(data, type);
            }
            catch (EndOfStreamException) when (limit < nal.Length)
            {
                continue;
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidDataException("Truncated slice header.", exception);
            }
            // 对齐位（头部最后一个 1）之前的比特原样保留，之后补对齐位重新对齐。
            var writer = new BitWriter();
            writer.Copy(data, 0, idStart);
            writer.WriteUe(PpsId);
            writer.Copy(data, idEnd, Rbsp.LastOne(data, end * 8));
            var header = writer.Finish();
            var rest = nal[(positions[end - 1] + 1)..];
            var result = new byte[header.Length + rest.Length];
            header.CopyTo(result, 0);
            rest.CopyTo(result.AsSpan(header.Length));
            return result;
        }
    }

    /// <summary>解析切片段头，返回 PPS 编号的比特区间与头部（含 byte_alignment）结束的字节位置。</summary>
    private (int IdStart, int IdEnd, int End) SliceHeader(byte[] data, int type)
    {
        var reader = new BitReader(data, 16);
        var first = reader.Flag();
        if (type is >= 16 and <= 23)
            reader.Skip(1); // no_output_of_prior_pics_flag
        var idStart = reader.Position;
        if (reader.Ue() != pps.Id)
            throw new InvalidDataException("Slice references an unknown PPS.");
        var idEnd = reader.Position;
        var dependent = false;
        if (!first)
        {
            if (pps.DependentSliceSegments)
                dependent = reader.Flag();
            reader.Skip(CeilLog2(sps.PicSizeInCtbs));
        }
        if (!dependent)
        {
            reader.Skip(pps.ExtraSliceHeaderBits);
            var sliceType = reader.Ue(); // 0 = B，1 = P，2 = I
            if (pps.OutputFlagPresent)
                reader.Skip(1);
            if (sps.SeparateColourPlane)
                reader.Skip(2);
            var temporalMvp = false;
            if (type is not (19 or 20))
            {
                reader.Skip(sps.Log2MaxPocLsb);
                if (!reader.Flag())
                    Sps.ShortTermRefPicSet(reader, sps.NumShortTermRefPicSets, sps.NumDeltaPocs);
                else if (sps.NumShortTermRefPicSets > 1)
                    reader.Skip(CeilLog2(sps.NumShortTermRefPicSets));
                if (sps.LongTermRefPicsPresent)
                {
                    var fromSps = sps.NumLongTermRefPicsSps > 0 ? (int)reader.Ue() : 0;
                    var pictures = (int)reader.Ue();
                    for (var i = 0; i < fromSps + pictures; i++)
                    {
                        if (i < fromSps)
                        {
                            if (sps.NumLongTermRefPicsSps > 1)
                                reader.Skip(CeilLog2(sps.NumLongTermRefPicsSps));
                        }
                        else
                            reader.Skip(sps.Log2MaxPocLsb + 1);
                        if (reader.Flag())
                            reader.Ue();
                    }
                }
                if (sps.TemporalMvp)
                    temporalMvp = reader.Flag();
            }
            bool saoLuma = false, saoChroma = false;
            if (sps.Sao)
            {
                saoLuma = reader.Flag();
                if (sps.ChromaArrayType != 0)
                    saoChroma = reader.Flag();
            }
            if (sliceType is 0 or 1)
            {
                var l0 = pps.NumRefIdxL0Default;
                var l1 = pps.NumRefIdxL1Default;
                if (reader.Flag())
                {
                    l0 = (int)reader.Ue();
                    if (sliceType == 0)
                        l1 = (int)reader.Ue();
                }
                if (pps.ListsModificationPresent)
                    throw new InvalidDataException("Reference picture list modification is not supported.");
                if (sliceType == 0)
                    reader.Skip(1); // mvd_l1_zero_flag
                if (pps.CabacInitPresent)
                    reader.Skip(1);
                if (temporalMvp)
                {
                    var fromL0 = sliceType != 0 || reader.Flag();
                    if (fromL0 && l0 > 0 || !fromL0 && l1 > 0)
                        reader.Ue();
                }
                if (pps.WeightedPred && sliceType == 1 || pps.WeightedBipred && sliceType == 0)
                    PredWeightTable(reader, l0, sliceType == 0 ? l1 : -1);
                reader.Ue(); // five_minus_max_num_merge_cand
            }
            reader.Se(); // slice_qp_delta
            if (pps.SliceChromaQpOffsetsPresent)
            {
                reader.Se();
                reader.Se();
            }
            if (pps.ChromaQpOffsetListEnabled)
                reader.Skip(1);
            var deblockingDisabled = pps.DeblockingDisabled;
            if (pps.DeblockingOverrideEnabled && reader.Flag())
            {
                deblockingDisabled = reader.Flag();
                if (!deblockingDisabled)
                {
                    reader.Se();
                    reader.Se();
                }
            }
            if (pps.LoopFilterAcrossSlices && (saoLuma || saoChroma || !deblockingDisabled))
                reader.Skip(1);
        }
        if (pps.Tiles || pps.EntropyCodingSync)
        {
            var offsets = reader.Ue();
            if (offsets > 0)
            {
                var length = (int)reader.Ue() + 1;
                for (var i = 0u; i < offsets; i++)
                    reader.Skip(length);
            }
        }
        if (pps.SliceHeaderExtensionPresent)
            reader.Skip(checked((int)reader.Ue() * 8));
        if (!reader.Flag())
            throw new InvalidDataException("Missing slice header alignment bit.");
        while ((reader.Position & 7) != 0)
            if (reader.Flag())
                throw new InvalidDataException("Invalid slice header alignment.");
        return (idStart, idEnd, reader.Position >> 3);
    }

    private void PredWeightTable(BitReader reader, int l0, int l1)
    {
        reader.Ue(); // luma_log2_weight_denom
        var chroma = sps.ChromaArrayType != 0;
        if (chroma)
            reader.Se();
        foreach (var count in l1 >= 0 ? new[] { l0, l1 } : [l0])
        {
            var luma = new bool[count + 1];
            var chromaFlags = new bool[count + 1];
            for (var i = 0; i <= count; i++)
                luma[i] = reader.Flag();
            if (chroma)
                for (var i = 0; i <= count; i++)
                    chromaFlags[i] = reader.Flag();
            for (var i = 0; i <= count; i++)
            {
                if (luma[i])
                {
                    reader.Se();
                    reader.Se();
                }
                if (chromaFlags[i])
                    for (var j = 0; j < 4; j++)
                        reader.Se();
            }
        }
    }

    private static int CeilLog2(int value)
    {
        var bits = 0;
        while ((1 << bits) < value)
            bits++;
        return bits;
    }

    /// <summary>切片头改写需要的 SPS 字段。</summary>
    private sealed class Sps
    {
        public int IdStart, IdEnd;
        public int ChromaArrayType;
        public bool SeparateColourPlane;
        public int Log2MaxPocLsb;
        public int NumShortTermRefPicSets;
        public int[] NumDeltaPocs = [];
        public bool LongTermRefPicsPresent;
        public int NumLongTermRefPicsSps;
        public bool TemporalMvp;
        public bool Sao;
        public int PicSizeInCtbs;

        /// <summary>读到 sps_seq_parameter_set_id 为止（源 SPS 只需要编号）。</summary>
        private static BitReader ReadToId(byte[] data, out int maxSubLayers)
        {
            var reader = new BitReader(data, 16);
            reader.Skip(4); // sps_video_parameter_set_id
            maxSubLayers = (int)reader.Bits(3);
            reader.Skip(1);
            ProfileTierLevel(reader, maxSubLayers);
            return reader;
        }

        public static int ReadId(byte[] data) => (int)ReadToId(data, out _).Ue();

        public static Sps Parse(byte[] data)
        {
            var reader = ReadToId(data, out var maxSubLayers);
            var sps = new Sps { IdStart = reader.Position };
            reader.Ue();
            sps.IdEnd = reader.Position;
            var chroma = (int)reader.Ue();
            sps.SeparateColourPlane = chroma == 3 && reader.Flag();
            sps.ChromaArrayType = sps.SeparateColourPlane ? 0 : chroma;
            var width = (int)reader.Ue();
            var height = (int)reader.Ue();
            if (reader.Flag())
                for (var i = 0; i < 4; i++)
                    reader.Ue();
            reader.Ue();
            reader.Ue();
            sps.Log2MaxPocLsb = (int)reader.Ue() + 4;
            var ordering = reader.Flag();
            for (var i = ordering ? 0 : maxSubLayers; i <= maxSubLayers; i++)
            {
                reader.Ue();
                reader.Ue();
                reader.Ue();
            }
            var minCb = (int)reader.Ue() + 3;
            var ctb = minCb + (int)reader.Ue();
            for (var i = 0; i < 4; i++)
                reader.Ue();
            if (reader.Flag() && reader.Flag())
                ScalingListData(reader);
            reader.Skip(1); // amp_enabled_flag
            sps.Sao = reader.Flag();
            if (reader.Flag())
            {
                reader.Skip(8);
                reader.Ue();
                reader.Ue();
                reader.Skip(1);
            }
            sps.NumShortTermRefPicSets = (int)reader.Ue();
            if (sps.NumShortTermRefPicSets > 64)
                throw new InvalidDataException("Too many short-term reference picture sets.");
            sps.NumDeltaPocs = new int[sps.NumShortTermRefPicSets + 1];
            for (var i = 0; i < sps.NumShortTermRefPicSets; i++)
                sps.NumDeltaPocs[i] = ShortTermRefPicSet(reader, i, sps.NumDeltaPocs, sps.NumShortTermRefPicSets);
            sps.LongTermRefPicsPresent = reader.Flag();
            if (sps.LongTermRefPicsPresent)
            {
                sps.NumLongTermRefPicsSps = (int)reader.Ue();
                for (var i = 0; i < sps.NumLongTermRefPicsSps; i++)
                    reader.Skip(sps.Log2MaxPocLsb + 1);
            }
            sps.TemporalMvp = reader.Flag();
            var size = 1 << ctb;
            sps.PicSizeInCtbs = (width + size - 1) / size * ((height + size - 1) / size);
            return sps;
        }

        /// <summary>切片头中的参考画面集（编号为 SPS 中的集合数）。</summary>
        public static void ShortTermRefPicSet(BitReader reader, int count, int[] numDeltaPocs) =>
            ShortTermRefPicSet(reader, count, numDeltaPocs, count);

        /// <summary>st_ref_pic_set(index)：返回该集合的 NumDeltaPocs。</summary>
        private static int ShortTermRefPicSet(BitReader reader, int index, int[] numDeltaPocs, int count)
        {
            if (index != 0 && reader.Flag())
            {
                var delta = index == count ? (int)reader.Ue() + 1 : 1;
                if (delta > index)
                    throw new InvalidDataException("Invalid reference picture set prediction.");
                reader.Skip(1);
                reader.Ue();
                var total = 0;
                for (var j = 0; j <= numDeltaPocs[index - delta]; j++)
                    if (reader.Flag() || reader.Flag())
                        total++;
                return total;
            }
            var negative = reader.Ue();
            var positive = reader.Ue();
            if (negative + positive > 32)
                throw new InvalidDataException("Invalid reference picture set.");
            for (var i = 0u; i < negative + positive; i++)
            {
                reader.Ue();
                reader.Skip(1);
            }
            return (int)(negative + positive);
        }

        private static void ProfileTierLevel(BitReader reader, int maxSubLayers)
        {
            reader.Skip(96); // general_profile_space … general_level_idc
            var profile = new bool[maxSubLayers];
            var level = new bool[maxSubLayers];
            for (var i = 0; i < maxSubLayers; i++)
            {
                profile[i] = reader.Flag();
                level[i] = reader.Flag();
            }
            if (maxSubLayers > 0)
                reader.Skip(2 * (8 - maxSubLayers));
            for (var i = 0; i < maxSubLayers; i++)
            {
                if (profile[i])
                    reader.Skip(88);
                if (level[i])
                    reader.Skip(8);
            }
        }
    }

    /// <summary>切片头改写需要的 PPS 字段。</summary>
    private sealed class Pps
    {
        public int Id;
        public bool DependentSliceSegments, OutputFlagPresent, CabacInitPresent, SliceChromaQpOffsetsPresent;
        public int ExtraSliceHeaderBits, NumRefIdxL0Default, NumRefIdxL1Default;
        public bool WeightedPred, WeightedBipred, Tiles, EntropyCodingSync, LoopFilterAcrossSlices;
        public bool DeblockingOverrideEnabled, DeblockingDisabled, ListsModificationPresent, SliceHeaderExtensionPresent;
        public bool ChromaQpOffsetListEnabled;

        public static (int Id, int End) ReadIds(byte[] data)
        {
            var reader = new BitReader(data, 16);
            var id = (int)reader.Ue();
            reader.Ue();
            return (id, reader.Position);
        }

        public static Pps Parse(byte[] data)
        {
            var reader = new BitReader(data, 16);
            var pps = new Pps { Id = (int)reader.Ue() };
            reader.Ue();
            pps.DependentSliceSegments = reader.Flag();
            pps.OutputFlagPresent = reader.Flag();
            pps.ExtraSliceHeaderBits = (int)reader.Bits(3);
            reader.Skip(1);
            pps.CabacInitPresent = reader.Flag();
            pps.NumRefIdxL0Default = (int)reader.Ue();
            pps.NumRefIdxL1Default = (int)reader.Ue();
            reader.Se();
            reader.Skip(1);
            var transformSkip = reader.Flag();
            if (reader.Flag())
                reader.Ue();
            reader.Se();
            reader.Se();
            pps.SliceChromaQpOffsetsPresent = reader.Flag();
            pps.WeightedPred = reader.Flag();
            pps.WeightedBipred = reader.Flag();
            reader.Skip(1);
            pps.Tiles = reader.Flag();
            pps.EntropyCodingSync = reader.Flag();
            if (pps.Tiles)
            {
                var columns = reader.Ue();
                var rows = reader.Ue();
                if (!reader.Flag())
                    for (var i = 0u; i < columns + rows; i++)
                        reader.Ue();
                reader.Skip(1);
            }
            pps.LoopFilterAcrossSlices = reader.Flag();
            if (reader.Flag())
            {
                pps.DeblockingOverrideEnabled = reader.Flag();
                pps.DeblockingDisabled = reader.Flag();
                if (!pps.DeblockingDisabled)
                {
                    reader.Se();
                    reader.Se();
                }
            }
            if (reader.Flag())
                ScalingListData(reader);
            pps.ListsModificationPresent = reader.Flag();
            reader.Ue();
            pps.SliceHeaderExtensionPresent = reader.Flag();
            if (reader.Flag())
            {
                var range = reader.Flag();
                reader.Skip(2); // multilayer、3D 扩展：只影响非基础层
                if (reader.Flag())
                    throw new InvalidDataException("Screen content coding extension is not supported.");
                reader.Skip(4);
                if (range)
                {
                    if (transformSkip)
                        reader.Ue();
                    reader.Skip(1);
                    pps.ChromaQpOffsetListEnabled = reader.Flag();
                }
            }
            return pps;
        }
    }

    private static void ScalingListData(BitReader reader)
    {
        for (var size = 0; size < 4; size++)
        for (var matrix = 0; matrix < 6; matrix += size == 3 ? 3 : 1)
        {
            if (!reader.Flag())
            {
                reader.Ue();
                continue;
            }
            if (size > 1)
                reader.Se();
            for (var i = Math.Min(64, 1 << (4 + (size << 1))); i > 0; i--)
                reader.Se();
        }
    }

    /// <summary>RBSP 与带防伪字节的 NAL 之间的转换。</summary>
    private static class Rbsp
    {
        /// <summary>去掉防伪字节；Positions[i] 为第 i 个 RBSP 字节在原 NAL 中的位置。</summary>
        public static (byte[] Data, int[] Positions) Unescape(ReadOnlySpan<byte> nal)
        {
            var data = new byte[nal.Length];
            var positions = new int[nal.Length];
            var count = 0;
            var zeros = 0;
            for (var i = 0; i < nal.Length; i++)
            {
                if (zeros >= 2 && nal[i] == 3)
                {
                    zeros = 0;
                    continue;
                }
                zeros = nal[i] == 0 ? zeros + 1 : 0;
                data[count] = nal[i];
                positions[count++] = i;
            }
            return (data[..count], positions[..count]);
        }

        public static byte[] Escape(ReadOnlySpan<byte> data)
        {
            var output = new List<byte>(data.Length + 8);
            var zeros = 0;
            foreach (var value in data)
            {
                if (zeros >= 2 && value <= 3)
                {
                    output.Add(3);
                    zeros = 0;
                }
                output.Add(value);
                zeros = value == 0 ? zeros + 1 : 0;
            }
            return output.ToArray();
        }

        /// <summary>RBSP 停止位（rbsp_trailing_bits 的 1）的比特位置。</summary>
        public static int StopBit(byte[] data) => LastOne(data, data.Length * 8);

        /// <summary>end 比特之前最后一个为 1 的比特位置。</summary>
        public static int LastOne(byte[] data, int end)
        {
            for (var bit = end - 1; bit >= 0; bit--)
                if ((data[bit >> 3] >> (7 - (bit & 7)) & 1) != 0)
                    return bit;
            throw new InvalidDataException("Missing RBSP stop bit.");
        }
    }

    private sealed class BitReader(byte[] data, int position)
    {
        public int Position => position;

        public uint Bits(int count)
        {
            if (count > 32 || position + count > data.Length * 8)
                throw new EndOfStreamException();
            var value = 0u;
            for (var i = 0; i < count; i++, position++)
                value = value << 1 | (uint)(data[position >> 3] >> (7 - (position & 7)) & 1);
            return value;
        }

        public bool Flag() => Bits(1) != 0;

        public void Skip(int count)
        {
            if (count < 0 || position + count > data.Length * 8)
                throw new EndOfStreamException();
            position += count;
        }

        public uint Ue()
        {
            var zeros = 0;
            while (!Flag())
                if (++zeros > 31)
                    throw new InvalidDataException("Invalid Exp-Golomb code.");
            return (uint)((1L << zeros) - 1 + Bits(zeros));
        }

        public int Se()
        {
            var value = Ue();
            return (value & 1) != 0 ? (int)((value + 1) / 2) : -(int)(value / 2);
        }
    }

    private sealed class BitWriter
    {
        private readonly List<byte> bytes = [];
        private int bits;

        public void Write(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
                Put((value >> i & 1) != 0);
        }

        public void WriteUe(int value)
        {
            var code = (uint)value + 1;
            var length = 32 - System.Numerics.BitOperations.LeadingZeroCount(code);
            for (var i = 0; i < length - 1; i++)
                Put(false);
            for (var i = length - 1; i >= 0; i--)
                Put((code >> i & 1) != 0);
        }

        public void Copy(byte[] data, int from, int to)
        {
            for (var bit = from; bit < to; bit++)
                Put((data[bit >> 3] >> (7 - (bit & 7)) & 1) != 0);
        }

        /// <summary>写入停止位（切片头即对齐位）并补零到字节边界，转为带防伪字节的 NAL 数据。</summary>
        public byte[] Finish()
        {
            Put(true);
            while ((bits & 7) != 0)
                Put(false);
            return Rbsp.Escape(bytes.ToArray());
        }

        private void Put(bool bit)
        {
            if ((bits & 7) == 0)
                bytes.Add(0);
            if (bit)
                bytes[^1] |= (byte)(0x80 >> (bits & 7));
            bits++;
        }
    }
}
