using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using FFmpeg.AutoGen.Abstractions;
using TSCutter.GUI.FFmpeg;
using TSCutter.GUI.Models.Discs;
using FF = TSCutter.GUI.FFmpeg.NativeMethods;

namespace TSCutter.GUI.Utils.Media;

public static partial class MediaInfoBuilder
{
    internal static unsafe string BuildDisc(FormatContext fc, DiscInput disc, DiscTitleInfo? title)
    {
        var sb = new StringBuilder();
        var chapters = ReadDiscChapters(fc, disc, title);
        AppendSection(sb, "General");
        AppendField(sb, "Complete name", disc.SourcePath);
        AppendField(sb, "Format", disc.Kind == DiscKind.Dvd ? "DVD-Video" : "Blu-ray");
        var directory = Directory.Exists(disc.SourcePath);
        AppendField(sb, "Source type", directory ? "Disc folder" : "ISO image");
        // 镜像包含整张光盘，不能当作当前标题的文件大小或用于估算标题码率。
        if (!directory && File.Exists(disc.SourcePath))
            AppendField(sb, "Image size", ToSize(new FileInfo(disc.SourcePath).Length));
        if (disc.Kind == DiscKind.BluRay)
            AppendField(sb, "Playlist", disc.TitleNumber.ToString("D5", CultureInfo.InvariantCulture) + ".mpls");
        else
            AppendField(sb, "Title", disc.TitleNumber.ToString(CultureInfo.InvariantCulture));
        var angle = disc.Kind == DiscKind.BluRay ? disc.Angle + 1 : disc.Angle;
        AppendField(sb, "Angle", title is { AngleCount: > 0 }
            ? $"{angle} / {title.AngleCount}" : angle.ToString(CultureInfo.InvariantCulture));
        AppendField(sb, "Duration", FormatDuration((long)Math.Round(disc.Duration.TotalSeconds * FF.AV_TIME_BASE)));
        AppendField(sb, "Chapter count", (title?.ChapterCount ?? chapters.Count).ToString(CultureInfo.InvariantCulture));
        if (disc.Kind == DiscKind.BluRay)
            AppendField(sb, "PlayItem count", disc.Segments.Count.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine();

        // 普通文件与光盘复用轨道排版。光盘轨道使用标题时长，不显示基于底层文件的大小占比。
        WriteStreams(sb, fc, fi: null, titleDuration: disc.Duration);
        if (chapters.Count > 0)
        {
            AppendSection(sb, "Chapters");
            for (var i = 0; i < chapters.Count; i++)
            {
                var chapter = chapters[i];
                var timestamp = string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:mm\\:ss\\.fff}",
                    (int)chapter.Start.TotalHours, chapter.Start);
                AppendField(sb, timestamp, string.IsNullOrWhiteSpace(chapter.Name) ? $"Chapter {i + 1}" : chapter.Name);
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static unsafe IReadOnlyList<DiscChapter> ReadDiscChapters(FormatContext fc, DiscInput disc, DiscTitleInfo? title)
    {
        if (title?.Chapters is { Count: > 0 } chapters)
            return chapters;
        // dvdvideo 已从 IFO 建立标题相对章节时间；保持默认 preindex=0，避免两遍扫描整片。
        var result = new List<DiscChapter>();
        AVFormatContext* raw = fc;
        for (var i = 0; i < raw->nb_chapters; i++)
        {
            var chapter = raw->chapters[i];
            if (chapter == null || chapter->start == FF.AV_NOPTS_VALUE ||
                chapter->time_base.num <= 0 || chapter->time_base.den <= 0)
                continue;
            var seconds = chapter->start * chapter->time_base.ToDouble();
            if (!double.IsFinite(seconds) || seconds < 0 || seconds >= disc.Duration.TotalSeconds)
                continue;
            var metadata = new NativeMetadata(chapter->metadata);
            metadata.TryGetValue("title", out var name);
            result.Add(new(TimeSpan.FromSeconds(seconds), name));
        }
        // dvdvideo 对只有一个章节的标题省略章节表，其起点仍然是标题零点。
        if (result.Count == 0 && title?.ChapterCount == 1)
            result.Add(new(TimeSpan.Zero));
        return result;
    }
}
