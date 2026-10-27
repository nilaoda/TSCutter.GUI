using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TSCutter.GUI.Models.Discs;
using TSCutter.GUI.Utils.Discs;

namespace TSCutter.GUI.Services.Discs;

public sealed record DiscScanResult(
    DiscKind Kind,
    string? DiscName,
    IReadOnlyList<DiscTitleInfo> Titles,
    DiscInput[] Inputs,
    int RecommendedIndex);

/// <summary>
/// 识别光盘类型并枚举可播放标题。扫描在调用线程执行（通常由 Task.Run 包装），
/// 仅读取 IFO/MPLS 结构，无需完整读取光盘内容；在标题之间检查取消请求。
/// </summary>
public static class DiscScanService
{
    public static bool IsDiscPath(string path) => Directory.Exists(path)
        ? GetDirectoryRoot(path) is not null
        : Path.GetExtension(path.AsSpan()).Equals(".iso", StringComparison.OrdinalIgnoreCase);

    // 只检查当前目录及约定的结构目录，不递归遍历整张光盘。
    internal static string? GetDirectoryRoot(string path)
    {
        if (!Directory.Exists(path))
            return null;
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (File.Exists(Path.Combine(directory, "VIDEO_TS", "VIDEO_TS.IFO")) ||
            File.Exists(Path.Combine(directory, "BDMV", "index.bdmv")))
            return directory;
        var name = Path.GetFileName(directory);
        if ((name.Equals("VIDEO_TS", StringComparison.OrdinalIgnoreCase) &&
             File.Exists(Path.Combine(directory, "VIDEO_TS.IFO"))) ||
            (name.Equals("BDMV", StringComparison.OrdinalIgnoreCase) &&
             File.Exists(Path.Combine(directory, "index.bdmv"))))
            return Directory.GetParent(directory)?.FullName;
        return null;
    }

    public static Task<DiscScanResult> ScanAsync(string sourcePath, CancellationToken cancellationToken) =>
        Task.Run(() => Scan(sourcePath, cancellationToken), cancellationToken);

    public static DiscScanResult Scan(string sourcePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Directory.Exists(sourcePath))
        {
            sourcePath = GetDirectoryRoot(sourcePath) ?? throw new DiscScanException(
                DiscFailureKind.NotDiscOrCorrupt, "The folder has no VIDEO_TS or BDMV video disc structure.");
        }
        else if (!File.Exists(sourcePath))
            throw new FileNotFoundException(sourcePath);
        List<(DiscTitleInfo Info, DiscInput Input)>? titles = null;
        DiscKind kind = default;
        string? discName = null;
        DiscScanException? blurayFailure = null;
        // 不兼容的蓝光库不阻止 DVD 读取；只有显式打开蓝光时才拒绝旧 ABI。
        var hasBluraySupport = DiscCapabilities.HasBluraySupport;
        if (hasBluraySupport)
        {
            try
            {
                using var disc = BlurayDisc.Open(sourcePath);
                discName = disc.DiscName;
                titles = disc.EnumerateTitles(sourcePath, cancellationToken);
                kind = DiscKind.BluRay;
            }
            catch (DiscScanException exception) when (exception.Kind is not DiscFailureKind.ProtectedDisc)
            {
                blurayFailure = exception;
            }
        }
        if (titles is null && DiscCapabilities.HasDvdSupport)
        {
            try
            {
                using var disc = DvdDisc.Open(sourcePath);
                titles = disc.EnumerateTitles(sourcePath, cancellationToken);
                kind = DiscKind.Dvd;
            }
            catch (DiscScanException exception) when (exception.Kind is not DiscFailureKind.ProtectedDisc)
            {
                if (blurayFailure?.Kind == DiscFailureKind.StructureUnsupported)
                    throw blurayFailure;
                // 不让尚未安装的另一种读取器遮盖已经识别到的有效 DVD。
                if (exception.Kind == DiscFailureKind.NotDiscOrCorrupt && !hasBluraySupport)
                    throw new DiscScanException(DiscFailureKind.RuntimeMissing, "The Blu-ray reader is not available.");
                throw;
            }
        }
        if (titles is null)
            throw blurayFailure?.Kind == DiscFailureKind.StructureUnsupported ? blurayFailure :
                new DiscScanException(DiscFailureKind.RuntimeMissing, "The required disc reader is not available.");
        if (titles.Count == 0)
            throw new DiscScanException(DiscFailureKind.StructureUnsupported,
                "No playable titles were found on the disc image.");
        var recommended = -1;
        for (var i = 0; i < titles.Count; i++)
            if (recommended < 0 || titles[i].Info.Duration > titles[recommended].Info.Duration)
                recommended = i;
        cancellationToken.ThrowIfCancellationRequested();
        var result = titles
            .Select((title, index) => (title.Info with { Recommended = index == recommended }, title.Input))
            .ToArray();
        return new DiscScanResult(
            kind,
            discName,
            [.. result.Select(item => item.Item1)],
            [.. result.Select(item => item.Item2)],
            recommended);
    }

    public static string DescribeFailure(Exception exception)
    {
        if (exception is not DiscScanException scan)
            return exception.Message;
        return scan.Kind switch
        {
            DiscFailureKind.ProtectedDisc => string.Format(
                Utils.Application.LocalizationManager.Instance.String_Disc_ProtectedError, scan.Message),
            DiscFailureKind.RuntimeMissing => Utils.Application.LocalizationManager.Instance.String_Disc_RuntimeMissing,
            DiscFailureKind.StructureUnsupported => string.Format(
                Utils.Application.LocalizationManager.Instance.String_Disc_StructureError, scan.Message),
            _ => string.Format(
                Utils.Application.LocalizationManager.Instance.String_Disc_NotDiscError, scan.Message)
        };
    }
}
