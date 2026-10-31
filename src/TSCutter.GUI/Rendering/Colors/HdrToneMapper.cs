// P5 重建公式参考 libplacebo（LGPL-2.1-or-later）；CPU/SIMD、查找表和 SDR 输出为本项目实现。
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FFmpeg.AutoGen.Abstractions;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Rendering.Colors;

internal sealed unsafe partial class HdrToneMapper
{
    private const int TableSize = 16384;
    private const float SdrWhiteNits = 203f;
    private const float SdrBlackRatio = 1f / 1000;
    private static readonly float SdrBlackPq = NitsToPq(SdrWhiteNits * SdrBlackRatio);
    private static readonly float[] PqTable = BuildTable(PqToNits);
    private static readonly float[] SrgbTable = BuildTable(x => 255f *
        (x <= 0.0031308f ? 12.92f * x : 1.055f * MathF.Pow(x, 1f / 2.4f) - 0.055f));
    private RowBuffer[] rowBuffers = [];
    private readonly int maximumParallelism;

    internal HdrToneMapper(int maximumParallelism = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumParallelism);
        this.maximumParallelism = maximumParallelism == 0 ? Math.Min(8, Environment.ProcessorCount)
            : Math.Min(maximumParallelism, Environment.ProcessorCount);
    }
    private SamplingGrid? samplingGrid;
    private readonly float[] toneTable = new float[TableSize + 1];
    private float previousPeak;
    private static readonly float[] HlgTable = BuildTable(HlgToLinear);

    internal void Convert(AVFrame* source, AVFrame* destination, HdrFrameMetadata metadata, bool useSimd = true)
    {
        if (destination->format != (int)AVPixelFormat.AV_PIX_FMT_BGRA || destination->width <= 0 || destination->height <= 0
            || destination->data[0] == null || Math.Abs((long)destination->linesize[0]) < destination->width * 4L)
            throw new ArgumentException("HDR preview requires a valid BGRA destination.");
        var descriptor = FF.av_pix_fmt_desc_get((AVPixelFormat)source->format);
        // 只接受可在 CPU 上读取的、小端整字节 YUV 格式；P010 与平面 10/12 位走相同采样路径。
        if (descriptor == null || descriptor->nb_components != 3 || (descriptor->flags & 0x2f) != 0
            || source->width <= 0 || source->height <= 0)
            throw new NotSupportedException("Unsupported HDR preview pixel format.");
        var geometry = new SampleGeometry(source->width, source->height, destination->width, destination->height,
            (AVPixelFormat)source->format, source->chroma_location);
        if (samplingGrid?.Geometry != geometry) samplingGrid = new SamplingGrid(geometry, descriptor);
        var ySampler = new ComponentSampler(source, descriptor, 0, metadata.BitDepth, samplingGrid.Luma, destination->height);
        var uSampler = new ComponentSampler(source, descriptor, 1, metadata.BitDepth, samplingGrid.Chroma, destination->height);
        var vSampler = new ComponentSampler(source, descriptor, 2, metadata.BitDepth, samplingGrid.Chroma, destination->height);
        // 每个分区至少承担约 12.8 万像素，避免小缩略图的调度成本超过计算收益。
        var pixels = destination->width * (long)destination->height;
        var workerCount = (int)Math.Min(Math.Min(maximumParallelism, destination->height), Math.Max(1, pixels / 128_000));
        Prepare(destination->width, metadata.PeakNits, workerCount);
        var gamut = GetGamut(metadata.GamutTo709);
        var destinationAddress = (nint)destination->data[0];
        var stride = destination->linesize[0];
        var height = destination->height;
        var width = destination->width;
        if (workerCount == 1)
            ConvertRows(0, 1, height, width, destinationAddress, stride, ySampler, uSampler, vSampler, metadata, useSimd, gamut);
        else
            ConvertParallel(workerCount, height, width, destinationAddress, stride, ySampler, uSampler, vSampler, metadata, useSimd, gamut);
    }

    private void ConvertParallel(int workerCount, int height, int width, nint address, int stride,
        ComponentSampler ySampler, ComponentSampler uSampler, ComponentSampler vSampler,
        HdrFrameMetadata metadata, bool useSimd, GamutDescription gamut)
    {
        // 每个分区独占行缓冲，所有任务返回后才允许解除目标位图锁。
        // 闭包限于此方法，小预览的单分区路径不产生调度分配。
        Parallel.For(0, workerCount, worker => ConvertRows(worker, workerCount, height, width,
            address, stride, ySampler, uSampler, vSampler, metadata, useSimd, gamut));
    }

    private void ConvertRows(int worker, int workerCount, int height, int width, nint address, int stride,
        ComponentSampler ySampler, ComponentSampler uSampler, ComponentSampler vSampler,
        HdrFrameMetadata metadata, bool useSimd, GamutDescription gamut)
    {
        var buffer = rowBuffers[worker];
        var a = buffer.Red; var b = buffer.Green; var c = buffer.Blue;
        for (var y = worker; y < height; y += workerCount)
        {
            ySampler.SampleRow(y, a);
            uSampler.SampleRow(y, b);
            vSampler.SampleRow(y, c);
            ProcessSignals(metadata, metadata.Linear, buffer, width, useSimd, gamut);
            var pixels = new Span<uint>((byte*)address + y * stride, width);
            for (var x = 0; x < pixels.Length; x++)
                pixels[x] = (uint)(Encode(c[x]) | Encode(b[x]) << 8 | Encode(a[x]) << 16) | 0xff000000u;
        }
    }

    private void Prepare(int width, float peak, int workerCount = 1)
    {
        var length = (width + Vector<float>.Count - 1) / Vector<float>.Count * Vector<float>.Count;
        // 行缓冲按容量复用，分辨率缩小时只处理有效 Span，切回大尺寸无需重新分配。
        if (rowBuffers.Length < workerCount)
        {
            var expanded = new RowBuffer[workerCount];
            for (var worker = 0; worker < expanded.Length; worker++)
                expanded[worker] = worker < rowBuffers.Length ? rowBuffers[worker] : new RowBuffer(length);
            rowBuffers = expanded;
        }
        for (var worker = 0; worker < workerCount; worker++)
            if (rowBuffers[worker].Red.Length < length) rowBuffers[worker] = new RowBuffer(length);
        peak = Math.Clamp(peak, SdrWhiteNits, 10000f);
        if (!float.IsFinite(peak)) throw new InvalidOperationException("Invalid HDR source peak.");
        if (peak == previousPeak) return;
        previousPeak = peak;
        // BT.2390 曲线保留中间调亮度，仅在高光侧平滑压缩；固定源峰值避免逐帧亮度波动。
        var black = NitsToPq(0);
        var sourcePq = NitsToPq(peak);
        var targetPq = NitsToPq(SdrWhiteNits);
        var range = sourcePq - black;
        var target = (targetPq - black) / range;
        var minimum = (SdrBlackPq - black) / range;
        var knee = 1.5f * target - 0.5f;
        var gain = target < 1 ? 1 / (1 + minimum / target * MathF.Pow(1 - target, 4)) : 1;
        for (var i = 0; i <= TableSize; i++)
        {
            var value = Math.Clamp((i / (float)TableSize - black) / range, 0, 1);
            if (value > knee && target < 1)
            {
                var t = (value - knee) / (1 - knee);
                var t2 = t * t;
                var t3 = t2 * t;
                value = (2 * t3 - 3 * t2 + 1) * knee
                    + (t3 - 2 * t2 + t) * (1 - knee)
                    + (-2 * t3 + 3 * t2) * target;
            }
            // 黑位补偿在暗部平滑衰减，并保持输出白点不变。
            value += minimum * MathF.Pow(1 - value, 4);
            value = gain * (value - minimum) + minimum;
            toneTable[i] = Math.Clamp(value * range + black, SdrBlackPq, targetPq);
        }
    }

    // 分离信号处理，便于在没有 FFmpeg 动态库的机器上对比标量和 SIMD 结果。
    internal void MapSignals(HdrFrameMetadata metadata, ReadOnlySpan<float> y, ReadOnlySpan<float> u,
        ReadOnlySpan<float> v, Span<float> r, Span<float> g, Span<float> blue, bool useSimd)
    {
        if (y.Length != u.Length || y.Length != v.Length || r.Length != y.Length || g.Length != y.Length || blue.Length != y.Length)
            throw new ArgumentException("Signal lengths must match.");
        Prepare(y.Length, metadata.PeakNits);
        var buffer = rowBuffers[0];
        var a = buffer.Red; var b = buffer.Green; var c = buffer.Blue;
        y.CopyTo(a); u.CopyTo(b); v.CopyTo(c);
        ProcessSignals(metadata, metadata.Linear, buffer, y.Length, useSimd, GetGamut(metadata.GamutTo709));
        a.AsSpan(0, y.Length).CopyTo(r); b.AsSpan(0, y.Length).CopyTo(g); c.AsSpan(0, y.Length).CopyTo(blue);
    }

    private void ProcessSignals(HdrFrameMetadata metadata, ColorMatrix linear, RowBuffer buffer, int width, bool useSimd, GamutDescription gamut)
    {
        var a = buffer.Red; var b = buffer.Green; var c = buffer.Blue;
        var decodingTable = metadata.Transfer == HdrTransfer.Hlg ? HlgTable : PqTable;
        var vectorWidth = (width + Vector<float>.Count - 1) / Vector<float>.Count * Vector<float>.Count;
        if (useSimd && Vector.IsHardwareAccelerated && width >= Vector<float>.Count)
        {
            var nonlinear = metadata.Nonlinear;
            if (metadata.Curves is not { } curves)
            {
                var offset0 = new Vector<float>(metadata.Offset[0]);
                var offset1 = new Vector<float>(metadata.Offset[1]);
                var offset2 = new Vector<float>(metadata.Offset[2]);
                for (var x = 0; x < vectorWidth; x += Vector<float>.Count)
                {
                    nonlinear.Transform(new Vector<float>(a, x) - offset0, new Vector<float>(b, x) - offset1,
                        new Vector<float>(c, x) - offset2, out var l, out var m, out var s);
                    l.CopyTo(a, x); m.CopyTo(b, x); s.CopyTo(c, x);
                }
            }
            else
            {
                for (var x = 0; x < vectorWidth; x += Vector<float>.Count)
                {
                    var y = Vector.Min(Vector<float>.One, Vector.Max(Vector<float>.Zero, new Vector<float>(a, x)));
                    var u = Vector.Min(Vector<float>.One, Vector.Max(Vector<float>.Zero, new Vector<float>(b, x)));
                    var v = Vector.Min(Vector<float>.One, Vector.Max(Vector<float>.Zero, new Vector<float>(c, x)));
                    var s0 = Reshape(curves[0], y, u, v, y) - new Vector<float>(metadata.Offset[0]);
                    var s1 = Reshape(curves[1], y, u, v, u) - new Vector<float>(metadata.Offset[1]);
                    var s2 = Reshape(curves[2], y, u, v, v) - new Vector<float>(metadata.Offset[2]);
                    nonlinear.Transform(s0, s1, s2, out var l, out var m, out var s);
                    l.CopyTo(a, x); m.CopyTo(b, x); s.CopyTo(c, x);
                }
            }
            for (var x = 0; x < width; x++)
            {
                a[x] = Lookup(decodingTable, a[x]); b[x] = Lookup(decodingTable, b[x]); c[x] = Lookup(decodingTable, c[x]);
            }
        }
        else
        {
            for (var x = 0; x < width; x++)
            {
                var y = a[x]; var u = b[x]; var v = c[x];
                var s0 = (metadata.Curves is { } curves0 ? Reshape(curves0[0], y, u, v, y) : y) - metadata.Offset[0];
                var s1 = (metadata.Curves is { } curves1 ? Reshape(curves1[1], y, u, v, u) : u) - metadata.Offset[1];
                var s2 = (metadata.Curves is { } curves2 ? Reshape(curves2[2], y, u, v, v) : v) - metadata.Offset[2];
                metadata.Nonlinear.Transform(s0, s1, s2, out var l, out var m, out var s);
                a[x] = Lookup(decodingTable, l);
                b[x] = Lookup(decodingTable, m);
                c[x] = Lookup(decodingTable, s);
            }
        }
        MapLinearColors(metadata, linear, buffer, width, useSimd, gamut);
    }

    internal static float Reshape(ReshapeCurve curve, float y, float u, float v, float signal)
    {
        y = Math.Clamp(y, 0, 1); u = Math.Clamp(u, 0, 1); v = Math.Clamp(v, 0, 1);
        signal = Math.Clamp(signal, 0, 1);
        var index = 0;
        while (index + 1 < curve.Pieces.Length && signal >= curve.Pivots[index + 1]) index++;
        var piece = curve.Pieces[index]; var k = piece.Coefficients;
        var value = piece.Method == 0 ? (k[2] * signal + k[1]) * signal + k[0] : piece.Constant;
        if (piece.Method == 1)
        {
            value = piece.Constant;
            var yz = y * v; var yu = y * u; var uv = u * v; var yuv = yu * v;
            var yp = y; var up = u; var vp = v; var yzp = yz; var yup = yu; var uvp = uv; var yuvp = yuv;
            for (var order = 0; order < piece.Order; order++)
            {
                var start = order * 7;
                value += k[start] * yp + k[start + 1] * up + k[start + 2] * vp
                    + k[start + 3] * yup + k[start + 4] * yzp + k[start + 5] * uvp + k[start + 6] * yuvp;
                if (order + 1 < piece.Order)
                {
                    yp *= y; up *= u; vp *= v; yup *= yu; yzp *= yz; uvp *= uv; yuvp *= yuv;
                }
            }
        }
        return Math.Clamp(value, curve.Pivots[0], curve.Pivots[^1]);
    }

    private static Vector<float> Reshape(ReshapeCurve curve, Vector<float> y, Vector<float> u, Vector<float> v, Vector<float> signal)
    {
        var value = Vector<float>.Zero;
        for (var i = 0; i < curve.Pieces.Length; i++)
        {
            var piece = curve.Pieces[i]; var k = piece.Coefficients;
            var result = piece.Method == 0
                ? (new Vector<float>(k[2]) * signal + new Vector<float>(k[1])) * signal + new Vector<float>(k[0])
                : new Vector<float>(piece.Constant);
            if (piece.Method == 1)
            {
                result = new Vector<float>(piece.Constant);
                var yz = y * v; var yu = y * u; var uv = u * v; var yuv = yu * v;
                var yp = y; var up = u; var vp = v; var yzp = yz; var yup = yu; var uvp = uv; var yuvp = yuv;
                for (var order = 0; order < piece.Order; order++)
                {
                    var start = order * 7;
                    result += new Vector<float>(k[start]) * yp + new Vector<float>(k[start + 1]) * up + new Vector<float>(k[start + 2]) * vp
                        + new Vector<float>(k[start + 3]) * yup + new Vector<float>(k[start + 4]) * yzp
                        + new Vector<float>(k[start + 5]) * uvp + new Vector<float>(k[start + 6]) * yuvp;
                    if (order + 1 < piece.Order)
                    {
                        yp *= y; up *= u; vp *= v; yup *= yu; yzp *= yz; uvp *= uv; yuvp *= yuv;
                    }
                }
            }
            value = i == 0 ? result : Vector.ConditionalSelect(Vector.GreaterThanOrEqual(signal, new Vector<float>(curve.Pivots[i])), result, value);
        }
        return Vector.Min(new Vector<float>(curve.Pivots[^1]), Vector.Max(new Vector<float>(curve.Pivots[0]), value));
    }

    internal static float HlgToLinear(float value)
    {
        value = Math.Clamp(value, 0, 1);
        return value <= 0.5f ? value * value / 3
            : (MathF.Exp((value - 0.55991073f) / 0.17883277f) + 0.28466892f) / 12;
    }

    internal static float PqToNits(float value)
    {
        const float m1 = 2610f / 16384, m2 = 2523f / 32, c1 = 3424f / 4096, c2 = 2413f / 128, c3 = 2392f / 128;
        var p = MathF.Pow(Math.Clamp(value, 0, 1), 1 / m2);
        return 10000 * MathF.Pow(Math.Max(p - c1, 0) / (c2 - c3 * p), 1 / m1);
    }

    internal static float NitsToPq(float value)
    {
        const float m1 = 2610f / 16384, m2 = 2523f / 32, c1 = 3424f / 4096, c2 = 2413f / 128, c3 = 2392f / 128;
        var p = MathF.Pow(Math.Clamp(value / 10000, 0, 1), m1);
        return MathF.Pow((c1 + c2 * p) / (1 + c3 * p), m2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Lookup(float[] table, float value)
    {
        var position = Math.Clamp(value, 0, 1) * TableSize;
        var index = Math.Min((int)position, TableSize - 1);
        return table[index] + (table[index + 1] - table[index]) * (position - index);
    }

    private static byte Encode(float value) => (byte)Math.Clamp((int)(Lookup(SrgbTable, value) + 0.5f), 0, 255);
    private static float[] BuildTable(Func<float, float> function)
    {
        var table = new float[TableSize + 1];
        for (var i = 0; i <= TableSize; i++) table[i] = function(i / (float)TableSize);
        return table;
    }

    private sealed class RowBuffer(int length)
    {
        internal readonly float[] Red = new float[length];
        internal readonly float[] Green = new float[length];
        internal readonly float[] Blue = new float[length];
        internal readonly float[] ToneScale = new float[length];
    }

}
