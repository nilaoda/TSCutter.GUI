// 感知色域映射参考 libplacebo（LGPL-2.1-or-later）；色相边界缓存为 CPU 查找表。
using System;
using System.Collections.Generic;

namespace TSCutter.GUI.Rendering.Colors;

internal sealed unsafe partial class HdrToneMapper
{
    private static readonly ColorMatrix Identity = new(1, 0, 0, 0, 1, 0, 0, 0, 1);
    private static readonly Lazy<GamutDescription> TargetGamutCache = new(() => new GamutDescription(Identity));
    private static GamutDescription TargetGamut => TargetGamutCache.Value;
    private static readonly Dictionary<ColorMatrix, GamutDescription> Gamuts = new();

    private static GamutDescription GetGamut(ColorMatrix to709)
    {
        if (to709 == Identity)
            return TargetGamut;
        lock (Gamuts)
        {
            if (!Gamuts.TryGetValue(to709, out var gamut))
                Gamuts.Add(to709, gamut = new GamutDescription(to709));
            return gamut;
        }
    }

    private static void MapGamut(float l, float m, float s, GamutDescription source,
        out float r, out float g, out float blue)
    {
        if (l == m && m == s)
        {
            r = g = blue = l <= SdrBlackPq + 1e-6f ? 0 : EncodeSdrLinear(DecodeMappedPq(l));
            return;
        }
        var ln = DecodeMappedPq(l); var mn = DecodeMappedPq(m); var sn = DecodeMappedPq(s);
        if (!ReferenceEquals(source, TargetGamut))
        {
            LmsToIpt.Transform(l, m, s, out var i, out var p, out var t);
            var chroma = MathF.Sqrt(p * p + t * t);
            var hue = (MathF.Atan2(t, p) / (2 * MathF.PI) + 0.5f) * GamutDescription.HueCount;
            var maximum = Math.Max(source.ChromaAt(hue), TargetGamut.ChromaAt(hue));
            var blend = Math.Clamp((chroma / Math.Max(maximum, 1e-6f) - 0.3f) / 0.7f, 0, 1);
            blend = blend * blend * (3 - 2 * blend) * 0.8f;
            if (blend > 0)
            {
                // 色度较高时逐步转向相对色度映射；低色度区域保留原色，保护肤色和灰阶。
                source.FromLms.Transform(ln, mn, sn, out var sr, out var sg, out var sb);
                Rgb709ToLms.Transform(sr, sg, sb, out var ml, out var mm, out var ms);
                LmsToIpt.Transform(EncodeMappedPq(ml), EncodeMappedPq(mm), EncodeMappedPq(ms), out var mi, out var mp, out var mt);
                IptToLms.Transform(i + blend * (mi - i), p + blend * (mp - p), t + blend * (mt - t), out l, out m, out s);
                ln = DecodeMappedPq(l); mn = DecodeMappedPq(m); sn = DecodeMappedPq(s);
            }
        }
        LmsToRgb709.Transform(ln, mn, sn, out r, out g, out blue);
        var peak = Math.Max(r, Math.Max(g, blue));
        r = EncodeSdrLinear(SoftClip(r, peak)); g = EncodeSdrLinear(SoftClip(g, peak)); blue = EncodeSdrLinear(SoftClip(blue, peak));
    }

    private static float EncodeSdrLinear(float value) => Math.Clamp((value - SdrBlackRatio) / (1 - SdrBlackRatio), 0, 1);

    private static float EncodeMappedPq(float value)
        => Lookup(PqEncodingTable, MathF.Sqrt(Math.Max(0, value * SdrWhiteNits)) * 0.01f);

    private sealed class GamutDescription
    {
        internal const int HueCount = 360;
        private readonly float[] maximumChroma = new float[HueCount];
        internal readonly ColorMatrix FromLms;

        internal GamutDescription(ColorMatrix to709)
        {
            var toLms = Rgb709ToLms * to709;
            FromLms = Invert(toLms);
            // 仅初始化时采样色域表面。播放热路径只插值色相边界，不做迭代搜索。
            const int steps = 32;
            for (var axis = 0; axis < 3; axis++)
            for (var face = 0; face < 2; face++)
            for (var x = 0; x <= steps; x++)
            for (var y = 0; y <= steps; y++)
            {
                var a = SdrBlackRatio + (1 - SdrBlackRatio) * x / steps; var b = SdrBlackRatio + (1 - SdrBlackRatio) * y / steps;
                var edge = face == 0 ? SdrBlackRatio : 1;
                var (r, g, blue) = axis switch { 0 => (edge, a, b), 1 => (a, edge, b), _ => (a, b, edge) };
                toLms.Transform(r, g, blue, out var l, out var m, out var s);
                LmsToIpt.Transform(EncodeMappedPq(l), EncodeMappedPq(m), EncodeMappedPq(s), out _, out var p, out var t);
                var chroma = MathF.Sqrt(p * p + t * t);
                var index = (int)((MathF.Atan2(t, p) / (2 * MathF.PI) + 0.5f) * HueCount);
                index %= HueCount;
                maximumChroma[index] = Math.Max(maximumChroma[index], chroma);
                maximumChroma[(index + 1) % HueCount] = Math.Max(maximumChroma[(index + 1) % HueCount], chroma);
            }
            for (var index = 0; index < HueCount; index++)
            {
                if (maximumChroma[index] > 0)
                    continue;
                for (var distance = 1; distance < HueCount; distance++)
                {
                    var value = Math.Max(maximumChroma[(index + distance) % HueCount], maximumChroma[(index + HueCount - distance) % HueCount]);
                    if (value <= 0)
                        continue;
                    maximumChroma[index] = value;
                    break;
                }
            }
        }

        internal float ChromaAt(float hue)
        {
            var index = (int)hue;
            var fraction = hue - index;
            index %= HueCount;
            return maximumChroma[index] + fraction * (maximumChroma[(index + 1) % HueCount] - maximumChroma[index]);
        }

        private static ColorMatrix Invert(ColorMatrix m)
        {
            var a = m.M11 * m.M22 - m.M12 * m.M21;
            var b = m.M12 * m.M20 - m.M10 * m.M22;
            var c = m.M10 * m.M21 - m.M11 * m.M20;
            var determinant = m.M00 * a + m.M01 * b + m.M02 * c;
            return new ColorMatrix(a / determinant, (m.M02 * m.M21 - m.M01 * m.M22) / determinant, (m.M01 * m.M12 - m.M02 * m.M11) / determinant,
                b / determinant, (m.M00 * m.M22 - m.M02 * m.M20) / determinant, (m.M02 * m.M10 - m.M00 * m.M12) / determinant,
                c / determinant, (m.M01 * m.M20 - m.M00 * m.M21) / determinant, (m.M00 * m.M11 - m.M01 * m.M10) / determinant);
        }
    }
}
