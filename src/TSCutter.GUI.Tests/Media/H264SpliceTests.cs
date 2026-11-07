using TSCutter.GUI.Utils.Media;
using Xunit;

namespace TSCutter.GUI.Tests.Media;

/// <summary>
/// 普通 I 帧衔接的码流改写（不依赖媒体文件）：合成与常见隔行广播相同结构的场编码（PAFF）片段——
/// I 顶场 + P 底场（参考列表修改）、按场编号的 MMCO——验证改写为 IDR、frame_num 重编号、
/// 删除指向 I 帧之前画面的 MMCO，以及可用参考不足时以显式修改补齐参考列表。
/// </summary>
public sealed class H264SpliceTests
{
    private const int Log2MaxFrameNum = 6;

    [Fact]
    public void RewritesFieldPairSpliceIntoConformingIdrStream()
    {
        var sets = new H264Splice.ParameterSets();
        var sps = Nal(0x67, Sps);
        var pps = Nal(0x68, Pps);
        sets.Add(sps);
        sets.Add(pps);
        var rewriter = new H264Splice.CopyRewriter(sets);
        // 1. I 顶场：MMCO 取消 I 帧之前第 2 帧的两个场。
        var intra = Rewrite(rewriter, true, [sps, pps, Nal(0x61, Slice(7, 44, bottom: false, poc: 8,
            mmco: [(1, 3), (1, 4)]))]);
        // 2. P 底场：参考列表修改把 I 顶场放到首位，列表长度 2（原第 2 项是 I 帧之前的场）。
        var secondField = Rewrite(rewriter, false, [Nal(0x41, Slice(5, 44, bottom: true, poc: 9,
            modification: [(0, 0)]))]);
        // 3. P 顶场：MMCO 指向 I 帧之前的帧，新码流中不存在。
        var nextTop = Rewrite(rewriter, false, [Nal(0x41, Slice(5, 45, bottom: false, poc: 14,
            mmco: [(1, 3), (1, 4)]))]);
        // 4. P 底场：l0 = 3，现有参考恰好 3 个场，不需要改动。
        var nextBottom = Rewrite(rewriter, false, [Nal(0x41, Slice(5, 45, bottom: true, poc: 15, l0: 3))]);
        // 5. P 顶场：MMCO 指向 I 帧（仍存在），保留。
        var later = Rewrite(rewriter, false, [Nal(0x41, Slice(5, 46, bottom: false, poc: 20,
            mmco: [(1, 3), (1, 4)]))]);

        var header = Parse(intra, sets, out var mmco, out _);
        Assert.Equal(5, header.NalType);
        Assert.Equal(0, header.FrameNum);
        Assert.True(header is { Field: true, Bottom: false, Intra: true });
        Assert.Empty(mmco);

        header = Parse(secondField, sets, out _, out var modifications);
        Assert.Equal(1, header.NalType);
        Assert.Equal(0, header.FrameNum);
        // 两项都指向 I 顶场（底场的 CurrPicNum = 1，相反场性的 0 号帧 picNum = 0）。
        Assert.Equal([(0, 0), (0, 2 * (1 << Log2MaxFrameNum) - 1)], modifications);

        header = Parse(nextTop, sets, out mmco, out _);
        Assert.Equal(1, header.FrameNum);
        Assert.True(header.Adaptive);
        Assert.Empty(mmco);

        header = Parse(nextBottom, sets, out mmco, out modifications);
        Assert.Equal(1, header.FrameNum);
        Assert.False(header.ModificationL0);
        Assert.Equal(3, header.NumRefIdxL0);

        header = Parse(later, sets, out mmco, out _);
        Assert.Equal(2, header.FrameNum);
        Assert.Equal([new H264Splice.Mmco(1, 3, 0), new H264Splice.Mmco(1, 4, 0)], mmco);
    }

    [Fact]
    public void KeepsUnaffectedSlicesAndRestartsAtStreamIdr()
    {
        var sets = new H264Splice.ParameterSets();
        var sps = Nal(0x67, Sps);
        var pps = Nal(0x68, Pps);
        sets.Add(sps);
        sets.Add(pps);
        var rewriter = new H264Splice.CopyRewriter(sets);
        Rewrite(rewriter, true, [sps, pps, Nal(0x61, Slice(7, 10, bottom: false, poc: 8, mmco: [(1, 3), (1, 4)]))]);
        var b = Nal(0x01, Slice(6, 10, bottom: true, poc: 9, l0: 1, l1: 1));
        // 非参考 B 场只改 frame_num；码流自身的 IDR 之后不再改写（原样输出）。
        var rewritten = Rewrite(rewriter, false, [b]);
        Assert.Equal(0, Parse(rewritten, sets, out _, out _).FrameNum);
        var idr = Nal(0x65, Slice(7, 0, bottom: false, poc: 0, idr: true));
        Assert.Equal(idr, Rewrite(rewriter, false, [idr]));
        Assert.False(rewriter.Active);
    }

    /// <summary>
    /// B 帧金字塔的开放 GOP（与部分广播编码器一致）：I 帧之后紧跟作参考的前导 B 帧，P 帧以参考列表修改跳过它、指向 I 帧。
    /// 前导画面不复制：作参考的前导 B 帧不再占用 frame_num，此后画面依次前移；参考列表修改与 MMCO 按原编号找到目标，
    /// 以新编号重写，指向被丢弃画面的 MMCO 删除。
    /// </summary>
    [Fact]
    public void RenumbersPicturesAfterDroppedLeadingReferences()
    {
        var sets = new H264Splice.ParameterSets();
        var sps = Nal(0x67, Sps);
        var pps = Nal(0x68, Pps);
        sets.Add(sps);
        sets.Add(pps);
        var rewriter = new H264Splice.CopyRewriter(sets);
        Rewrite(rewriter, true, [sps, pps, Nal(0x61, Slice(7, 10, bottom: false, poc: 24, field: false))]);
        // 前导：作参考的 B 帧（frame_num 11，MMCO 取消 I 帧之前的画面，新码流中本就不存在）与非参考 B 帧。
        Skip(rewriter, Nal(0x21, Slice(6, 11, bottom: false, poc: 20, l0: 1, l1: 1, field: false, reference: true,
            mmco: [(1, 4)])));
        Skip(rewriter, Nal(0x01, Slice(6, 12, bottom: false, poc: 18, l0: 1, l1: 1, field: false)));
        // P：原 CurrPicNum 12，差值 2 指向 I 帧（10）。
        var p = Rewrite(rewriter, false, [Nal(0x41, Slice(5, 12, bottom: false, poc: 32, l0: 1, field: false,
            modification: [(0, 1)]))]);
        var b = Rewrite(rewriter, false, [Nal(0x21, Slice(6, 13, bottom: false, poc: 28, l0: 1, l1: 1, field: false,
            reference: true))]);
        // P：参考列表修改指向上一个 P（12）；MMCO 先取消 I 帧（差值 4），再取消已丢弃的前导 B 帧（差值 3）。
        var next = Rewrite(rewriter, false, [Nal(0x41, Slice(5, 14, bottom: false, poc: 40, l0: 1, field: false,
            modification: [(0, 1)], mmco: [(1, 3), (1, 2)]))]);

        var header = Parse(p, sets, out _, out var modifications);
        Assert.Equal(1, header.FrameNum);
        Assert.Equal([(0, 0)], modifications);
        Assert.Equal(2, Parse(b, sets, out _, out _).FrameNum);
        header = Parse(next, sets, out var mmco, out modifications);
        Assert.Equal(3, header.FrameNum);
        Assert.Equal([(0, 1)], modifications);
        Assert.Equal([new H264Splice.Mmco(1, 2, 0)], mmco);
    }

    /// <summary>
    /// 不能安全丢弃的前导参考画面明确报出不支持，不静默改写：场画面、出现在保留画面之后、MMCO 取消 I 帧（保留画面）。
    /// </summary>
    [Fact]
    public void RejectsLeadingReferencesThatCannotBeRenumbered()
    {
        var sets = new H264Splice.ParameterSets();
        var sps = Nal(0x67, Sps);
        var pps = Nal(0x68, Pps);
        sets.Add(sps);
        sets.Add(pps);
        var fields = new H264Splice.CopyRewriter(sets);
        Rewrite(fields, true, [sps, pps, Nal(0x61, Slice(7, 10, bottom: false, poc: 24, field: false))]);
        Assert.Throws<InvalidDataException>(() =>
            Skip(fields, Nal(0x21, Slice(6, 11, bottom: false, poc: 20, l0: 1, l1: 1, reference: true))));

        var unmarking = new H264Splice.CopyRewriter(sets);
        Rewrite(unmarking, true, [sps, pps, Nal(0x61, Slice(7, 10, bottom: false, poc: 24, field: false))]);
        Assert.Throws<InvalidDataException>(() => Skip(unmarking, Nal(0x21, Slice(6, 11, bottom: false, poc: 20, l0: 1, l1: 1,
            field: false, reference: true, mmco: [(1, 0)]))));

        var late = new H264Splice.CopyRewriter(sets);
        Rewrite(late, true, [sps, pps, Nal(0x61, Slice(7, 10, bottom: false, poc: 24, field: false))]);
        Rewrite(late, false, [Nal(0x41, Slice(5, 11, bottom: false, poc: 32, l0: 1, field: false))]);
        Assert.Throws<InvalidDataException>(() =>
            Skip(late, Nal(0x21, Slice(6, 12, bottom: false, poc: 28, l0: 1, l1: 1, field: false, reference: true))));
    }

    private static void Skip(H264Splice.CopyRewriter rewriter, byte[] nal) =>
        rewriter.SkipLeading(new byte[] { 0, 0, 0, 1 }.Concat(nal).ToArray(), 0);

    private static byte[] Rewrite(H264Splice.CopyRewriter rewriter, bool convert, byte[][] nals)
    {
        var data = nals.SelectMany(nal => new byte[] { 0, 0, 0, 1 }.Concat(nal)).ToArray();
        var output = new byte[H264Splice.CopyRewriter.OutputBound(data, 0, null)];
        var length = rewriter.Rewrite(data, 0, convert, 1, null, output);
        // 只返回最后一个 NAL（切片）。
        var last = VideoBitstream.SplitNals(output.AsSpan(0, length), 0)[^1];
        return output.AsSpan(0, length)[last].ToArray();
    }

    private static H264Splice.SliceHeader Parse(byte[] nal, H264Splice.ParameterSets sets, out List<H264Splice.Mmco> mmco,
        out List<(int Idc, int Value)> modifications)
    {
        mmco = [];
        modifications = [];
        var rbsp = new byte[nal.Length];
        var length = H264Splice.Unescape(nal.AsSpan(1), rbsp);
        Assert.True(H264Splice.TryParseSlice(rbsp.AsSpan(0, length), nal[0], sets, mmco, out var header, modifications));
        return header;
    }

    private delegate void Syntax(ref H264Splice.BitWriter writer);

    private static byte[] Nal(int header, Syntax body)
    {
        var rbsp = new byte[256];
        var writer = new H264Splice.BitWriter(rbsp);
        body(ref writer);
        var length = writer.Position / 8;
        var escaped = new byte[H264Splice.MaxEscapedLength(length) + 1];
        escaped[0] = (byte)header;
        return escaped.AsSpan(0, 1 + H264Splice.Escape(rbsp.AsSpan(0, length), escaped.AsSpan(1))).ToArray();
    }

    private static void Trailing(ref H264Splice.BitWriter w)
    {
        w.WriteBits(1, 1);
        while (w.Position % 8 != 0)
            w.WriteBits(0, 1);
    }

    // High 档次 4:2:0；frame_mbs_only=0（MBAFF/场编码），POC 类型 0。
    private static void Sps(ref H264Splice.BitWriter w)
    {
        w.WriteBits(100, 8);
        w.WriteBits(0, 8);
        w.WriteBits(40, 8);
        w.WriteUe(0);
        w.WriteUe(1);
        w.WriteUe(0);
        w.WriteUe(0);
        w.WriteBits(0, 1);
        w.WriteBits(0, 1);
        w.WriteUe(Log2MaxFrameNum - 4);
        w.WriteUe(0);
        w.WriteUe(4);
        w.WriteUe(4);
        w.WriteBits(0, 1);
        w.WriteUe(119);
        w.WriteUe(33);
        w.WriteBits(0, 1);
        w.WriteBits(1, 1);
        w.WriteBits(1, 1);
        w.WriteBits(0, 1);
        w.WriteBits(0, 1);
        Trailing(ref w);
    }

    // CABAC，场 POC 存在 delta_pic_order_cnt_bottom，默认 l0 = 2、l1 = 1。
    private static void Pps(ref H264Splice.BitWriter w)
    {
        w.WriteUe(0);
        w.WriteUe(0);
        w.WriteBits(1, 1);
        w.WriteBits(1, 1);
        w.WriteUe(0);
        w.WriteUe(1);
        w.WriteUe(0);
        w.WriteBits(0, 1);
        w.WriteBits(0, 2);
        w.WriteUe(0);
        w.WriteUe(0);
        w.WriteUe(0);
        w.WriteBits(1, 1);
        w.WriteBits(0, 1);
        w.WriteBits(0, 1);
        Trailing(ref w);
    }

    private static Syntax Slice(int sliceType, int frameNum, bool bottom, int poc, (int, int)[]? mmco = null,
        (int, int)[]? modification = null, int? l0 = null, int? l1 = null, bool idr = false, bool field = true,
        bool? reference = null) =>
        (ref H264Splice.BitWriter w) =>
        {
            var type = sliceType % 5;
            w.WriteUe(0);
            w.WriteUe((uint)sliceType);
            w.WriteUe(0);
            w.WriteBits((uint)frameNum, Log2MaxFrameNum);
            w.WriteBits(field ? 1u : 0u, 1);
            if (field)
                w.WriteBits(bottom ? 1u : 0u, 1);
            if (idr)
                w.WriteUe(0);
            w.WriteBits((uint)poc, 8);
            // 帧画面：PPS 声明了 delta_pic_order_cnt_bottom。
            if (!field)
                w.WriteUe(0);
            if (type == 1)
                w.WriteBits(1, 1);
            if (type is 0 or 1)
            {
                var overrides = l0 is not null || l1 is not null;
                w.WriteBits(overrides ? 1u : 0u, 1);
                if (overrides)
                {
                    w.WriteUe((uint)(l0 ?? 2) - 1);
                    if (type == 1)
                        w.WriteUe((uint)(l1 ?? 1) - 1);
                }
                w.WriteBits(modification is null ? 0u : 1u, 1);
                if (modification is not null)
                {
                    foreach (var (idc, value) in modification)
                    {
                        w.WriteUe((uint)idc);
                        w.WriteUe((uint)value);
                    }
                    w.WriteUe(3);
                }
                if (type == 1)
                    w.WriteBits(0, 1);
            }
            var referenced = reference ?? sliceType is 7 or 5;
            if (referenced && idr)
                w.WriteBits(0, 2);
            else if (referenced)
            {
                w.WriteBits(mmco is null ? 0u : 1u, 1);
                if (mmco is not null)
                {
                    foreach (var (op, value) in mmco)
                    {
                        w.WriteUe((uint)op);
                        w.WriteUe((uint)value);
                    }
                    w.WriteUe(0);
                }
            }
            if (type != 2)
                w.WriteUe(0);
            w.WriteUe(0);
            w.WriteUe(0);
            w.WriteUe(0);
            w.WriteUe(0);
            // CABAC 对齐位与少量切片数据。
            while (w.Position % 8 != 0)
                w.WriteBits(1, 1);
            w.WriteBits(0xAB, 8);
            w.WriteBits(0xCD, 8);
            w.WriteBits(0x80, 8);
        };
}
