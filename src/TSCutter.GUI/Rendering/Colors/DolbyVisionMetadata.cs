// 元数据解释及颜色重建参考 libplacebo（LGPL-2.1-or-later）；见 THIRD_PARTY_NOTICES.md。
using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Rendering.Colors;

internal sealed unsafe class DolbyVisionMetadata
{
    internal required ReshapeCurve[] Curves { get; init; }
    internal required ColorMatrix Nonlinear { get; init; }
    internal required ColorMatrix Linear { get; init; }
    internal required float[] Offset { get; init; }
    internal int BitDepth { get; init; }
    internal float PeakNits { get; init; }

    internal static DolbyVisionMetadata? Read(AVFrame* frame)
    {
        var side = FF.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_DOVI_METADATA);
        if (side == null) return null;
        if (side->data == null || side->size < (ulong)(3 * sizeof(nuint)))
            throw new InvalidOperationException("Invalid Dolby Vision metadata.");
        var offsets = (nuint*)side->data;
        var header = ReadPart<NativeHeader>(side, offsets[0]);
        // P5 使用全范围单层 IPT-PQ；其他 Profile 保持原有预览路径。
        if (header->Profile != 0 || header->FullRange == 0) return null;
        if (header->DisableResidual == 0 || header->BitDepth is < 8 or > 16 || header->CoefficientDenominator > 32)
            throw new NotSupportedException("Unsupported Dolby Vision P5 reconstruction metadata.");
        var mapping = ReadPart<NativeMapping>(side, offsets[1]);
        var color = ReadPart<NativeColor>(side, offsets[2]);
        if (mapping->ColorSpace != 0 || mapping->ChromaFormat != 0)
            throw new NotSupportedException("Unsupported Dolby Vision P5 mapping format.");
        var scale = 1f / MathF.Pow(2, header->CoefficientDenominator);
        var maximum = (1 << header->BitDepth) - 1f;
        var curves = new ReshapeCurve[3];
        for (var c = 0; c < 3; c++)
        {
            var source = c == 0 ? &mapping->C0 : c == 1 ? &mapping->C1 : &mapping->C2;
            if (source->PivotCount is < 2 or > 9)
                throw new InvalidOperationException("Invalid Dolby Vision reshape pivots.");
            var curve = new ReshapeCurve(source->PivotCount);
            for (var i = 0; i < source->PivotCount; i++)
            {
                curve.Pivots[i] = source->Pivots[i] / maximum;
                if (curve.Pivots[i] is < 0 or > 1 || i > 0 && curve.Pivots[i] <= curve.Pivots[i - 1])
                    throw new InvalidOperationException("Invalid Dolby Vision reshape pivot range.");
            }
            for (var i = 0; i < curve.Pieces.Length; i++)
            {
                var method = source->Method[i];
                var order = method == 0 ? source->PolynomialOrder[i] : source->MmrOrder[i];
                if (method is < 0 or > 1 || method == 0 && order is < 1 or > 2 || method == 1 && order is < 1 or > 3)
                    throw new NotSupportedException("Unsupported Dolby Vision reshape method.");
                var piece = new ReshapePiece(method, order);
                if (method == 0)
                {
                    for (var k = 0; k <= order; k++) piece.Coefficients[k] = source->Polynomial[i * 3 + k] * scale;
                }
                else
                {
                    piece.Constant = source->MmrConstant[i] * scale;
                    for (var k = 0; k < order * 7; k++) piece.Coefficients[k] = source->Mmr[i * 21 + k] * scale;
                }
                curve.Pieces[i] = piece;
            }
            curves[c] = curve;
        }
        return new DolbyVisionMetadata
        {
            BitDepth = header->BitDepth, Curves = curves,
            Nonlinear = ColorMatrix.Read(color->Nonlinear), Linear = ColorMatrix.Read(color->Linear),
            Offset = [Rational(color->Offset, 0), Rational(color->Offset, 1), Rational(color->Offset, 2)],
            PeakNits = HdrToneMapper.PqToNits(color->SourceMaxPq / 4095f)
        };
    }

    private static T* ReadPart<T>(AVFrameSideData* side, nuint offset) where T : unmanaged
    {
        if (offset < (nuint)(3 * sizeof(nuint)) || offset > side->size || (ulong)sizeof(T) > side->size - offset)
            throw new InvalidOperationException("Truncated Dolby Vision metadata.");
        return (T*)(side->data + offset);
    }

    internal static float Rational(int* data, int index)
    {
        var denominator = data[index * 2 + 1];
        if (denominator == 0) throw new InvalidOperationException("Invalid Dolby Vision color matrix.");
        return data[index * 2] / (float)denominator;
    }

    // FFmpeg 9 的 dovi_meta.h 布局。各部分通过元数据中的偏移定位，不假定整个载荷大小。
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeHeader
    {
        internal byte Type;
        internal ushort Format;
        internal byte Profile, Level, ChromaResampling, CoefficientType, CoefficientDenominator,
            Normalized, FullRange, BitDepth, ElBitDepth, VdrBitDepth, SpatialResampling,
            ElSpatialResampling, DisableResidual, ExtendedMapping0, ExtendedMapping1;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeCurve
    {
        internal byte PivotCount;
        internal fixed ushort Pivots[9];
        internal fixed int Method[8];
        internal fixed byte PolynomialOrder[8];
        internal fixed long Polynomial[24];
        internal fixed byte MmrOrder[8];
        internal fixed long MmrConstant[8];
        internal fixed long Mmr[168];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMapping
    {
        internal byte Id, ColorSpace, ChromaFormat;
        internal NativeCurve C0, C1, C2;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeColor
    {
        internal byte Id, SceneRefresh;
        internal fixed int Nonlinear[18];
        internal fixed int Offset[6];
        internal fixed int Linear[18];
        internal ushort Eotf, Eotf0, Eotf1;
        internal uint Eotf2;
        internal byte SignalBitDepth, SignalColorSpace, SignalChromaFormat, SignalFullRange;
        internal ushort SourceMinPq, SourceMaxPq, SourceDiagonal;
    }
}

internal sealed class ReshapeCurve(int pivotCount)
{
    internal float[] Pivots { get; } = new float[pivotCount];
    internal ReshapePiece[] Pieces { get; } = new ReshapePiece[pivotCount - 1];
}

internal sealed class ReshapePiece(int method, int order)
{
    internal int Method { get; } = method;
    internal int Order { get; } = order;
    internal float Constant { get; set; }
    internal float[] Coefficients { get; } = new float[method == 0 ? 3 : order * 7];
}
