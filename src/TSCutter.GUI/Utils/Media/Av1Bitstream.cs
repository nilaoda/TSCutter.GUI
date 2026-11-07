using System;
using System.Collections.Generic;

namespace TSCutter.GUI.Utils.Media;

/// <summary>
/// AV1 低开销格式（MP4/MKV 样本，每个样本是一个时间单元）中智能剪辑需要的 OBU 信息：
/// 是否为显示的关键帧（重置全部参考帧，相当于 IDR）、是否带序列头，以及 av1C 中的序列头。
/// </summary>
internal static class Av1Bitstream
{
    private const int SequenceHeader = 1;
    private const int FrameHeader = 3;
    private const int Frame = 6;

    /// <summary>时间单元中第一个帧头是显示的关键帧（frame_type = KEY_FRAME、show_frame = 1，不是 show_existing_frame）。</summary>
    public static bool IsKeyFrame(ReadOnlySpan<byte> unit)
    {
        var reduced = false;
        foreach (var obu in Obus(unit))
        {
            var payload = unit.Slice(obu.PayloadStart, obu.PayloadLength);
            if (obu.Type == SequenceHeader && payload.Length > 0)
                // 序列头开头：seq_profile(3)、still_picture(1)、reduced_still_picture_header(1)
                reduced = (payload[0] & 0x08) != 0;
            if (obu.Type is not (FrameHeader or Frame) || payload.Length == 0)
                continue;
            // 精简的静态图像头只有关键帧。
            if (reduced)
                return true;
            var bits = payload[0];
            if ((bits & 0x80) != 0)
                return false;
            return ((bits >> 5) & 3) == 0 && (bits & 0x10) != 0;
        }
        return false;
    }

    public static bool HasSequenceHeader(ReadOnlySpan<byte> unit)
    {
        foreach (var obu in Obus(unit))
            if (obu.Type == SequenceHeader)
                return true;
        return false;
    }

    /// <summary>附加数据（av1C 记录或直接的 OBU）中的序列头 OBU（含 OBU 头与长度），没有时为空。</summary>
    public static byte[] SequenceHeaderFrom(ReadOnlySpan<byte> extradata)
    {
        // av1C：marker(1)=1、version(7)=1，4 字节后为 configOBUs。
        var obus = extradata.Length >= 4 && extradata[0] == 0x81 ? extradata[4..] : extradata;
        foreach (var obu in Obus(obus))
            if (obu.Type == SequenceHeader)
                return obus.Slice(obu.Start, obu.Length).ToArray();
        return [];
    }

    private readonly record struct Obu(int Type, int Start, int Length, int PayloadStart, int PayloadLength);

    /// <summary>逐个读出 OBU 的位置；遇到不完整或非法的 OBU 时停止。</summary>
    private static List<Obu> Obus(ReadOnlySpan<byte> data)
    {
        var result = new List<Obu>();
        var offset = 0;
        while (offset < data.Length)
        {
            var start = offset;
            var header = data[offset++];
            if ((header & 0x80) != 0)
                break;
            var type = (header >> 3) & 0x0F;
            if ((header & 0x04) != 0)
                offset++;
            long size = 0;
            if ((header & 0x02) != 0)
            {
                var complete = false;
                for (var i = 0; i < 8 && offset < data.Length; i++)
                {
                    var value = data[offset++];
                    size |= (long)(value & 0x7F) << (7 * i);
                    if ((value & 0x80) == 0)
                    {
                        complete = true;
                        break;
                    }
                }
                if (!complete)
                    break;
            }
            else
                size = data.Length - offset;
            if (offset > data.Length || size > data.Length - offset)
                break;
            result.Add(new Obu(type, start, offset + (int)size - start, offset, (int)size));
            offset += (int)size;
        }
        return result;
    }
}
