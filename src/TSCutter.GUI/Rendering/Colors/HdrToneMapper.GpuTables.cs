using System;

namespace TSCutter.GUI.Rendering.Colors;

internal sealed unsafe partial class HdrToneMapper
{
    internal const int GpuTableWidth = 256;
    internal const int GpuTableHeight = 65;

    // 纹理通道归一化到 0..1，避免 Skia 的颜色采样夹断绝对亮度。
    // GPU 和 CPU 共享同一组查找表，避免播放与暂停使用不同的亮度曲线。
    internal static float[] CreateGpuTransferTable()
    {
        var values = new float[GpuTableWidth * GpuTableHeight * 4];
        for (var index = 0; index < values.Length / 4; index++)
        {
            var source = Math.Min(index, TableSize);
            values[index * 4] = PqTable[source] / 10000;
            values[index * 4 + 1] = PqEncodingTable[source];
            values[index * 4 + 2] = SrgbTable[source] / 255;
            values[index * 4 + 3] = 1;
        }
        return values;
    }

    internal float[] CreateGpuToneTable(float peak)
    {
        Prepare(1, peak);
        var values = new float[GpuTableWidth * GpuTableHeight * 4];
        for (var index = 0; index < values.Length / 4; index++)
        {
            var source = Math.Min(index, TableSize);
            values[index * 4] = toneTable[source];
            values[index * 4 + 1] = HlgDisplayScaleTable[source] / 1000;
            values[index * 4 + 2] = HlgTable[source];
            values[index * 4 + 3] = 1;
        }
        return values;
    }

    internal static float[] CreateGpuGamutTable(ColorMatrix to709, out ColorMatrix fromLms)
    {
        var source = GetGamut(to709);
        fromLms = source.FromLms;
        var values = new float[GamutDescription.HueCount * 4];
        for (var index = 0; index < GamutDescription.HueCount; index++)
        {
            values[index * 4] = source.ChromaAt(index);
            values[index * 4 + 1] = TargetGamut.ChromaAt(index);
            values[index * 4 + 3] = 1;
        }
        return values;
    }
}
