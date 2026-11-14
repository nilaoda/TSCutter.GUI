using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace TSCutter.GUI.Utils.Media;

/// <summary>
/// 逐行刷新（周期帧内刷新）H.264 的衔接。这类码流没有 IDR 与 I 帧，只在 P 画面上带恢复点，
/// 复制区开头的画面参考它之前的画面；输出中那些画面由头段重新编码的画面代替。为使衔接处是同一个合规的编码视频序列：
/// <list type="bullet">
/// <item>头段由 x264 按源 PPS 中影响解码的设置编码（熵编码、8x8 变换、色度量化偏移、受限帧内预测，参考帧数不超过源），
/// 切片头改写到源 SPS/PPS 上，首个画面为 IDR，此后按画面依次编号；</item>
/// <item>复制区每个切片的 frame_num 与 pic_order_cnt_lsb 平移到接续头段的编号（位宽不变，切片数据不变）。</item>
/// </list>
/// 头段画面与它代替的原画面一一对应，复制区画面按相对编号找到的参考画面正是对应的头段画面。
/// 重新编码的画面与原画面略有差别，这一差别随参考关系带入复制区，经过一个刷新周期后消失，此后与原片完全一致。
/// </summary>
internal static class H264RefreshSplice
{
    /// <summary>SPS 中决定切片语法与解码方式的字段，以及写图像定时 SEI 所需的 VUI 字段。</summary>
    internal sealed record SequenceSyntax(int Id, int Profile, int ChromaFormat, int BitDepthLuma, int BitDepthChroma,
        bool SeparateColourPlane, bool TransformBypass, bool ScalingMatrix, int Log2MaxFrameNum, int PocType,
        int Log2MaxPocLsb, int MaxNumRefFrames, int WidthMbs, int HeightMapUnits, bool FrameMbsOnly, bool Mbaff,
        bool PicStructPresent, bool CpbDpbDelaysPresent, int CpbRemovalDelayLength, int DpbOutputDelayLength,
        uint NumUnitsInTick, uint TimeScale)
    {
        public int HeightMbs => HeightMapUnits * (FrameMbsOnly ? 1 : 2);
    }

    /// <summary>PPS 中决定切片语法与解码方式的字段。</summary>
    internal sealed record PictureSyntax(int Id, int SpsId, bool Cabac, bool BottomFieldPicOrderPresent,
        int NumRefIdxL0Default, bool WeightedPred, int PicInitQp, int ChromaQpOffset, bool DeblockingControlPresent,
        bool ConstrainedIntraPred, bool RedundantPicCntPresent, bool Transform8x8, bool ScalingMatrix,
        int SecondChromaQpOffset);

    /// <summary>解析 SPS；语法截断、无效或不认识时返回 null。带缩放矩阵时只解析到该标志为止。</summary>
    public static SequenceSyntax? ParseSps(ReadOnlySpan<byte> nal)
    {
        if (nal.Length < 4 || (nal[0] & 0x1F) != 7)
            return null;
        var rbsp = new byte[nal.Length];
        var length = H264Splice.Unescape(nal[1..], rbsp);
        try
        {
            var r = new H264Splice.BitReader(rbsp.AsSpan(0, length));
            var profile = (int)r.ReadBits(8);
            r.Skip(16);
            var id = (int)r.ReadUe();
            int chroma = 1, depthLuma = 8, depthChroma = 8;
            bool separate = false, bypass = false;
            if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                chroma = (int)r.ReadUe();
                if (chroma == 3)
                    separate = r.ReadBit();
                depthLuma = (int)r.ReadUe() + 8;
                depthChroma = (int)r.ReadUe() + 8;
                bypass = r.ReadBit();
                if (r.ReadBit())
                    return new SequenceSyntax(id, profile, chroma, depthLuma, depthChroma, separate, bypass, true,
                        0, 0, 0, 0, 0, 0, true, false, false, false, 0, 0, 0, 0);
            }
            var log2MaxFrameNum = (int)r.ReadUe() + 4;
            var pocType = (int)r.ReadUe();
            var log2MaxPocLsb = 0;
            if (pocType == 0)
                log2MaxPocLsb = (int)r.ReadUe() + 4;
            else if (pocType == 1)
            {
                r.Skip(1);
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
            var refs = (int)r.ReadUe();
            r.Skip(1);
            var width = (int)r.ReadUe() + 1;
            var height = (int)r.ReadUe() + 1;
            var frameMbsOnly = r.ReadBit();
            var mbaff = !frameMbsOnly && r.ReadBit();
            r.Skip(1);
            if (r.ReadBit())
                for (var i = 0; i < 4; i++)
                    r.ReadUe();
            bool picStruct = false, delays = false;
            int cpbLength = 24, dpbLength = 24;
            uint units = 0, scale = 0;
            if (r.ReadBit())
            {
                // VUI（E.1.1）。
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
                {
                    units = r.ReadBits(32);
                    scale = r.ReadBits(32);
                    r.Skip(1);
                }
                var nalHrd = r.ReadBit();
                if (nalHrd)
                    ReadHrd(ref r, ref cpbLength, ref dpbLength);
                var vclHrd = r.ReadBit();
                if (vclHrd)
                    ReadHrd(ref r, ref cpbLength, ref dpbLength);
                delays = nalHrd || vclHrd;
                if (delays)
                    r.Skip(1);
                picStruct = r.ReadBit();
            }
            if (log2MaxFrameNum > 16 || log2MaxPocLsb > 16)
                return null;
            return new SequenceSyntax(id, profile, chroma, depthLuma, depthChroma, separate, bypass, false, log2MaxFrameNum,
                pocType, log2MaxPocLsb, refs, width, height, frameMbsOnly, mbaff, picStruct, delays, cpbLength, dpbLength,
                units, scale);
        }
        catch (InvalidDataException) { return null; }

        static void ReadHrd(ref H264Splice.BitReader r, ref int cpbLength, ref int dpbLength)
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
            r.Skip(5);
            cpbLength = (int)r.ReadBits(5) + 1;
            dpbLength = (int)r.ReadBits(5) + 1;
            r.Skip(5);
        }
    }

    /// <summary>解析 PPS；使用切片组或语法无效时返回 null。带缩放矩阵时只解析到该标志为止。</summary>
    public static PictureSyntax? ParsePps(ReadOnlySpan<byte> nal)
    {
        if (nal.Length < 2 || (nal[0] & 0x1F) != 8)
            return null;
        var rbsp = new byte[nal.Length];
        var length = H264Splice.Unescape(nal[1..], rbsp);
        try
        {
            var r = new H264Splice.BitReader(rbsp.AsSpan(0, length));
            var id = (int)r.ReadUe();
            var spsId = (int)r.ReadUe();
            var cabac = r.ReadBit();
            var bottom = r.ReadBit();
            if (r.ReadUe() != 0)
                return null;
            var l0 = (int)r.ReadUe() + 1;
            r.ReadUe();
            var weighted = r.ReadBit();
            r.Skip(2);
            var initQp = 26 + r.ReadSe();
            r.ReadSe();
            var chroma = r.ReadSe();
            var deblocking = r.ReadBit();
            var constrained = r.ReadBit();
            var redundant = r.ReadBit();
            bool transform = false, scaling = false;
            var second = chroma;
            if (r.Position < StopBit(rbsp.AsSpan(0, length)))
            {
                transform = r.ReadBit();
                scaling = r.ReadBit();
                if (!scaling)
                    second = r.ReadSe();
            }
            return new PictureSyntax(id, spsId, cabac, bottom, l0, weighted, initQp, chroma, deblocking, constrained,
                redundant, transform, scaling, second);
        }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>源参数集中重编码段无法沿用的语法；都支持时返回 null。</summary>
    public static string? Unsupported(SequenceSyntax sps, PictureSyntax pps) =>
        sps.ScalingMatrix || pps.ScalingMatrix ? "scaling matrices"
        : sps.PocType == 1 ? "POC type 1"
        : sps.SeparateColourPlane || sps.TransformBypass || sps.ChromaFormat is 0 or 3 ? "chroma format"
        : pps.RedundantPicCntPresent ? "redundant pictures"
        : sps.MaxNumRefFrames < 1 ? "no reference frames"
        : null;

    /// <summary>
    /// 编码器参数集与源参数集在解码上是否等价（切片头改写之外的部分）：画面尺寸、色度格式与位深、MBAFF、
    /// 熵编码、8x8 变换、色度量化偏移、受限帧内预测、缩放矩阵一致，不用加权预测，参考帧数不超过源。等价时返回 null。
    /// </summary>
    public static string? Mismatch(SequenceSyntax source, PictureSyntax sourcePps, SequenceSyntax encoded, PictureSyntax encodedPps) =>
        encoded.WidthMbs != source.WidthMbs || encoded.HeightMbs != source.HeightMbs ? "picture size"
        : encoded.ChromaFormat != source.ChromaFormat || encoded.BitDepthLuma != source.BitDepthLuma ||
          encoded.BitDepthChroma != source.BitDepthChroma ? "chroma format"
        : encoded.Mbaff != source.Mbaff ? "MBAFF"
        : encoded.ScalingMatrix || encodedPps.ScalingMatrix ? "scaling matrices"
        : encoded.MaxNumRefFrames > source.MaxNumRefFrames ? "reference frames"
        : encodedPps.Cabac != sourcePps.Cabac ? "entropy coding"
        : encodedPps.Transform8x8 != sourcePps.Transform8x8 ? "8x8 transform"
        : encodedPps.ChromaQpOffset != sourcePps.ChromaQpOffset ||
          encodedPps.SecondChromaQpOffset != sourcePps.SecondChromaQpOffset ? "chroma QP offset"
        : encodedPps.ConstrainedIntraPred != sourcePps.ConstrainedIntraPred ? "constrained intra prediction"
        : encodedPps.WeightedPred ? "weighted prediction"
        : null;

    /// <summary>
    /// 头段画面（帧编码）的 pic_struct：逐行码流为 0（帧）；隔行按场序为 3（顶场在前）或 4（底场在前）。
    /// 源 SPS 声明了 pic_struct 时，播放器按它判断隔行与场序。
    /// </summary>
    public static int PicStruct(SequenceSyntax sps, bool interlaced, bool topFieldFirst) =>
        sps.FrameMbsOnly || !interlaced ? 0 : topFieldFirst ? 3 : 4;

    /// <summary>
    /// 把头段的 x264 输出改写到源参数集上：去掉编码器的参数集与 SEI，切片头按源 SPS/PPS 的语法重写
    /// （PPS 编号、frame_num 与 POC 按画面序号、参考帧数、加权表、量化参数差、去块参数），切片数据不变。
    /// 源 SPS 声明 pic_struct 时，每个画面前写图像定时 SEI。
    /// </summary>
    internal sealed class HeadRewriter(SequenceSyntax sps, PictureSyntax pps, H264Splice.ParameterSets encoderSets,
        PictureSyntax encoderPps, int picStruct, int ticksPerFrame)
    {
        private readonly List<H264Splice.Mmco> mmco = [];

        /// <summary>已写出的画面数，即复制区首个画面接续的编号。</summary>
        public int Pictures { get; private set; }

        public static int OutputBound(ReadOnlySpan<byte> data, IReadOnlyList<byte[]>? prefix) =>
            H264Splice.CopyRewriter.OutputBound(data, 0, prefix) + 64;

        /// <summary>改写一个 x264 输出的访问单元（Annex B），首个画面前写入 prefix（源参数集），返回写入长度。</summary>
        public int Rewrite(ReadOnlySpan<byte> data, IReadOnlyList<byte[]>? prefix, Span<byte> output)
        {
            var position = 0;
            if (prefix is not null)
                foreach (var nal in prefix)
                    Write(nal, output, ref position);
            var slices = 0;
            foreach (var range in VideoBitstream.EnumerateNals(data, 0))
            {
                var nal = data[range];
                var type = nal[0] & 0x1F;
                if (type is 7 or 8)
                    encoderSets.Add(nal);
                if (type is not (1 or 5))
                    continue;
                if (slices == 0 && sps.PicStructPresent)
                    WritePicTiming(output, ref position);
                RewriteSlice(nal, output, ref position);
                slices++;
            }
            if (slices == 0)
                throw new InvalidDataException("No slice in the encoded picture.");
            Pictures++;
            return position;
        }

        private void RewriteSlice(ReadOnlySpan<byte> nal, Span<byte> output, ref int position)
        {
            var rbsp = ArrayPool<byte>.Shared.Rent(nal.Length);
            var rewritten = ArrayPool<byte>.Shared.Rent(nal.Length + 64);
            try
            {
                var length = H264Splice.Unescape(nal[1..], rbsp);
                var source = rbsp.AsSpan(0, length);
                if (!H264Splice.TryParseSlice(source, nal[0], encoderSets, mmco, out var h))
                    throw new InvalidDataException("Unsupported encoded slice header.");
                if (h.SliceType is not (0 or 2) || h.Field)
                    throw new InvalidDataException("Unexpected encoded slice.");
                var idr = h.NalType == 5;
                if (idr != (Pictures == 0))
                    throw new InvalidDataException("The encoded head must start with its only IDR picture.");
                var w = new H264Splice.BitWriter(rewritten);
                w.WriteUe((uint)h.FirstMb);
                w.WriteUe((uint)h.RawSliceType);
                w.WriteUe((uint)pps.Id);
                w.WriteBits((uint)(Pictures & ((1 << sps.Log2MaxFrameNum) - 1)), sps.Log2MaxFrameNum);
                if (!sps.FrameMbsOnly)
                    w.WriteBits(0, 1);
                if (idr)
                    w.WriteUe((uint)h.IdrPicId);
                if (sps.PocType == 0)
                {
                    w.WriteBits((uint)(2 * Pictures & ((1 << sps.Log2MaxPocLsb) - 1)), sps.Log2MaxPocLsb);
                    if (pps.BottomFieldPicOrderPresent)
                        WriteSe(ref w, 0);
                }
                if (h.SliceType == 0)
                {
                    var overridden = h.NumRefIdxL0 != pps.NumRefIdxL0Default;
                    w.WriteBits(overridden ? 1u : 0u, 1);
                    if (overridden)
                        w.WriteUe((uint)(h.NumRefIdxL0 - 1));
                }
                // 参考列表修改与参考标记的语法只取决于切片类型与 NAL 类型，差值按编号相对计算，照抄编码器的写法。
                w.CopyBits(source, h.ModificationStart, h.ModificationEnd - h.ModificationStart);
                if (pps.WeightedPred && h.SliceType == 0)
                {
                    // 源 PPS 要求加权表：写默认权重（各项标志为 0），与不加权的预测相同。
                    w.WriteUe(0);
                    w.WriteUe(0);
                    for (var i = 0; i < h.NumRefIdxL0; i++)
                        w.WriteBits(0, 2);
                }
                if (h.NalRefIdc != 0)
                    w.CopyBits(source, h.MarkingStart, h.MarkingEnd - h.MarkingStart);
                if (pps.Cabac && !h.Intra)
                    w.WriteUe((uint)h.CabacInitIdc);
                WriteSe(ref w, encoderPps.PicInitQp + h.SliceQpDelta - pps.PicInitQp);
                if (pps.DeblockingControlPresent)
                {
                    w.WriteUe((uint)h.DeblockingIdc);
                    if (h.DeblockingIdc != 1)
                    {
                        WriteSe(ref w, h.SliceAlphaOffset);
                        WriteSe(ref w, h.SliceBetaOffset);
                    }
                }
                else if (h.DeblockingIdc != 0 || h.SliceAlphaOffset != 0 || h.SliceBetaOffset != 0)
                    throw new InvalidDataException("Deblocking parameters cannot be signalled with the source PPS.");
                if (pps.Cabac)
                {
                    // CABAC：切片数据从字节边界开始，对齐位全为 1。
                    while (w.Position % 8 != 0)
                        w.WriteBits(1, 1);
                    w.CopyBytes(source[((h.HeaderEnd + 7) / 8)..]);
                }
                else
                {
                    var stop = StopBit(source);
                    if (stop < h.HeaderEnd)
                        throw new InvalidDataException("Missing rbsp_stop_one_bit.");
                    w.CopyBits(source, h.HeaderEnd, stop - h.HeaderEnd);
                    w.WriteBits(1, 1);
                    while (w.Position % 8 != 0)
                        w.WriteBits(0, 1);
                }
                BinaryPrimitives.WriteInt32BigEndian(output[position..], 1);
                position += 4;
                output[position++] = nal[0];
                position += H264Splice.Escape(rewritten.AsSpan(0, w.Position / 8), output[position..]);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rbsp);
                ArrayPool<byte>.Shared.Return(rewritten);
            }
        }

        /// <summary>
        /// 图像定时 SEI（D.1.3）：CPB 移除延迟按画面序号递增（相对头段开头），DPB 输出延迟为 0（头段没有重排序），
        /// pic_struct 按场序，不带时钟时间戳。
        /// </summary>
        private void WritePicTiming(Span<byte> output, ref int position)
        {
            Span<byte> payload = stackalloc byte[16];
            payload.Clear();
            var w = new H264Splice.BitWriter(payload);
            if (sps.CpbDpbDelaysPresent)
            {
                var mask = sps.CpbRemovalDelayLength >= 32 ? uint.MaxValue : (1u << sps.CpbRemovalDelayLength) - 1;
                w.WriteBits((uint)((long)ticksPerFrame * Pictures) & mask, sps.CpbRemovalDelayLength);
                w.WriteBits(0, sps.DpbOutputDelayLength);
            }
            w.WriteBits((uint)picStruct, 4);
            var clocks = picStruct switch { 3 or 4 or 7 => 2, 5 or 6 or 8 => 3, _ => 1 };
            for (var i = 0; i < clocks; i++)
                w.WriteBits(0, 1);
            if (w.Position % 8 != 0)
            {
                w.WriteBits(1, 1);
                while (w.Position % 8 != 0)
                    w.WriteBits(0, 1);
            }
            var size = w.Position / 8;
            Span<byte> sei = stackalloc byte[size + 3];
            sei[0] = 1;
            sei[1] = (byte)size;
            payload[..size].CopyTo(sei[2..]);
            sei[^1] = 0x80;
            BinaryPrimitives.WriteInt32BigEndian(output[position..], 1);
            position += 4;
            output[position++] = 0x06;
            position += H264Splice.Escape(sei, output[position..]);
        }
    }

    /// <summary>
    /// 复制区的切片重编号：首个画面的 frame_num 接续头段的画面数，POC 接续头段最后一个画面；
    /// 此后各切片平移同样的量（取模，位宽不变）。遇到码流自身的 IDR 后停止改写。
    /// 新码流中只有头段画面与复制的画面，长期参考帧无法对应，出现时抛出 <see cref="InvalidDataException"/>。
    /// </summary>
    internal sealed class CopyRenumberer(H264Splice.ParameterSets sets, int headPictures)
    {
        private readonly List<H264Splice.Mmco> mmco = [];
        private readonly List<(int Idc, int Value)> modifications = [];
        private bool started;
        private bool reset;
        private int frameOffset;
        private int pocOffset;

        public bool Active { get; private set; } = true;

        /// <summary>改写一个访问单元并写成 Annex B，返回写入长度；输出缓冲区大小见 <see cref="H264Splice.CopyRewriter.OutputBound"/>。</summary>
        public int Rewrite(ReadOnlySpan<byte> data, int lengthSize, Span<byte> output)
        {
            var position = 0;
            foreach (var range in VideoBitstream.EnumerateNals(data, lengthSize))
            {
                var nal = data[range];
                var type = nal[0] & 0x1F;
                if (type is 7 or 8)
                    sets.Add(nal);
                if (type == 5)
                    Active = false;
                if (!Active || type != 1)
                {
                    Write(nal, output, ref position);
                    continue;
                }
                Renumber(nal, output, ref position);
            }
            return position;
        }

        private void Renumber(ReadOnlySpan<byte> nal, Span<byte> output, ref int position)
        {
            if (TryRenumberInPlace(nal, output, ref position))
                return;
            var rbsp = ArrayPool<byte>.Shared.Rent(nal.Length);
            H264Splice.SliceHeader h;
            try
            {
                var length = H264Splice.Unescape(nal[1..], rbsp);
                if (!H264Splice.TryParseSlice(rbsp.AsSpan(0, length), nal[0], sets, mmco, out h, modifications))
                    throw new InvalidDataException("Unsupported H.264 slice header.");
            }
            finally { ArrayPool<byte>.Shared.Return(rbsp); }
            if (h.FirstMb == 0)
                StartPicture(h);
            WriteEscaped(nal, h, output, ref position);
        }

        /// <summary>
        /// 快速路径：只解析切片头前部，frame_num 与 POC 之前没有防竞争字节时直接在输出的 NAL 上改写这几位，
        /// 切片数据整体复制，不再逐字节去掉再加回防竞争字节。改写后在改动处形成防竞争模式等情况交给完整路径。
        /// </summary>
        private bool TryRenumberInPlace(ReadOnlySpan<byte> nal, Span<byte> output, ref int position)
        {
            var escaped = nal[1..Math.Min(nal.Length, 129)];
            Span<byte> head = stackalloc byte[escaped.Length];
            var length = H264Splice.Unescape(escaped, head);
            if (!H264Splice.TryParseSlice(head[..length], nal[0], sets, mmco, out var h, modifications))
                return false;
            var end = h.Sps.PocType == 0 ? h.PocLsbBit + h.Sps.Log2MaxPocLsb : h.FrameNumBit + h.Sps.Log2MaxFrameNum;
            // 改写的字节及其后两字节都在前缀内且没有防竞争字节时，RBSP 与转义后的字节一一对应。
            var span = (end + 7) / 8 + 2;
            if (span > escaped.Length || escaped[..span].IndexOf((ReadOnlySpan<byte>)[0, 0, 3]) >= 0)
                return false;
            if (h.FirstMb == 0)
                StartPicture(h);
            BinaryPrimitives.WriteInt32BigEndian(output[position..], 1);
            var target = output.Slice(position + 4, nal.Length);
            nal.CopyTo(target);
            var payload = target[1..];
            var maxFrameNum = h.Sps.MaxFrameNum;
            SetBits(payload, h.FrameNumBit, (uint)(((h.FrameNum - frameOffset) % maxFrameNum + maxFrameNum) % maxFrameNum),
                h.Sps.Log2MaxFrameNum);
            if (h.Sps.PocType == 0)
            {
                var maxLsb = 1 << h.Sps.Log2MaxPocLsb;
                SetBits(payload, h.PocLsbBit, (uint)(((h.PocLsb - pocOffset) % maxLsb + maxLsb) % maxLsb), h.Sps.Log2MaxPocLsb);
            }
            // 改写后不能在这段出现 0x000000–0x000003（需要防竞争字节）；出现时退回完整路径（画面状态已更新，只重写字节）。
            for (var i = 0; i + 2 < span; i++)
                if (payload[i] == 0 && payload[i + 1] == 0 && payload[i + 2] <= 3)
                {
                    WriteEscaped(nal, h, output, ref position);
                    return true;
                }
            position += 4 + nal.Length;
            return true;
        }

        private void WriteEscaped(ReadOnlySpan<byte> nal, in H264Splice.SliceHeader header, Span<byte> output, ref int position)
        {
            var rbsp = ArrayPool<byte>.Shared.Rent(nal.Length);
            try
            {
                var length = H264Splice.Unescape(nal[1..], rbsp);
                var maxFrameNum = header.Sps.MaxFrameNum;
                SetBits(rbsp, header.FrameNumBit,
                    (uint)(((header.FrameNum - frameOffset) % maxFrameNum + maxFrameNum) % maxFrameNum), header.Sps.Log2MaxFrameNum);
                if (header.Sps.PocType == 0)
                {
                    var maxLsb = 1 << header.Sps.Log2MaxPocLsb;
                    SetBits(rbsp, header.PocLsbBit, (uint)(((header.PocLsb - pocOffset) % maxLsb + maxLsb) % maxLsb),
                        header.Sps.Log2MaxPocLsb);
                }
                BinaryPrimitives.WriteInt32BigEndian(output[position..], 1);
                position += 4;
                output[position++] = nal[0];
                position += H264Splice.Escape(rbsp.AsSpan(0, length), output[position..]);
            }
            finally { ArrayPool<byte>.Shared.Return(rbsp); }
        }

        private void StartPicture(in H264Splice.SliceHeader h)
        {
            if (reset)
            {
                // MMCO5 之后原码流从 0 重新编号，改写后的码流同样执行了 MMCO5，不再平移。
                frameOffset = 0;
                pocOffset = 0;
                reset = false;
            }
            if (!started)
            {
                started = true;
                frameOffset = h.FrameNum - headPictures;
                pocOffset = h.PocLsb - 2 * headPictures;
            }
            if (h.Adaptive)
                foreach (var op in mmco)
                {
                    if (op.Op is 2 or 3 or 4 or 6)
                        throw new InvalidDataException("Long-term reference marking is not supported at a refresh splice.");
                    reset |= op.Op == 5;
                }
            foreach (var (idc, _) in modifications)
                if (idc == 2)
                    throw new InvalidDataException("Long-term reference list modification is not supported at a refresh splice.");
        }
    }

    private static void SetBits(Span<byte> data, int position, uint value, int count)
    {
        for (var i = count - 1; i >= 0; i--, position++)
        {
            var mask = (byte)(0x80 >> (position & 7));
            if (((value >> i) & 1) != 0)
                data[position >> 3] |= mask;
            else
                data[position >> 3] &= (byte)~mask;
        }
    }

    private static void WriteSe(ref H264Splice.BitWriter w, int value) =>
        w.WriteUe(value > 0 ? (uint)(2 * value - 1) : (uint)(-2 * value));

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

    private static void Write(ReadOnlySpan<byte> nal, Span<byte> output, ref int position)
    {
        BinaryPrimitives.WriteInt32BigEndian(output[position..], 1);
        position += 4;
        nal.CopyTo(output[position..]);
        position += nal.Length;
    }
}
