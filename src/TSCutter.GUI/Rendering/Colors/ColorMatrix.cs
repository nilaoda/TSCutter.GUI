using System.Numerics;

namespace TSCutter.GUI.Rendering.Colors;

internal readonly record struct ColorMatrix(float M00, float M01, float M02,
    float M10, float M11, float M12, float M20, float M21, float M22)
{
    internal static unsafe ColorMatrix Read(int* data) => new(
        DolbyVisionMetadata.Rational(data, 0), DolbyVisionMetadata.Rational(data, 1), DolbyVisionMetadata.Rational(data, 2),
        DolbyVisionMetadata.Rational(data, 3), DolbyVisionMetadata.Rational(data, 4), DolbyVisionMetadata.Rational(data, 5),
        DolbyVisionMetadata.Rational(data, 6), DolbyVisionMetadata.Rational(data, 7), DolbyVisionMetadata.Rational(data, 8));

    internal void Transform(float a, float b, float c, out float r, out float g, out float blue)
    {
        r = M00 * a + M01 * b + M02 * c; g = M10 * a + M11 * b + M12 * c; blue = M20 * a + M21 * b + M22 * c;
    }

    internal void Transform(Vector<float> a, Vector<float> b, Vector<float> c,
        out Vector<float> r, out Vector<float> g, out Vector<float> blue)
    {
        r = new Vector<float>(M00) * a + new Vector<float>(M01) * b + new Vector<float>(M02) * c;
        g = new Vector<float>(M10) * a + new Vector<float>(M11) * b + new Vector<float>(M12) * c;
        blue = new Vector<float>(M20) * a + new Vector<float>(M21) * b + new Vector<float>(M22) * c;
    }

    public static ColorMatrix operator *(ColorMatrix a, ColorMatrix b) => new(
        a.M00 * b.M00 + a.M01 * b.M10 + a.M02 * b.M20, a.M00 * b.M01 + a.M01 * b.M11 + a.M02 * b.M21, a.M00 * b.M02 + a.M01 * b.M12 + a.M02 * b.M22,
        a.M10 * b.M00 + a.M11 * b.M10 + a.M12 * b.M20, a.M10 * b.M01 + a.M11 * b.M11 + a.M12 * b.M21, a.M10 * b.M02 + a.M11 * b.M12 + a.M12 * b.M22,
        a.M20 * b.M00 + a.M21 * b.M10 + a.M22 * b.M20, a.M20 * b.M01 + a.M21 * b.M11 + a.M22 * b.M21, a.M20 * b.M02 + a.M21 * b.M12 + a.M22 * b.M22);
}
