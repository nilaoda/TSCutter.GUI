using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace TSCutter.GUI.Utils.Media;

/// <summary>
/// 在普通（非 IDR）I 帧处衔接 H.264 所需的码流处理：参数集与切片头解析，以及把复制区改写为以 IDR 开始的合法码流。
/// 广播 H.264 常只有非 IDR 的 I 帧（开放 GOP）。参数集只能在 IDR 处切换，重编码段使用自己的参数集，
/// 因此复制区首个 I 帧改写为 IDR；IDR 的 frame_num 必须为 0，复制区每个切片的 frame_num 减去该 I 帧的值
/// （取模，位宽不变）；指向 I 帧之前画面的 MMCO 操作删除。切片数据不变：CABAC 的切片数据字节对齐，
/// 切片头长度变化由对齐位吸收；CAVLC 按位平移。前导画面（解码在 I 后、显示在前）由调用方丢弃并重新编码。
/// </summary>
internal static class H264Splice
{
    internal sealed record Sps(int Id, int ChromaArrayType, bool SeparateColourPlane, int Log2MaxFrameNum, int PocType,
        int Log2MaxPocLsb, bool DeltaPicOrderAlwaysZero, int MaxNumRefFrames, bool FrameMbsOnly)
    {
        public int MaxFrameNum => 1 << Log2MaxFrameNum;
    }

    internal sealed record Pps(int Id, int SpsId, bool Cabac, bool BottomFieldPicOrderPresent, int NumRefIdxL0Default,
        int NumRefIdxL1Default, bool WeightedPred, int WeightedBipredIdc, bool DeblockingControlPresent,
        bool RedundantPicCntPresent);

    /// <summary>按编号保存的 SPS/PPS；带内参数集出现时更新。不支持的参数集（如切片组）不保存，引用它的切片无法解析。</summary>
    internal sealed class ParameterSets
    {
        private readonly Dictionary<int, Sps> sps = new();
        private readonly Dictionary<int, Pps> pps = new();

        public void Add(ReadOnlySpan<byte> nal)
        {
            if (nal.Length < 2)
                return;
            var type = nal[0] & 0x1F;
            if (type is not (7 or 8))
                return;
            var buffer = ArrayPool<byte>.Shared.Rent(nal.Length);
            try
            {
                var length = Unescape(nal[1..], buffer);
                var reader = new BitReader(buffer.AsSpan(0, length));
                if (type == 7 && TryParseSps(ref reader) is { } parsed)
                    sps[parsed.Id] = parsed;
                else if (type == 8)
                {
                    var (id, value) = TryParsePps(ref reader);
                    if (value is not null)
                        pps[id] = value;
                    else if (id >= 0)
                        pps.Remove(id);
                }
            }
            catch (InvalidDataException) { }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        public void AddAll(IEnumerable<byte[]> nals)
        {
            foreach (var nal in nals)
                Add(nal);
        }

        public bool TryGet(int ppsId, out Sps? sequence, out Pps? picture)
        {
            sequence = null;
            return pps.TryGetValue(ppsId, out picture) && sps.TryGetValue(picture.SpsId, out sequence);
        }
    }

    internal readonly record struct Mmco(int Op, int A, int B);

    /// <summary>画面级信息：是否场画面、底场、frame_num、是否作参考。</summary>
    internal readonly record struct PictureInfo(bool Field, bool Bottom, int FrameNum, bool Reference);

    /// <summary>读取访问单元首个切片的画面信息，并以其中的带内参数集更新 sets；无法解析时返回 null。</summary>
    public static PictureInfo? ReadPicture(ReadOnlySpan<byte> data, int lengthSize, ParameterSets sets, List<Mmco> scratch)
    {
        try
        {
            foreach (var range in VideoBitstream.EnumerateNals(data, lengthSize))
            {
                var nal = data[range];
                var type = nal[0] & 0x1F;
                if (type is 7 or 8)
                    sets.Add(nal);
                if (type is not (1 or 5))
                    continue;
                // 场标志位于切片头前部，读取前 64 字节足够；超出时 TryParseSlice 返回 false，只取前部字段。
                Span<byte> rbsp = stackalloc byte[256];
                var length = Unescape(nal[1..Math.Min(nal.Length, 257)], rbsp);
                return TryParseSlice(rbsp[..length], nal[0], sets, scratch, out var header) ||
                       header.Sps is not null && header.AfterFieldBit > 0
                    ? new PictureInfo(header.Field, header.Bottom, header.FrameNum, header.NalRefIdc != 0)
                    : null;
            }
        }
        catch (InvalidDataException) { }
        return null;
    }

    /// <summary>访问单元的首个切片是否为 I/SI 切片（只读 first_mb_in_slice 与 slice_type，不需要参数集）。</summary>
    public static bool IsIntraPicture(ReadOnlySpan<byte> data, int lengthSize)
    {
        try
        {
            foreach (var range in VideoBitstream.EnumerateNals(data, lengthSize))
            {
                var nal = data[range];
                if ((nal[0] & 0x1F) is not (1 or 5))
                    continue;
                Span<byte> rbsp = stackalloc byte[16];
                var header = nal[1..Math.Min(nal.Length, 17)];
                var length = Unescape(header, rbsp);
                var reader = new BitReader(rbsp[..length]);
                reader.ReadUe();
                return reader.ReadUe() % 5 is 2 or 4;
            }
        }
        catch (InvalidDataException) { }
        return false;
    }

    /// <summary>切片头字段与关键位置（相对 NAL 头之后的 RBSP，以位计）。</summary>
    internal struct SliceHeader
    {
        public int NalRefIdc;
        public int NalType;
        public int FirstMb;
        /// <summary>slice_type % 5：0=P、1=B、2=I、3=SP、4=SI。</summary>
        public int SliceType;
        public Sps Sps;
        public Pps Pps;
        public int FrameNum;
        public bool Field;
        public bool Bottom;
        public int FrameNumBit;
        public int AfterFieldBit;
        public int AfterIdrBit;
        /// <summary>参考列表修改语法的位置与内容：两个列表的标志、列表 0 的操作数（操作存入调用方提供的列表）。</summary>
        public int ModificationStart;
        public int ModificationEnd;
        public bool ModificationL0;
        public bool ModificationL1;
        public int ModificationL0Count;
        public int MarkingStart;
        public int MarkingEnd;
        public int HeaderEnd;
        public bool Adaptive;
        public int NumRefIdxL0;
        public int NumRefIdxL1;
        public bool Intra => SliceType is 2 or 4;
    }

    /// <summary>解析切片头；mmco 收到自适应标记操作。不支持的语法（MVC、切片组等）返回 false。</summary>
    public static bool TryParseSlice(ReadOnlySpan<byte> rbsp, byte nalHeader, ParameterSets sets, List<Mmco> mmco,
        out SliceHeader header, List<(int Idc, int Value)>? modifications = null)
    {
        header = default;
        mmco.Clear();
        modifications?.Clear();
        try { return TryParseSliceCore(rbsp, nalHeader, sets, mmco, out header, modifications); }
        catch (InvalidDataException) { return false; }
    }

    private static bool TryParseSliceCore(ReadOnlySpan<byte> rbsp, byte nalHeader, ParameterSets sets, List<Mmco> mmco,
        out SliceHeader h, List<(int Idc, int Value)>? modifications)
    {
        h = default;
        h.NalRefIdc = (nalHeader >> 5) & 3;
        h.NalType = nalHeader & 0x1F;
        if (h.NalType is not (1 or 5))
            return false;
        var r = new BitReader(rbsp);
        h.FirstMb = (int)r.ReadUe();
        var sliceType = r.ReadUe();
        if (sliceType > 9)
            return false;
        h.SliceType = (int)(sliceType % 5);
        var ppsId = (int)r.ReadUe();
        if (!sets.TryGet(ppsId, out var sps, out var pps))
            return false;
        h.Sps = sps!;
        h.Pps = pps!;
        if (h.Sps.SeparateColourPlane)
            r.Skip(2);
        h.FrameNumBit = r.Position;
        h.FrameNum = (int)r.ReadBits(h.Sps.Log2MaxFrameNum);
        if (!h.Sps.FrameMbsOnly)
        {
            h.Field = r.ReadBit();
            if (h.Field)
                h.Bottom = r.ReadBit();
        }
        h.AfterFieldBit = r.Position;
        if (h.NalType == 5)
            r.ReadUe();
        h.AfterIdrBit = r.Position;
        if (h.Sps.PocType == 0)
        {
            r.Skip(h.Sps.Log2MaxPocLsb);
            if (h.Pps.BottomFieldPicOrderPresent && !h.Field)
                r.ReadSe();
        }
        if (h.Sps.PocType == 1 && !h.Sps.DeltaPicOrderAlwaysZero)
        {
            r.ReadSe();
            if (h.Pps.BottomFieldPicOrderPresent && !h.Field)
                r.ReadSe();
        }
        if (h.Pps.RedundantPicCntPresent)
            r.ReadUe();
        var b = h.SliceType == 1;
        var p = h.SliceType is 0 or 3;
        if (b)
            r.Skip(1);
        h.NumRefIdxL0 = h.Pps.NumRefIdxL0Default;
        h.NumRefIdxL1 = h.Pps.NumRefIdxL1Default;
        if (p || b)
        {
            if (r.ReadBit())
            {
                h.NumRefIdxL0 = checked((int)r.ReadUe() + 1);
                if (b)
                    h.NumRefIdxL1 = checked((int)r.ReadUe() + 1);
            }
            if (h.NumRefIdxL0 > 32 || h.NumRefIdxL1 > 32)
                return false;
        }
        h.ModificationStart = r.Position;
        if (!h.Intra)
        {
            h.ModificationL0 = ReadListModification(ref r, modifications);
            h.ModificationL0Count = modifications?.Count ?? 0;
            if (b)
                h.ModificationL1 = ReadListModification(ref r, modifications);
        }
        h.ModificationEnd = r.Position;
        if (h.Pps.WeightedPred && p || h.Pps.WeightedBipredIdc == 1 && b)
        {
            r.ReadUe();
            if (h.Sps.ChromaArrayType != 0)
                r.ReadUe();
            SkipWeights(ref r, h.NumRefIdxL0, h.Sps.ChromaArrayType != 0);
            if (b)
                SkipWeights(ref r, h.NumRefIdxL1, h.Sps.ChromaArrayType != 0);
        }
        h.MarkingStart = r.Position;
        if (h.NalRefIdc != 0)
        {
            if (h.NalType == 5)
                r.Skip(2);
            else if (h.Adaptive = r.ReadBit())
            {
                while (true)
                {
                    var op = (int)r.ReadUe();
                    if (op == 0)
                        break;
                    if (op > 6 || mmco.Count >= 66)
                        return false;
                    int a = 0, second = 0;
                    if (op is 1 or 3)
                        a = (int)r.ReadUe();
                    if (op == 2)
                        a = (int)r.ReadUe();
                    if (op is 3 or 6)
                        second = (int)r.ReadUe();
                    if (op == 4)
                        a = (int)r.ReadUe();
                    mmco.Add(new Mmco(op, a, second));
                }
            }
        }
        h.MarkingEnd = r.Position;
        if (h.Pps.Cabac && !h.Intra)
            r.ReadUe();
        r.ReadSe();
        if (h.SliceType is 3 or 4)
        {
            if (h.SliceType == 3)
                r.Skip(1);
            r.ReadSe();
        }
        if (h.Pps.DeblockingControlPresent && r.ReadUe() != 1)
        {
            r.ReadSe();
            r.ReadSe();
        }
        h.HeaderEnd = r.Position;
        return true;
    }

    private static bool ReadListModification(ref BitReader r, List<(int Idc, int Value)>? into)
    {
        if (!r.ReadBit())
            return false;
        for (var count = 0; ; count++)
        {
            var idc = (int)r.ReadUe();
            if (idc == 3)
                return true;
            if (idc > 2 || count > 64)
                throw new InvalidDataException("Invalid reference list modification.");
            var value = (int)r.ReadUe();
            into?.Add((idc, value));
        }
    }

    private static void SkipWeights(ref BitReader r, int count, bool chroma)
    {
        for (var i = 0; i < count; i++)
        {
            if (r.ReadBit())
            {
                r.ReadSe();
                r.ReadSe();
            }
            if (chroma && r.ReadBit())
                for (var j = 0; j < 4; j++)
                    r.ReadSe();
        }
    }

    private static Sps? TryParseSps(ref BitReader r)
    {
        var profile = (int)r.ReadBits(8);
        r.Skip(16);
        var id = (int)r.ReadUe();
        if (id > 31)
            return null;
        var chromaFormat = 1;
        var separate = false;
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            chromaFormat = (int)r.ReadUe();
            if (chromaFormat == 3)
                separate = r.ReadBit();
            r.ReadUe();
            r.ReadUe();
            r.Skip(1);
            if (r.ReadBit())
                for (var i = 0; i < (chromaFormat != 3 ? 8 : 12); i++)
                    if (r.ReadBit())
                        SkipScalingList(ref r, i < 6 ? 16 : 64);
        }
        var log2MaxFrameNum = (int)r.ReadUe() + 4;
        var pocType = (int)r.ReadUe();
        var log2MaxPocLsb = 0;
        var alwaysZero = false;
        if (pocType == 0)
            log2MaxPocLsb = (int)r.ReadUe() + 4;
        else if (pocType == 1)
        {
            alwaysZero = r.ReadBit();
            r.ReadSe();
            r.ReadSe();
            var cycle = r.ReadUe();
            if (cycle > 255)
                return null;
            for (var i = 0; i < cycle; i++)
                r.ReadSe();
        }
        else if (pocType != 2)
            return null;
        var maxRefs = (int)r.ReadUe();
        r.Skip(1);
        r.ReadUe();
        r.ReadUe();
        var frameMbsOnly = r.ReadBit();
        if (log2MaxFrameNum > 16 || log2MaxPocLsb > 16)
            return null;
        return new Sps(id, separate ? 0 : chromaFormat, separate, log2MaxFrameNum, pocType, log2MaxPocLsb, alwaysZero,
            maxRefs, frameMbsOnly);
    }

    /// <summary>
    /// 将 SPS 的 VUI 码流限制信息中的 max_num_reorder_frames 提高到 reorder（max_dec_frame_buffering 随之不小于它），
    /// 用于不带 B 帧的重编码段：广播编码器的 SPS 常不声明重排序深度，解码器按此前画面推测，
    /// 从无 B 帧的重编码段进入复制区时来不及加深重排序缓冲，会丢掉第一个乱序输出的画面。
    /// 声明的深度大于实际只增加输出延迟，码流仍然合规。SPS 没有码流限制信息（无法确定 DPB 上限）或已足够时原样返回；
    /// SPS 语法截断或无效时抛出 <see cref="InvalidDataException"/>。
    /// </summary>
    internal static byte[] WithReorderDepth(byte[] sps, int reorder)
    {
        if (reorder <= 0 || sps.Length < 2 || (sps[0] & 0x1F) != 7)
            return sps;
        var rbsp = new byte[sps.Length];
        var length = Unescape(sps.AsSpan(1), rbsp);
        var r = new BitReader(rbsp.AsSpan(0, length));
        var profile = (int)r.ReadBits(8);
        r.Skip(16);
        r.ReadUe();
        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            var chromaFormat = r.ReadUe();
            if (chromaFormat == 3)
                r.Skip(1);
            r.ReadUe();
            r.ReadUe();
            r.Skip(1);
            if (r.ReadBit())
                for (var i = 0; i < (chromaFormat != 3 ? 8 : 12); i++)
                    if (r.ReadBit())
                        SkipScalingList(ref r, i < 6 ? 16 : 64);
        }
        r.ReadUe();
        var pocType = r.ReadUe();
        if (pocType == 0)
            r.ReadUe();
        else if (pocType == 1)
        {
            r.Skip(1);
            r.ReadSe();
            r.ReadSe();
            var cycle = r.ReadUe();
            if (cycle > 255)
                return sps;
            for (var i = 0; i < cycle; i++)
                r.ReadSe();
        }
        r.ReadUe();
        r.Skip(1);
        r.ReadUe();
        r.ReadUe();
        if (!r.ReadBit())
            r.Skip(1);
        r.Skip(1);
        if (r.ReadBit())
            for (var i = 0; i < 4; i++)
                r.ReadUe();
        if (!r.ReadBit())
            return sps;
        // VUI（E.1.1）：码流限制信息位于最后。
        if (r.ReadBit() && r.ReadBits(8) == 255)
            r.Skip(32);
        if (r.ReadBit())
            r.Skip(1);
        if (r.ReadBit())
        {
            r.Skip(4);
            if (r.ReadBit())
                r.Skip(24);
        }
        if (r.ReadBit())
        {
            r.ReadUe();
            r.ReadUe();
        }
        if (r.ReadBit())
            r.Skip(65);
        var nalHrd = r.ReadBit();
        if (nalHrd)
            SkipHrd(ref r);
        var vclHrd = r.ReadBit();
        if (vclHrd)
            SkipHrd(ref r);
        if (nalHrd || vclHrd)
            r.Skip(1);
        r.Skip(1);
        var restriction = r.Position;
        if (!r.ReadBit())
            return sps;
        var fields = new uint[7];
        fields[0] = r.ReadBits(1);
        for (var i = 1; i < 7; i++)
            fields[i] = r.ReadUe();
        if (fields[5] >= reorder)
            return sps;
        fields[5] = (uint)reorder;
        fields[6] = Math.Max(fields[6], (uint)reorder);
        var output = new byte[length + 16];
        var w = new BitWriter(output);
        w.CopyBits(rbsp, 0, restriction);
        w.WriteBits(1, 1);
        w.WriteBits(fields[0], 1);
        for (var i = 1; i < 7; i++)
            w.WriteUe(fields[i]);
        w.WriteBits(1, 1);
        while (w.Position % 8 != 0)
            w.WriteBits(0, 1);
        var escaped = new byte[MaxEscapedLength(w.Position / 8) + 1];
        escaped[0] = sps[0];
        var size = Escape(output.AsSpan(0, w.Position / 8), escaped.AsSpan(1));
        return escaped.AsSpan(0, size + 1).ToArray();

        static void SkipHrd(ref BitReader r)
        {
            var count = r.ReadUe() + 1;
            if (count > 32)
                throw new InvalidDataException("Too many HRD schedules.");
            r.Skip(8);
            for (var i = 0; i < count; i++)
            {
                r.ReadUe();
                r.ReadUe();
                r.Skip(1);
            }
            r.Skip(20);
        }
    }

    private static void SkipScalingList(ref BitReader r, int size)
    {
        int last = 8, next = 8;
        for (var j = 0; j < size; j++)
        {
            if (next != 0)
                next = (last + r.ReadSe() + 256) % 256;
            last = next == 0 ? last : next;
        }
    }

    private static (int Id, Pps? Value) TryParsePps(ref BitReader r)
    {
        var id = (int)r.ReadUe();
        var spsId = (int)r.ReadUe();
        if (id > 255 || spsId > 31)
            return (-1, null);
        var cabac = r.ReadBit();
        var bottom = r.ReadBit();
        // 切片组（FMO）不支持，按缺失处理。
        if (r.ReadUe() != 0)
            return (id, null);
        var l0 = (int)r.ReadUe() + 1;
        var l1 = (int)r.ReadUe() + 1;
        var weighted = r.ReadBit();
        var bipred = (int)r.ReadBits(2);
        r.ReadSe();
        r.ReadSe();
        r.ReadSe();
        var deblocking = r.ReadBit();
        r.Skip(1);
        var redundant = r.ReadBit();
        return (id, new Pps(id, spsId, cabac, bottom, l0, l1, weighted, bipred, deblocking, redundant));
    }

    /// <summary>
    /// 改写一个切片：frame_num 换成 frameNum；toIdr 时改为 IDR（插入 idr_pic_id、标记改为 IDR 形式）；
    /// marking 非空时以其替换自适应标记操作。返回新 RBSP（不含 NAL 头）写入 output 的字节数。
    /// </summary>
    public static int RewriteSlice(ReadOnlySpan<byte> rbsp, in SliceHeader h, int frameNum, bool toIdr, int idrPicId,
        IReadOnlyList<Mmco>? marking, Span<byte> output, IReadOnlyList<(int Idc, int Value)>? modifications = null)
    {
        var w = new BitWriter(output);
        w.CopyBits(rbsp, 0, h.FrameNumBit);
        w.WriteBits((uint)frameNum, h.Sps.Log2MaxFrameNum);
        w.CopyBits(rbsp, h.FrameNumBit + h.Sps.Log2MaxFrameNum, h.AfterFieldBit - h.FrameNumBit - h.Sps.Log2MaxFrameNum);
        if (toIdr)
            w.WriteUe((uint)idrPicId);
        else
            w.CopyBits(rbsp, h.AfterFieldBit, h.AfterIdrBit - h.AfterFieldBit);
        if (modifications is null)
            w.CopyBits(rbsp, h.AfterIdrBit, h.MarkingStart - h.AfterIdrBit);
        else
        {
            // 以新的参考列表修改替换原语法，列表长度与其余字段不变。
            w.CopyBits(rbsp, h.AfterIdrBit, h.ModificationStart - h.AfterIdrBit);
            WriteListModification(ref w, h.ModificationL0, modifications, 0, h.ModificationL0Count);
            if (h.SliceType == 1)
                WriteListModification(ref w, h.ModificationL1, modifications, h.ModificationL0Count, modifications.Count);
            w.CopyBits(rbsp, h.ModificationEnd, h.MarkingStart - h.ModificationEnd);
        }
        if (h.NalRefIdc != 0 && toIdr)
            w.WriteBits(0, 2);
        else if (h.NalRefIdc != 0 && marking is not null)
        {
            w.WriteBits(1, 1);
            foreach (var op in marking)
            {
                w.WriteUe((uint)op.Op);
                if (op.Op is 1 or 2 or 3 or 4)
                    w.WriteUe((uint)op.A);
                if (op.Op is 3 or 6)
                    w.WriteUe((uint)op.B);
            }
            w.WriteUe(0);
        }
        else
            w.CopyBits(rbsp, h.MarkingStart, h.MarkingEnd - h.MarkingStart);
        w.CopyBits(rbsp, h.MarkingEnd, h.HeaderEnd - h.MarkingEnd);
        if (h.Pps.Cabac)
        {
            // CABAC：切片数据从字节边界开始，对齐位全为 1。
            while (w.Position % 8 != 0)
                w.WriteBits(1, 1);
            var dataStart = (h.HeaderEnd + 7) / 8;
            w.CopyBytes(rbsp[dataStart..]);
            return w.Position / 8;
        }
        // CAVLC：切片数据紧跟切片头，按位平移到 rbsp_stop_one_bit 为止，再补停止位与对齐零。
        var stop = StopBit(rbsp);
        if (stop < h.HeaderEnd)
            throw new InvalidDataException("Missing rbsp_stop_one_bit.");
        w.CopyBits(rbsp, h.HeaderEnd, stop - h.HeaderEnd);
        w.WriteBits(1, 1);
        while (w.Position % 8 != 0)
            w.WriteBits(0, 1);
        return w.Position / 8;
    }

    private static void WriteListModification(ref BitWriter w, bool present, IReadOnlyList<(int Idc, int Value)> operations,
        int from, int to)
    {
        w.WriteBits(present ? 1u : 0u, 1);
        if (!present)
            return;
        for (var i = from; i < to; i++)
        {
            w.WriteUe((uint)operations[i].Idc);
            w.WriteUe((uint)operations[i].Value);
        }
        w.WriteUe(3);
    }

    private static int StopBit(ReadOnlySpan<byte> rbsp)
    {
        var last = rbsp.Length - 1;
        while (last >= 0 && rbsp[last] == 0)
            last--;
        if (last < 0)
            return -1;
        var value = rbsp[last];
        var bit = 7;
        while ((value & (1 << (7 - bit))) == 0)
            bit--;
        return last * 8 + bit;
    }

    /// <summary>去掉防竞争字节，返回 RBSP 长度。output 长度须不小于 nal。</summary>
    public static int Unescape(ReadOnlySpan<byte> nal, Span<byte> output)
    {
        var length = 0;
        var zeros = 0;
        foreach (var value in nal)
        {
            if (zeros >= 2 && value == 3)
            {
                zeros = 0;
                continue;
            }
            zeros = value == 0 ? zeros + 1 : 0;
            output[length++] = value;
        }
        return length;
    }

    /// <summary>加入防竞争字节写入 output，返回长度；末字节为 0 时补 0x03。output 长度须不小于 rbsp 的 3/2 加 1。</summary>
    public static int Escape(ReadOnlySpan<byte> rbsp, Span<byte> output)
    {
        var length = 0;
        var zeros = 0;
        foreach (var value in rbsp)
        {
            if (zeros >= 2 && value <= 3)
            {
                output[length++] = 3;
                zeros = 0;
            }
            output[length++] = value;
            zeros = value == 0 ? zeros + 1 : 0;
        }
        if (length > 0 && output[length - 1] == 0)
            output[length++] = 3;
        return length;
    }

    public static int MaxEscapedLength(int rbspLength) => rbspLength + rbspLength / 2 + 2;

    /// <summary>
    /// 复制区的逐个访问单元改写：首个 I 帧改为 IDR，之后的切片 frame_num 减去该 I 帧的值，
    /// 并删除指向 I 帧之前参考画面的 MMCO 操作（这些画面在新码流中不存在）。遇到原有 IDR 后码流本身已重新编号，停止改写。
    /// 参考帧模型只跟踪改写后码流中的短期参考帧（按新编号），用于判断 MMCO 的目标是否存在。
    /// I 帧之后被丢弃的前导画面（见 <see cref="SkipLeading"/>）若作参考，在新码流中不再占用 frame_num，
    /// 此后的画面编号依次前移；参考列表修改与 MMCO 按原编号找到目标，再按新编号重写。
    /// </summary>
    internal sealed class CopyRewriter(ParameterSets sets)
    {
        /// <summary>
        /// 改写后码流中的短期参考帧：FrameNum 为新编号，Original 为原编号（相对 I 帧），记录两个场是否各自作参考。
        /// </summary>
        private readonly record struct ReferenceFrame(int FrameNum, int Original, bool Top, bool Bottom);

        private readonly List<Mmco> mmco = [];
        private readonly List<ReferenceFrame> references = [];
        // 当前画面解码前（标记前）的参考帧，参考列表在此基础上构造。
        private readonly List<ReferenceFrame> pictureReferences = [];
        private readonly List<(int Idc, int Value)> modifications = [];
        private readonly List<int?> targets = [];
        private int offset;
        private bool converted;
        private (int FrameNum, bool Bottom, bool Reference)? firstField;
        private int pictureFrameNum;
        // 当前画面的原编号（相对 I 帧），参考列表修改与 MMCO 的差值按原编号计算。
        private int pictureOriginal;
        // 已丢弃的前导参考画面数：此后画面的新编号 = 原编号 − skipped。
        private int skipped;
        // 已处理 I 帧之后的保留画面：前导参考画面只能紧跟 I 帧（及其第二场），否则编号无法整体前移。
        private bool pastLeading;
        // 刚丢弃的前导参考场（原 frame_num、是否底场），用于识别同一帧的第二场。
        private (int FrameNum, bool Bottom)? leadingField;
        private List<Mmco>? pictureMarking;

        public bool Active { get; private set; } = true;

        /// <summary>输出缓冲区的上限：每个 NAL 加起始码、切片头增长与防竞争字节，以及插入的参数集。</summary>
        public static int OutputBound(ReadOnlySpan<byte> data, int lengthSize, IReadOnlyList<byte[]>? prefix)
        {
            var size = 0;
            foreach (var range in VideoBitstream.EnumerateNals(data, lengthSize))
                size = checked(size + 4 + MaxEscapedLength(range.End.Value - range.Start.Value + 32));
            if (prefix is not null)
                foreach (var nal in prefix)
                    size = checked(size + 4 + nal.Length);
            return size;
        }

        /// <summary>改写一个访问单元并写成 Annex B（访问单元分隔符之后插入 prefix），返回写入长度。</summary>
        public int Rewrite(ReadOnlySpan<byte> data, int lengthSize, bool convertToIdr, int idrPicId, IReadOnlyList<byte[]>? prefix,
            Span<byte> output)
        {
            if (convertToIdr == converted)
                throw new InvalidOperationException(convertToIdr ? "Already converted." : "The first picture must be converted.");
            var position = 0;
            var nals = VideoBitstream.EnumerateNals(data, lengthSize);
            var pending = nals.MoveNext();
            if (pending && (data[nals.Current][0] & 0x1F) == 9)
            {
                Write(data[nals.Current], output, ref position);
                pending = nals.MoveNext();
            }
            if (prefix is not null)
                foreach (var nal in prefix)
                {
                    sets.Add(nal);
                    Write(nal, output, ref position);
                }
            for (; pending; pending = nals.MoveNext())
            {
                var nal = data[nals.Current];
                var type = nal[0] & 0x1F;
                if (type is 7 or 8)
                    sets.Add(nal);
                if (type == 5 && !convertToIdr)
                    Active = false;
                if (!Active || type is not (1 or 5))
                {
                    Write(nal, output, ref position);
                    continue;
                }
                ProcessSlice(nal, convertToIdr, idrPicId, output, ref position);
            }
            converted = true;
            return position;
        }

        /// <summary>
        /// 记录衔接点 I 帧之后不复制的前导画面（显示早于 I 帧，由头段重新编码）。非参考画面不影响编号；
        /// 参考画面在新码流中不存在：其 frame_num 不再占位，指向它的参考项按缺失处理。
        /// 支持的前提（否则抛出 <see cref="InvalidDataException"/>）：紧跟 I 帧、早于任何保留画面，
        /// frame_num 与上一参考画面连续（参考场对的两场共用一个 frame_num，第二场紧跟第一场）；
        /// 带 MMCO 时只能取消新码流中本就不存在的画面（I 帧之前或已丢弃的前导画面），
        /// 这样丢掉该画面连同其 MMCO 后，保留画面的参考帧状态不变。
        /// </summary>
        public void SkipLeading(ReadOnlySpan<byte> data, int lengthSize)
        {
            if (!converted)
                throw new InvalidOperationException("The splice picture must be converted first.");
            if (!Active)
                return;
            var started = false;
            foreach (var range in VideoBitstream.EnumerateNals(data, lengthSize))
            {
                var nal = data[range];
                var type = nal[0] & 0x1F;
                if (type is 7 or 8)
                    sets.Add(nal);
                if (type == 5)
                    throw new InvalidDataException("IDR picture among the leading pictures.");
                if (type != 1 || started)
                    continue;
                started = true;
                var rbsp = ArrayPool<byte>.Shared.Rent(nal.Length);
                try
                {
                    var length = Unescape(nal[1..], rbsp);
                    if (!TryParseSlice(rbsp.AsSpan(0, length), nal[0], sets, mmco, out var header))
                        throw new InvalidDataException("Unsupported H.264 slice header.");
                    if (header.NalRefIdc == 0)
                    {
                        leadingField = null;
                        return;
                    }
                    if (pastLeading)
                        throw new InvalidDataException("Leading reference picture after a kept picture.");
                    var max = header.Sps.MaxFrameNum;
                    var original = ((header.FrameNum - offset) % max + max) % max;
                    // 参考场对的第二场：紧跟刚丢弃的第一场，frame_num 相同、场性相反，不再占用新的编号。
                    var secondField = header.Field && leadingField is { } first && first.FrameNum == header.FrameNum &&
                                      first.Bottom != header.Bottom;
                    leadingField = header.Field && !secondField ? (header.FrameNum, header.Bottom) : null;
                    if (original != (secondField ? skipped : skipped + 1))
                        throw new InvalidDataException("Gap in frame_num among the leading pictures.");
                    // 原编号 0 是 I 帧，其余较小的编号是已丢弃的前导画面，负数是 I 帧之前的画面。
                    if (header.Adaptive && mmco.Exists(op => op.Op != 1 || UnmarkTarget(header, original, op.A) == 0))
                        throw new InvalidDataException("Memory management in a leading picture affects kept pictures.");
                    if (!secondField)
                        skipped++;
                }
                finally { ArrayPool<byte>.Shared.Return(rbsp); }
            }
        }

        /// <summary>MMCO1 取消的画面所属帧的原编号（相对 I 帧）：帧画面为 picNum，场画面由 picNum 换算出所属帧。</summary>
        private static int UnmarkTarget(in SliceHeader h, int original, int difference)
        {
            if (!h.Field)
                return original - (difference + 1);
            var picNum = 2 * original + 1 - (difference + 1);
            return (picNum & 1) != 0 ? (picNum - 1) >> 1 : picNum >> 1;
        }

        private void ProcessSlice(ReadOnlySpan<byte> nal, bool convertToIdr, int idrPicId, Span<byte> output, ref int position)
        {
            var rbsp = ArrayPool<byte>.Shared.Rent(nal.Length);
            var rewritten = ArrayPool<byte>.Shared.Rent(nal.Length + 64);
            try
            {
                var length = Unescape(nal[1..], rbsp);
                if (!TryParseSlice(rbsp.AsSpan(0, length), nal[0], sets, mmco, out var header, modifications))
                    throw new InvalidDataException("Unsupported H.264 slice header.");
                if (header.FirstMb == 0)
                    StartPicture(header, convertToIdr);
                var replaced = !convertToIdr && !header.Intra && FixReferenceLists(ref header);
                var size = H264Splice.RewriteSlice(rbsp.AsSpan(0, length), header, pictureFrameNum, convertToIdr, idrPicId,
                    pictureMarking, rewritten, replaced ? modifications : null);
                BinaryPrimitives.WriteInt32BigEndian(output[position..], 1);
                position += 4;
                output[position++] = convertToIdr ? (byte)((nal[0] & 0xE0) | 5) : nal[0];
                position += Escape(rewritten.AsSpan(0, size), output[position..]);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rbsp);
                ArrayPool<byte>.Shared.Return(rewritten);
            }
        }

        private void StartPicture(in SliceHeader h, bool convertToIdr)
        {
            pictureMarking = null;
            pictureReferences.Clear();
            pictureReferences.AddRange(references);
            // 第二场：紧跟同一帧（同一原始 frame_num）的相反场；与第一场组成一帧，frame_num 相同。
            var secondField = h.Field && firstField is { } first && first.FrameNum == h.FrameNum && first.Bottom != h.Bottom;
            var pairedReference = secondField && firstField!.Value.Reference;
            firstField = h.Field && !secondField ? (h.FrameNum, h.Bottom, h.NalRefIdc != 0) : null;
            if (convertToIdr)
            {
                if (!h.Intra || h.NalRefIdc == 0 || secondField)
                    throw new InvalidDataException("The splice picture is not an intra reference picture.");
                offset = h.FrameNum;
                pictureFrameNum = 0;
                pictureOriginal = 0;
                skipped = 0;
                pastLeading = false;
                leadingField = null;
                references.Clear();
                references.Add(new ReferenceFrame(0, 0, !h.Field || !h.Bottom, !h.Field || h.Bottom));
                return;
            }
            // I 帧的第二场仍在前导画面之前，不算保留画面。
            pastLeading |= !secondField;
            var max = h.Sps.MaxFrameNum;
            var original = ((h.FrameNum - offset) % max + max) % max;
            var frameNum = ((original - skipped) % max + max) % max;
            pictureOriginal = original;
            pictureFrameNum = frameNum;
            if (h.NalRefIdc == 0)
                return;
            int Wrap(int value) => value > frameNum ? value - max : value;
            List<Mmco>? kept = null;
            var reset = false;
            if (h.Adaptive)
            {
                // 按原编号找到目标后以新编号重写差值；目标是 I 帧之前或已丢弃的画面时（新码流中不存在）删除该操作。
                var rebuilt = new List<Mmco>(mmco.Count);
                var changed = false;
                foreach (var op in mmco)
                {
                    switch (op.Op)
                    {
                        case 1:
                            if (Unmark(h, op.A, Wrap) is { } difference)
                            {
                                rebuilt.Add(op with { A = difference });
                                changed |= difference != op.A;
                            }
                            else
                                changed = true;
                            break;
                        case 5:
                            references.Clear();
                            reset = true;
                            rebuilt.Add(op);
                            break;
                        default:
                            throw new InvalidDataException("Long-term reference marking is not supported at a splice.");
                    }
                }
                kept = changed ? rebuilt : null;
            }
            else if (!pairedReference && references.Count >= Math.Max(1, h.Sps.MaxNumRefFrames))
            {
                // 滑动窗口：移除 FrameNumWrap 最小的帧（或场对）；参考场对的第二场不触发。
                var oldest = 0;
                for (var i = 1; i < references.Count; i++)
                    if (Wrap(references[i].FrameNum) < Wrap(references[oldest].FrameNum))
                        oldest = i;
                references.RemoveAt(oldest);
            }
            pictureMarking = kept;
            if (reset)
            {
                // MMCO5 之后原码流与新码流的 frame_num 都从 0 重新计数，本画面视为 0 号。
                offset = 0;
                skipped = 0;
                frameNum = 0;
                original = 0;
                if (firstField is { } current)
                    firstField = (0, current.Bottom, true);
            }
            var existing = pairedReference ? references.FindIndex(reference => reference.FrameNum == frameNum) : -1;
            if (existing >= 0)
                references[existing] = h.Bottom ? references[existing] with { Bottom = true } : references[existing] with { Top = true };
            else
                references.Add(new ReferenceFrame(frameNum, original, !h.Field || !h.Bottom, !h.Field || h.Bottom));
        }

        /// <summary>
        /// 衔接点之后的头几个画面中，参考列表可能依赖 I 帧之前的画面（新码流中不存在）：
        /// 修改操作指向这些画面，或可用参考数少于 num_ref_idx_active。列表长度不能改（切片数据按 ref_idx 解析），
        /// 因此缺失的项改为指向现有参考画面；P 切片按默认列表规则构造完整列表后，以显式修改操作写出。
        /// 验证已确认这些项未被画面数据实际使用，画面不变，码流合规。
        /// 丢弃过前导参考画面时编号已前移，修改操作的差值按原编号求出目标后，以新编号重新写出。返回 modifications 是否改写。
        /// </summary>
        private bool FixReferenceLists(ref SliceHeader h)
        {
            var max = h.Sps.MaxFrameNum;
            var frameNum = pictureFrameNum;
            var field = h.Field;
            var bottomField = h.Bottom;
            var maxPicNum = field ? 2 * max : max;
            var current = field ? 2 * frameNum + 1 : frameNum;
            var currentOriginal = field ? 2 * pictureOriginal + 1 : pictureOriginal;
            int Wrap(int value) => value > frameNum ? value - max : value;
            int WrapOriginal(int value) => value > pictureOriginal ? value - max : value;
            // 按原编号的 picNum 找到参考画面，返回其新编号的 picNum；画面不存在（I 帧之前或已丢弃）时为 null。
            int? Find(int originalPicNum)
            {
                if (!field)
                {
                    var index = pictureReferences.FindIndex(reference => reference.Top && reference.Bottom &&
                                                                         WrapOriginal(reference.Original) == originalPicNum);
                    return index < 0 ? null : Wrap(pictureReferences[index].FrameNum);
                }
                var same = (originalPicNum & 1) != 0;
                var wrap = same ? (originalPicNum - 1) >> 1 : originalPicNum >> 1;
                var bottom = same ? bottomField : !bottomField;
                var found = pictureReferences.FindIndex(reference => WrapOriginal(reference.Original) == wrap &&
                                                                     (bottom ? reference.Bottom : reference.Top));
                return found < 0 ? null : 2 * Wrap(pictureReferences[found].FrameNum) + (same ? 1 : 0);
            }
            // 默认参考列表（8.2.4.2.1 帧 P：PicNum 降序；8.2.4.2.5 场 P：按 FrameNumWrap 降序的帧，从同场性开始交替取场）。
            var ordered = pictureReferences.Where(reference => field || reference.Top && reference.Bottom)
                .OrderByDescending(reference => Wrap(reference.FrameNum)).ToList();
            var initial = new List<int>();
            if (!field)
                initial.AddRange(ordered.Select(reference => Wrap(reference.FrameNum)));
            else
            {
                var same = ordered.Where(reference => bottomField ? reference.Bottom : reference.Top)
                    .Select(reference => 2 * Wrap(reference.FrameNum) + 1).ToList();
                var opposite = ordered.Where(reference => bottomField ? reference.Top : reference.Bottom)
                    .Select(reference => 2 * Wrap(reference.FrameNum)).ToList();
                for (var i = 0; i < Math.Max(same.Count, opposite.Count); i++)
                {
                    if (i < same.Count)
                        initial.Add(same[i]);
                    if (i < opposite.Count)
                        initial.Add(opposite[i]);
                }
            }
            if (initial.Count == 0)
                throw new InvalidDataException("No reference picture after the splice.");
            var latest = initial[0];
            var changed = false;
            var lists = h.SliceType == 1 ? 2 : 1;
            var rebuilt = new List<(int Idc, int Value)>();
            var counts = new int[2];
            for (var list = 0; list < lists; list++)
            {
                var present = list == 0 ? h.ModificationL0 : h.ModificationL1;
                var from = list == 0 ? 0 : h.ModificationL0Count;
                var to = !present ? from : list == 0 ? h.ModificationL0Count : modifications.Count;
                var active = list == 0 ? h.NumRefIdxL0 : h.NumRefIdxL1;
                // 按修改操作计算目标（原编号）并换算为新编号，不存在的目标改为最近的参考画面。
                targets.Clear();
                var predicted = currentOriginal;
                var missing = false;
                for (var i = from; i < to; i++)
                {
                    var (idc, value) = modifications[i];
                    if (idc == 2)
                        throw new InvalidDataException("Long-term reference list modification is not supported at a splice.");
                    var noWrap = idc == 0 ? predicted - (value + 1) : predicted + (value + 1);
                    if (noWrap < 0)
                        noWrap += maxPicNum;
                    else if (noWrap >= maxPicNum)
                        noWrap -= maxPicNum;
                    predicted = noWrap;
                    var target = Find(noWrap > currentOriginal ? noWrap - maxPicNum : noWrap);
                    missing |= target is null;
                    targets.Add(target ?? latest);
                }
                List<int?> final;
                var padded = false;
                if (h.SliceType is 0 or 3)
                {
                    // P：完整列表 = 默认列表经修改操作重排（放入目标并删除其后的重复项），截取 active 项，不足时补齐。
                    var entries = new List<int>(initial);
                    for (var i = 0; i < targets.Count; i++)
                    {
                        var target = targets[i]!.Value;
                        entries.Insert(Math.Min(i, entries.Count), target);
                        for (var j = entries.Count - 1; j > i; j--)
                            if (entries[j] == target)
                                entries.RemoveAt(j);
                    }
                    if (entries.Count > active)
                        entries.RemoveRange(active, entries.Count - active);
                    while (entries.Count < active)
                    {
                        entries.Add(entries[0]);
                        padded = true;
                    }
                    final = entries.Select(value => (int?)value).ToList();
                }
                else
                {
                    // B：默认列表依赖 POC，只替换不存在的修改目标。
                    final = [.. targets];
                }
                // 编号前移后，原差值指向的画面不同，须按新编号重写。
                if (!missing && !padded && skipped == 0)
                {
                    for (var i = from; i < to; i++)
                        rebuilt.Add(modifications[i]);
                    counts[list] = to - from;
                    continue;
                }
                changed = true;
                predicted = current;
                foreach (var target in final)
                {
                    var noWrap = target!.Value < 0 ? target.Value + maxPicNum : target.Value;
                    rebuilt.Add(noWrap < predicted ? (0, predicted - noWrap - 1)
                        : noWrap > predicted ? (1, noWrap - predicted - 1)
                        : (0, maxPicNum - 1));
                    predicted = noWrap;
                }
                counts[list] = final.Count;
            }
            if (!changed)
                return false;
            modifications.Clear();
            modifications.AddRange(rebuilt);
            h.ModificationL0 = h.ModificationL0 || counts[0] > 0;
            h.ModificationL1 = h.ModificationL1 || counts[1] > 0;
            h.ModificationL0Count = counts[0];
            return true;
        }

        /// <summary>
        /// MMCO1：帧画面按 picNum = FrameNumWrap 取消整帧；场画面 CurrPicNum = 2*frame_num+1，
        /// 奇数 picNum 指同场性的场、偶数指相反场性的场。差值按原编号求出目标；目标存在时取消标记，
        /// 并返回按新编号重写的 difference_of_pic_nums_minus1，不存在时返回 null。
        /// </summary>
        private int? Unmark(in SliceHeader h, int difference, Func<int, int> wrap)
        {
            var max = h.Sps.MaxFrameNum;
            int WrapOriginal(int value) => value > pictureOriginal ? value - max : value;
            if (!h.Field)
            {
                var target = pictureOriginal - (difference + 1);
                var index = references.FindIndex(reference => reference.Top && reference.Bottom &&
                                                              WrapOriginal(reference.Original) == target);
                if (index < 0)
                    return null;
                var rewritten = pictureFrameNum - wrap(references[index].FrameNum) - 1;
                references.RemoveAt(index);
                return rewritten;
            }
            var picNum = 2 * pictureOriginal + 1 - (difference + 1);
            var sameParity = (picNum & 1) != 0;
            var frameNumWrap = sameParity ? (picNum - 1) >> 1 : picNum >> 1;
            var bottom = sameParity ? h.Bottom : !h.Bottom;
            var found = references.FindIndex(reference => WrapOriginal(reference.Original) == frameNumWrap &&
                                                          (bottom ? reference.Bottom : reference.Top));
            if (found < 0)
                return null;
            var result = 2 * pictureFrameNum + 1 - (2 * wrap(references[found].FrameNum) + (sameParity ? 1 : 0)) - 1;
            var updated = bottom ? references[found] with { Bottom = false } : references[found] with { Top = false };
            if (updated.Top || updated.Bottom)
                references[found] = updated;
            else
                references.RemoveAt(found);
            return result;
        }

        private static void Write(ReadOnlySpan<byte> nal, Span<byte> output, ref int position)
        {
            BinaryPrimitives.WriteInt32BigEndian(output[position..], 1);
            position += 4;
            nal.CopyTo(output[position..]);
            position += nal.Length;
        }
    }

    internal ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        public int Position { get; private set; }

        public bool ReadBit()
        {
            if (Position >= data.Length * 8)
                throw new InvalidDataException("Truncated H.264 syntax.");
            var bit = (data[Position >> 3] >> (7 - (Position & 7))) & 1;
            Position++;
            return bit != 0;
        }

        public uint ReadBits(int count)
        {
            uint value = 0;
            for (var i = 0; i < count; i++)
                value = (value << 1) | (ReadBit() ? 1u : 0u);
            return value;
        }

        public void Skip(int count)
        {
            if (Position + count > data.Length * 8)
                throw new InvalidDataException("Truncated H.264 syntax.");
            Position += count;
        }

        public uint ReadUe()
        {
            var zeros = 0;
            while (!ReadBit())
                if (++zeros > 31)
                    throw new InvalidDataException("Invalid Exp-Golomb code.");
            return zeros == 0 ? 0 : (1u << zeros) - 1 + ReadBits(zeros);
        }

        public int ReadSe()
        {
            var value = ReadUe();
            return (value & 1) != 0 ? (int)((value + 1) / 2) : -(int)(value / 2);
        }
    }

    internal ref struct BitWriter(Span<byte> output)
    {
        private readonly Span<byte> output = output;
        public int Position { get; private set; }

        public void WriteBits(uint value, int count)
        {
            for (var i = count - 1; i >= 0; i--)
            {
                var index = Position >> 3;
                if (index >= output.Length)
                    throw new InvalidDataException("H.264 rewrite buffer too small.");
                var mask = (byte)(0x80 >> (Position & 7));
                if (((value >> i) & 1) != 0)
                    output[index] |= mask;
                else
                    output[index] &= (byte)~mask;
                Position++;
            }
        }

        public void WriteUe(uint value)
        {
            var coded = value + 1;
            var length = 32 - System.Numerics.BitOperations.LeadingZeroCount(coded);
            WriteBits(0, length - 1);
            WriteBits(coded, length);
        }

        /// <summary>从 source 的第 start 位起复制 count 位；源与目标同位对齐时整字节复制。</summary>
        public void CopyBits(ReadOnlySpan<byte> source, int start, int count)
        {
            if (count <= 0)
                return;
            if (start + count > source.Length * 8)
                throw new InvalidDataException("Truncated H.264 syntax.");
            while (count > 0 && (Position & 7) != 0)
            {
                WriteBits((uint)((source[start >> 3] >> (7 - (start & 7))) & 1), 1);
                start++;
                count--;
            }
            var shift = start & 7;
            var bytes = count / 8;
            if (bytes > 0)
            {
                var target = output.Slice(Position >> 3, bytes);
                var from = start >> 3;
                if (shift == 0)
                    source.Slice(from, bytes).CopyTo(target);
                else
                {
                    // 每个输出字节取源中相邻两字节的拼接。
                    for (var i = 0; i < bytes; i++)
                    {
                        var high = source[from + i] << shift;
                        var low = from + i + 1 < source.Length ? source[from + i + 1] >> (8 - shift) : 0;
                        target[i] = (byte)(high | low);
                    }
                }
                Position += bytes * 8;
                start += bytes * 8;
                count -= bytes * 8;
            }
            while (count-- > 0)
            {
                WriteBits((uint)((source[start >> 3] >> (7 - (start & 7))) & 1), 1);
                start++;
            }
        }

        public void CopyBytes(ReadOnlySpan<byte> source)
        {
            if ((Position & 7) != 0)
                throw new InvalidOperationException("Unaligned byte copy.");
            source.CopyTo(output[(Position >> 3)..]);
            Position += source.Length * 8;
        }
    }
}
