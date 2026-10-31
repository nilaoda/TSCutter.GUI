// IPT 变换及色度适配参考 libplacebo（LGPL-2.1-or-later），改为 CPU/SIMD 和查找表处理。
using System;
using System.Numerics;

namespace TSCutter.GUI.Rendering.Colors;

internal sealed unsafe partial class HdrToneMapper
{
    // HPE D65 锥体响应加入 4% 串扰；矩阵与逆矩阵在同一白点下使用。
    private static readonly ColorMatrix Rgb709ToLms = new(
        0.29576408f, 0.62307245f, 0.08116675f,
        0.15619198f, 0.72725164f, 0.11655793f,
        0.03510228f, 0.15658995f, 0.80830303f);
    private static readonly ColorMatrix LmsToRgb709 = new(
        6.17353266f, -5.32089882f, 0.14735489f,
        -1.32403191f, 2.56026977f, -0.23623862f,
        -0.01159839f, -0.26492145f, 1.27652634f);
    private static readonly ColorMatrix LmsToIpt = new(
        0.4f, 0.4f, 0.2f, 4.455f, -4.851f, 0.396f, 0.8056f, 0.3572f, -1.1628f);
    private static readonly ColorMatrix IptToLms = new(
        1, 0.09756893f, 0.20522643f, 1, -0.11387649f, 0.13321716f, 1, 0.03261511f, -0.67688718f);
    // 平方分布提高暗部的 PQ 编码精度；每像素只需 SIMD sqrt 和表查询。
    private static readonly float[] PqEncodingTable = BuildTable(x => NitsToPq(10000 * x * x));
    private static readonly float[] HlgDisplayScaleTable = BuildTable(x => 1000 * MathF.Pow(x, 0.2f));

    private void MapLinearColors(HdrFrameMetadata metadata, ColorMatrix linear, RowBuffer buffer, int width, bool useSimd, GamutDescription gamut)
    {
        var a = buffer.Red; var b = buffer.Green; var c = buffer.Blue; var scale = buffer.ToneScale;
        var toLms = Rgb709ToLms * linear;
        var hlg = metadata.Transfer == HdrTransfer.Hlg;
        var y0 = 0.2126f * linear.M00 + 0.7152f * linear.M10 + 0.0722f * linear.M20;
        var y1 = 0.2126f * linear.M01 + 0.7152f * linear.M11 + 0.0722f * linear.M21;
        var y2 = 0.2126f * linear.M02 + 0.7152f * linear.M12 + 0.0722f * linear.M22;
        var vectorWidth = (width + Vector<float>.Count - 1) / Vector<float>.Count * Vector<float>.Count;
        if (useSimd && Vector.IsHardwareAccelerated && width >= Vector<float>.Count)
        {
            for (var x = 0; x < vectorWidth; x += Vector<float>.Count)
            {
                var r = new Vector<float>(a, x); var g = new Vector<float>(b, x); var blue = new Vector<float>(c, x);
                toLms.Transform(r, g, blue, out var l, out var m, out var s);
                l.CopyTo(a, x); m.CopyTo(b, x); s.CopyTo(c, x);
                if (hlg)
                    (r * new Vector<float>(y0) + g * new Vector<float>(y1) + blue * new Vector<float>(y2)).CopyTo(scale, x);
            }
            for (var x = 0; x < width; x++)
                scale[x] = hlg ? Lookup(HlgDisplayScaleTable, scale[x]) : 1;
            for (var x = 0; x < vectorWidth; x += Vector<float>.Count)
            {
                var factor = new Vector<float>(scale, x);
                (Vector.SquareRoot(Vector.Max(Vector<float>.Zero, new Vector<float>(a, x) * factor)) * new Vector<float>(0.01f)).CopyTo(a, x);
                (Vector.SquareRoot(Vector.Max(Vector<float>.Zero, new Vector<float>(b, x) * factor)) * new Vector<float>(0.01f)).CopyTo(b, x);
                (Vector.SquareRoot(Vector.Max(Vector<float>.Zero, new Vector<float>(c, x) * factor)) * new Vector<float>(0.01f)).CopyTo(c, x);
            }
            for (var x = 0; x < width; x++)
            {
                a[x] = Lookup(PqEncodingTable, a[x]); b[x] = Lookup(PqEncodingTable, b[x]); c[x] = Lookup(PqEncodingTable, c[x]);
            }
            for (var x = 0; x < vectorWidth; x += Vector<float>.Count)
            {
                LmsToIpt.Transform(new Vector<float>(a, x), new Vector<float>(b, x), new Vector<float>(c, x), out var i, out var p, out var t);
                i.CopyTo(a, x); p.CopyTo(b, x); t.CopyTo(c, x);
            }
            for (var x = 0; x < width; x++)
                scale[x] = Lookup(toneTable, a[x]);
            for (var x = 0; x < vectorWidth; x += Vector<float>.Count)
            {
                var original = new Vector<float>(a, x);
                var mapped = new Vector<float>(scale, x);
                var epsilon = new Vector<float>(1e-6f);
                // 亮度变化时同步调整感知色度，避免高光压缩后色彩显得过浓。
                var chroma = Vector.Min(original / Vector.Max(mapped, epsilon),
                    IntensityHull(mapped) / Vector.Max(IntensityHull(original), epsilon));
                var p = new Vector<float>(b, x); var t = new Vector<float>(c, x);
                // SDR 8 位输出中不可见的中性色度归零，避免矩阵舍入使白色平台微微偏色或倒退。
                var neutral = Vector.LessThan(Vector.Abs(p), new Vector<float>(0.0001f)) & Vector.LessThan(Vector.Abs(t), new Vector<float>(0.0001f));
                p = Vector.ConditionalSelect(neutral, Vector<float>.Zero, p);
                t = Vector.ConditionalSelect(neutral, Vector<float>.Zero, t);
                IptToLms.Transform(mapped, p * chroma, t * chroma, out var l, out var m, out var s);
                l.CopyTo(a, x); m.CopyTo(b, x); s.CopyTo(c, x);
            }
            for (var x = 0; x < width; x++)
                MapGamut(a[x], b[x], c[x], gamut, out a[x], out b[x], out c[x]);
        }
        else
        {
            for (var x = 0; x < width; x++)
            {
                var factor = hlg ? Lookup(HlgDisplayScaleTable, y0 * a[x] + y1 * b[x] + y2 * c[x]) : 1;
                toLms.Transform(a[x], b[x], c[x], out var l, out var m, out var s);
                l = Lookup(PqEncodingTable, MathF.Sqrt(Math.Max(0, l * factor)) * 0.01f);
                m = Lookup(PqEncodingTable, MathF.Sqrt(Math.Max(0, m * factor)) * 0.01f);
                s = Lookup(PqEncodingTable, MathF.Sqrt(Math.Max(0, s * factor)) * 0.01f);
                LmsToIpt.Transform(l, m, s, out var i, out var p, out var t);
                var mapped = Lookup(toneTable, i);
                var chroma = Math.Min(i / Math.Max(mapped, 1e-6f), IntensityHull(mapped) / Math.Max(IntensityHull(i), 1e-6f));
                if (Math.Abs(p) < 0.0001f && Math.Abs(t) < 0.0001f)
                    p = t = 0;
                IptToLms.Transform(mapped, p * chroma, t * chroma, out l, out m, out s);
                MapGamut(l, m, s, gamut, out a[x], out b[x], out c[x]);
            }
        }
    }

    private static Vector<float> IntensityHull(Vector<float> value)
        => ((value - new Vector<float>(6)) * value + new Vector<float>(9)) * value;

    private static float IntensityHull(float value) => ((value - 6) * value + 9) * value;

    private static float DecodeMappedPq(float value) => value <= 1e-6f ? 0 : Lookup(PqTable, value) / SdrWhiteNits;

    // Libplacebo 的 Mobius 软裁切：保留中间调，高光平滑收进 SDR 色域。
    private static float SoftClip(float value, float peak)
    {
        const float knee = 0.7f;
        if (peak <= 1 || value <= knee)
            return Math.Clamp(value, 0, 1);
        var a = -knee * knee * (peak - 1) / (knee * knee - 2 * knee + peak);
        var b = (knee * knee - 2 * knee * peak + peak) / Math.Max(1e-6f, peak - 1);
        var scale = (b + knee) * (b + knee) / (b - a);
        return Math.Clamp(scale * (Math.Min(value, peak) + a) / (Math.Min(value, peak) + b), 0, 1);
    }

}
