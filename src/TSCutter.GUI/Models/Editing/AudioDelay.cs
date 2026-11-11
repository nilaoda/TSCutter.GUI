using System;

namespace TSCutter.GUI.Models.Editing;

/// <summary>
/// 音频相对视频的延迟（毫秒，正数表示声音推后）。导出时只改写音轨的时间戳，画面与其他轨道不变；
/// 预览播放与导航条波形按同样的延迟平移。偏移越大，部分硬件播放器越可能丢掉开头的声音，范围限制在 ±5 秒。
/// </summary>
public static class AudioDelay
{
    public const int MaxMilliseconds = 5000;

    public static int Clamp(int milliseconds) => Math.Clamp(milliseconds, -MaxMilliseconds, MaxMilliseconds);

    public static long To90k(int milliseconds) => milliseconds * 90L;
}
