using System;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Discs;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Utils.Discs;

/// <summary>
/// 通过 libbluray 直接读取 BDMV 结构：光碟信息、播放列表枚举与片段区间。
/// 句柄仅在本类实例内使用；打开播放列表与枚举由 FFmpeg 的 bluray 协议完成。
/// </summary>
internal sealed unsafe class BlurayDisc : IDisposable
{
    private nint handle;
    internal nint Handle => handle;

    private BlurayDisc(nint handle, string? discName)
    {
        this.handle = handle;
        DiscName = string.IsNullOrWhiteSpace(discName) ? null : discName;
    }

    public string? DiscName { get; }

    public static BlurayDisc Open(string sourcePath)
    {
        DiscCapabilities.RequireBluraySupport();
        var handle = FF.bd_open(sourcePath, 0);
        if (handle == 0)
            throw new DiscScanException(DiscFailureKind.NotDiscOrCorrupt,
                "bd_open failed: no BDMV structure was found.");
        try
        {
            var info = (DiscStructs.BlurayDiscInfo*)FF.bd_get_disc_info(handle);
            if (info == null || info->BlurayDetected == 0)
                throw new DiscScanException(DiscFailureKind.NotDiscOrCorrupt,
                    "bd_open succeeded but no Blu-ray disc structure was detected.");
            if (info->AacsDetected != 0)
                throw new DiscScanException(DiscFailureKind.ProtectedDisc,
                    "The Blu-ray disc uses AACS encryption.");
            if (info->BdPlusDetected != 0)
                throw new DiscScanException(DiscFailureKind.ProtectedDisc,
                    "The Blu-ray disc uses BD+ encryption.");
            return new BlurayDisc(handle, info->DiscName);
        }
        catch
        {
            FF.bd_close(handle);
            throw;
        }
    }

    /// <summary>枚举播放列表（保留不同播放顺序和重复引用），返回标题信息与播放列表编号。</summary>
    public List<(DiscTitleInfo Info, DiscInput Input)> EnumerateTitles(string sourcePath, CancellationToken cancellationToken = default)
    {
        var count = FF.bd_get_titles(handle, FF.BlurayTitlesAll, 0);
        if (count == 0 || count > 99999)
            throw new DiscScanException(DiscFailureKind.StructureUnsupported,
                "No usable playlists were found on the Blu-ray disc.");
        var titles = new List<(DiscTitleInfo, DiscInput)>();
        for (var index = 0u; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = FF.bd_get_title_info(handle, index, 0);
            if (info == 0)
                continue;
            try
            {
                var parsed = (DiscStructs.BlurayTitleInfo*)info;
                // 损坏结构的计数可能是任意值；libbluray 的有效播放列表上限 99999。
                if (parsed->ClipCount is 0 or > 99999 || parsed->Clips == null)
                    continue;
                titles.Add(CreateTitle(parsed, sourcePath));
            }
            finally
            {
                FF.bd_free_title_info(info);
            }
        }
        if (titles.Count == 0)
            throw new DiscScanException(DiscFailureKind.StructureUnsupported,
                "Playlist information could not be read from the Blu-ray disc.");
        return titles;
    }

    private static (DiscTitleInfo, DiscInput) CreateTitle(DiscStructs.BlurayTitleInfo* info, string sourcePath)
    {
        var segments = new List<DiscSegment>();
        string? videoSpec = null;
        var audioLanguages = new List<string>();
        var subtitleLanguages = new List<string>();
        var clips = info->Clips;
        var titleStartByte = 0L;
        for (var i = 0u; i < info->ClipCount && clips != null; i++)
        {
            var clip = clips[i];
            var titleEndByte = titleStartByte + (long)clip.PktCount * 192;
            segments.Add(new((long)clip.InTime, (long)clip.OutTime, (long)clip.StartTime,
                titleStartByte, titleEndByte));
            titleStartByte = titleEndByte;
            if (videoSpec is null && clip.VideoStreams != null && clip.VideoStreamCount > 0)
                videoSpec = DescribeVideoStream(clip.VideoStreams[0]);
            for (var a = 0; a < clip.AudioStreamCount && clip.AudioStreams != null; a++)
                AddLanguage(audioLanguages, clip.AudioStreams[a].Language);
            for (var p = 0; p < clip.PgStreamCount && clip.PgStreams != null; p++)
                AddLanguage(subtitleLanguages, clip.PgStreams[p].Language);
        }
        var duration = TimeSpan.FromSeconds(info->Duration / 90000.0);
        var input = new DiscInput(DiscKind.BluRay, sourcePath, (int)info->Playlist, 0, duration, segments);
        var title = new DiscTitleInfo(
            (int)info->Playlist,
            duration,
            (int)info->ChapterCount,
            info->AngleCount,
            (int)info->ClipCount,
            videoSpec,
            audioLanguages,
            subtitleLanguages,
            Recommended: false);
        // 在释放 libbluray 的标题结构前复制章节时间；无需读取任何视频包。
        var chapters = new List<DiscChapter>();
        for (var i = 0u; i < info->ChapterCount && info->Chapters != null; i++)
        {
            var chapter = info->Chapters[i];
            var start = TimeSpan.FromSeconds(chapter.Start / 90000.0);
            if (start >= TimeSpan.Zero && start < duration)
                chapters.Add(new(start, Marshal.PtrToStringUTF8(chapter.ChapterName)));
        }
        return (title with { Chapters = chapters }, input);
    }

    private static string DescribeVideoStream(DiscStructs.BlurayStreamInfo stream)
    {
        // bd_video_format_e：1=480i 2=576i 3=480p 4=1080i 5=720p 6=1080p 7=576p 8=2160p。
        var format = stream.Format switch
        {
            1 => "480i", 2 => "576i", 3 => "480p", 4 => "1080i",
            5 => "720p", 6 => "1080p", 7 => "576p", 8 => "2160p",
            _ => "BD"
        };
        // bd_video_rate_e：帧率代码 1..6。
        var rate = stream.Rate switch
        {
            1 => "23.976", 2 => "24", 3 => "25", 4 => "29.97", 6 => "50", 7 => "59.94",
            _ => null
        };
        return rate is null ? format : $"{format} {rate}fps";
    }

    private static void AddLanguage(List<string> languages, string language)
    {
        if (!string.IsNullOrWhiteSpace(language) && !languages.Contains(language))
            languages.Add(language);
    }

    public void Dispose()
    {
        if (handle != 0)
            FF.bd_close(handle);
        handle = 0;
    }
}
