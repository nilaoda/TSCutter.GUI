// 转换流程参考 Sdcb.FFmpeg，Copyright © sdcb, Ruslan Balanukhin 2022。
// 原始代码适用 LGPL-3.0-only；修改及许可声明见 THIRD_PARTY_NOTICES.md。
using System;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.FFmpeg;

public sealed unsafe class VideoFrameConverter() : SafeHandle(IntPtr.Zero, true)
{
    public override bool IsInvalid => handle == IntPtr.Zero;
    public void ConvertFrame(Frame source, Frame destination)
    {
        // sws_getCachedContext 在尺寸或像素格式变化时释放并替换旧上下文。
        // 格式稳定时不分配新上下文，平面和步长直接使用原生数据，避免临时数组。
        SetHandle((IntPtr)FF.sws_getCachedContext((SwsContext*)handle,
            source.Width, source.Height, (AVPixelFormat)source.Format,
            destination.Width, destination.Height, (AVPixelFormat)destination.Format,
            2 /* SWS_BILINEAR */, null, null, null));
        if (IsInvalid) throw new OutOfMemoryException("Cannot create swscale context.");
        AVFrame* src = source;
        AVFrame* dst = destination;
        FFmpegException.Check(FF.sws_scale((SwsContext*)handle,
            (byte**)&src->data, (int*)&src->linesize, 0, src->height,
            (byte**)&dst->data, (int*)&dst->linesize));
    }
    protected override bool ReleaseHandle()
    {
        FF.sws_freeContext((SwsContext*)handle);
        handle = IntPtr.Zero;
        return true;
    }
}
