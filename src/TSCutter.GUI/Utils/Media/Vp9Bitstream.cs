using System;

namespace TSCutter.GUI.Utils.Media;

/// <summary>
/// VP9 样本（MP4/MKV 中每个样本是一个画面，可能是带隐藏帧的超帧）中智能剪辑需要的信息：
/// 样本的第一帧是否为关键帧。关键帧刷新全部 8 个参考帧槽，之后的画面不再参考它之前的画面，相当于 IDR。
/// </summary>
internal static class Vp9Bitstream
{
    /// <summary>
    /// 样本的第一帧为关键帧（frame_type = KEY_FRAME，不是 show_existing_frame）。关键帧可以隐藏（show_frame = 0）：
    /// 在线视频站的素材中常见“隐藏关键帧 + 只参考它的显示帧”组成的超帧，从该样本开始解码同样不依赖之前的画面。
    /// </summary>
    public static bool IsKeyFrame(ReadOnlySpan<byte> sample)
    {
        var frame = FirstFrame(sample);
        if (frame.IsEmpty)
            return false;
        var bits = new BitReader(frame);
        // 帧标记 frame_marker(2) 固定为 2
        if (bits.Read(2) != 2)
            return false;
        var profile = bits.Read(1) | (bits.Read(1) << 1);
        if (profile == 3 && bits.Read(1) != 0)
            return false;
        // show_existing_frame：直接显示已解码的帧，不是新画面
        if (bits.Read(1) != 0)
            return false;
        // frame_type(1)：0 为关键帧
        return bits.Read(1) == 0;
    }

    /// <summary>超帧（末尾带超帧索引）时返回其中第一帧，否则返回整个样本；索引不合法时为空。</summary>
    private static ReadOnlySpan<byte> FirstFrame(ReadOnlySpan<byte> sample)
    {
        if (sample.IsEmpty)
            return [];
        var marker = sample[^1];
        if ((marker & 0xE0) != 0xC0)
            return sample;
        var frames = (marker & 0x07) + 1;
        var magnitude = ((marker >> 3) & 0x03) + 1;
        var indexSize = 2 + magnitude * frames;
        if (sample.Length < indexSize || sample[^indexSize] != marker)
            return sample;
        long size = 0;
        for (var i = 0; i < magnitude; i++)
            size |= (long)sample[sample.Length - indexSize + 1 + i] << (8 * i);
        return size > 0 && size <= sample.Length - indexSize ? sample[..(int)size] : [];
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> data = data;
        private int position;

        /// <summary>按高位在前读出 count 位；越界的位按 0 计。</summary>
        public int Read(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++, position++)
            {
                var index = position >> 3;
                var bit = index < data.Length ? (data[index] >> (7 - (position & 7))) & 1 : 0;
                value = (value << 1) | bit;
            }
            return value;
        }
    }
}
