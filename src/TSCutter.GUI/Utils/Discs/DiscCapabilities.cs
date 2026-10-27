using System;
using System.Collections.Generic;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Discs;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;
using TSCutter.GUI.Utils.Media;

namespace TSCutter.GUI.Utils.Discs;

/// <summary>
/// 检查当前 FFmpeg 运行库是否具备蓝光/DVD 打开能力：
/// 蓝光使用内置的 libbluray 公开接口；DVD 还要求 dvdvideo 解复用器。
/// 优先使用静态合入 avformat 的接口，兼容开发环境中匹配的独立库。
/// </summary>
internal static class DiscCapabilities
{
    private static readonly object SyncRoot = new();
    private static bool? dvdVideoDemuxer;

    public static bool HasBluraySupport
    {
        get
        {
            if (!HasBlurayLibrary)
                return false;
            FF.bd_get_version(out var major, out var minor, out _);
            return SupportsBlurayVersion(major, minor);
        }
    }

    internal static bool SupportsBlurayVersion(int major, int minor) => major == 1 && minor >= 5;

    public static void RequireBluraySupport()
    {
        if (!HasBlurayLibrary)
            throw new DiscScanException(DiscFailureKind.RuntimeMissing,
                "libbluray is not available in the FFmpeg runtime directory.");
        FF.bd_get_version(out var major, out var minor, out var micro);
        // 显式打开前检查 ABI；旧版查询返回 false，避免旧版库让测试发现阶段抛异常。
        if (!SupportsBlurayVersion(major, minor))
            throw new DiscScanException(DiscFailureKind.RuntimeMissing,
                $"libbluray 1.5 or later (ABI 4) is required; found {major}.{minor}.{micro}.");
    }

    public static bool HasDvdSupport => HasDvdVideoDemuxer && HasDvdReadLibrary;

    public static bool HasDvdVideoDemuxer
    {
        get
        {
            lock (SyncRoot)
            {
                dvdVideoDemuxer ??= HasInputFormat("dvdvideo");
                return dvdVideoDemuxer.Value;
            }
        }
    }

    private static unsafe bool HasInputFormat(string shortName) => FF.av_find_input_format(shortName) != null;

    /// <summary>
    /// 返回当前运行库缺失的光盘能力说明；能力齐备时返回 null。
    /// 用于测试跳过与界面提示，区分“运行库缺失”与“光盘本身有问题”。
    /// </summary>
    public static string? DescribeSupport()
    {
        var missing = new List<string>();
        if (!HasBluraySupport)
            missing.Add("libbluray >= 1.5 (ABI 4)");
        if (!HasDvdVideoDemuxer)
            missing.Add("dvdvideo demuxer");
        if (!HasDvdReadLibrary)
            missing.Add("libdvdread");
        return missing.Count == 0
            ? null
            : "当前 FFmpeg 运行库缺少光盘输入能力（" + string.Join(", ", missing)
              + "）。使用启用 libbluray/libdvdnav/libdvdread 的 FFmpeg 运行库后重试。";
    }

    public static bool HasBlurayLibrary => FFmpegNativeBootstrapper.TryGetOptionalLibrary("bluray", out _);

    public static bool HasDvdReadLibrary => FFmpegNativeBootstrapper.TryGetOptionalLibrary("dvdread", out _);

}
