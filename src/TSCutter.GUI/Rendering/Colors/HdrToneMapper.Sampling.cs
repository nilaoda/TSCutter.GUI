using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using FFmpeg.AutoGen.Abstractions;

namespace TSCutter.GUI.Rendering.Colors;

internal sealed unsafe partial class HdrToneMapper
{
    private readonly record struct SampleGeometry(int Width, int Height, int OutputWidth, int OutputHeight,
        AVPixelFormat Format, AVChromaLocation ChromaLocation);

    // 只缓存最近一种尺寸的采样坐标，不持有输入帧或原生内存。
    private sealed class SamplingGrid
    {
        internal SampleGeometry Geometry { get; }
        internal AxisSample[] Luma { get; }
        internal AxisSample[] Chroma { get; }

        internal SamplingGrid(SampleGeometry geometry, AVPixFmtDescriptor* descriptor)
        {
            Geometry = geometry;
            Luma = BuildAxis(geometry, 0, false);
            var centered = geometry.ChromaLocation is AVChromaLocation.AVCHROMA_LOC_CENTER
                or AVChromaLocation.AVCHROMA_LOC_TOP or AVChromaLocation.AVCHROMA_LOC_BOTTOM;
            Chroma = descriptor->log2_chroma_w == 0 ? Luma : BuildAxis(geometry, descriptor->log2_chroma_w, centered);
        }

        private static AxisSample[] BuildAxis(SampleGeometry geometry, int subX, bool centered)
        {
            var samples = new AxisSample[geometry.OutputWidth];
            var width = (geometry.Width + (1 << subX) - 1) >> subX;
            for (var x = 0; x < samples.Length; x++)
            {
                var position = ((x + 0.5f) * geometry.Width / geometry.OutputWidth - 0.5f
                    - (subX > 0 && centered ? 0.5f : 0)) / (1 << subX);
                samples[x] = AxisSample.At(position, width);
            }
            return samples;
        }
    }

    private readonly struct ComponentSampler
    {
        private readonly byte* data;
        private readonly int stride, step, offset, shift, depth, height;
        private readonly bool directHorizontal;
        private readonly float normalization, yScale, chromaY;
        private readonly AxisSample[] xSamples;

        internal ComponentSampler(AVFrame* frame, AVPixFmtDescriptor* descriptor, int component,
            int bitDepth, AxisSample[] horizontalSamples, int outputHeight)
        {
            var info = descriptor->comp[(uint)component];
            var subX = component == 0 ? 0 : descriptor->log2_chroma_w;
            var subY = component == 0 ? 0 : descriptor->log2_chroma_h;
            if (info.depth != bitDepth || info.depth is < 8 or > 16 || info.step <= 0 || info.offset < 0
                || info.shift < 0 || info.depth + info.shift > 16 || info.plane is < 0 or > 3)
                throw new NotSupportedException("Unsupported HDR component layout.");
            var width = (frame->width + (1 << subX) - 1) >> subX;
            height = (frame->height + (1 << subY) - 1) >> subY;
            data = frame->data[(uint)info.plane]; stride = frame->linesize[(uint)info.plane];
            step = info.step; offset = info.offset; shift = info.shift; depth = info.depth;
            if (data == null || Math.Abs((long)stride) < (width - 1L) * step + offset + (depth + shift + 7) / 8)
                throw new InvalidOperationException("Invalid HDR component buffer.");
            normalization = 1f / ((1 << depth) - 1);
            var location = frame->chroma_location;
            var locationY = location is AVChromaLocation.AVCHROMA_LOC_TOPLEFT or AVChromaLocation.AVCHROMA_LOC_TOP ? 0f
                : location is AVChromaLocation.AVCHROMA_LOC_BOTTOMLEFT or AVChromaLocation.AVCHROMA_LOC_BOTTOM ? 1f : 0.5f;
            chromaY = subY == 0 ? 0 : locationY;
            yScale = frame->height / (float)outputHeight;
            xSamples = horizontalSamples;
            directHorizontal = subX == 0 && horizontalSamples.Length == frame->width;
            ChromaVerticalSubsampling = 1 << subY;
        }

        private int ChromaVerticalSubsampling { get; }
        internal void SampleRow(int y, Span<float> output)
        {
            var vertical = AxisSample.At(((y + 0.5f) * yScale - 0.5f - chromaY) / ChromaVerticalSubsampling, height);
            var row0 = data + vertical.Left * stride;
            var row1 = data + vertical.Right * stride;
            var samples = xSamples.AsSpan();
            output = output[..samples.Length];
            if (directHorizontal && vertical.Weight == 0 && offset == 0 && (step == 1 || step == 2))
            {
                SampleDirectRow(row0, output);
                return;
            }
            for (var x = 0; x < samples.Length; x++)
            {
                var sample = samples[x];
                var top = Read(row0, sample.Left) * (1 - sample.Weight) + Read(row0, sample.Right) * sample.Weight;
                var bottom = Read(row1, sample.Left) * (1 - sample.Weight) + Read(row1, sample.Right) * sample.Weight;
                output[x] = (top * (1 - vertical.Weight) + bottom * vertical.Weight) * normalization;
            }
        }

        // 原尺寸亮度平面不做重复双线性采样，用 Span 直接读取并批量归一化。
        private void SampleDirectRow(byte* row, Span<float> output)
        {
            var x = 0;
            if (Vector.IsHardwareAccelerated && step == 2)
            {
                var input = new ReadOnlySpan<ushort>(row, output.Length);
                var mask = new Vector<ushort>((ushort)((1 << depth) - 1));
                for (; x <= output.Length - Vector<ushort>.Count; x += Vector<ushort>.Count)
                {
                    var values = new Vector<ushort>(input[x..]);
                    values = (values >> shift) & mask;
                    WriteNormalized(values, output[x..]);
                }
            }
            else if (Vector.IsHardwareAccelerated && step == 1)
            {
                var input = new ReadOnlySpan<byte>(row, output.Length);
                for (; x <= output.Length - Vector<byte>.Count; x += Vector<byte>.Count)
                {
                    Vector.Widen(new Vector<byte>(input[x..]), out var low, out var high);
                    WriteNormalized(low, output[x..]);
                    WriteNormalized(high, output[(x + Vector<ushort>.Count)..]);
                }
            }
            for (; x < output.Length; x++) output[x] = Read(row, x) * normalization;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void WriteNormalized(Vector<ushort> values, Span<float> output)
        {
            Vector.Widen(values, out var low, out var high);
            var scale = new Vector<float>(normalization);
            (Vector.ConvertToSingle(low) * scale).CopyTo(output);
            (Vector.ConvertToSingle(high) * scale).CopyTo(output[Vector<float>.Count..]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int Read(byte* row, int x)
        {
            var address = row + x * step + offset;
            var value = depth + shift <= 8 ? *address : Unsafe.ReadUnaligned<ushort>(address);
            return (value >> shift) & ((1 << depth) - 1);
        }
    }

    private readonly record struct AxisSample(int Left, int Right, float Weight)
    {
        internal static AxisSample At(float position, int size)
        {
            position = Math.Clamp(position, 0, size - 1);
            var left = (int)position;
            return new AxisSample(left, Math.Min(left + 1, size - 1), position - left);
        }
    }
}
