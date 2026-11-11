using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TSCutter.GUI.Models.Preview;
using TSCutter.GUI.Utils;
using TSCutter.GUI.Utils.Application;

namespace TSCutter.GUI.ViewModels.Preview;

public enum TimelineSegmentKind
{
    EpgProgram,
    Chapter
}

/// <summary>
/// 节目带上的一段：录制中的 EPG 节目或文件章节。起止为时间轴秒数，标签与悬停说明按当前语言生成；
/// EPG 节目另带节目信息，供媒体信息列表对应。
/// </summary>
public sealed record TimelineSegmentItem(
    TimelineSegmentKind Kind,
    string Title,
    double StartSeconds,
    double EndSeconds,
    bool StartsAtBeginning,
    bool EndsAtEnd,
    string Label,
    string Tooltip,
    EpgEvent? Event = null)
{
    /// <summary>跳到这一段开始的时刻：从录制开头开始的段取时间轴起点。</summary>
    public double JumpStartSeconds => StartsAtBeginning ? 0 : StartSeconds;

    public static IReadOnlyList<TimelineSegmentItem> FromEpg(EpgGuide guide)
    {
        var strings = LocalizationManager.Instance;
        return guide.Programs.Select(program =>
        {
            var schedule = EpgText.Schedule(program.Event, guide.LocalOffset);
            var label = schedule.Length == 0 ? program.Title : $"{program.Title} · {schedule}";
            var lines = new List<string> { program.Title };
            if (schedule.Length > 0)
                lines.Add(string.Format(strings.String_Epg_Schedule, EpgText.Schedule(program.Event, guide.LocalOffset, withDate: true),
                    EpgText.Duration(program.Event.Duration)));
            lines.Add(string.Format(strings.String_Epg_InRecording,
                CommonUtil.FormatSeconds(program.StartSeconds), CommonUtil.FormatSeconds(program.EndSeconds)));
            if (!string.IsNullOrWhiteSpace(program.Event.Description))
                lines.Add(program.Event.Description);
            lines.Add(strings.String_Epg_TimingHint);
            return new TimelineSegmentItem(TimelineSegmentKind.EpgProgram, program.Title, program.StartSeconds,
                program.EndSeconds, program.StartsAtRecordingStart, program.EndsAtRecordingEnd, label,
                string.Join("\n", lines), program.Event);
        }).ToArray();
    }

    /// <summary>没有标题的章节按序号命名（如 DVD 章节）。</summary>
    public static IReadOnlyList<TimelineSegmentItem> FromChapters(IReadOnlyList<MediaChapter> chapters, double duration)
    {
        var strings = LocalizationManager.Instance;
        return chapters.Select(chapter =>
        {
            var numbered = string.Format(strings.String_Chapter_Default, chapter.Number);
            var title = chapter.Title ?? numbered;
            var lines = new List<string> { title };
            if (chapter.Title is not null)
                lines.Add(numbered);
            lines.Add(string.Format(strings.String_Chapter_Range, CommonUtil.FormatSeconds(chapter.StartSeconds),
                CommonUtil.FormatSeconds(chapter.EndSeconds),
                CommonUtil.FormatSeconds(Math.Round(chapter.EndSeconds - chapter.StartSeconds, 3))));
            return new TimelineSegmentItem(TimelineSegmentKind.Chapter, title, chapter.StartSeconds, chapter.EndSeconds,
                chapter.StartSeconds <= 0, chapter.EndSeconds >= duration - 0.000001, title, string.Join("\n", lines));
        }).ToArray();
    }
}

/// <summary>
/// 媒体信息中节目/章节列表的一行；关联到节目带上某一段的行可单击跳转。
/// 日期标题行（<see cref="IsHeader"/>）只显示 <see cref="Title"/> 中的日期，不可选中。
/// </summary>
public sealed record SegmentListItem(string Time, string Title, string Description, string Status,
    TimelineSegmentItem? Segment)
{
    public bool IsHeader { get; init; }
    public bool CanJump => Segment is not null;
    public bool HasDescription => Description.Length > 0;

    /// <summary>
    /// 按开播时间列出读到的全部 EPG 节目，录制中的节目关联节目带上的对应段。
    /// 日期变化处插入一行日期标题，节目行只写时段，标题与简介不因日期多占一行。
    /// </summary>
    public static IReadOnlyList<SegmentListItem> FromEpg(EpgGuide guide, IReadOnlyList<TimelineSegmentItem> segments)
    {
        var strings = LocalizationManager.Instance;
        var items = new List<SegmentListItem>(guide.Events.Count + 1);
        var lastDate = "";
        foreach (var value in guide.Events)
        {
            var date = EpgText.Date(value, guide.LocalOffset);
            if (date.Length > 0 && date != lastDate)
            {
                items.Add(new SegmentListItem("", date, "", "", null) { IsHeader = true });
                lastDate = date;
            }
            var segment = segments.FirstOrDefault(item => item.Event?.EventId == value.EventId);
            var status = segment is null
                ? strings.String_Epg_NotInRecording
                : string.Format(strings.String_Epg_InRecording, CommonUtil.FormatSeconds(segment.StartSeconds),
                    CommonUtil.FormatSeconds(segment.EndSeconds));
            items.Add(new SegmentListItem(EpgText.Schedule(value, guide.LocalOffset), value.Title,
                value.Description.Trim(), status, segment));
        }
        return items;
    }

    public static IReadOnlyList<SegmentListItem> FromChapters(IReadOnlyList<TimelineSegmentItem> segments) =>
        segments.Select(segment => new SegmentListItem(CommonUtil.FormatSeconds(segment.StartSeconds), segment.Title, "",
            string.Format(LocalizationManager.Instance.String_Chapter_Length,
                CommonUtil.FormatSeconds(Math.Round(segment.EndSeconds - segment.StartSeconds, 3))), segment)).ToArray();
}

/// <summary>
/// EPG 时间的显示格式：排期按广播的本地时差（没有时按本机时区）显示为“时:分”，需要时在前面加上节目表中的日期。
/// </summary>
public static class EpgText
{
    public static string Schedule(EpgEvent value, TimeSpan? localOffset, bool withDate = false)
    {
        if (Local(value, localOffset) is not { } local)
            return "";
        var time = value.Duration > TimeSpan.Zero
            ? $"{local:HH:mm}–{local + value.Duration:HH:mm}"
            : $"{local:HH:mm}";
        return withDate ? $"{Date(value, localOffset)} {time}" : time;
    }

    /// <summary>开播日期（与排期同一时区）；节目表没有开播时间时为空。</summary>
    public static string Date(EpgEvent value, TimeSpan? localOffset) =>
        Local(value, localOffset)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    private static DateTimeOffset? Local(EpgEvent value, TimeSpan? localOffset) => value.Start is not { } start ? null
        : localOffset is { } offset ? start.ToOffset(offset) : start.ToLocalTime();

    public static string Duration(TimeSpan duration)
    {
        var strings = LocalizationManager.Instance;
        var hours = (int)duration.TotalHours;
        var minutes = duration.Minutes;
        if (hours > 0 && minutes > 0)
            return string.Format(strings.String_Epg_DurationHoursMinutes, hours, minutes);
        return hours > 0
            ? string.Format(strings.String_Epg_DurationHours, hours)
            : string.Format(strings.String_Epg_DurationMinutes, Math.Max(1, (int)Math.Round(duration.TotalMinutes)));
    }
}
